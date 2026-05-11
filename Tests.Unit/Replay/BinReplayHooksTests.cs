using Common.Protocol;
using Common.Replay;
using Core.Bus;
using Core.Ecu;
using Core.Replay;

namespace EcuSimulator.Tests.Replay;

// Verifies the start/stop trigger plumbing through VirtualBus,
// TesterPresentTicker and IdleBusSupervisor.
public class BinReplayHooksTests
{
    [Fact]
    public void IdleReset_TransitionsCoordinatorToStopped()
    {
        var bus = new VirtualBus();
        var coord = new BinReplayCoordinator(bus);
        bus.Replay = coord;
        coord.Load(FakeBinSource.Default());
        coord.MaybeStart(0);
        Assert.Equal(BinReplayState.Running, coord.State);

        bus.RaiseIdleReset();
        Assert.Equal(BinReplayState.Stopped, coord.State);
    }

    [Fact]
    public void AllNodesP3CTimeout_StopsPlayback()
    {
        // Two ECUs, both Active P3C, both timed-out simultaneously.
        // Ticker should call coord.MaybeStop after the second exit fires.
        var bus = new VirtualBus();
        var coord = new BinReplayCoordinator(bus);
        bus.Replay = coord;
        coord.Load(FakeBinSource.Default());
        coord.MaybeStart(0);

        var n1 = new EcuNode { Name = "ECM", PhysicalRequestCanId = 0x7E0,
                               UsdtResponseCanId = 0x7E8, UudtResponseCanId = 0x5E8 };
        var n2 = new EcuNode { Name = "TCM", PhysicalRequestCanId = 0x7E1,
                               UsdtResponseCanId = 0x7E9, UudtResponseCanId = 0x5E9 };
        bus.AddNode(n1); bus.AddNode(n2);
        n1.TesterPresent.Activate();
        n1.TesterPresent.TimerMs = Timing.P3Cnom - 25;
        n2.TesterPresent.Activate();
        n2.TesterPresent.TimerMs = Timing.P3Cnom - 25;

        bus.Ticker.Start();
        try
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(500);
            while (coord.State != BinReplayState.Stopped && DateTime.UtcNow < deadline)
                Thread.Sleep(20);
            Assert.Equal(BinReplayState.Stopped, coord.State);
        }
        finally { bus.Ticker.Dispose(); }
    }

    [Fact]
    public void OneNodeStillActive_DoesNotStopPlayback()
    {
        // Two ECUs; only one exits. Coordinator should stay Running.
        var bus = new VirtualBus();
        var coord = new BinReplayCoordinator(bus);
        bus.Replay = coord;
        coord.Load(FakeBinSource.Default());
        coord.MaybeStart(0);

        var n1 = new EcuNode { Name = "ECM", PhysicalRequestCanId = 0x7E0,
                               UsdtResponseCanId = 0x7E8, UudtResponseCanId = 0x5E8 };
        var n2 = new EcuNode { Name = "TCM", PhysicalRequestCanId = 0x7E1,
                               UsdtResponseCanId = 0x7E9, UudtResponseCanId = 0x5E9 };
        bus.AddNode(n1); bus.AddNode(n2);
        n1.TesterPresent.Activate();
        n1.TesterPresent.TimerMs = Timing.P3Cnom - 25;   // about to time out
        n2.TesterPresent.Activate();                      // fresh, won't time out

        bus.Ticker.Start();
        try
        {
            // Wait a bit longer than the first ECU's timeout window but
            // shorter than the second ECU's.
            Thread.Sleep(200);
            Assert.Equal(TesterPresentTimerState.Inactive, n1.TesterPresent.State);
            Assert.Equal(TesterPresentTimerState.Active,   n2.TesterPresent.State);
            Assert.Equal(BinReplayState.Running, coord.State);
        }
        finally { bus.Ticker.Dispose(); }
    }
}
