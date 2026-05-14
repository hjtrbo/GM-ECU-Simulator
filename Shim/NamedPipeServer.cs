using System.Diagnostics;
using System.IO.Pipes;
using Common.Wire;
using Core.Bus;

namespace Shim.Ipc;

// Pipe server. Single-instance: only one J2534 host may be connected at a
// time. A second client trying to open the pipe while one is connected
// gets ERROR_PIPE_BUSY from the OS until the first disconnects. This is
// enforced two ways: the NamedPipeServerStream is constructed with
// maxNumberOfServerInstances = 1, and the accept loop awaits the current
// handler before constructing the next listening instance.
//
// Lifecycle: Start / StopAsync are paired and may be cycled. The simulator
// uses this to bind the pipe to J2534 registration state: when the user
// unregisters the shim DLL, the pipe stops accepting and any in-flight
// client handler is cancelled and disposed. That guarantees a host can
// no longer reach the simulator once unregister succeeds, instead of the
// pre-fix behaviour where an already-connected host stayed live forever
// because the registry write was the only thing Unregister touched.
public sealed class NamedPipeServer : IAsyncDisposable
{
    public const string PipeName = "GmEcuSim.PassThru";

    private readonly Action<string> log;
    private readonly VirtualBus bus;

    // Recreated on every Start so Stop -> Start cycles work cleanly. Null
    // while the server is idle. The lifecycle lock serialises mutations to
    // cts + acceptLoop so concurrent Start / Stop calls can't tear state.
    private CancellationTokenSource? cts;
    private Task? acceptLoop;
    private readonly Lock lifecycleLock = new();

    // The accept loop awaits the current handler inline before constructing
    // the next listening pipe instance, so at most one handler is ever in
    // flight. StopAsync only has to wait for the accept loop to exit; the
    // handler is guaranteed to have drained by then.

    public NamedPipeServer(VirtualBus bus, Action<string>? log = null)
    {
        this.bus = bus;
        this.log = log ?? Console.WriteLine;
    }

    /// <summary>True while the accept loop is running.</summary>
    public bool IsRunning
    {
        get { lock (lifecycleLock) return acceptLoop != null; }
    }

    /// <summary>
    /// Starts accepting clients. Idempotent: a call while the server is
    /// already running is a no-op (no exception). Pairs with StopAsync.
    /// </summary>
    public void Start()
    {
        lock (lifecycleLock)
        {
            if (acceptLoop != null) return;
            cts = new CancellationTokenSource();
            var token = cts.Token;
            acceptLoop = Task.Run(() => AcceptLoopAsync(token));
            log($"NamedPipeServer listening on \\\\.\\pipe\\{PipeName}");
        }
    }

    /// <summary>
    /// Cancels the accept loop and waits for it + every in-flight handler
    /// to drain. Each handler's pipe is disposed via its own `await using`,
    /// which forces the connected client to see a broken pipe on its next
    /// read or write. Safe to call when already stopped (no-op).
    /// </summary>
    public async Task StopAsync()
    {
        Task? loopToAwait;
        CancellationTokenSource? toDispose;
        lock (lifecycleLock)
        {
            if (acceptLoop == null) return;
            cts?.Cancel();
            loopToAwait = acceptLoop;
            toDispose = cts;
            // Clear state up-front so a Start() that races behind us can
            // begin a fresh listener as soon as we return.
            acceptLoop = null;
            cts = null;
        }

        // Awaiting the accept loop is sufficient: the loop awaits each
        // client handler inline before iterating, so when it exits, no
        // handler is still running.
        if (loopToAwait != null) try { await loopToAwait.ConfigureAwait(false); } catch { }

        toDispose?.Dispose();
        log("NamedPipeServer stopped.");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        // Exponential backoff after consecutive accept failures so a persistent
        // OS error (ACL change, name collision, etc.) doesn't pin a CPU.
        int consecutiveFailures = 0;
        const int MaxBackoffMs = 5000;

        while (!ct.IsCancellationRequested)
        {
            // Nullable + assigned-null so the catch blocks can dispose the
            // pipe if WaitForConnectionAsync throws after construction.
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    // Single-instance: a second client gets ERROR_PIPE_BUSY
                    // until the current host disconnects.
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                consecutiveFailures = 0;
            }
            catch (OperationCanceledException)
            {
                pipe?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                pipe?.Dispose();
                consecutiveFailures++;
                int backoffMs = Math.Min(MaxBackoffMs, 100 * (1 << Math.Min(consecutiveFailures - 1, 6)));
                log($"Pipe accept error ({consecutiveFailures}): {ex.Message} - retrying in {backoffMs} ms");
                try { await Task.Delay(backoffMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            // Control only reaches here on the happy path: pipe was constructed,
            // a client connected, no exception escaped the try block.
            log("Pipe client connected.");
            // Await inline so no second listening instance is created until
            // this host disconnects. HandleClientAsync owns the pipe and
            // swallows its own exceptions in the finally block.
            try { await HandleClientAsync(pipe!, ct).ConfigureAwait(false); }
            catch { /* defence in depth; handler already catches internally */ }
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

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
