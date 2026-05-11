using Common.PassThru;
using Common.Protocol;
using Core.Bus;
using Core.Services;

namespace EcuSimulator.Tests.Core;

public class Service2DHandlerTests
{
    private static ChannelSession NewChannel() =>
        new() { Id = 1, Protocol = ProtocolID.CAN, Baud = 500000 };

    [Fact]
    public void DefinesNewPidMirroringExistingWaveform()
    {
        var node = TestEcus.BuildEcm();
        var ch = NewChannel();
        // SID 0x2D | PID 0xFE40 | MA 0x1234 | MS 2
        Service2DHandler.Handle(node, [0x2D, 0xFE, 0x40, 0x12, 0x34, 0x02], ch);

        Assert.True(ch.RxQueue.TryDequeue(out var msg));
        Assert.Equal(0x6D, msg!.Data[5]);                 // positive response
        Assert.Equal(0xFE, msg.Data[6]);
        Assert.Equal(0x40, msg.Data[7]);

        // The new PID must be retrievable by its dynamic id and mirror the same
        // engineering value as the source PID (waveform config is copied — the
        // produced sample value at any time equals the source's).
        var dynamicPid = node.GetPid(0xFE40);
        var sourcePid = node.GetPid(0x1234);
        Assert.NotNull(dynamicPid);
        Assert.NotNull(sourcePid);
        Assert.Equal(sourcePid!.Waveform.Sample(0), dynamicPid!.Waveform.Sample(0));
    }

    [Fact]
    public void UnknownAddress_ReturnsNrcRequestOutOfRange()
    {
        var node = TestEcus.BuildEcm();
        var ch = NewChannel();
        Service2DHandler.Handle(node, [0x2D, 0xFE, 0x40, 0xDE, 0xAD, 0x02], ch);
        Assert.True(ch.RxQueue.TryDequeue(out var msg));
        Assert.Equal(Nrc.RequestOutOfRange, msg!.Data[7]);
    }

    [Fact]
    public void InvalidMemorySize_ReturnsNrcRequestOutOfRange()
    {
        var node = TestEcus.BuildEcm();
        var ch = NewChannel();
        Service2DHandler.Handle(node, [0x2D, 0xFE, 0x40, 0x12, 0x34, 0x08], ch);  // size 8 invalid
        Assert.True(ch.RxQueue.TryDequeue(out var msg));
        Assert.Equal(Nrc.RequestOutOfRange, msg!.Data[7]);
    }
}
