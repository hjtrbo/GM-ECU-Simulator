using Common.Protocol;
using Xunit;

namespace EcuSimulator.Tests.Protocol;

// Golden-sample coverage for the bus-log annotator. The trace comes from a
// real T43 transmission flash session captured at the J2534 layer; each
// (canId, payload) pair pairs with the friendly tag the file logger should
// append after the hex.
public sealed class Gmw3110AnnotatorTests
{
    [Theory]
    // Functional broadcast: $10 02 Programming
    [InlineData(0x101u, new byte[] { 0xFE, 0x02, 0x10, 0x02, 0x00, 0x00, 0x00, 0x00 },
        "StartDiagnosticSession (Programming) - functional")]
    // Physical: $10 02 Programming
    [InlineData(0x7E2u, new byte[] { 0x02, 0x10, 0x02 },
        "StartDiagnosticSession (Programming)")]
    // Positive response to $10 02
    [InlineData(0x7EAu, new byte[] { 0x02, 0x50, 0x02 },
        "StartDiagnosticSession+ (Programming)")]
    // $27 01 RequestSeed
    [InlineData(0x7E2u, new byte[] { 0x02, 0x27, 0x01 },
        "SecurityAccess - RequestSeed")]
    // $67 01 Seed (positive response)
    [InlineData(0x7EAu, new byte[] { 0x04, 0x67, 0x01, 0x00, 0x00 },
        "SecurityAccess+ - Seed")]
    // $27 02 SendKey
    [InlineData(0x7E2u, new byte[] { 0x04, 0x27, 0x02, 0x00, 0x00 },
        "SecurityAccess - SendKey")]
    // $67 02 KeyAccepted
    [InlineData(0x7EAu, new byte[] { 0x02, 0x67, 0x02 },
        "SecurityAccess+ - KeyAccepted")]
    // $28 DisableNormalCommunication
    [InlineData(0x7E2u, new byte[] { 0x01, 0x28 },
        "DisableNormalCommunication")]
    // $68 positive response
    [InlineData(0x7EAu, new byte[] { 0x01, 0x68 },
        "DisableNormalCommunication+")]
    // Functional $28 broadcast
    [InlineData(0x101u, new byte[] { 0xFE, 0x01, 0x28, 0x00, 0x00, 0x00, 0x00, 0x00 },
        "DisableNormalCommunication - functional")]
    // $A2 ReportProgrammedState
    [InlineData(0x7E2u, new byte[] { 0x01, 0xA2 },
        "ReportProgrammedState")]
    // $E2 00 FullyProgrammed response
    [InlineData(0x7EAu, new byte[] { 0x02, 0xE2, 0x00 },
        "ReportProgrammedState+ FullyProgrammed")]
    // $A5 01 Request
    [InlineData(0x7E2u, new byte[] { 0x02, 0xA5, 0x01 },
        "ProgrammingMode - Request")]
    // $E5 positive response (no sub echo when 1-byte response)
    [InlineData(0x7EAu, new byte[] { 0x01, 0xE5 },
        "ProgrammingMode+")]
    // $A5 03 Enable
    [InlineData(0x7E2u, new byte[] { 0x02, 0xA5, 0x03 },
        "ProgrammingMode - Enable")]
    // Functional $3E TesterPresent
    [InlineData(0x101u, new byte[] { 0xFE, 0x01, 0x3E, 0x00, 0x00, 0x00, 0x00, 0x00 },
        "TesterPresent - functional")]
    // $34 RequestDownload, size=0x000C20 = 3104
    [InlineData(0x7E2u, new byte[] { 0x05, 0x34, 0x00, 0x00, 0x0C, 0x20, 0x00, 0x00 },
        "RequestDownload size=3104")]
    // $74 positive
    [InlineData(0x7EAu, new byte[] { 0x01, 0x74 },
        "RequestDownload+")]
    // ISO-TP FF (0xC26 = 3110B) carrying $36 Download @ 0x003FAFE0
    [InlineData(0x7E2u, new byte[] { 0x1C, 0x26, 0x36, 0x00, 0x00, 0x3F, 0xAF, 0xE0 },
        "ISO-TP FF (3110B) - TransferData Download @ 003FAFE0")]
    // ISO-TP FC CTS, BS=1, STmin=0
    [InlineData(0x7EAu, new byte[] { 0x30, 0x01, 0x00 },
        "ISO-TP FC CTS BS=1 STmin=0")]
    // ISO-TP CF #1
    [InlineData(0x7E2u, new byte[] { 0x21, 0x00, 0x3F, 0xB0, 0x00, 0x00, 0x3F, 0xB0 },
        "ISO-TP CF #1")]
    public void AnnotatesGoldenTrace(uint canId, byte[] payload, string expected)
    {
        var actual = Gmw3110Annotator.Annotate(canId, payload);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void NegativeResponse_NamesNrc()
    {
        // $7F $27 $35 = SecurityAccess - InvalidKey
        var tag = Gmw3110Annotator.Annotate(0x7EA, new byte[] { 0x03, 0x7F, 0x27, 0x35 });
        Assert.Equal("SecurityAccess- InvalidKey", tag);
    }

    [Fact]
    public void EmptyPayload_ReturnsNull()
    {
        Assert.Null(Gmw3110Annotator.Annotate(0x7E0, ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void UnknownService_ReturnsNull()
    {
        // SF len 1 with SID $99 (not a known service)
        Assert.Null(Gmw3110Annotator.Annotate(0x7E0, new byte[] { 0x01, 0x99 }));
    }

    [Fact]
    public void FlowControlWait_NamesFs()
    {
        var tag = Gmw3110Annotator.Annotate(0x7EA, new byte[] { 0x31, 0x00, 0x00 });
        Assert.Equal("ISO-TP FC Wait BS=0 STmin=0", tag);
    }

    [Fact]
    public void Obd2FunctionalRequest_IsTaggedFunctional()
    {
        // $7DF uses NORMAL addressing - no $FE byte. PCI is data[0].
        var tag = Gmw3110Annotator.Annotate(0x7DF, new byte[] { 0x02, 0x3E, 0x00 });
        Assert.Equal("TesterPresent - functional", tag);
    }
}
