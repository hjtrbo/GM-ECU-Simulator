using Common.Protocol;
using Core.Bus;
using Core.Ecu;
using Core.Services;

namespace Core.Scheduler;

// STUBBED 2026-05-15. The time-based "no IPC for N seconds -> reset every
// ECU to default session" behaviour was removed because it punished the
// legitimate "user stepped away for a minute mid-flash" case: a host that
// had a periodic $3E registered was still feeding spec-correct keepalives
// to every ECU through DispatchHostTx, but the supervisor measured IPC
// silence instead of TesterPresent delivery and tore the session down
// anyway. That was contrary to GMW3110 §6.2.4 - the ECU should keep its
// session while $3E is arriving on time, regardless of who's generating
// the frame.
//
// Both responsibilities the old supervisor covered are now handled by
// spec-correct paths that already existed:
//   - "Host vanished" cleanup: NamedPipeServer.HandleClientAsync's finally
//     block detects pipe drop (the authoritative signal - USB unplug, host
//     crash, clean PassThruClose) and raises VirtualBus.HostDisconnected;
//     IpcSessionState.Dispose then cancels every periodic timer the host
//     had registered. Once those timers stop, the per-ECU TesterPresent
//     watchdog takes over (see below).
//   - "Session timeout": TesterPresentTicker ticks each EcuNode's
//     TesterPresentState at 50 ms granularity; Service3EHandler resets it
//     on every observed $3E. When P3Cnom (5000 ms) elapses without a
//     reset, the ticker runs EcuExitLogic on that node. This is exactly
//     what the spec calls for and runs per-ECU, not bus-wide.
//
// The class is kept (rather than deleted) so the DoReset() helper remains
// available - it's a tidy "tear everything down right now" path that may
// be useful for a future explicit "force-idle" UI action, or if some new
// signal turns up that genuinely warrants a bus-wide reset. Start() is
// intentionally a no-op; nothing calls RaiseIdleReset() in the current
// build, so subscribers (IpcSessionState, BinReplayCoordinator,
// MainWindow's file-log lifecycle) stay subscribed but never see it fire.
public sealed class IdleBusSupervisor : IDisposable
{
    private readonly VirtualBus bus;
    private readonly DpidScheduler scheduler;

    /// <summary>
    /// Retained for API compatibility; the supervisor no longer runs a
    /// timer so this value is not consulted. See file header.
    /// </summary>
    public int IdleThresholdMs { get; set; } = 10_000;

    public IdleBusSupervisor(VirtualBus bus, DpidScheduler scheduler)
    {
        this.bus = bus;
        this.scheduler = scheduler;
    }

    /// <summary>
    /// No-op stub. The time-based idle reset was removed - see file header
    /// for the rationale and the per-ECU watchdog that replaces it. Left
    /// callable from App.OnStartup so re-enabling the supervisor in future
    /// is a single-line change inside this method.
    /// </summary>
    public void Start() { }

    public void Dispose() { }

    /// <summary>
    /// Spec-compliant "tear it all down" helper. Not currently invoked by
    /// any timer in this build, but kept available for a future explicit
    /// force-idle path. Runs the GMW3110 Exit_Diagnostic_Services flow on
    /// every active ECU, clears LastEnhancedChannel, raises
    /// <see cref="VirtualBus.IdleReset"/> so session subscribers can cancel
    /// their host-driven periodic timers, and posts a status-bar message.
    /// </summary>
    public void DoReset(long idleMs)
    {
        bus.LogDiagnostic?.Invoke(
            $"[idle] forced reset after {idleMs} ms - applying exit_diagnostic_services to active ECUs");

        foreach (var node in bus.Nodes)
        {
            if (node.State.TesterPresent.State == TesterPresentTimerState.Active)
            {
                EcuExitLogic.Run(node, scheduler, node.State.LastEnhancedChannel);
                bus.LogDiagnostic?.Invoke($"[idle] {node.Name}: exit_diagnostic_services applied");
            }
            node.State.LastEnhancedChannel = null;
        }

        bus.RaiseIdleReset();
        bus.OnStatusMessage?.Invoke("J2534 host idle - ECUs reset to default-session state");
    }
}
