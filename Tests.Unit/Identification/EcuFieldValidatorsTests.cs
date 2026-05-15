using Core.Identification;
using Xunit;

namespace EcuSimulator.Tests.Identification;

// Per-field validator coverage. Each validator returns null when valid
// (including blank), or a short human-readable message otherwise. These
// rules drive the red border in the inspector and the tooltip text the
// user sees, so they're worth pinning.
public sealed class EcuFieldValidatorsTests
{
    // -------------------------- VIN ($90) --------------------------

    [Fact]
    public void ValidateVin_Empty_IsValid()
    {
        Assert.Null(EcuFieldValidators.ValidateVin(""));
        Assert.Null(EcuFieldValidators.ValidateVin(null));
        Assert.Null(EcuFieldValidators.ValidateVin("   "));
    }

    [Theory]
    [InlineData("6G1FK5EP6GL206970")]   // real HSV LSA VIN from one of the test bins
    [InlineData("1G6DN57P290150727")]   // real CTS-V VIN
    [InlineData("WBAAAAAAAAAAAAAA1")]
    public void ValidateVin_ValidVins_AreAccepted(string vin)
    {
        Assert.Null(EcuFieldValidators.ValidateVin(vin));
    }

    [Theory]
    [InlineData("1G6DN57P29015072",     "17 characters")]   // 16 chars
    [InlineData("1G6DN57P2901507272",   "17 characters")]   // 18 chars
    [InlineData("1G6DN57P290I50727",    "I, O, Q")]         // I disallowed
    [InlineData("1G6DN57P290O50727",    "I, O, Q")]         // O disallowed
    [InlineData("1G6DN57P290Q50727",    "I, O, Q")]         // Q disallowed
    [InlineData("1g6dn57p290150727",    "uppercase")]
    [InlineData("1G6DN57P-90150727",    "letters and digits")]  // hyphen
    public void ValidateVin_InvalidVins_ReportMeaningfulError(string vin, string expectedSubstring)
    {
        var error = EcuFieldValidators.ValidateVin(vin);
        Assert.NotNull(error);
        Assert.Contains(expectedSubstring, error!, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------- Supplier ($92, $98) --------------------------

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("DV5053Q103619515")]
    [InlineData("10344202406002ZC1078")]
    [InlineData("X")]
    public void ValidateSupplierAscii_PrintableValuesUpTo32Chars_AreAccepted(string? value)
    {
        Assert.Null(EcuFieldValidators.ValidateSupplierAscii(value));
    }

    [Fact]
    public void ValidateSupplierAscii_TooLong_IsRejected()
    {
        var thirtyThree = new string('A', 33);
        var error = EcuFieldValidators.ValidateSupplierAscii(thirtyThree);
        Assert.NotNull(error);
        Assert.Contains("too long", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateSupplierAscii_NonPrintableByte_IsRejected()
    {
        // 0x01 is non-printable; should be flagged.
        var error = EcuFieldValidators.ValidateSupplierAscii("ABC\x01DEF");
        Assert.NotNull(error);
        Assert.Contains("printable ASCII", error!, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------- Part numbers ($C1, $C2) --------------------------

    [Theory]
    [InlineData("")]
    [InlineData("24264923")]
    [InlineData("12656942")]
    [InlineData("AB-12345-C")]   // allow service-code style; printable ASCII
    public void ValidatePartNumber_PrintableValues_AreAccepted(string value)
    {
        Assert.Null(EcuFieldValidators.ValidatePartNumber(value));
    }

    [Fact]
    public void ValidatePartNumber_TooLong_IsRejected()
    {
        Assert.NotNull(EcuFieldValidators.ValidatePartNumber(new string('A', 33)));
    }

    // -------------------------- $CC diag addr --------------------------

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("0x12")]
    [InlineData("12")]
    [InlineData("FF")]
    [InlineData("0xFF")]
    [InlineData("0")]
    [InlineData("00")]
    public void ValidateDiagAddrHex_ValidInputs_AreAccepted(string? value)
    {
        Assert.Null(EcuFieldValidators.ValidateDiagAddrHex(value));
    }

    [Theory]
    [InlineData("0x")]
    [InlineData("ZZ")]
    [InlineData("0x123")]   // > 2 hex digits = > 1 byte
    [InlineData("100")]
    [InlineData("12 34")]
    public void ValidateDiagAddrHex_InvalidInputs_AreRejected(string value)
    {
        var error = EcuFieldValidators.ValidateDiagAddrHex(value);
        Assert.NotNull(error);
        Assert.Contains("hex byte", error!, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------- 4-byte BE hex ($C1, $CC) --------------------------

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("0x017240DB")]
    [InlineData("017240DB")]
    [InlineData("0X017240DB")]
    [InlineData("0x00000000")]
    [InlineData("0xFFFFFFFF")]
    [InlineData("deadbeef")]   // lowercase tolerated on input
    public void Validate4ByteHexBE_ValidInputs_AreAccepted(string? value)
    {
        Assert.Null(EcuFieldValidators.Validate4ByteHexBE(value));
    }

    [Theory]
    [InlineData("0xZZZZZZZZ",  "4-byte hex required")]
    [InlineData("0xZZ",        "exactly 8")]   // 2 digits after prefix strip - length error trumps
    [InlineData("017240D",     "exactly 8")]   // 7 digits
    [InlineData("017240DBA",   "exactly 8")]   // 9 digits
    [InlineData("0x017240DBA", "exactly 8")]
    [InlineData("01 72 40 DB", "exactly 8")]   // embedded spaces - length mismatch after prefix strip
    [InlineData("0x1234567G",  "4-byte hex required")]
    public void Validate4ByteHexBE_InvalidInputs_ReportMeaningfulError(string value, string expectedSubstring)
    {
        var error = EcuFieldValidators.Validate4ByteHexBE(value);
        Assert.NotNull(error);
        Assert.Contains(expectedSubstring, error!, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------- BCC (4-char alphanumeric) --------------------------

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("5053")]    // typical numeric BCC
    [InlineData("ABCD")]    // alphabetic
    [InlineData("a1B2")]    // mixed case
    public void ValidateBroadcastCode_ValidInputs_AreAccepted(string? value)
    {
        Assert.Null(EcuFieldValidators.ValidateBroadcastCode(value));
    }

    [Theory]
    [InlineData("505",     "4 characters")]
    [InlineData("50533",   "4 characters")]
    [InlineData("AB-D",    "alphanumeric")]
    [InlineData("50 3",    "alphanumeric")]
    public void ValidateBroadcastCode_InvalidInputs_ReportMeaningfulError(string value, string expectedSubstring)
    {
        var error = EcuFieldValidators.ValidateBroadcastCode(value);
        Assert.NotNull(error);
        Assert.Contains(expectedSubstring, error!, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------- Programming Date (8 digits) --------------------------

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("20220326")]
    [InlineData("00000000")]   // accepted - we don't validate calendar; partial/dev dates allowed
    public void ValidateProgrammingDate_ValidInputs_AreAccepted(string? value)
    {
        Assert.Null(EcuFieldValidators.ValidateProgrammingDate(value));
    }

    [Theory]
    [InlineData("2022032",   "8 digits")]
    [InlineData("202203266", "8 digits")]
    [InlineData("2022-03-26", "8 digits")]   // hyphens make length 10
    [InlineData("2022032A",  "digits")]      // letters not allowed
    public void ValidateProgrammingDate_InvalidInputs_ReportMeaningfulError(string value, string expectedSubstring)
    {
        var error = EcuFieldValidators.ValidateProgrammingDate(value);
        Assert.NotNull(error);
        Assert.Contains(expectedSubstring, error!, StringComparison.OrdinalIgnoreCase);
    }
}
