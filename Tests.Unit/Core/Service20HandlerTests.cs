using Common.PassThru;
using Common.Protocol;
using Core.Bus;
using Core.Ecu;
using Core.Scheduler;
using Core.Services;

namespace EcuSimulator.Tests.Core;

public class Service20HandlerTests
{
    private static ChannelSession NewChannel() =>
        new() { Id = 1, Protocol = ProtocolID.CAN, Baud = 500000 };

    [Fact]
    public void ValidRequest_RunsExitLogicAndRespondsPositive()
    {
        var bus = new VirtualBus();
        var node = TestEcus.BuildEcm();
        bus.AddNode(node);
        node.TesterPresent.Activate();
        var ch = NewChannel();

        Service20Handler.Handle(node, [0x20], ch, bus.Scheduler);

        Assert.True(ch.RxQueue.TryDequeue(out var msg));
        Assert.Equal(0x60, msg!.Data[5]);                                // positive response
        Assert.Equal(TesterPresentTimerState.Inactive, node.TesterPresent.State);
    }

    [Fact]
    public void WrongLength_ReturnsNrc12()
    {
        var bus = new VirtualBus();
        var node = TestEcus.BuildEcm();
        bus.AddNode(node);
        var ch = NewChannel();

        Service20Handler.Handle(node, [0x20, 0x00], ch, bus.Scheduler);

        Assert.True(ch.RxQueue.TryDequeue(out var msg));
        Assert.Equal(Service.NegativeResponse, msg!.Data[5]);
        Assert.Equal(Nrc.SubFunctionNotSupportedInvalidFormat, msg.Data[7]);
    }
}
