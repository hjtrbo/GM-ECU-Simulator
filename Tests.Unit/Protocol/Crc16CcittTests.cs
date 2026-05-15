using Common.Protocol;
using Xunit;

namespace EcuSimulator.Tests.Protocol;

// CRC-16/CCITT-FALSE conformance vectors. The first three are the standard
// reference checks any CCITT-FALSE implementation must satisfy (see
// reveng.sourceforge.io / pycrc); the rest exercise edge cases the
// $31 $0401 CheckMemoryByAddress path can hit.
public sealed class Crc16CcittTests
{
    [Fact]
    public void Empty_ReturnsInitialValue()
    {
        Assert.Equal(0xFFFF, Crc16Ccitt.Compute(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void AsciiCheckString_MatchesReferenceVector()
    {
        // CRC-16/CCITT-FALSE("123456789") = 0x29B1.
        var data = System.Text.Encoding.ASCII.GetBytes("123456789");
        Assert.Equal(0x29B1, Crc16Ccitt.Compute(data));
    }

    [Fact]
    public void AsciiSingleByteA_MatchesReferenceVector()
    {
        // CRC-16/CCITT-FALSE("A") = 0xB915. Sanity check for the bit-shift
        // inner loop against the documented per-byte transition.
        Assert.Equal(0xB915, Crc16Ccitt.Compute(new byte[] { (byte)'A' }));
    }

    [Fact]
    public void AllFf_OneKilobyte_IsStable()
    {
        // 1024 $FF bytes is the post-erase state of a flash region the kernel
        // would CRC right after $FF00 erase before any $36 writes. Captures
        // the regression target: a freshly erased region must return a stable,
        // deterministic CRC (not 0, not 0xFFFF, since $FF != identity here).
        var data = new byte[1024];
        data.AsSpan().Fill(0xFF);
        ushort first = Crc16Ccitt.Compute(data);
        ushort second = Crc16Ccitt.Compute(data);
        Assert.Equal(first, second);
        Assert.NotEqual(0x0000, first);
    }
}
