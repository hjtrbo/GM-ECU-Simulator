using Common.PassThru;
using Common.Protocol;
using Core.Bus;
using Core.Ecu;

namespace EcuSimulator.Tests.Core;

public class TesterPresentTickerTests
{
    private static ChannelSession NewChannel() =>
        new() { Id = 1, Protocol = ProtocolID.CAN, Baud = 500000 };

    [Fact]
    public void State_TickReturnsTrueExactlyOnceAtThreshold()
    {
        // Contract underlying the ticker: TickAndCheckTimeout consumes the
        // edge so a follow-up tick before Deactivate doesn't fire twice.
        var state = new TesterPresentState();
        state.Activate();

        Assert.False(state.TickAndCheckTimeout(2000, Timing.P3Cnom));      // 2000ms
        Assert.False(state.TickAndCheckTimeout(2000, Timing.P3Cnom));      // 4000ms
        Assert.True(state.TickAndCheckTimeout(2000, Timing.P3Cnom));       // 6000ms — fires
        Assert.False(state.TickAndCheckTimeout(50, Timing.P3Cnom));        // counter reset, no double-fire
    }

    [Fact]
    public void State_InactiveDoesNotFire()
    {
        var state = new TesterPresentState();
        // Never Activated.
        Assert.False(state.TickAndCheckTimeout(Timing.P3Cnom + 1000, Timing.P3Cnom));
    }

    [Fact]
    public void Ticker_FiresExitLogic_WhenTimerCrossesThreshold()
    {
        // End-to-end: prime an ECU's P3C counter just under P3Cnom, start the
        // ticker, and confirm EcuExitLogic runs (TesterPresent goes Inactive,
        // unsolicited $60 arrives on the channel) within ~5 ticks.
        var bus = new VirtualBus();
        var node = TestEcus.BuildEcm();
        bus.AddNode(node);
        var ch = NewChannel();
        node.LastEnhancedChannel = ch;
        node.TesterPresent.Activate();
        node.TesterPresent.TimerMs = Timing.P3Cnom - 25;                   // one tick away

        bus.Ticker.Start();
        try
        {
            // Tick period is 50 ms — give it up to 500 ms (10 ticks) to fire.
            var deadline = DateTime.UtcNow.AddMilliseconds(500);
            while (node.TesterPresent.State == TesterPresentTimerState.Active && DateTime.UtcNow < deadline)
                Thread.Sleep(20);

            Assert.Equal(TesterPresentTimerState.Inactive, node.TesterPresent.State);
            Assert.True(ch.RxQueue.TryDequeue(out var msg));
            Assert.Equal(0x60, msg!.Data[5]);                              // unsolicited positive $20
        }
        finally
        {
            bus.Ticker.Dispose();
        }
    }

    [Fact]
    public void Ticker_DoesNotFire_WhenStateIsInactive()
    {
        var bus = new VirtualBus();
        var node = TestEcus.BuildEcm();
        bus.AddNode(node);
        var ch = NewChannel();
        // Active state never set — TimerMs primed past threshold should be ignored.
        node.TesterPresent.TimerMs = Timing.P3Cnom + 5000;

        bus.Ticker.Start();
        try
        {
            Thread.Sleep(150);
            Assert.Equal(TesterPresentTimerState.Inactive, node.TesterPresent.State);
            Assert.Empty(ch.RxQueue);
        }
        finally
        {
            bus.Ticker.Dispose();
        }
    }
}
