using Common.Protocol;
using Xunit;

namespace EcuSimulator.Tests.Protocol;

// Placeholder byte arrays for the editor's "Auto-populate" path. The set
// has to cover every entry in Gmw3110DidNames.KnownDids (so one click really
// does fill every row) and the format expectations encoded in the comments
// have to match real wire-format conventions or DPS / other testers will
// reject the response on the wire.
public class DefaultDidValuesTests
{
    [Fact]
    public void Every_known_did_has_a_default()
    {
        foreach (var did in Gmw3110DidNames.KnownDids)
        {
            var bytes = DefaultDidValues.Get(did);
            Assert.NotNull(bytes);
            Assert.NotEmpty(bytes);
        }
    }

    [Fact]
    public void Unknown_did_returns_null()
    {
        // Random byte not in the known set.
        Assert.Null(DefaultDidValues.Get(0x77));
        // The $1A SID itself doesn't index a default (it's a service byte).
        Assert.Null(DefaultDidValues.Get(0x1A));
    }

    [Fact]
    public void Vin_default_is_17_ascii_bytes()
    {
        // Real $1A $90 wire response is 17 ASCII chars per GMW3110 §8.3.2.
        // The placeholder must match length so a tester decoding the
        // response doesn't truncate or pad.
        var bytes = DefaultDidValues.Get(0x90);
        Assert.NotNull(bytes);
        Assert.Equal(17, bytes!.Length);
        foreach (var b in bytes)
            Assert.InRange(b, (byte)0x20, (byte)0x7E);
    }

    [Theory]
    [InlineData((byte)0xC0)]
    [InlineData((byte)0xC1)]
    [InlineData((byte)0xC2)]
    [InlineData((byte)0xCB)]
    [InlineData((byte)0xCC)]
    public void Cxx_part_number_defaults_are_4_bytes(byte did)
    {
        // GM operating-software / part-number DIDs are 4-byte BE uint32 on
        // the wire (the bin loader's FlashUInt32BE source kind).
        var bytes = DefaultDidValues.Get(did);
        Assert.NotNull(bytes);
        Assert.Equal(4, bytes!.Length);
    }

    [Fact]
    public void Programming_date_default_is_eight_ascii_digits()
    {
        var bytes = DefaultDidValues.Get(0x99);
        Assert.NotNull(bytes);
        Assert.Equal(8, bytes!.Length);
        foreach (var b in bytes)
            Assert.InRange(b, (byte)'0', (byte)'9');
    }
}
