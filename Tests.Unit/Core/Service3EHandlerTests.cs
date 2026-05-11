using Common.PassThru;
using Common.Protocol;
using Core.Bus;
using Core.Ecu;
using Core.Services;

namespace EcuSimulator.Tests.Core;

public class Service3EHandlerTests
{
    private static ChannelSession NewChannel() =>
        new() { Id = 1, Protocol = ProtocolID.CAN, Baud = 500000 };

    [Fact]
    public void Physical_ResetsTimerAndRespondsPositive()
    {
        var node = TestEcus.BuildEcm();
        node.TesterPresent.Activate();
        node.TesterPresent.TimerMs = 3000;
        var ch = NewChannel();

        Service3EHandler.Handle(node, [0x3E], ch, isFunctional: false);

        Assert.Equal(0, node.TesterPresent.TimerMs);
        Assert.True(ch.RxQueue.TryDequeue(out var msg));
        Assert.Equal(0x7E, msg!.Data[5]);                 // positive response
    }

    [Fact]
    public void Functional_ResetsTimerSilently()
    {
        var node = TestEcus.BuildEcm();
        node.TesterPresent.Activate();
        node.TesterPresent.TimerMs = 3000;
        var ch = NewChannel();

        Service3EHandler.Handle(node, [0x3E], ch, isFunctional: true);

        Assert.Equal(0, node.TesterPresent.TimerMs);
        Assert.Empty(ch.RxQueue);
    }

    [Fact]
    public void ZeroSubFunctionPhysical_RespondsPositive()
    {
        // [$3E $00] is the ISO 14229 zeroSubFunction form — most tester stacks
        // (incl. the sibling DataLogger) send this rather than the bare [$3E].
        // It must be accepted and answered with $7E, otherwise every periodic
        // keepalive from the host would NRC and P3C would never reset.
        var node = TestEcus.BuildEcm();
        node.TesterPresent.Activate();
        node.TesterPresent.TimerMs = 3000;
        var ch = NewChannel();

        Service3EHandler.Handle(node, [0x3E, 0x00], ch, isFunctional: false);

        Assert.Equal(0, node.TesterPresent.TimerMs);
        Assert.True(ch.RxQueue.TryDequeue(out var msg));
        Assert.Equal(0x7E, msg!.Data[5]);
    }

    [Fact]
    public void SuppressPositiveResponse_ResetsTimerSilently()
    {
        // [$3E $80] = ISO 14229 suppressPosRspMsgIndication — reset the timer
        // but emit no response.
        var node = TestEcus.BuildEcm();
        node.TesterPresent.Activate();
        node.TesterPresent.TimerMs = 3000;
        var ch = NewChannel();

        Service3EHandler.Handle(node, [0x3E, 0x80], ch, isFunctional: false);

        Assert.Equal(0, node.TesterPresent.TimerMs);
        Assert.Empty(ch.RxQueue);
    }

    [Fact]
    public void UnknownSubFunctionPhysical_ReturnsNrc12()
    {
        var node = TestEcus.BuildEcm();
        var ch = NewChannel();

        Service3EHandler.Handle(node, [0x3E, 0x42], ch, isFunctional: false);

        Assert.True(ch.RxQueue.TryDequeue(out var msg));
        Assert.Equal(Service.NegativeResponse, msg!.Data[5]);
        Assert.Equal(Nrc.SubFunctionNotSupportedInvalidFormat, msg.Data[7]);
    }

    [Fact]
    public void OverlongPhysical_ReturnsNrc12()
    {
        var node = TestEcus.BuildEcm();
        var ch = NewChannel();

        Service3EHandler.Handle(node, [0x3E, 0x00, 0x00], ch, isFunctional: false);

        Assert.True(ch.RxQueue.TryDequeue(out var msg));
        Assert.Equal(Service.NegativeResponse, msg!.Data[5]);
        Assert.Equal(Nrc.SubFunctionNotSupportedInvalidFormat, msg.Data[7]);
    }

    [Fact]
    public void OverlongFunctional_IsSilent()
    {
        var node = TestEcus.BuildEcm();
        var ch = NewChannel();

        Service3EHandler.Handle(node, [0x3E, 0x00, 0x00], ch, isFunctional: true);

        Assert.Empty(ch.RxQueue);
    }
}
