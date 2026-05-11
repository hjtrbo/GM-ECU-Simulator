using System.Diagnostics;
using System.IO.Pipes;
using Common.Wire;
using Core.Bus;

namespace Core.Ipc;

// Pipe server. Each accepted connection gets its own IpcSessionState +
// RequestDispatcher; all sessions share the global VirtualBus.
public sealed class NamedPipeServer : IAsyncDisposable
{
    public const string PipeName = "GmEcuSim.PassThru";

    private readonly CancellationTokenSource cts = new();
    private readonly Action<string> log;
    private readonly VirtualBus bus;
    private Task? acceptLoop;
    // Tracks every active client-handler task. DisposeAsync awaits them all
    // so a fresh server created on the same pipe name (e.g. between xUnit
    // tests) doesn't race against a still-running handler from the previous
    // server instance.
    private readonly List<Task> clientHandlers = new();
    private readonly Lock handlersLock = new();

    public NamedPipeServer(VirtualBus bus, Action<string>? log = null)
    {
        this.bus = bus;
        this.log = log ?? Console.WriteLine;
    }

    public void Start()
    {
        if (acceptLoop != null) throw new InvalidOperationException("Server already started.");
        acceptLoop = Task.Run(() => AcceptLoopAsync(cts.Token));
        log($"NamedPipeServer listening on \\\\.\\pipe\\{PipeName}");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        // Exponential backoff after consecutive accept failures so a persistent
        // OS error (ACL change, name collision, etc.) doesn't pin a CPU.
        int consecutiveFailures = 0;
        const int MaxBackoffMs = 5000;

        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                consecutiveFailures = 0;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                consecutiveFailures++;
                int backoffMs = Math.Min(MaxBackoffMs, 100 * (1 << Math.Min(consecutiveFailures - 1, 6)));
                log($"Pipe accept error ({consecutiveFailures}): {ex.Message} — retrying in {backoffMs} ms");
                try { await Task.Delay(backoffMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            log("Pipe client connected.");
            var handler = Task.Run(() => HandleClientAsync(pipe, ct), ct);
            lock (handlersLock)
            {
                clientHandlers.RemoveAll(t => t.IsCompleted);
                clientHandlers.Add(handler);
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var sessionState = new IpcSessionState(bus);
        var dispatcher = new RequestDispatcher(sessionState);
        var dispatchTimer = new Stopwatch();
        await using (pipe)
        {
            try
            {
                while (pipe.IsConnected && !ct.IsCancellationRequested)
                {
                    var (msgType, payload) = await FrameTransport.ReadFrameAsync(pipe, ct).ConfigureAwait(false);

                    // STALL DETECTOR (IPC side) -- KEEP THIS. Pairs with the
                    // [stall] detector in DpidScheduler.Entry.Tick. Tracks the
                    // intermittent host-freeze bug seen 2026-05-10 where the
                    // J2534 host UI hangs for ~0.5-1 s when the simulator window
                    // has focus. Logs whenever a single dispatch takes longer
                    // than 300 ms; ReadMsgs is excluded because it intentionally
                    // blocks up to the host-supplied timeout. If [stall-ipc]
                    // lines fire during a glitch, the simulator's pipe handler
                    // is the bottleneck. If they stay silent (and [stall] is
                    // also silent), the freeze is on the host side of the pipe.
                    // DO NOT remove until the root cause is identified.
                    dispatchTimer.Restart();
                    var (respType, respPayload) = dispatcher.Dispatch(msgType, payload);
                    long dispatchMs = dispatchTimer.ElapsedMilliseconds;
                    if (msgType != IpcMessageTypes.ReadMsgsRequest && dispatchMs > 300)
                        bus.LogDiagnostic?.Invoke(
                            $"[stall-ipc] dispatch type=0x{msgType:X2} took {dispatchMs} ms");

                    await FrameTransport.WriteFrameAsync(pipe, respType, respPayload, ct).ConfigureAwait(false);
                }
            }
            catch (EndOfStreamException) { /* client closed */ }
            catch (OperationCanceledException) { /* shutting down */ }
            catch (Exception ex) { log($"Pipe client error: {ex.Message}"); }
            finally
            {
                // Unsubscribes from VirtualBus.IdleReset and disposes any
                // remaining periodic timers so the broken pipe doesn't leak.
                sessionState.Dispose();
                log("Pipe client disconnected.");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        cts.Cancel();
        if (acceptLoop != null) try { await acceptLoop.ConfigureAwait(false); } catch { }

        Task[] handlers;
        lock (handlersLock) handlers = clientHandlers.ToArray();
        foreach (var h in handlers) try { await h.ConfigureAwait(false); } catch { }

        cts.Dispose();
    }
}
