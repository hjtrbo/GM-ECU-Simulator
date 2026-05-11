using Common.Protocol;
using Core.Bus;
using Core.Ecu;
using Core.Services;

namespace Core.Scheduler;

// Watches the bus for prolonged silence from the host and resets ECU/session
// state when the bus has been idle longer than IdleThresholdMs. Catches the
// case where the host went away without a graceful PassThruDisconnect (USB
// unplug, host crash, user paused logging without closing the session) and
// any leftover state that would otherwise carry across into the next session.
//
// "Idle" is measured by the timestamp VirtualBus.LastHostActivityMs, which
// RequestDispatcher updates on every incoming IPC request. The simulator's
// own internal timers (e.g. PassThruStartPeriodicMsg-driven $3E) do NOT
// update it — those would mask a vanished host.
//
// On reset:
//   1. For every ECU whose TesterPresent state is Active, run the spec-
//      compliant Exit_Diagnostic_Services flow (clears DPID schedule, security,
//      $2D dynamic PIDs, sends unsolicited $60 if a channel is still alive).
//   2. Clear LastEnhancedChannel on every ECU so a phantom $60 from the P3C
//      ticker doesn't fire onto an orphaned channel later.
//   3. Raise VirtualBus.IdleReset so each IpcSessionState can cancel its
//      PassThruStartPeriodicMsg timers — there's no host left to receive
//      whatever they were generating.
//
// Uses a System.Threading.Timer (1s tick); TimerOnDelay would be overkill —
// the threshold is in seconds, not sub-millisecond.
public sealed class IdleBusSupervisor : IDisposable
{
    private const int TickPeriodMs = 1000;

    private readonly VirtualBus bus;
    private readonly DpidScheduler scheduler;
    private Timer? timer;

    // Latches once a reset fires so we don't reset every tick while the
    // bus stays idle. Cleared as soon as host activity returns.
    private bool didReset;

    /// <summary>Idle threshold in milliseconds. Default 10000 (10 seconds).</summary>
    public int IdleThresholdMs { get; set; } = 10_000;

    public IdleBusSupervisor(VirtualBus bus, DpidScheduler scheduler)
    {
        this.bus = bus;
        this.scheduler = scheduler;
    }

    public void Start()
    {
        timer ??= new Timer(_ => Tick(), null, TickPeriodMs, TickPeriodMs);
    }

    public void Dispose()
    {
        timer?.Dispose();
        timer = null;
    }

    private void Tick()
    {
        long last = bus.LastHostActivityMs;
        if (last == 0)
        {
            // Never seen host activity — fresh launch with no clients ever connected.
            // Don't trigger a reset; nothing to clean up.
            return;
        }

        long now = (long)bus.NowMs;
        long idleMs = now - last;
        bool isIdle = idleMs >= IdleThresholdMs;

        if (isIdle && !didReset)
        {
            didReset = true;
            try { DoReset(idleMs); }
            catch (Exception ex) { bus.LogDiagnostic?.Invoke($"[idle] reset error: {ex.Message}"); }
        }
        else if (!isIdle && didReset)
        {
            // Host came back — re-arm so the next idle window will reset again.
            didReset = false;
            bus.LogDiagnostic?.Invoke("[idle] host activity resumed");
        }
    }

    private void DoReset(long idleMs)
    {
        bus.LogDiagnostic?.Invoke(
            $"[idle] no host activity for {idleMs}ms — resetting all ECUs to default-session state");

        // 1. Run the spec-compliant exit on every active ECU. Any channel
        //    references on EcuNode get cleared so a stray P3C tick after the
        //    fact doesn't enqueue onto an orphaned ChannelSession.
        foreach (var node in bus.Nodes)
        {
            if (node.TesterPresent.State == TesterPresentTimerState.Active)
            {
                EcuExitLogic.Run(node, scheduler, node.LastEnhancedChannel);
                bus.LogDiagnostic?.Invoke($"[idle] {node.Name}: exit_diagnostic_services applied");
            }
            node.LastEnhancedChannel = null;
        }

        // 2. Notify session-level subscribers so they can cancel host-driven
        //    PassThruStartPeriodicMsg timers (the host is gone — those frames
        //    have nowhere to go and would keep the bus artificially "alive").
        bus.RaiseIdleReset();
    }
}
