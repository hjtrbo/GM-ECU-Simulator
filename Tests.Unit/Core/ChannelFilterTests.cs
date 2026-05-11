using Common.PassThru;
using Common.Protocol;
using Core.Bus;

namespace EcuSimulator.Tests.Core;

public class ChannelFilterTests
{
    private static ChannelSession NewChannel() =>
        new() { Id = 1, Protocol = ProtocolID.CAN, Baud = 500000 };

    private static ChannelFilter Pass(byte[] mask, byte[] pattern) =>
        new() { Id = 1, Type = FilterType.PASS_FILTER, Mask = mask, Pattern = pattern };
    private static ChannelFilter Block(byte[] mask, byte[] pattern) =>
        new() { Id = 2, Type = FilterType.BLOCK_FILTER, Mask = mask, Pattern = pattern };

    [Fact]
    public void Matches_ExactMatch_ReturnsTrue()
    {
        var f = Pass([0xFF, 0xFF, 0xFF, 0xFF], [0x00, 0x00, 0x07, 0xE8]);
        Assert.True(f.Matches([0x00, 0x00, 0x07, 0xE8, 0x05, 0x62]));
    }

    [Fact]
    public void Matches_NonMatch_ReturnsFalse()
    {
        var f = Pass([0xFF, 0xFF, 0xFF, 0xFF], [0x00, 0x00, 0x07, 0xE8]);
        Assert.False(f.Matches([0x00, 0x00, 0x07, 0xE0, 0x05, 0x62]));
    }

    [Fact]
    public void Matches_PartialMaskAllowsDontCareBits()
    {
        // Mask 0xF0 on the low ID byte: 0x7E?  matches.
        var f = Pass([0xFF, 0xFF, 0xFF, 0xF0], [0x00, 0x00, 0x07, 0xE0]);
        Assert.True(f.Matches([0x00, 0x00, 0x07, 0xE5]));
        Assert.True(f.Matches([0x00, 0x00, 0x07, 0xEF]));
        Assert.False(f.Matches([0x00, 0x00, 0x07, 0xD0]));
    }

    [Fact]
    public void Matches_MaskZero_AlwaysMatches()
    {
        // No bits required — everything passes.
        var f = Pass([0x00, 0x00, 0x00, 0x00], [0x12, 0x34, 0x56, 0x78]);
        Assert.True(f.Matches([0x00, 0x00, 0x00, 0x00]));
        Assert.True(f.Matches([0xFF, 0xFF, 0xFF, 0xFF]));
    }

    [Fact]
    public void Matches_ShorterFrameThanFilter_OnlyComparesAvailableBytes()
    {
        var f = Pass([0xFF, 0xFF, 0xFF, 0xFF], [0x00, 0x00, 0x07, 0xE8]);
        Assert.True(f.Matches([0x00, 0x00]));                              // first 2 bytes match
        Assert.False(f.Matches([0x01, 0x00]));                             // first byte mismatches
    }

    [Fact]
    public void Channel_NoFilters_DeliversEverything()
    {
        var ch = NewChannel();
        ch.EnqueueRx(new PassThruMsg { ProtocolID = ProtocolID.CAN, Data = [0x00, 0x00, 0x07, 0xE8, 0x01, 0x7E] });
        Assert.Single(ch.RxQueue);
    }

    [Fact]
    public void Channel_PassFilter_OnlyMatchingFramesDeliver()
    {
        var ch = NewChannel();
        ch.AddFilter(Pass([0xFF, 0xFF, 0xFF, 0xFF], [0x00, 0x00, 0x07, 0xE8]));

        ch.EnqueueRx(new PassThruMsg { ProtocolID = ProtocolID.CAN, Data = [0x00, 0x00, 0x07, 0xE8, 0x01, 0x7E] });
        ch.EnqueueRx(new PassThruMsg { ProtocolID = ProtocolID.CAN, Data = [0x00, 0x00, 0x05, 0xE8, 0x05, 0xFE] });

        Assert.Single(ch.RxQueue);
    }

    [Fact]
    public void Channel_BlockFilterOverridesPass()
    {
        var ch = NewChannel();
        ch.AddFilter(Pass([0xFF, 0xFF, 0xF0, 0x00], [0x00, 0x00, 0x00, 0x00]));   // pass anything in 0x000-0xFFF range
        ch.AddFilter(Block([0xFF, 0xFF, 0xFF, 0xFF], [0x00, 0x00, 0x05, 0xE8]));  // but block 0x5E8

        ch.EnqueueRx(new PassThruMsg { ProtocolID = ProtocolID.CAN, Data = [0x00, 0x00, 0x07, 0xE8, 0x01, 0x7E] });
        ch.EnqueueRx(new PassThruMsg { ProtocolID = ProtocolID.CAN, Data = [0x00, 0x00, 0x05, 0xE8, 0x05, 0xFE] });

        Assert.Single(ch.RxQueue);
        Assert.True(ch.RxQueue.TryDequeue(out var msg));
        Assert.Equal(0xE8, msg!.Data[3]);
        // The 0x5E8 frame got blocked.
    }

    [Fact]
    public void Channel_FlowControlFilterCountsAsPass()
    {
        var ch = NewChannel();
        ch.AddFilter(new ChannelFilter
        {
            Id = 1, Type = FilterType.FLOW_CONTROL_FILTER,
            Mask = [0xFF, 0xFF, 0xFF, 0xFF],
            Pattern = [0x00, 0x00, 0x07, 0xE8],
        });

        ch.EnqueueRx(new PassThruMsg { ProtocolID = ProtocolID.CAN, Data = [0x00, 0x00, 0x07, 0xE8, 0x01, 0x7E] });
        ch.EnqueueRx(new PassThruMsg { ProtocolID = ProtocolID.CAN, Data = [0x00, 0x00, 0x05, 0xE8, 0x05, 0xFE] });

        // FLOW_CONTROL_FILTER is treated as a pass requirement, so the
        // unmatched 0x5E8 frame is dropped.
        Assert.Single(ch.RxQueue);
    }
}
