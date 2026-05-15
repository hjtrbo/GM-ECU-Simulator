using Common.PassThru;
using Common.Protocol;
using Core.Bus;
using Core.Ecu;
using Core.Scheduler;
using Core.Services;
using Core.Transport;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Services;

// GMW3110-2010 §8.19 ReadDataByPacketIdentifier ($AA) coverage. Direct
// handler-level tests; the full periodic scheduler is exercised by
// DataLogger-style integration paths and not duplicated here.
public sealed class ServiceAAHandlerTests
{
    private static (VirtualBus bus, EcuNode node, ChannelSession ch, DpidScheduler scheduler) Wire()
    {
        var bus = new VirtualBus();
        var node = NodeFactory.CreateNode();
        node.AddPid(new Pid { Address = 0x000C, Size = PidSize.Byte });
        bus.AddNode(node);
        var ch = new ChannelSession
        {
            Id = 1,
            Protocol = ProtocolID.CAN,
            Baud = 500_000,
            Bus = bus,
        };
        return (bus, node, ch, bus.Scheduler);
    }

    /// <summary>Dequeues a 5-byte UUDT frame and returns [DPID, payload...].</summary>
    private static byte[] DequeueUudt(ChannelSession ch)
    {
        Assert.True(ch.RxQueue.TryDequeue(out var msg), "expected a UUDT frame on the Rx queue");
        var data = msg!.Data;
        Assert.True(data.Length > CanFrame.IdBytes, "frame too short for DPID byte");
        return data.AsSpan(CanFrame.IdBytes).ToArray();
    }

    [Fact]
    public void StopSending_EmitsUudtWithDpidZero()
    {
        // §8.19.1.3: "The positive response to a stopSending DPID request is
        // a single UUDT diagnostic message with a value of $00 in the
        // DPID/message number position and no additional data bytes."
        var (_, node, ch, scheduler) = Wire();

        bool activatesP3C = ServiceAAHandler.Handle(node, new byte[] { 0xAA, 0x00 }, ch, scheduler);

        Assert.False(activatesP3C);     // §8.19: stop doesn't extend P3C
        Assert.Equal(new byte[] { 0x00 }, DequeueUudt(ch));
    }

    [Fact]
    public void StopSending_WithSpecificDpids_StillEmitsUudtZero()
    {
        // §8.19.1.3: stop with selective DPIDs - same UUDT $00 acknowledge.
        var (_, node, ch, scheduler) = Wire();

        ServiceAAHandler.Handle(node, new byte[] { 0xAA, 0x00, 0xFE, 0xFD }, ch, scheduler);

        Assert.Equal(new byte[] { 0x00 }, DequeueUudt(ch));
    }

    [Fact]
    public void SendOneResponse_UnknownDpid_ReturnsNrc31()
    {
        // §8.19.4 NRC $31: any DPID in the request is invalid (not defined).
        var (_, node, ch, scheduler) = Wire();

        ServiceAAHandler.Handle(node, new byte[] { 0xAA, 0x01, 0xFE }, ch, scheduler);

        Assert.True(ch.RxQueue.TryDequeue(out var msg));
        // NRC arrives as a USDT SF $7F AA 31 on the USDT response CAN-ID.
        var sfPayload = TestFrame.SingleFramePayload(msg!.Data);
        Assert.Equal(new byte[] { Service.NegativeResponse, Service.ReadDataByPacketIdentifier, Nrc.RequestOutOfRange }, sfPayload);
    }

    [Fact]
    public void SendOneResponse_NoDpids_ReturnsNrc12()
    {
        // §8.19.4: sub-function $01 with message length < 3.
        var (_, node, ch, scheduler) = Wire();

        ServiceAAHandler.Handle(node, new byte[] { 0xAA, 0x01 }, ch, scheduler);

        Assert.True(ch.RxQueue.TryDequeue(out var msg));
        var sfPayload = TestFrame.SingleFramePayload(msg!.Data);
        Assert.Equal(new byte[] { Service.NegativeResponse, Service.ReadDataByPacketIdentifier, Nrc.SubFunctionNotSupportedInvalidFormat }, sfPayload);
    }

    [Fact]
    public void SendOneResponse_KnownDpid_EmitsUudtWithDpidId()
    {
        // §8.19.1.1: one UUDT response per requested DPID.
        var (_, node, ch, scheduler) = Wire();
        var pid = node.GetPid(0x000C)!;
        node.State.AddDpid(new Dpid { Id = 0xFE, Pids = new[] { pid } });

        bool activatesP3C = ServiceAAHandler.Handle(node, new byte[] { 0xAA, 0x01, 0xFE }, ch, scheduler);

        Assert.True(activatesP3C);
        var uudt = DequeueUudt(ch);
        Assert.Equal(0xFE, uudt[0]);
        Assert.Equal(2, uudt.Length);   // DPID + 1 Byte-sized PID
    }

    [Fact]
    public void InvalidRate_ReturnsNrc12()
    {
        // §8.19.4: sub-function not defined ($05-$FF reserved).
        var (_, node, ch, scheduler) = Wire();

        ServiceAAHandler.Handle(node, new byte[] { 0xAA, 0x09, 0xFE }, ch, scheduler);

        Assert.True(ch.RxQueue.TryDequeue(out var msg));
        var sfPayload = TestFrame.SingleFramePayload(msg!.Data);
        Assert.Equal(new byte[] { Service.NegativeResponse, Service.ReadDataByPacketIdentifier, Nrc.SubFunctionNotSupportedInvalidFormat }, sfPayload);
    }

    [Fact]
    public void MissingSubFunction_ReturnsNrc12()
    {
        // §8.19.4: message length < 2.
        var (_, node, ch, scheduler) = Wire();

        ServiceAAHandler.Handle(node, new byte[] { 0xAA }, ch, scheduler);

        Assert.True(ch.RxQueue.TryDequeue(out var msg));
        var sfPayload = TestFrame.SingleFramePayload(msg!.Data);
        Assert.Equal(new byte[] { Service.NegativeResponse, Service.ReadDataByPacketIdentifier, Nrc.SubFunctionNotSupportedInvalidFormat }, sfPayload);
    }
}
