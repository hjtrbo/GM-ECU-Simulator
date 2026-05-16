using Common.Protocol;
using Xunit;

namespace EcuSimulator.Tests.Protocol;

// Two-way conversion between $1A identifier byte arrays and the user-typed
// strings the editor's Identifiers grid surfaces. These rules drive whether
// the grid initialises a row as text or hex, and whether user edits round-
// trip cleanly back into EcuNode.Identifiers.
public class IdentifierValueParserTests
{
    [Theory]
    [InlineData(new byte[] { }, true)]
    [InlineData(new byte[] { 0x20 }, true)]
    [InlineData(new byte[] { 0x7E }, true)]
    [InlineData(new byte[] { (byte)'V', (byte)'I', (byte)'N' }, true)]
    [InlineData(new byte[] { 0x09, 0x0A, 0x0D }, true)]
    [InlineData(new byte[] { 0x00 }, false)]
    [InlineData(new byte[] { 0x1F }, false)]
    [InlineData(new byte[] { 0x7F }, false)]
    [InlineData(new byte[] { (byte)'A', 0x00, (byte)'Z' }, false)]
    [InlineData(new byte[] { 0xFF }, false)]
    public void IsPrintableAscii_classifies_bytes_correctly(byte[] input, bool expected)
    {
        Assert.Equal(expected, IdentifierValueParser.IsPrintableAscii(input));
    }

    [Fact]
    public void ToHexString_uses_spaced_uppercase_pairs()
    {
        Assert.Equal("", IdentifierValueParser.ToHexString(Array.Empty<byte>()));
        Assert.Equal("AB", IdentifierValueParser.ToHexString(new byte[] { 0xAB }));
        Assert.Equal("01 02 03", IdentifierValueParser.ToHexString(new byte[] { 1, 2, 3 }));
        Assert.Equal("DE AD BE EF", IdentifierValueParser.ToHexString(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }));
    }

    [Theory]
    [InlineData("",            new byte[] { })]
    [InlineData("   ",         new byte[] { })]
    [InlineData("AB",          new byte[] { 0xAB })]
    [InlineData("AB CD EF",    new byte[] { 0xAB, 0xCD, 0xEF })]
    [InlineData("ab cd ef",    new byte[] { 0xAB, 0xCD, 0xEF })]
    [InlineData("0xAB 0xCD",   new byte[] { 0xAB, 0xCD })]
    [InlineData("AB,CD,EF",    new byte[] { 0xAB, 0xCD, 0xEF })]
    [InlineData("AB\tCD\nEF",  new byte[] { 0xAB, 0xCD, 0xEF })]
    [InlineData("1 2 3",       new byte[] { 0x01, 0x02, 0x03 })]
    public void TryParseHexBytes_accepts_expected_forms(string input, byte[] expected)
    {
        var got = IdentifierValueParser.TryParseHexBytes(input);
        Assert.NotNull(got);
        Assert.Equal(expected, got);
    }

    [Theory]
    [InlineData("GG")]              // invalid hex digit
    [InlineData("AB CD ZZ")]        // one bad token in middle
    [InlineData("0xZZ")]            // bad after 0x prefix
    [InlineData("1234")]            // 4-digit token isn't 1..2 hex digits
    public void TryParseHexBytes_returns_null_on_malformed_input(string input)
    {
        Assert.Null(IdentifierValueParser.TryParseHexBytes(input));
    }

    [Theory]
    [InlineData("11",   0x11)]
    [InlineData("0x11", 0x11)]
    [InlineData("$11",  0x11)]
    [InlineData("FF",   0xFF)]
    [InlineData(" 0xab ", 0xAB)]
    public void TryParseHexByte_accepts_decorated_prefixes(string input, byte expected)
    {
        Assert.True(IdentifierValueParser.TryParseHexByte(input, out var v));
        Assert.Equal(expected, v);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ZZ")]
    [InlineData("0x100")]         // overflow
    [InlineData("0x")]
    public void TryParseHexByte_rejects_garbage(string input)
    {
        Assert.False(IdentifierValueParser.TryParseHexByte(input, out _));
    }
}
