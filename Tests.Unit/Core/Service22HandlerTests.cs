using Common.PassThru;
using Common.Protocol;
using Core.Bus;
using Core.Services;

namespace EcuSimulator.Tests.Core;

public class Service22HandlerTests
{
    private static ChannelSession NewChannel() =>
        new() { Id = 1, Protocol = ProtocolID.CAN, Baud = 500000 };

    [Fact]
    public void SinglePid_ReturnsPositiveResponseWithEncodedValue()
    {
        var node = TestEcus.BuildEcm();
        var ch = NewChannel();
        var usdt = new byte[] { 0x22, 0x12, 0x34 };

        Service22Handler.Handle(node, usdt, ch, timeMs: 0);

        Assert.True(ch.RxQueue.TryDequeue(out var msg));
        // CAN(4) + PCI(1) + SID(1) + PID(2) + value(2) = 10
        Assert.Equal(10, msg!.Data.Length);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x06, 0x41 }, msg.Data[0..4]);
        Assert.Equal(0x05, msg.Data[4]);                 // PCI: SF len 5
        Assert.Equal(0x62, msg.Data[5]);                 // positive SID
        Assert.Equal(new byte[] { 0x12, 0x34 }, msg.Data[6..8]);
        // 80°C with scalar=0.0625, offset=-40 -> raw 1920 = 0x0780
        Assert.Equal(new byte[] { 0x07, 0x80 }, msg.Data[8..10]);
    }

    [Fact]
    public void MultiPid_FragmentsResponseAcrossFirstAndConsecutiveFrames()
    {
        var node = TestEcus.BuildEcm();
        var ch = NewChannel();
        var usdt = new byte[] { 0x22, 0x12, 0x34, 0x56, 0x78 };   // two PIDs

        Service22Handler.Handle(node, usdt, ch, timeMs: 0);

        // Response payload: 1 SID + 2*(2 PID echo + 2 value) = 9 bytes -> FF + 1 CF
        Assert.Equal(2, ch.RxQueue.Count);
    }

    [Fact]
    public void UnknownPid_ReturnsNrcRequestOutOfRange()
    {
        var node = TestEcus.BuildEcm();
        var ch = NewChannel();
        Service22Handler.Handle(node, [0x22, 0xDE, 0xAD], ch, timeMs: 0);

        Assert.True(ch.RxQueue.TryDequeue(out var msg));
        Assert.Equal(Service.NegativeResponse, msg!.Data[5]);
        Assert.Equal(Service.ReadDataByParameterIdentifier, msg.Data[6]);
        Assert.Equal(Nrc.RequestOutOfRange, msg.Data[7]);
    }

    [Fact]
    public void OddPidByteCount_ReturnsNrcInvalidFormat()
    {
        var node = TestEcus.BuildEcm();
        var ch = NewChannel();
        // 4 trailing bytes after SID: 0x12, 0x34, 0x56 = 3 bytes (odd) -> NRC $12
        Service22Handler.Handle(node, [0x22, 0x12, 0x34, 0x56], ch, timeMs: 0);

        Assert.True(ch.RxQueue.TryDequeue(out var msg));
        Assert.Equal(Nrc.SubFunctionNotSupportedInvalidFormat, msg!.Data[7]);
    }
}
