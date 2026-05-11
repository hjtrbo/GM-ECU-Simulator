using Common.PassThru;
using Common.Protocol;
using Core.Bus;
using Core.Ecu;

namespace EcuSimulator.Tests.Core;

public class VirtualBusTests
{
    private static ChannelSession NewChannel() =>
        new() { Id = 1, Protocol = ProtocolID.CAN, Baud = 500000 };

    [Fact]
    public void DispatchHostTx_RoutesByDestinationCanId()
    {
        var bus = new VirtualBus();
        var ecm = TestEcus.BuildEcm();
        bus.AddNode(ecm);
        var ch = NewChannel();

        // CAN ID 0x241 + PCI 0x03 + SID 0x22 + PID 0x1234
        var frame = new byte[] { 0x00, 0x00, 0x02, 0x41, 0x03, 0x22, 0x12, 0x34 };
        bus.DispatchHostTx(frame, ch);

        Assert.True(ch.RxQueue.TryDequeue(out var msg));
        Assert.Equal(0x62, msg!.Data[5]);                  // positive $22 response
    }

    [Fact]
    public void DispatchHostTx_UnknownCanId_DropsFrame()
    {
        var bus = new VirtualBus();
        var ecm = TestEcus.BuildEcm();
        bus.AddNode(ecm);
        var ch = NewChannel();

        var frame = new byte[] { 0x00, 0x00, 0x09, 0x99, 0x03, 0x22, 0x12, 0x34 };
        bus.DispatchHostTx(frame, ch);

        Assert.Empty(ch.RxQueue);
    }

    [Fact]
    public void FunctionalBroadcast_3E_ResetsAllNodeTimers()
    {
        var bus = new VirtualBus();
        var ecm = TestEcus.BuildEcm();
        var tcm = new EcuNode
        {
            Name = "TCM",
            PhysicalRequestCanId = 0x242,
            UsdtResponseCanId = 0x642,
            UudtResponseCanId = 0x542,
        };
        bus.AddNode(ecm);
        bus.AddNode(tcm);
        ecm.TesterPresent.Activate(); ecm.TesterPresent.TimerMs = 3000;
        tcm.TesterPresent.Activate(); tcm.TesterPresent.TimerMs = 3000;
        var ch = NewChannel();

        // CAN 0x101 | ext 0xFE | PCI 0x01 | SID 0x3E
        var frame = new byte[] { 0x00, 0x00, 0x01, 0x01, 0xFE, 0x01, 0x3E };
        bus.DispatchHostTx(frame, ch);

        Assert.Equal(0, ecm.TesterPresent.TimerMs);
        Assert.Equal(0, tcm.TesterPresent.TimerMs);
        Assert.Empty(ch.RxQueue);                          // functional $3E is silent
    }

    [Fact]
    public void DispatchHostTx_AAPeriodicMarksLastEnhancedChannel()
    {
        var bus = new VirtualBus();
        var ecm = TestEcus.BuildEcm();
        bus.AddNode(ecm);
        var dpid = new global::Core.Ecu.Dpid { Id = 0xFE, Pids = new[] { ecm.GetPid(0x1234)! } };
        ecm.AddDpid(dpid);
        var ch = NewChannel();

        // CAN 0x241 | PCI 0x03 | SID 0xAA | rate 0x04 (Fast) | DPID 0xFE
        var frame = new byte[] { 0x00, 0x00, 0x02, 0x41, 0x03, 0xAA, 0x04, 0xFE };
        bus.DispatchHostTx(frame, ch);

        Assert.Same(ch, ecm.LastEnhancedChannel);
        Assert.Equal(TesterPresentTimerState.Active, ecm.TesterPresent.State);

        bus.Scheduler.Stop(ecm, Array.Empty<byte>());
    }
}
