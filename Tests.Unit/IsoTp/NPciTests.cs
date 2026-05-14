using Common.IsoTp;
using Xunit;

namespace EcuSimulator.Tests.IsoTp;

// Tests for the ISO 15765-2:2016 N_PCI codec. The cases are pulled directly
// from the spec tables (§9.6) so any drift between code and standard fails
// here first. Each Theory cites the table or section it codifies.
public class NPciTests
{
    // -----------------------------------------------------------------------
    // SF (classical, CAN_DL <= 8) - §9.6.2.1, Table 9 row 1, Table 10
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(1, new byte[] { 0x01, 0xAA })]
    [InlineData(3, new byte[] { 0x03, 0x22, 0xF1, 0x90 })]
    [InlineData(7, new byte[] { 0x07, 0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16 })]
    public void EncodeSingleFrameClassical_writes_low_nibble_length(int dataLen, byte[] expected)
    {
        var data = new byte[dataLen];
        for (int i = 0; i < dataLen; i++) data[i] = expected[i + 1];

        var dest = new byte[8];
        int n = NPci.EncodeSingleFrameClassical(dest, data);

        Assert.Equal(1 + dataLen, n);
        Assert.Equal(expected, dest.AsSpan(0, n).ToArray());
    }

    [Fact]
    public void EncodeSingleFrameClassical_rejects_zero_length()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => NPci.EncodeSingleFrameClassical(new byte[8], ReadOnlySpan<byte>.Empty));

    [Fact]
    public void EncodeSingleFrameClassical_rejects_eight_bytes_of_payload()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => NPci.EncodeSingleFrameClassical(new byte[16], new byte[8]));

    // -----------------------------------------------------------------------
    // SF (CAN-FD escape) - §9.6.2.1, Table 9 row 2, Table 11
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(8)]    // smallest valid SF_DL with the escape (Table 11)
    [InlineData(9)]
    [InlineData(62)]   // largest from Table 13 normal addressing CAN_DL=64 row
    public void EncodeSingleFrameEscape_writes_zero_first_then_length(int sfDl)
    {
        var data = new byte[sfDl];
        for (int i = 0; i < sfDl; i++) data[i] = (byte)(0xA0 + i);

        var dest = new byte[2 + sfDl];
        int n = NPci.EncodeSingleFrameEscape(dest, data);

        Assert.Equal(2 + sfDl, n);
        Assert.Equal(0x00, dest[0]);
        Assert.Equal((byte)sfDl, dest[1]);
        for (int i = 0; i < sfDl; i++) Assert.Equal(data[i], dest[2 + i]);
    }

    // -----------------------------------------------------------------------
    // FF short (FF_DL <= 4095) - §9.6.3.1, Table 9 row 3
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(8, 0x10, 0x08)]
    [InlineData(20, 0x10, 0x14)]
    [InlineData(0x0FF, 0x10, 0xFF)]
    [InlineData(0xFFF, 0x1F, 0xFF)]   // max short FF_DL = 4095
    public void EncodeFirstFrameShort_packs_12bit_length(int ffDl, byte b0, byte b1)
    {
        var dest = new byte[2];
        int n = NPci.EncodeFirstFrameShort(dest, (ushort)ffDl);

        Assert.Equal(2, n);
        Assert.Equal(b0, dest[0]);
        Assert.Equal(b1, dest[1]);
    }

    [Fact]
    public void EncodeFirstFrameShort_rejects_lengths_over_4095()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => NPci.EncodeFirstFrameShort(new byte[2], 4096));

    // -----------------------------------------------------------------------
    // FF escape (FF_DL > 4095) - §9.6.3.1, Table 9 row 4
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(4096u,        new byte[] { 0x10, 0x00, 0x00, 0x00, 0x10, 0x00 })]
    [InlineData(0x12345678u,  new byte[] { 0x10, 0x00, 0x12, 0x34, 0x56, 0x78 })]
    [InlineData(0xFFFFFFFFu,  new byte[] { 0x10, 0x00, 0xFF, 0xFF, 0xFF, 0xFF })]
    public void EncodeFirstFrameEscape_writes_be32_length(uint ffDl, byte[] expected)
    {
        var dest = new byte[6];
        int n = NPci.EncodeFirstFrameEscape(dest, ffDl);

        Assert.Equal(6, n);
        Assert.Equal(expected, dest);
    }

    [Fact]
    public void EncodeFirstFrameEscape_rejects_lengths_in_short_range()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => NPci.EncodeFirstFrameEscape(new byte[6], 4095));

    // -----------------------------------------------------------------------
    // CF - §9.6.4.3, Table 17
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(0, 0x20)]
    [InlineData(1, 0x21)]
    [InlineData(0xE, 0x2E)]
    [InlineData(0xF, 0x2F)]
    public void EncodeConsecutiveFrame_packs_sn_in_low_nibble(int sn, byte expected)
    {
        var dest = new byte[1];
        int n = NPci.EncodeConsecutiveFrame(dest, (byte)sn);

        Assert.Equal(1, n);
        Assert.Equal(expected, dest[0]);
    }

    [Fact]
    public void EncodeConsecutiveFrame_rejects_sn_over_15()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => NPci.EncodeConsecutiveFrame(new byte[1], 16));

    // -----------------------------------------------------------------------
    // FC - §9.6.5, Table 9 row 6
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(FlowStatus.ContinueToSend, 0x00, 0x00, 0x30, 0x00, 0x00)]
    [InlineData(FlowStatus.ContinueToSend, 0x10, 0x0A, 0x30, 0x10, 0x0A)]   // BS=16, STmin=10ms
    [InlineData(FlowStatus.Wait,           0xFF, 0x7F, 0x31, 0xFF, 0x7F)]
    [InlineData(FlowStatus.Overflow,       0x00, 0x00, 0x32, 0x00, 0x00)]
    public void EncodeFlowControl_packs_fs_bs_stmin(
        FlowStatus fs, byte bs, byte stmin, byte b0, byte b1, byte b2)
    {
        var dest = new byte[3];
        int n = NPci.EncodeFlowControl(dest, fs, bs, stmin);

        Assert.Equal(3, n);
        Assert.Equal(b0, dest[0]);
        Assert.Equal(b1, dest[1]);
        Assert.Equal(b2, dest[2]);
    }

    // -----------------------------------------------------------------------
    // Decoder - happy paths
    // -----------------------------------------------------------------------

    [Fact]
    public void Decode_classical_SF_returns_sf_dl_in_low_nibble()
    {
        Assert.True(NPci.TryDecode(new byte[] { 0x07, 1, 2, 3, 4, 5, 6, 7 }, out var h));
        Assert.Equal(NPciType.SingleFrame, h.Type);
        Assert.False(h.UsesEscape);
        Assert.Equal(1, h.PciByteCount);
        Assert.Equal(7, h.Length);
    }

    [Fact]
    public void Decode_escape_SF_uses_byte2_for_length()
    {
        var frame = new byte[2 + 20];
        frame[0] = 0x00;
        frame[1] = 20;
        Assert.True(NPci.TryDecode(frame, out var h));
        Assert.Equal(NPciType.SingleFrame, h.Type);
        Assert.True(h.UsesEscape);
        Assert.Equal(2, h.PciByteCount);
        Assert.Equal(20, h.Length);
    }

    [Fact]
    public void Decode_short_FF_returns_12bit_length()
    {
        Assert.True(NPci.TryDecode(new byte[] { 0x12, 0x34, 1, 2, 3, 4, 5, 6 }, out var h));
        Assert.Equal(NPciType.FirstFrame, h.Type);
        Assert.False(h.UsesEscape);
        Assert.Equal(2, h.PciByteCount);
        Assert.Equal(0x234, h.Length);
        Assert.Equal(0x234u, h.Length32);
    }

    [Fact]
    public void Decode_escape_FF_returns_32bit_length()
    {
        // FF_DL = 0x12345678 (much bigger than 4095)
        var frame = new byte[] { 0x10, 0x00, 0x12, 0x34, 0x56, 0x78, 1, 2 };
        Assert.True(NPci.TryDecode(frame, out var h));
        Assert.Equal(NPciType.FirstFrame, h.Type);
        Assert.True(h.UsesEscape);
        Assert.Equal(6, h.PciByteCount);
        Assert.Equal(0x12345678u, h.Length32);
    }

    [Fact]
    public void Decode_CF_returns_sequence_number()
    {
        Assert.True(NPci.TryDecode(new byte[] { 0x25, 1, 2, 3, 4, 5, 6, 7 }, out var h));
        Assert.Equal(NPciType.ConsecutiveFrame, h.Type);
        Assert.Equal(1, h.PciByteCount);
        Assert.Equal(5, h.SequenceNumber);
    }

    [Fact]
    public void Decode_FC_returns_fs_bs_stmin()
    {
        Assert.True(NPci.TryDecode(new byte[] { 0x30, 0x08, 0x14 }, out var h));
        Assert.Equal(NPciType.FlowControl, h.Type);
        Assert.Equal(3, h.PciByteCount);
        Assert.Equal(FlowStatus.ContinueToSend, h.FlowStatus);
        Assert.Equal(8, h.BlockSize);
        Assert.Equal(0x14, h.STminRaw);
    }

    // -----------------------------------------------------------------------
    // Decoder - error handling per §9.6.2.2 / §9.6.3.2 / §9.6.5.2
    // -----------------------------------------------------------------------

    [Fact]
    public void Decode_empty_frame_data_returns_false()
        => Assert.False(NPci.TryDecode(ReadOnlySpan<byte>.Empty, out _));

    [Fact]
    public void Decode_escape_SF_with_byte2_under_8_is_rejected()
    {
        // §9.6.2.2: escape SF (low nibble of byte 1 = 0) with SF_DL < 8 is reserved/invalid.
        var frame = new byte[] { 0x00, 0x07, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF };
        Assert.False(NPci.TryDecode(frame, out _));
    }

    [Fact]
    public void Decode_escape_FF_with_short_length_is_rejected()
    {
        // §9.6.3.2: an escape FF whose 32-bit FF_DL still fits in 12 bits (<=4095)
        // shall be ignored.
        var frame = new byte[] { 0x10, 0x00, 0x00, 0x00, 0x0F, 0xFF, 1, 2 };
        Assert.False(NPci.TryDecode(frame, out _));
    }

    [Fact]
    public void Decode_short_FF_with_only_byte0_returns_false()
    {
        // FF needs at least 2 PCI bytes; truncated frames are ignored.
        Assert.False(NPci.TryDecode(new byte[] { 0x10 }, out _));
    }

    [Fact]
    public void Decode_FC_truncated_to_two_bytes_returns_false()
    {
        // FC needs 3 PCI bytes (PCI + BS + STmin).
        Assert.False(NPci.TryDecode(new byte[] { 0x30, 0x08 }, out _));
    }

    [Theory]
    [InlineData(0x40)]  // reserved per §9.6.1
    [InlineData(0x50)]
    [InlineData(0xA0)]
    [InlineData(0xF0)]
    public void Decode_reserved_pci_high_nibble_returns_false(byte b0)
    {
        var frame = new byte[8];
        frame[0] = b0;
        Assert.False(NPci.TryDecode(frame, out _));
    }

    // -----------------------------------------------------------------------
    // Reserved FC FlowStatus surfaces but is up to the upper layer to reject (§9.6.5.2)
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(0x33)]   // FS = 3 (reserved)
    [InlineData(0x3F)]   // FS = F (reserved)
    public void Decode_reserved_FC_FlowStatus_still_decodes(byte b0)
    {
        Assert.True(NPci.TryDecode(new byte[] { b0, 0x00, 0x00 }, out var h));
        Assert.Equal(NPciType.FlowControl, h.Type);
        Assert.True((byte)h.FlowStatus > 2,
            "reserved FlowStatus must surface to the caller so it can raise N_INVALID_FS");
    }
}
