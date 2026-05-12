using Common.PassThru;
using Core.Bus;
using Core.Transport;
using Xunit;

namespace EcuSimulator.Tests.TestHelpers;

internal static class TestFrame
{
    /// <summary>
    /// Pops one Single Frame ISO-TP message from the channel's Rx queue and
    /// returns the USDT payload (post-PCI). Asserts that exactly one frame
    /// is available and it is a Single Frame. Sufficient for the short $27
    /// responses we exercise in tests.
    /// </summary>
    public static byte[] DequeueSingleFrameUsdt(ChannelSession ch)
    {
        Assert.True(ch.RxQueue.TryDequeue(out var msg), "expected a response frame on the Rx queue");
        var data = msg!.Data;
        Assert.True(data.Length > CanFrame.IdBytes, "frame too short for PCI");
        byte pci = data[CanFrame.IdBytes];
        Assert.Equal(0x00, pci & 0xF0); // Single Frame nibble
        int len = pci & 0x0F;
        Assert.True(data.Length >= CanFrame.IdBytes + 1 + len, "frame truncated");
        return data.AsSpan(CanFrame.IdBytes + 1, len).ToArray();
    }

    /// <summary>Asserts the queue is empty (caller already drained the expected frames).</summary>
    public static void AssertEmpty(ChannelSession ch)
        => Assert.False(ch.RxQueue.TryDequeue(out _), "did not expect any more frames on the Rx queue");
}
