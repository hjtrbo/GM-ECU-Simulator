using Common.PassThru;
using Common.Protocol;
using Core.Bus;
using Core.Transport;

namespace EcuSimulator.Tests.Core;

public class IsoTpFragmenterTests
{
    [Fact]
    public void ShortPayload_FitsInSingleFrame()
    {
        var ch = new ChannelSession { Id = 1, Protocol = ProtocolID.CAN, Baud = 500000 };
        IsoTpFragmenter.EnqueueResponse(ch, 0x641, new byte[] { 0x62, 0x12, 0x34, 0xAB, 0xCD });
        Assert.Single(ch.RxQueue);
        Assert.True(ch.RxQueue.TryDequeue(out var msg));
        // CAN ID + PCI(0x05) + payload(5)
        Assert.Equal(new byte[] { 0x00, 0x00, 0x06, 0x41, 0x05, 0x62, 0x12, 0x34, 0xAB, 0xCD }, msg!.Data);
    }

    [Fact]
    public void NineBytePayload_SplitsIntoFirstAndOneConsecutive()
    {
        var ch = new ChannelSession { Id = 1, Protocol = ProtocolID.CAN, Baud = 500000 };
        var payload = new byte[] { 0x62, 0x12, 0x34, 0xAA, 0xBB, 0x56, 0x78, 0xCC, 0xDD };
        IsoTpFragmenter.EnqueueResponse(ch, 0x641, payload);
        Assert.Equal(2, ch.RxQueue.Count);
        Assert.True(ch.RxQueue.TryDequeue(out var ff));
        Assert.True(ch.RxQueue.TryDequeue(out var cf));
        // FF: PCI 0x10 + length nibble 0x0, then length low byte 0x09, then 6 payload bytes.
        Assert.Equal(0x10, ff!.Data[4]);
        Assert.Equal(0x09, ff.Data[5]);
        Assert.Equal(new byte[] { 0x62, 0x12, 0x34, 0xAA, 0xBB, 0x56 }, ff.Data[6..]);
        // CF: PCI 0x21, then remaining 3 payload bytes.
        Assert.Equal(0x21, cf!.Data[4]);
        Assert.Equal(new byte[] { 0x78, 0xCC, 0xDD }, cf.Data[5..]);
    }

    [Fact]
    public void LongPayload_PacksFullCFsThenTrailing()
    {
        var ch = new ChannelSession { Id = 1, Protocol = ProtocolID.CAN, Baud = 500000 };
        var payload = new byte[20];
        for (int i = 0; i < 20; i++) payload[i] = (byte)i;
        IsoTpFragmenter.EnqueueResponse(ch, 0x641, payload);
        // 20 bytes: FF carries 6, then CFs at 7 each. 20-6=14 -> two CFs (7+7).
        Assert.Equal(3, ch.RxQueue.Count);
        ch.RxQueue.TryDequeue(out var ff);
        ch.RxQueue.TryDequeue(out var cf1);
        ch.RxQueue.TryDequeue(out var cf2);
        Assert.Equal(0x10, ff!.Data[4]);          // First Frame, length nibble 0
        Assert.Equal(0x14, ff.Data[5]);           // total = 0x14 = 20
        Assert.Equal(0x21, cf1!.Data[4]);         // CF seq 1
        Assert.Equal(0x22, cf2!.Data[4]);         // CF seq 2
    }
}
