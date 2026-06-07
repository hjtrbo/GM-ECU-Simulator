using Common.Protocol;
using Xunit;

namespace EcuSimulator.Tests.Protocol;

// Golden-sample coverage for the bus-log annotator. The trace comes from a
// real T43 transmission flash session captured at the J2534 layer; each
// (canId, payload) pair pairs with the friendly tag the file logger should
// append after the hex.
public sealed class UdsAnnotatorTests
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
    // Raw (no ISO-TP PCI) functional TesterPresent: $101 FE 3E - bare SID
    // straight after the ext-addr byte, no Single-Frame length nibble.
    [InlineData(0x101u, new byte[] { 0xFE, 0x3E },
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
    // ---- Ford PCM (ford-uds persona) ----
    // Mode 09 InfoType $02 (VIN) request
    [InlineData(0x7E0u, new byte[] { 0x02, 0x09, 0x02 },
        "VehicleInfo (Mode09) - InfoType $02 (VIN)")]
    // Mode 09 InfoType $04 (CalID) request
    [InlineData(0x7E0u, new byte[] { 0x02, 0x09, 0x04 },
        "VehicleInfo (Mode09) - InfoType $04 (CalID)")]
    // $23 ReadMemoryByAddress: addr=0x0000EFF0 len=4
    [InlineData(0x7E0u, new byte[] { 0x07, 0x23, 0x00, 0x00, 0xEF, 0xF0, 0x00, 0x04 },
        "ReadMemoryByAddress - addr=$0000EFF0 len=4")]
    // $63 positive: printable ASCII bytes rendered as a quoted string
    [InlineData(0x7E8u, new byte[] { 0x05, 0x63, 0x59, 0x31, 0x4C, 0x53 },
        "ReadMemoryByAddress+ \"Y1LS\"")]
    // $63 positive: non-printable / erased bytes fall back to hex
    [InlineData(0x7E8u, new byte[] { 0x05, 0x63, 0xFF, 0xFF, 0xFF, 0xFF },
        "ReadMemoryByAddress+ FF FF FF FF")]
    // $B1 ReadBlock/flash-erase command (B1 00 B2 AA)
    [InlineData(0x7E0u, new byte[] { 0x04, 0xB1, 0x00, 0xB2, 0xAA },
        "ReadBlock (Ford) 00 B2 AA")]
    // $F1 positive response to the $B1 erase
    [InlineData(0x7E8u, new byte[] { 0x04, 0xF1, 0x00, 0xB2, 0xAA },
        "ReadBlock (Ford)+ 00 B2 AA")]
    public void AnnotatesGoldenTrace(uint canId, byte[] payload, string expected)
    {
        var actual = UdsAnnotator.Annotate(canId, payload);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void NegativeResponse_NamesNrc()
    {
        // $7F $27 $35 = SecurityAccess - InvalidKey
        var tag = UdsAnnotator.Annotate(0x7EA, new byte[] { 0x03, 0x7F, 0x27, 0x35 });
        Assert.Equal("SecurityAccess- InvalidKey", tag);
    }

    [Fact]
    public void EmptyPayload_ReturnsNull()
    {
        Assert.Null(UdsAnnotator.Annotate(0x7E0, ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void UnknownService_ReturnsNull()
    {
        // SF len 1 with SID $99 (not a known service)
        Assert.Null(UdsAnnotator.Annotate(0x7E0, new byte[] { 0x01, 0x99 }));
    }

    [Fact]
    public void FlowControlWait_NamesFs()
    {
        var tag = UdsAnnotator.Annotate(0x7EA, new byte[] { 0x31, 0x00, 0x00 });
        Assert.Equal("ISO-TP FC Wait BS=0 STmin=0", tag);
    }

    [Fact]
    public void Obd2FunctionalRequest_IsTaggedFunctional()
    {
        // $7DF uses NORMAL addressing - no $FE byte. PCI is data[0].
        var tag = UdsAnnotator.Annotate(0x7DF, new byte[] { 0x02, 0x3E, 0x00 });
        Assert.Equal("TesterPresent - functional", tag);
    }

    // The "Hide $3E" toolbar toggle drops frames flagged by IsTesterPresent
    // from the live log. These cover the forms a host actually puts on the
    // wire - the ISO-TP Single Frame and the raw bare-SID functional
    // broadcast ($101 FE 3E in the bus log) - plus the negative cases that
    // must NOT be hidden.
    [Theory]
    // Raw no-PCI functional keepalive ($101 FE 3E) - the reported case.
    [InlineData(0x101u, new byte[] { 0xFE, 0x3E }, true)]
    // Raw no-PCI functional positive response ($101 FE 7E).
    [InlineData(0x101u, new byte[] { 0xFE, 0x7E }, true)]
    // Gateway ext-addr ($FD) raw form.
    [InlineData(0x101u, new byte[] { 0xFD, 0x3E }, true)]
    // ISO-TP Single-Frame functional ($101 FE 01 3E).
    [InlineData(0x101u, new byte[] { 0xFE, 0x01, 0x3E, 0x00, 0x00, 0x00, 0x00, 0x00 }, true)]
    // Physical Single-Frame request/response.
    [InlineData(0x7E0u, new byte[] { 0x01, 0x3E }, true)]
    [InlineData(0x7E8u, new byte[] { 0x01, 0x7E }, true)]
    // Not TesterPresent: $10 02 functional must stay visible.
    [InlineData(0x101u, new byte[] { 0xFE, 0x02, 0x10, 0x02 }, false)]
    // A genuine Flow Control frame ($30..) on a physical ID is not a TP.
    [InlineData(0x7E8u, new byte[] { 0x30, 0x00, 0x00 }, false)]
    // Wrong ext-addr byte on $101 - not a recognised functional frame.
    [InlineData(0x101u, new byte[] { 0x00, 0x3E }, false)]
    public void IsTesterPresent_ClassifiesKeepalives(uint canId, byte[] payload, bool expected)
    {
        Assert.Equal(expected, UdsAnnotator.IsTesterPresent(canId, payload));
    }
}
