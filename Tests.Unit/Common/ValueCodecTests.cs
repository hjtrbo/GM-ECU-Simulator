using Common.Protocol;

namespace EcuSimulator.Tests.Common;

public class ValueCodecTests
{
    [Fact]
    public void Unsigned16_ScaledAndOffset_RoundTrips()
    {
        // GMW3110 coolant temp PID: scalar=0.0625, offset=-40 -> raw = (eng - offset) / scalar.
        // Eng 80°C -> raw 1920 = 0x0780.
        var dest = new byte[2];
        ValueCodec.Encode(80.0, scalar: 0.0625, offset: -40.0,
                          PidDataType.Unsigned, sizeBytes: 2, dest);
        Assert.Equal(0x07, dest[0]);
        Assert.Equal(0x80, dest[1]);
    }

    [Theory]
    [InlineData(0.0, 0x00, 0x00)]
    [InlineData(255.0, 0x00, 0xFF)]
    [InlineData(65535.0, 0xFF, 0xFF)]
    public void Unsigned16_ClampsToTypeRange(double engValue, byte expectedHi, byte expectedLo)
    {
        var dest = new byte[2];
        ValueCodec.Encode(engValue, scalar: 1.0, offset: 0.0, PidDataType.Unsigned, 2, dest);
        Assert.Equal(expectedHi, dest[0]);
        Assert.Equal(expectedLo, dest[1]);
    }

    [Fact]
    public void Signed16_NegativeEncodedAsTwosComplementBigEndian()
    {
        var dest = new byte[2];
        ValueCodec.Encode(-1.0, scalar: 1.0, offset: 0.0, PidDataType.Signed, 2, dest);
        Assert.Equal(0xFF, dest[0]);
        Assert.Equal(0xFF, dest[1]);
    }

    [Fact]
    public void Bool_AboveThresholdEncodesOne()
    {
        var dest = new byte[1];
        ValueCodec.Encode(0.7, 1.0, 0.0, PidDataType.Bool, 1, dest);
        Assert.Equal((byte)1, dest[0]);

        ValueCodec.Encode(0.3, 1.0, 0.0, PidDataType.Bool, 1, dest);
        Assert.Equal((byte)0, dest[0]);
    }

    [Fact]
    public void Unsigned32_BigEndianLayout()
    {
        var dest = new byte[4];
        ValueCodec.Encode(0x12345678, 1.0, 0.0, PidDataType.Unsigned, 4, dest);
        Assert.Equal(new byte[] { 0x12, 0x34, 0x56, 0x78 }, dest);
    }

    [Fact]
    public void DestTooShort_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            ValueCodec.Encode(1.0, 1.0, 0.0, PidDataType.Unsigned, 4, new byte[2]));
    }
}
