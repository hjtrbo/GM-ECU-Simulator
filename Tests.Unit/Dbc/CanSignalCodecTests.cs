using Common.Dbc;
using Xunit;

namespace EcuSimulator.Tests.Dbc;

// Bit-packing vectors for CanSignalCodec. These are the load-bearing tests: the Motorola sawtooth
// layout is the easiest thing in the whole feature to get subtly wrong, so each case asserts exact
// payload bytes (not just a round-trip).
public sealed class CanSignalCodecTests
{
    private static DbcSignal Sig(int start, int len, DbcByteOrder order, bool signed = false,
                                 double scale = 1.0, double offset = 0.0)
        => new() { Name = "S", StartBit = start, Length = len, ByteOrder = order, Signed = signed, Scale = scale, Offset = offset };

    [Fact]
    public void Motorola_EngineSpeed_PacksBigEndianAcrossBytes0And1()
    {
        // ENGINE_SPEED : 7|16@0+ (0.25,0). 800 rpm -> raw 3200 = 0x0C80.
        var sig = Sig(7, 16, DbcByteOrder.Motorola, scale: 0.25);
        var buf = new byte[8];
        CanSignalCodec.Pack(buf, sig, 800.0);

        Assert.Equal(0x0C, buf[0]);
        Assert.Equal(0x80, buf[1]);
        Assert.Equal(800.0, CanSignalCodec.Unpack(buf, sig), 3);
    }

    [Fact]
    public void Motorola_16Bit_AtByte4_LandsInBytes4And5()
    {
        // VSKPH-style: 39|16@0+ (0.0078125,0). raw 0x1234 -> physical 4660 * 0.0078125.
        var sig = Sig(39, 16, DbcByteOrder.Motorola, scale: 0.0078125);
        var buf = new byte[8];
        CanSignalCodec.Pack(buf, sig, 4660 * 0.0078125);

        Assert.Equal(0x12, buf[4]);
        Assert.Equal(0x34, buf[5]);
    }

    [Fact]
    public void Motorola_SubByteField_PacksWithinOneByte()
    {
        // OTS_CRK_STAT : 49|2@0+ . Start bit 49 = byte6 bit1; the 2-bit field occupies byte6 bits 1..0.
        var sig = Sig(49, 2, DbcByteOrder.Motorola);
        var buf = new byte[8];
        CanSignalCodec.Pack(buf, sig, 2.0);

        Assert.Equal(0x02, buf[6]);                 // MSB set, LSB clear
        Assert.Equal(2.0, CanSignalCodec.Unpack(buf, sig), 3);
    }

    [Fact]
    public void Motorola_Signed_NegativeValue_TwosComplementBigEndian()
    {
        // DNDT_FG : 23|16@0- (0.25,0). -256 -> raw -1024 = 0xFC00 across bytes 2..3.
        var sig = Sig(23, 16, DbcByteOrder.Motorola, signed: true, scale: 0.25);
        var buf = new byte[8];
        CanSignalCodec.Pack(buf, sig, -256.0);

        Assert.Equal(0xFC, buf[2]);
        Assert.Equal(0x00, buf[3]);
        Assert.Equal(-256.0, CanSignalCodec.Unpack(buf, sig), 3);
    }

    [Fact]
    public void Intel_16Bit_PacksLittleEndian()
    {
        // Intel signal at start bit 8, 16 bits: raw 0x1234 -> byte1 = 0x34 (low), byte2 = 0x12 (high).
        var sig = Sig(8, 16, DbcByteOrder.Intel);
        var buf = new byte[8];
        CanSignalCodec.Pack(buf, sig, 0x1234);

        Assert.Equal(0x34, buf[1]);
        Assert.Equal(0x12, buf[2]);
        Assert.Equal(0x1234, CanSignalCodec.Unpack(buf, sig), 3);
    }

    [Fact]
    public void Unsigned_OverflowValue_ClampsToFieldMax()
    {
        // 8-bit unsigned field; a value past 255 clamps to 0xFF rather than wrapping into neighbours.
        var sig = Sig(7, 8, DbcByteOrder.Motorola);
        var buf = new byte[8];
        CanSignalCodec.Pack(buf, sig, 99999.0);

        Assert.Equal(0xFF, buf[0]);
        Assert.Equal(0x00, buf[1]);                 // neighbour untouched
    }

    [Fact]
    public void SignalBeyondPayload_IsSkipped_NoThrow()
    {
        // Signal at byte 4 but a 2-byte payload: must not throw, and unpack reads 0.
        var sig = Sig(39, 16, DbcByteOrder.Motorola);
        var buf = new byte[2];
        CanSignalCodec.Pack(buf, sig, 1234.0);      // no exception
        Assert.Equal(0.0, CanSignalCodec.Unpack(buf, sig), 3);
    }
}
