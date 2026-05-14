using Common.IsoTp;
using Xunit;

namespace EcuSimulator.Tests.IsoTp;

// ISO 15765-2:2016 §9.6.5.4 / Table 20 STmin codec.
public class STminCodecTests
{
    // §9.6.5.4 row 1: 0x00..0x7F = 0..127 ms.
    [Theory]
    [InlineData(0x00, 0)]
    [InlineData(0x0A, 10_000)]
    [InlineData(0x7F, 127_000)]
    public void Decode_ms_range_returns_microseconds(byte raw, int expectedUs)
        => Assert.Equal(expectedUs, STminCodec.DecodeMicroseconds(raw));

    // §9.6.5.4 row 3: 0xF1..0xF9 = 100..900 us.
    [Theory]
    [InlineData(0xF1, 100)]
    [InlineData(0xF5, 500)]
    [InlineData(0xF9, 900)]
    public void Decode_sub_ms_range_returns_microseconds(byte raw, int expectedUs)
        => Assert.Equal(expectedUs, STminCodec.DecodeMicroseconds(raw));

    // §9.6.5.5: any reserved STmin shall be remapped to 127 ms (longest spec value).
    [Theory]
    [InlineData(0x80)]
    [InlineData(0xC0)]
    [InlineData(0xF0)]
    [InlineData(0xFA)]
    [InlineData(0xFF)]
    public void Decode_reserved_values_remap_to_127_ms(byte raw)
        => Assert.Equal(127_000, STminCodec.DecodeMicroseconds(raw));

    [Theory]
    [InlineData(0x00, 0)]
    [InlineData(0x0A, 10)]
    [InlineData(0x7F, 127)]
    [InlineData(0xF1, 1)]    // 100 us rounds up to 1 ms for ms-only timer paths
    [InlineData(0xF9, 1)]    // 900 us also rounds up to 1 ms
    public void DecodeMillisecondsCeiling_rounds_sub_ms_up_to_1ms(byte raw, int expectedMs)
        => Assert.Equal(expectedMs, STminCodec.DecodeMillisecondsCeiling(raw));

    [Theory]
    [InlineData(0, 0x00)]
    [InlineData(10, 0x0A)]
    [InlineData(127, 0x7F)]
    public void EncodeMilliseconds_roundtrips(int ms, byte expected)
    {
        Assert.Equal(expected, STminCodec.EncodeMilliseconds(ms));
        Assert.Equal(ms * 1000, STminCodec.DecodeMicroseconds(expected));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(128)]
    public void EncodeMilliseconds_rejects_out_of_range(int ms)
        => Assert.Throws<ArgumentOutOfRangeException>(() => STminCodec.EncodeMilliseconds(ms));

    [Theory]
    [InlineData(100, 0xF1)]
    [InlineData(500, 0xF5)]
    [InlineData(900, 0xF9)]
    public void EncodeMicroseconds_roundtrips(int us, byte expected)
    {
        Assert.Equal(expected, STminCodec.EncodeMicroseconds(us));
        Assert.Equal(us, STminCodec.DecodeMicroseconds(expected));
    }

    [Theory]
    [InlineData(50)]      // not a 100 us multiple
    [InlineData(150)]     // not a 100 us multiple
    [InlineData(0)]       // < 100 us
    [InlineData(1000)]    // > 900 us
    public void EncodeMicroseconds_rejects_out_of_range(int us)
        => Assert.Throws<ArgumentOutOfRangeException>(() => STminCodec.EncodeMicroseconds(us));

    [Theory]
    [InlineData(0x00, false)]
    [InlineData(0x7F, false)]
    [InlineData(0x80, true)]
    [InlineData(0xF0, true)]
    [InlineData(0xF1, false)]
    [InlineData(0xF9, false)]
    [InlineData(0xFA, true)]
    [InlineData(0xFF, true)]
    public void IsReserved_classifies_per_table20(byte raw, bool reserved)
        => Assert.Equal(reserved, STminCodec.IsReserved(raw));
}
