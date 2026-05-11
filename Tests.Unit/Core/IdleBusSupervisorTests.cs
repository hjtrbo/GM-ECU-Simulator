using Common.PassThru;
using Common.Protocol;
using Core.Bus;
using Core.Ecu;

namespace EcuSimulator.Tests.Core;

// IdleBusSupervisor.Tick fires once per second; tests use a small idle
// threshold (~200 ms) and wait ~1.5 s for a single tick to land. Slower
// than handler-level unit tests but the only way to exercise the timer
// loop without exposing internals.
public class IdleBusSupervisorTests
{
    private static ChannelSession NewChannel() =>
        new() { Id = 1, Protocol = ProtocolID.CAN, Baud = 500000 };

    [Fact]
    public void NeverActiveHost_DoesNotResetOrFireEvent()
    {
        // Fresh launch with no client ever connected: LastHostActivityMs == 0.
        // Supervisor must not interpret that as "vanished host".
        var bus = new VirtualBus();
        var node = TestEcus.BuildEcm();
        bus.AddNode(node);
        node.TesterPresent.Activate();                                     // primed
        bus.IdleSupervisor.IdleThresholdMs = 100;
        bool fired = false;
        bus.IdleReset += () => fired = true;

        bus.IdleSupervisor.Start();
        try
        {
            Thread.Sleep(1500);                                            // > 1 tick
            Assert.False(fired);
            Assert.Equal(TesterPresentTimerState.Active, node.TesterPresent.State);
        }
        finally { bus.IdleSupervisor.Dispose(); }
    }

    [Fact]
    public void HostVanishes_RunsExitLogicAndRaisesIdleReset()
    {
        var bus = new VirtualBus();
        // NoteHostActivity stores (long)NowMs. On a fast agent the bus clock
        // can still be < 1 ms here, casting to 0 — which Tick interprets as
        // "host never seen". Sleep enough that the stopwatch crosses 1 ms.
        Thread.Sleep(5);
        var node = TestEcus.BuildEcm();
        bus.AddNode(node);
        var ch = NewChannel();
        node.LastEnhancedChannel = ch;
        node.TesterPresent.Activate();
        bus.IdleSupervisor.IdleThresholdMs = 100;
        bool fired = false;
        bus.IdleReset += () => fired = true;

        bus.NoteHostActivity();                                            // prime then go silent
        bus.IdleSupervisor.Start();
        try
        {
            // Wait long enough for the 1s tick + 100ms idle threshold.
            var deadline = DateTime.UtcNow.AddMilliseconds(2500);
            while (!fired && DateTime.UtcNow < deadline) Thread.Sleep(50);

            Assert.True(fired, "IdleReset event should have fired");
            Assert.Equal(TesterPresentTimerState.Inactive, node.TesterPresent.State);
            Assert.Null(node.LastEnhancedChannel);                         // cleared so a stale P3C tick can't re-enqueue
        }
        finally { bus.IdleSupervisor.Dispose(); }
    }

    [Fact]
    public void ResetIsLatched_FiresOnceWhileBusStaysIdle()
    {
        var bus = new VirtualBus();
        Thread.Sleep(5);                                                   // see HostVanishes test for rationale
        var node = TestEcus.BuildEcm();
        bus.AddNode(node);
        node.TesterPresent.Activate();
        bus.IdleSupervisor.IdleThresholdMs = 100;
        int fireCount = 0;
        bus.IdleReset += () => Interlocked.Increment(ref fireCount);

        bus.NoteHostActivity();
        bus.IdleSupervisor.Start();
        try
        {
            Thread.Sleep(2500);                                            // ~2 ticks of continued idle
            Assert.Equal(1, fireCount);
        }
        finally { bus.IdleSupervisor.Dispose(); }
    }

    [Fact]
    public void HostReturnsAfterReset_RearmsForNextIdleWindow()
    {
        var bus = new VirtualBus();
        Thread.Sleep(5);                                                   // see HostVanishes test for rationale
        var node = TestEcus.BuildEcm();
        bus.AddNode(node);
        // Threshold must be larger than the activity-noting cadence below
        // so a tick during "host present" reads idleMs < threshold and
        // clears the latch.
        bus.IdleSupervisor.IdleThresholdMs = 300;
        int fireCount = 0;
        bus.IdleReset += () => Interlocked.Increment(ref fireCount);

        // First idle window.
        node.TesterPresent.Activate();
        bus.NoteHostActivity();
        bus.IdleSupervisor.Start();
        try
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(2500);
            while (fireCount == 0 && DateTime.UtcNow < deadline) Thread.Sleep(50);
            Assert.Equal(1, fireCount);

            // Host returns: keep activity fresh for ~1.5s so a tick lands
            // with idleMs < threshold and clears the didReset latch.
            node.TesterPresent.Activate();
            var until = DateTime.UtcNow.AddMilliseconds(1500);
            while (DateTime.UtcNow < until)
            {
                bus.NoteHostActivity();
                Thread.Sleep(100);
            }

            // Host vanishes again — second reset must fire on a subsequent tick.
            deadline = DateTime.UtcNow.AddMilliseconds(2500);
            while (fireCount < 2 && DateTime.UtcNow < deadline) Thread.Sleep(50);
            Assert.Equal(2, fireCount);
        }
        finally { bus.IdleSupervisor.Dispose(); }
    }

    [Fact]
    public void LastHostActivityMs_NotUpdatedByInternalTimers()
    {
        // Critical contract from CLAUDE.md: only RequestDispatcher updates
        // LastHostActivityMs. The simulator's own scheduler / ticker activity
        // must NOT mask a vanished host. Verified by observing that internal
        // bus operations (AddNode, scheduler activity) never touch the field.
        var bus = new VirtualBus();
        Assert.Equal(0, bus.LastHostActivityMs);

        var node = TestEcus.BuildEcm();
        bus.AddNode(node);
        var ch = NewChannel();
        var dpid = new Dpid { Id = 0xFE, Pids = new[] { node.GetPid(0x1234)! } };
        node.AddDpid(dpid);
        bus.Scheduler.Add(node, dpid, ch, DpidRate.Fast);
        Thread.Sleep(120);
        bus.Scheduler.Stop(node, Array.Empty<byte>());

        // Despite scheduler activity, LastHostActivityMs is still untouched.
        Assert.Equal(0, bus.LastHostActivityMs);

        // Only NoteHostActivity (called by RequestDispatcher) sets it.
        bus.NoteHostActivity();
        Assert.True(bus.LastHostActivityMs > 0);
    }
}
