using Common.PassThru;
using Common.Protocol;
using Core.Bus;
using Core.Ecu;
using Core.Services;

namespace EcuSimulator.Tests.Core;

public class Service10HandlerTests
{
    private static ChannelSession NewChannel() =>
        new() { Id = 1, Protocol = ProtocolID.CAN, Baud = 500000 };

    [Fact]
    public void ValidSubFunction_RespondsPositiveAndReturnsTrue()
    {
        var node = TestEcus.BuildEcm();
        var ch = NewChannel();

        bool activate = Service10Handler.Handle(node, [0x10, 0x02], ch);

        Assert.True(activate);
        Assert.True(ch.RxQueue.TryDequeue(out var msg));
        Assert.Equal(Service.Positive(Service.InitiateDiagnosticOperation), msg!.Data[5]);
        Assert.Equal(0x02, msg.Data[6]);                                   // sub-function echo
    }

    [Fact]
    public void MissingSubFunction_ReturnsNrc12AndFalse()
    {
        var node = TestEcus.BuildEcm();
        var ch = NewChannel();

        bool activate = Service10Handler.Handle(node, [0x10], ch);

        Assert.False(activate);
        Assert.True(ch.RxQueue.TryDequeue(out var msg));
        Assert.Equal(Service.NegativeResponse, msg!.Data[5]);
        Assert.Equal(Service.InitiateDiagnosticOperation, msg.Data[6]);
        Assert.Equal(Nrc.SubFunctionNotSupportedInvalidFormat, msg.Data[7]);
    }

    [Fact]
    public void ExtraBytes_ReturnsNrc12AndFalse()
    {
        var node = TestEcus.BuildEcm();
        var ch = NewChannel();

        bool activate = Service10Handler.Handle(node, [0x10, 0x02, 0x00], ch);

        Assert.False(activate);
        Assert.True(ch.RxQueue.TryDequeue(out var msg));
        Assert.Equal(Nrc.SubFunctionNotSupportedInvalidFormat, msg!.Data[7]);
    }

    [Fact]
    public void DispatchVia_ActivatesP3C()
    {
        // The handler doesn't activate P3C itself — VirtualBus.DispatchUsdt
        // does, on a true return. Cover the wired path so a refactor to the
        // dispatcher's switch can't silently skip the activation.
        var bus = new VirtualBus();
        var node = TestEcus.BuildEcm();
        bus.AddNode(node);
        var ch = NewChannel();

        var frame = new byte[] { 0x00, 0x00, 0x02, 0x41, 0x02, 0x10, 0x02 };
        bus.DispatchHostTx(frame, ch);

        Assert.Equal(TesterPresentTimerState.Active, node.TesterPresent.State);
        Assert.Same(ch, node.LastEnhancedChannel);
    }
}
