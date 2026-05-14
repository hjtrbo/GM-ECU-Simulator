using Common.IsoTp;
using Xunit;

namespace EcuSimulator.Tests.IsoTp;

// ISO 15765-2:2016 §10.3 addressing-format helpers.
public class AddressFormatTests
{
    [Theory]
    [InlineData(AddressFormat.Normal, 0)]
    [InlineData(AddressFormat.NormalFixed, 0)]
    [InlineData(AddressFormat.Extended, 1)]
    [InlineData(AddressFormat.Mixed, 1)]
    public void AddressPrefixBytes_matches_spec(AddressFormat fmt, int expected)
        => Assert.Equal(expected, AddressFormatLayout.AddressPrefixBytes(fmt));

    // §9.6.2.1 / Table 10: classical SF max payload = 7 (normal), 6 (ext/mixed).
    [Theory]
    [InlineData(AddressFormat.Normal, 7)]
    [InlineData(AddressFormat.NormalFixed, 7)]
    [InlineData(AddressFormat.Extended, 6)]
    [InlineData(AddressFormat.Mixed, 6)]
    public void MaxClassicalSfDl(AddressFormat fmt, int expected)
        => Assert.Equal(expected, AddressFormatLayout.MaxClassicalSfDl(fmt));

    // §9.6.3.1: classical FF first-chunk = 6 (normal), 5 (ext/mixed).
    [Theory]
    [InlineData(AddressFormat.Normal, 6)]
    [InlineData(AddressFormat.Extended, 5)]
    [InlineData(AddressFormat.Mixed, 5)]
    public void MaxClassicalFfData(AddressFormat fmt, int expected)
        => Assert.Equal(expected, AddressFormatLayout.MaxClassicalFfData(fmt));

    // §9.6.4.1: classical CF chunk = 7 (normal), 6 (ext/mixed).
    [Theory]
    [InlineData(AddressFormat.Normal, 7)]
    [InlineData(AddressFormat.Extended, 6)]
    [InlineData(AddressFormat.Mixed, 6)]
    public void MaxClassicalCfData(AddressFormat fmt, int expected)
        => Assert.Equal(expected, AddressFormatLayout.MaxClassicalCfData(fmt));

    // §9.6.3.1, Table 14: FF_DL_min for classical CAN.
    [Theory]
    [InlineData(AddressFormat.Normal, 8)]
    [InlineData(AddressFormat.Extended, 7)]
    [InlineData(AddressFormat.Mixed, 7)]
    public void FfDlMinClassical(AddressFormat fmt, int expected)
        => Assert.Equal(expected, AddressFormatLayout.FfDlMinClassical(fmt));

    [Fact]
    public void WriteAddressPrefix_normal_writes_nothing()
    {
        var buf = new byte[8];
        int n = AddressFormatLayout.WriteAddressPrefix(buf, AddressFormat.Normal, 0xAB);
        Assert.Equal(0, n);
        Assert.Equal(0, buf[0]);
    }

    [Fact]
    public void WriteAddressPrefix_extended_puts_nta_in_byte0()
    {
        var buf = new byte[8];
        int n = AddressFormatLayout.WriteAddressPrefix(buf, AddressFormat.Extended, 0xAB);
        Assert.Equal(1, n);
        Assert.Equal(0xAB, buf[0]);
    }

    [Fact]
    public void WriteAddressPrefix_mixed_puts_nae_in_byte0()
    {
        var buf = new byte[8];
        int n = AddressFormatLayout.WriteAddressPrefix(buf, AddressFormat.Mixed, 0xCD);
        Assert.Equal(1, n);
        Assert.Equal(0xCD, buf[0]);
    }

    [Fact]
    public void StripAddressPrefix_normal_passes_through()
    {
        var input = new byte[] { 0x07, 1, 2, 3, 4, 5, 6, 7 };
        var sliced = AddressFormatLayout.StripAddressPrefix(input, AddressFormat.Normal, out var prefix);
        Assert.Equal(0, prefix);
        Assert.Equal(input.Length, sliced.Length);
        Assert.Equal(0x07, sliced[0]);
    }

    [Fact]
    public void StripAddressPrefix_extended_returns_nta_and_remainder()
    {
        var input = new byte[] { 0xAB, 0x07, 1, 2, 3, 4, 5, 6, 7 };
        var sliced = AddressFormatLayout.StripAddressPrefix(input, AddressFormat.Extended, out var prefix);
        Assert.Equal(0xAB, prefix);
        Assert.Equal(8, sliced.Length);
        Assert.Equal(0x07, sliced[0]);
    }

    [Fact]
    public void StripAddressPrefix_mixed_returns_nae_and_remainder()
    {
        var input = new byte[] { 0xCD, 0x07, 1, 2, 3, 4, 5, 6, 7 };
        var sliced = AddressFormatLayout.StripAddressPrefix(input, AddressFormat.Mixed, out var prefix);
        Assert.Equal(0xCD, prefix);
        Assert.Equal(8, sliced.Length);
        Assert.Equal(0x07, sliced[0]);
    }
}

// ISO 15765-2:2016 §10.3.3 NormalFixed 29-bit CAN ID format.
public class NormalFixedAddressTests
{
    // §10.3.3 Table 26 (physical) - PF=0xDA, default priority 6 = 0x18000000.
    // ID bits: priority(3) | R(1) | DP(1) | PF(8) | TA(8) | SA(8)
    [Fact]
    public void BuildPhysical_uses_default_priority_6_and_pf_DA()
    {
        // 6 << 26 = 0x18000000, PF 0xDA << 16 = 0x00DA0000, TA=0x10, SA=0xF1
        uint expected = 0x18000000 | 0x00DA0000 | (0x10u << 8) | 0xF1u;
        Assert.Equal(expected, NormalFixedAddress.BuildPhysical(0x10, 0xF1));
    }

    [Fact]
    public void BuildFunctional_uses_default_priority_6_and_pf_DB()
    {
        uint expected = 0x18000000 | 0x00DB0000 | (0x33u << 8) | 0xF1u;
        Assert.Equal(expected, NormalFixedAddress.BuildFunctional(0x33, 0xF1));
    }

    [Fact]
    public void BuildId_rejects_priority_over_7()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => NormalFixedAddress.BuildId(8, NormalFixedAddress.PfPhysical, 0, 0));

    [Fact]
    public void TryParse_physical_returns_components()
    {
        uint id = NormalFixedAddress.BuildPhysical(0x55, 0xAA);
        Assert.True(NormalFixedAddress.TryParse(id, out var pf, out var ta, out var sa, out var func));
        Assert.Equal(NormalFixedAddress.PfPhysical, pf);
        Assert.Equal(0x55, ta);
        Assert.Equal(0xAA, sa);
        Assert.False(func);
    }

    [Fact]
    public void TryParse_functional_flags_isFunctional()
    {
        uint id = NormalFixedAddress.BuildFunctional(0x42, 0x99);
        Assert.True(NormalFixedAddress.TryParse(id, out _, out _, out _, out var func));
        Assert.True(func);
    }

    [Fact]
    public void TryParse_unknown_pf_returns_false()
    {
        // PF 0xCC is not a normal-fixed value; the receiver should not treat
        // this as our address scheme.
        uint id = (6u << 26) | (0xCCu << 16) | (0x10u << 8) | 0xF1u;
        Assert.False(NormalFixedAddress.TryParse(id, out _, out _, out _, out _));
    }
}
