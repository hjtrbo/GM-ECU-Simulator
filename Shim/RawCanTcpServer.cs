using Common.PassThru;
using Core.Bus;
using Core.Transport;
using System.Net;
using System.Net.Sockets;

namespace Shim.Ipc;

// Second ingress alongside NamedPipeServer: a localhost TCP listener that a
// separately developed gauge simulator connects to and exchanges raw CAN
// frames with, exactly as if it were a node on a shared CAN bus. Each 13-byte
// wire frame (see RawCanWire) is one classical CAN frame; ISO-TP lives on both
// ends, so this server does ZERO ISO-TP - it just shuttles single frames in and
// out and lets the bus's node-level reassembler/fragmenter do the work.
//
// Lifecycle mirrors NamedPipeServer: idempotent Start / StopAsync under a lock,
// IAsyncDisposable, single connection at a time (the accept loop awaits the
// active handler inline before accepting the next, so a second gauge waits in
// the OS backlog until the first disconnects). Only one transport - this or the
// pipe - is ever live (chosen by ConnectionType), so the connection raises the
// global VirtualBus.HostConnected / HostDisconnected events directly, with no
// per-channel teardown needed.
public sealed class RawCanTcpServer : IAsyncDisposable
{
    // 0xCA11 ("CALL"/CAN) in the dynamic/private port range. The gauge sim is
    // hand-configured with this; nothing needs to discover it via a registry.
    public const int DefaultPort = 0xCA11;        // 51729

    // Single shared-bus channel id. Only one gauge connection exists at a time,
    // so a constant is fine (cf. IpcSessionState's per-pipe-session allocation).
    private const uint GaugeChannelId = 0x6A06;
    private const uint GaugeBaud = 500_000;

    private readonly VirtualBus bus;
    private readonly Action<string> log;
    private readonly int requestedPort;

    private CancellationTokenSource? cts;
    private Task? acceptLoop;
    private TcpListener? listener;
    private readonly Lock lifecycleLock = new();

    private volatile bool isConnected;

    public RawCanTcpServer(VirtualBus bus, int port = DefaultPort, Action<string>? log = null)
    {
        this.bus = bus;
        this.requestedPort = port;
        this.log = log ?? Console.WriteLine;
    }

    /// <summary>True while the accept loop is running.</summary>
    public bool IsRunning
    {
        get { lock (lifecycleLock) return acceptLoop != null; }
    }

    /// <summary>True while a gauge is connected.</summary>
    public bool IsConnected => isConnected;

    /// <summary>
    /// The actual port the listener is bound to. Equals the requested port,
    /// except when 0 was requested (ephemeral) - then it reports the OS-chosen
    /// port after <see cref="Start"/>. 0 while stopped and an ephemeral port
    /// was requested.
    /// </summary>
    public int Port
    {
        get
        {
            lock (lifecycleLock)
                return listener?.LocalEndpoint is IPEndPoint ep ? ep.Port : requestedPort;
        }
    }

    /// <summary>
    /// Starts listening on 127.0.0.1. Idempotent: a call while already running
    /// is a no-op. The listener is created here (not in the ctor) so Stop -&gt;
    /// Start cycles re-listen cleanly.
    /// </summary>
    public void Start()
    {
        lock (lifecycleLock)
        {
            if (acceptLoop != null) return;
            listener = new TcpListener(IPAddress.Loopback, requestedPort);
            listener.Start();
            int boundPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            cts = new CancellationTokenSource();
            var token = cts.Token;
            var ln = listener;
            acceptLoop = Task.Run(() => AcceptLoopAsync(ln, token));
            log($"RawCanTcpServer listening on 127.0.0.1:{boundPort}");
        }
    }

    /// <summary>
    /// Stops accepting, drops any connected gauge, and waits for the accept
    /// loop (and its inline handler) to drain. Safe to call when stopped.
    /// </summary>
    public async Task StopAsync()
    {
        Task? loopToAwait;
        CancellationTokenSource? toDispose;
        TcpListener? toStop;
        lock (lifecycleLock)
        {
            if (acceptLoop == null) return;
            cts?.Cancel();
            loopToAwait = acceptLoop;
            toDispose = cts;
            toStop = listener;
            // Clear up-front so a racing Start() can re-listen as soon as we return.
            acceptLoop = null;
            cts = null;
            listener = null;
        }

        // Stop() unblocks a pending AcceptTcpClientAsync (it throws
        // ObjectDisposedException / SocketException, treated as cancellation).
        try { toStop?.Stop(); } catch { }

        if (loopToAwait != null) try { await loopToAwait.ConfigureAwait(false); } catch { }

        toDispose?.Dispose();
        isConnected = false;
        log("RawCanTcpServer stopped.");
    }

    private async Task AcceptLoopAsync(TcpListener ln, CancellationToken ct)
    {
        int consecutiveFailures = 0;
        const int MaxBackoffMs = 5000;

        while (!ct.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await ln.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                consecutiveFailures = 0;
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }            // listener.Stop() during shutdown
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) break;
                consecutiveFailures++;
                int backoffMs = Math.Min(MaxBackoffMs, 100 * (1 << Math.Min(consecutiveFailures - 1, 6)));
                log($"Raw-CAN accept error ({consecutiveFailures}): {ex.Message} - retrying in {backoffMs} ms");
                try { await Task.Delay(backoffMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            log("Raw-CAN gauge connected.");
            // Await inline so no second gauge is serviced until this one
            // disconnects. HandleClientAsync owns the client and catches its
            // own exceptions in the finally block.
            try { await HandleClientAsync(client!, ct).ConfigureAwait(false); }
            catch { /* defence in depth; handler already catches internally */ }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        // One ChannelSession represents the gauge's view of the shared wire.
        // Protocol = CAN (NOT ISO15765) so EnqueueRx stays on the raw-CAN path
        // and node-level ISO-TP handles segmentation. No filters -> the gauge
        // sees all bus traffic; Loopback left false -> it does not hear its own
        // transmits, like a real bus node.
        var ch = new ChannelSession
        {
            Id = GaugeChannelId,
            Protocol = ProtocolID.CAN,
            Baud = GaugeBaud,
            Bus = bus,
        };

        // Per-connection cancellation: cancelling stops the drain loop when the
        // read loop exits (and is also tripped by the server-wide token).
        using var connCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        isConnected = true;

        try { bus.RaiseHostConnected(); }
        catch (Exception ex) { log($"[raw-can] HostConnected subscriber threw: {ex.Message}"); }

        // Whatever opened the socket is just a raw-CAN TCP client - could be a
        // gauge, a logger, a test harness - so don't assume "gauge". Show the
        // remote endpoint instead; it's the one thing we actually know.
        // Symmetric with the disconnect message below. StatusText is event-driven
        // (last message wins), so without a connect message the status bar keeps
        // showing the stale "disconnected" from the previous session even after a
        // new client attaches - out of sync with the poll-driven titlebar pill
        // (ConnectionStatus reads IsConnected on the UI timer).
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        bus.OnStatusMessage?.Invoke($"raw-CAN TCP client connected ({remote})");

        using (client)
        {
            client.NoDelay = true;                                 // low-latency single frames
            var stream = client.GetStream();
            var drain = Task.Run(() => DrainAsync(ch, stream, connCts.Token));
            var readBuf = new byte[RawCanWire.FrameSize];
            try
            {
                while (client.Connected && !connCts.IsCancellationRequested)
                {
                    await stream.ReadExactlyAsync(readBuf.AsMemory(0, RawCanWire.FrameSize), connCts.Token)
                                .ConfigureAwait(false);
                    var frame = RawCanWire.ToInternal(readBuf);
                    bus.DispatchHostTx(frame, ch);
                }
            }
            catch (EndOfStreamException) { /* client closed */ }
            catch (OperationCanceledException) { /* shutting down */ }
            catch (Exception ex) { log($"Raw-CAN client error: {ex.Message}"); }
            finally
            {
                connCts.Cancel();                                  // stop the drain loop
                try { await drain.ConfigureAwait(false); } catch { }

                isConnected = false;
                // Mutually exclusive with the pipe (only one transport live),
                // so the global session-end signal is correct: CSV log trailer,
                // bin-replay stop, etc. Subscribers are idempotent.
                try { bus.RaiseHostDisconnected(); }
                catch (Exception ex) { log($"[raw-can] HostDisconnected subscriber threw: {ex.Message}"); }
                bus.OnStatusMessage?.Invoke($"raw-CAN TCP client disconnected ({remote})");
                log("Raw-CAN client disconnected.");
            }
        }
    }

    // Pumps the channel's Rx queue (ECU responses, the ECU's FC.CTS, and all
    // UUDT / $AA periodic pushes - everything funnels through ch.EnqueueRx ->
    // RxQueue) out to the gauge as 13-byte wire frames. Single writer to the
    // stream; the read loop is the only reader, and NetworkStream allows one of
    // each concurrently.
    private async Task DrainAsync(ChannelSession ch, NetworkStream stream, CancellationToken ct)
    {
        var wire = new byte[RawCanWire.FrameSize];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await ch.RxAvailable.WaitAsync(ct).ConfigureAwait(false);
                while (ch.RxQueue.TryDequeue(out var msg))
                {
                    if (msg.Data.Length < CanFrame.IdBytes) continue;   // malformed; nothing to send
                    RawCanWire.FromInternal(msg.Data, wire);
                    await stream.WriteAsync(wire.AsMemory(0, RawCanWire.FrameSize), ct).ConfigureAwait(false);
                }
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* connection ending */ }
        catch (Exception ex) { log($"Raw-CAN drain error: {ex.Message}"); }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
