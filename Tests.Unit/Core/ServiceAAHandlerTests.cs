using Common.PassThru;
using Common.Protocol;
using Core.Bus;
using Core.Ecu;
using Core.Services;

namespace EcuSimulator.Tests.Core;

public class ServiceAAHandlerTests
{
    private static ChannelSession NewChannel() =>
        new() { Id = 1, Protocol = ProtocolID.CAN, Baud = 500000 };

    private static (VirtualBus bus, EcuNode node, ChannelSession ch) BuildWithDpid(byte dpidId = 0xFE)
    {
        var bus = new VirtualBus();
        var node = TestEcus.BuildEcm();
        bus.AddNode(node);
        var dpid = new Dpid { Id = dpidId, Pids = new[] { node.GetPid(0x1234)! } };
        node.AddDpid(dpid);
        return (bus, node, NewChannel());
    }

    [Fact]
    public void StopSending_AlwaysReturnsFalseAndDoesNotEnqueueResponse()
    {
        var (bus, node, ch) = BuildWithDpid();
        bus.Scheduler.Add(node, node.Dpids[0xFE], ch, DpidRate.Slow);

        bool activate = ServiceAAHandler.Handle(node, [0xAA, 0x00], ch, bus.Scheduler);

        Assert.False(activate);
        // $AA $00 is silent per GMW3110 — verify no USDT response landed.
        // (Any frames in the queue from the prior Slow scheduling get drained
        // here since timing is loose; we just need to confirm no $EA SID).
        while (ch.RxQueue.TryDequeue(out var m))
            Assert.NotEqual(Service.Positive(Service.ReadDataByPacketIdentifier), m!.Data[5]);
    }

    [Fact]
    public void SendOneResponse_EnqueuesExactlyOneUudtAndReturnsTrue()
    {
        var (bus, node, ch) = BuildWithDpid();

        bool activate = ServiceAAHandler.Handle(node, [0xAA, 0x01, 0xFE], ch, bus.Scheduler);

        Assert.True(activate);
        Assert.Single(ch.RxQueue);
    }

    [Fact]
    public void SendOneResponse_UnknownDpid_ReturnsNrc31()
    {
        var (bus, node, ch) = BuildWithDpid();

        bool activate = ServiceAAHandler.Handle(node, [0xAA, 0x01, 0x42], ch, bus.Scheduler);

        Assert.False(activate);
        Assert.True(ch.RxQueue.TryDequeue(out var msg));
        Assert.Equal(Service.NegativeResponse, msg!.Data[5]);
        Assert.Equal(Service.ReadDataByPacketIdentifier, msg.Data[6]);
        Assert.Equal(Nrc.RequestOutOfRange, msg.Data[7]);
    }

    [Fact]
    public void ScheduleAtRate_NoDpidIds_ReturnsNrc12()
    {
        var (bus, node, ch) = BuildWithDpid();

        bool activate = ServiceAAHandler.Handle(node, [0xAA, 0x04], ch, bus.Scheduler);

        Assert.False(activate);
        Assert.True(ch.RxQueue.TryDequeue(out var msg));
        Assert.Equal(Nrc.SubFunctionNotSupportedInvalidFormat, msg!.Data[7]);
    }

    [Fact]
    public void ScheduleAtFast_RegistersAndActivatesP3C()
    {
        var (bus, node, ch) = BuildWithDpid();

        bool activate = ServiceAAHandler.Handle(node, [0xAA, 0x04, 0xFE], ch, bus.Scheduler);

        Assert.True(activate);
        // Give the scheduler one cycle, then stop and assert at least one frame.
        Thread.Sleep(120);
        bus.Scheduler.Stop(node, Array.Empty<byte>());
        Assert.True(ch.RxQueue.Count >= 1);
    }

    [Fact]
    public void UnknownRate_ReturnsNrc12()
    {
        var (bus, node, ch) = BuildWithDpid();

        bool activate = ServiceAAHandler.Handle(node, [0xAA, 0x42, 0xFE], ch, bus.Scheduler);

        Assert.False(activate);
        Assert.True(ch.RxQueue.TryDequeue(out var msg));
        Assert.Equal(Nrc.SubFunctionNotSupportedInvalidFormat, msg!.Data[7]);
    }

    [Fact]
    public void MissingSubFunction_ReturnsNrc12()
    {
        var (bus, node, ch) = BuildWithDpid();

        bool activate = ServiceAAHandler.Handle(node, [0xAA], ch, bus.Scheduler);

        Assert.False(activate);
        Assert.True(ch.RxQueue.TryDequeue(out var msg));
        Assert.Equal(Service.NegativeResponse, msg!.Data[5]);
        Assert.Equal(Service.ReadDataByPacketIdentifier, msg.Data[6]);
    }
}
