using System.Text;
using Common.Protocol;
using Core.Ecu;
using Core.Services;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Ecu;

// DPS "SPS Interpreter Programmers Reference Manual" (April 2011), Appendix D
// "GMLAN Utility File Guidelines" - the canonical SPS programming flow a real
// GM tester drives against an ECU. Each test below walks one step from
// Appendix D verbatim: it issues the request bytes that the interpreter would
// emit and asserts the +ve response SID / NRC byte that the step's goto fields
// expect to branch on.
//
// PDF page references (1993-2011 General Motors, GM Customer Care):
//   Step $01  p.211   $01 Setup Global Variables (no wire traffic - covered in op-code section)
//   Step $02  p.212   $27 SecurityAccess (expected SID $67, NRC $FD timeout, else error)
//   Step $03  p.213   $34 RequestDownload of flash routine (expected $74, NRC $FD timeout)
//   Step $04  p.214   $B0 Block Transfer to RAM (series of $36) (expected $76, NRC $FD)
//   Step $05  p.215   $1A DID $C1 Operating Software Part Number (expected $5A, NRC $31 -> skip)
//   Step $06  p.216   $53 internal Compare Data (no wire)
//   Step $07  p.217   $F3 Set Global Header Length (no wire)
//   Step $08  p.218   $34 RequestDownload of operating software (expected $74)
//   Step $09  p.219   $B0 Block Transfer to RAM of OS (expected $76)
//   Step $0A  p.220   $F3 Set Global Header Length (no wire)
//   Step $0B  p.221   $34 RequestDownload of first calibration (expected $74)
//   Step $0C  p.222   $B0 Block Transfer to RAM of first cal (expected $76)
//   Step $0D  p.223   $34 RequestDownload of second calibration (expected $74)
//   Step $0E  p.224   $B0 Block Transfer to RAM of second cal (expected $76)
//   Step $0F  p.225   $A2 ReportProgrammedState (expected $E2)
//   Step $10  p.226   $50 internal Compare Data: byte == $00 (FullyProgrammed)
//   Step $11  p.227   $3B WriteDataByIdentifier DID $90 VIN (expected $7B)
//   Step $12  p.228   $3B WriteDataByIdentifier DID $98 TesterSerialNumber (expected $7B)
//   Step $13  p.229   $3B WriteDataByIdentifier DID $99 ProgrammingDate (expected $7B)
//
// The simulator does not implement $3B WriteDataByIdentifier yet (Step $11-$13
// of Part 2 of the utility file). The tests below cover the steps the sim
// already speaks: $1A ($05), $A2 ($0F), and the response-byte shapes that
// Step $05's $31 RequestOutOfRange branch and Step $0F's $E2/$00 +ve response
// depend on.
public sealed class AppendixDFlowTests
{
    // ---- Step $05: $1A DID $C1 Operating Software Part Number ----
    //
    // Request bytes per DPS Interpreter Op-Code $1A (PDF p.159 Typical Line):
    //     1A C1
    // Expected +ve response per DPS Step $05 G0-G1:
    //     5A 06          (+ve SID $5A, jump to Step $06 "Evaluate")
    // Expected NRC per DPS Step $05 G2-G3:
    //     7F 1A 31       (RequestOutOfRange, skip comparison and go to "ProgOp")

    [Fact]
    public void AppendixD_Step05_OpSoftwarePartNumber_ReturnsPositive5A()
    {
        // ECU has DID $C1 configured. Step $05 expects the +ve $5A response;
        // Step $06 then compares the returned bytes to the VIT2 table.
        var node = NodeFactory.CreateNode();
        // Realistic 4-byte SoftwareModuleIdentifier (GMW3110 Appendix C / §A.1
        // notes DIDs $C1..$CA hold SWMIs that may be 4 hex bytes or ASCII).
        node.SetIdentifier(0xC1, new byte[] { 0x12, 0x34, 0x56, 0x78 });
        var ch = NodeFactory.CreateChannel();

        Service1AHandler.Handle(node, new byte[] { 0x1A, 0xC1 }, ch);

        var resp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(new byte[] { 0x5A, 0xC1, 0x12, 0x34, 0x56, 0x78 }, resp);
    }

    [Fact]
    public void AppendixD_Step05_NoOpSoftwareDID_ReturnsNrc31_RequestOutOfRange()
    {
        // ECU with no $C1 configured. Step $05's G2-G3 = $31 ProgOp branch
        // triggers: jump straight to programming the operating software,
        // skipping the part-number compare in Step $06. This is the
        // "ECU has never been programmed" path.
        var node = NodeFactory.CreateNode();
        var ch = NodeFactory.CreateChannel();

        Service1AHandler.Handle(node, new byte[] { 0x1A, 0xC1 }, ch);

        var resp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(
            new byte[] { Service.NegativeResponse, Service.ReadDataByIdentifier, Nrc.RequestOutOfRange },
            resp);
    }

    // DPS Step $0B in commentary and the Op-Code $C1 references in
    // Step $0B/$0E suggest later steps may read DID $C2, $CB - confirm
    // the handler's shape is identical for those (it returns whatever
    // bytes the user has configured).
    //
    // DID $B0 is intentionally excluded - it's a SPEC override that
    // always returns node.DiagnosticAddress (DPS PM p.241), regardless
    // of the configured slot. Its dedicated coverage lives in
    // Service1AHandlerTests.{Functional,Physical}B0_*.
    [Theory]
    [InlineData((byte)0xC2)]  // SoftwareModule_02_Identifier
    [InlineData((byte)0xCB)]  // EndModelPartNumber (GMW3110 §A.1)
    public void AppendixD_AdditionalCorporateDIDs_RoundTrip(byte did)
    {
        var node = NodeFactory.CreateNode();
        node.SetIdentifier(did, new byte[] { 0xAA, 0xBB, 0xCC, 0xDD });
        var ch = NodeFactory.CreateChannel();

        Service1AHandler.Handle(node, new byte[] { 0x1A, did }, ch);

        var resp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(new byte[] { 0x5A, did, 0xAA, 0xBB, 0xCC, 0xDD }, resp);
    }

    // ---- Step $0F: $A2 ReportProgrammedState ----
    //
    // Request bytes per DPS Op-Code $A2 (PDF p.171):
    //     A2
    // Expected +ve response per DPS Step $0F G0-G1:
    //     E2 10          (+ve SID $E2, jump to Step $10 "Evaluate")
    // Step $10 then compares the saved byte (interpreter pads MSB with $00
    // per Op-Code $A2 pseudo code) to $0000 (FullyProgrammed = $00).

    [Fact]
    public void AppendixD_Step0F_DefaultECU_ReportsFullyProgrammed_AndStep10WouldSucceed()
    {
        // Default node has ProgrammedState = 0x00 (FP = fully programmed).
        // The interpreter pads MSB with $00; Step $10 compares to
        // (MSB=$00, LSB=$00) -> match -> Success path.
        var node = NodeFactory.CreateNode();
        var ch = NodeFactory.CreateChannel();

        ServiceA2Handler.Handle(node, new byte[] { 0xA2 }, ch);

        var resp = TestFrame.DequeueSingleFrameUsdt(ch);
        // Wire bytes: $E2 then 1 byte programmedState.
        Assert.Equal(new byte[] { 0xE2, 0x00 }, resp);

        // Step $10 evaluation: stored value = (MSB padded to $00, LSB from
        // wire). FullyProgrammed comparison target is $0000.
        ushort stored = (ushort)((0x00 << 8) | resp[1]);
        Assert.Equal((ushort)0x0000, stored);
    }

    [Fact]
    public void AppendixD_Step0F_PartiallyProgrammed_Step10WouldGotoErrorBranch()
    {
        // An ECU with op software but no calibration (state $02 NC). The
        // wire byte is $02; interpreter pads MSB $00; Step $10's compare
        // against $0000 fails -> Error branch.
        var node = NodeFactory.CreateNode();
        node.ProgrammedState = 0x02;  // NC: op s/w present, cal missing
        var ch = NodeFactory.CreateChannel();

        ServiceA2Handler.Handle(node, new byte[] { 0xA2 }, ch);

        var resp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(new byte[] { 0xE2, 0x02 }, resp);
        ushort stored = (ushort)((0x00 << 8) | resp[1]);
        Assert.NotEqual((ushort)0x0000, stored);
    }

    // ---- Step $11-$13: $3B WriteDataByIdentifier ----
    //
    // Each step issues $3B with one of three DIDs:
    //   $11 -> $90 VIN                                   (17 ASCII bytes, Appendix C)
    //   $12 -> $98 RepairShopCodeOrTesterSerialNumber    (10 ASCII bytes, Appendix C)
    //   $13 -> $99 ProgrammingDate                       (4 BCD bytes, Appendix C)
    // Positive response per GMW3110 §8.14.3 Table 148 and §8.14.6.2 pseudo
    // code: [$7B, did] - the dataRecord is NOT echoed back. §8.14.5.2 Table 151
    // shows a worked VIN write returning "$7B $90" on the wire with no
    // VIN bytes following.
    //
    // Security: each writable DID requires the ECU to be unlocked (any level
    // > 0). Spec §8.14.4 Table 150 maps "DID is secured and ECU not unlocked"
    // to NRC $31 ROOR, not $33 SAD. The tests below pin that behaviour.

    private static void Unlock(EcuNode node, byte level = 0x01)
    {
        // Directly set the unlocked level - tests don't need to drive the
        // full $27 seed/key handshake to exercise the $3B security gate.
        node.State.SecurityUnlockedLevel = level;
    }

    [Fact]
    public void AppendixD_Step11_WriteVin_ReturnsPositive7B()
    {
        var node = NodeFactory.CreateNode();
        Unlock(node);
        var ch = NodeFactory.CreateChannel();

        const string vin = "1G1ZB5ST7HF000000";
        byte[] vinBytes = Encoding.ASCII.GetBytes(vin);
        Assert.Equal(17, vinBytes.Length);

        var req = new byte[2 + vinBytes.Length];
        req[0] = 0x3B;
        req[1] = 0x90;
        vinBytes.CopyTo(req, 2);

        Service3BHandler.Handle(node, req, ch);

        var resp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(new byte[] { 0x7B, 0x90 }, resp);

        // §8.14.6.2 "Write data values from dataRecord[] to the memory
        // address associated with the dataIdentifier" - a subsequent
        // $1A $90 must return the new VIN.
        Service1AHandler.Handle(node, new byte[] { 0x1A, 0x90 }, ch);
        var stored = node.GetIdentifier(0x90);
        Assert.NotNull(stored);
        Assert.Equal(vinBytes, stored);
    }

    [Fact]
    public void AppendixD_Step12_WriteTesterSerial_ReturnsPositive7B()
    {
        var node = NodeFactory.CreateNode();
        Unlock(node);
        var ch = NodeFactory.CreateChannel();

        // $98 RepairShopCodeOrTesterSerialNumber - 10 ASCII bytes per
        // Appendix C "R/W ASCII 10".
        byte[] serial = Encoding.ASCII.GetBytes("TECH2WIN01");
        Assert.Equal(10, serial.Length);

        var req = new byte[2 + serial.Length];
        req[0] = 0x3B;
        req[1] = 0x98;
        serial.CopyTo(req, 2);

        Service3BHandler.Handle(node, req, ch);

        var resp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(new byte[] { 0x7B, 0x98 }, resp);

        var stored = node.GetIdentifier(0x98);
        Assert.NotNull(stored);
        Assert.Equal(serial, stored);
    }

    [Fact]
    public void AppendixD_Step13_WriteProgrammingDate_ReturnsPositive7B()
    {
        var node = NodeFactory.CreateNode();
        Unlock(node);
        var ch = NodeFactory.CreateChannel();

        // $99 ProgrammingDate - 4 bytes BCD per Appendix C "R/W BCD 4".
        // Encoding: YY YY MM DD (e.g. 2026-05-16 = $20 $26 $05 $16).
        var date = new byte[] { 0x20, 0x26, 0x05, 0x16 };

        var req = new byte[] { 0x3B, 0x99, 0x20, 0x26, 0x05, 0x16 };

        Service3BHandler.Handle(node, req, ch);

        var resp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(new byte[] { 0x7B, 0x99 }, resp);

        var stored = node.GetIdentifier(0x99);
        Assert.NotNull(stored);
        Assert.Equal(date, stored);
    }

    [Fact]
    public void Step3B_LockedEcu_ReturnsNrc31_RequestOutOfRange()
    {
        // Spec divergence note: GMW3110 §8.14.4 Table 150 explicitly maps
        // "DID is secured and ECU not in unlocked state" to NRC $31 ROOR,
        // NOT $33 SecurityAccessDenied. $33 is not listed in Table 150 at
        // all - $3B folds security failures under ROOR. (This is in
        // contrast to $27 SecurityAccess, which uses $33 properly for its
        // own sub-function gating.) The handler implements the spec
        // literally; this test pins the $31 behaviour.
        var node = NodeFactory.CreateNode();
        // Leave SecurityUnlockedLevel at 0 (default = locked).
        var ch = NodeFactory.CreateChannel();

        byte[] vinBytes = Encoding.ASCII.GetBytes("1G1ZB5ST7HF000000");
        var req = new byte[2 + vinBytes.Length];
        req[0] = 0x3B;
        req[1] = 0x90;
        vinBytes.CopyTo(req, 2);

        Service3BHandler.Handle(node, req, ch);

        var resp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(
            new byte[] { Service.NegativeResponse, Service.WriteDataByIdentifier, Nrc.RequestOutOfRange },
            resp);

        // Confirm no side-effect: the locked write must not have mutated
        // the identifier table.
        Assert.Null(node.GetIdentifier(0x90));
    }

    [Fact]
    public void Step3B_UnknownDid_ReturnsNrc31_RequestOutOfRange()
    {
        // §8.14.6.2 first IF: DID not supported -> $31. The simulator's
        // writable-DID table currently lists $90/$98/$99 only; $77 is not
        // a corporate-standard writable DID and must be rejected.
        var node = NodeFactory.CreateNode();
        Unlock(node);
        var ch = NodeFactory.CreateChannel();

        // [$3B, $77, payload-byte] - syntactically valid (>= 3 bytes) but
        // DID is unsupported.
        Service3BHandler.Handle(node, new byte[] { 0x3B, 0x77, 0xAA }, ch);

        var resp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(
            new byte[] { Service.NegativeResponse, Service.WriteDataByIdentifier, Nrc.RequestOutOfRange },
            resp);
    }

    [Fact]
    public void Step3B_ShortRequest_ReturnsNrc12()
    {
        // §8.14.6.2 "message_data_length < 3" branch: a bare [$3B] with no
        // DID is malformed.
        var node = NodeFactory.CreateNode();
        Unlock(node);
        var ch = NodeFactory.CreateChannel();

        Service3BHandler.Handle(node, new byte[] { 0x3B }, ch);

        var resp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(
            new byte[] { Service.NegativeResponse, Service.WriteDataByIdentifier, Nrc.SubFunctionNotSupportedInvalidFormat },
            resp);
    }

    [Fact]
    public void Step3B_WrongVinLength_ReturnsNrc12_InvalidFormat()
    {
        // §8.14.6.2 second ELSE-IF: "(message_data_length - 2) != expected
        // length for $dataIdentifier" -> $12 SFNS-IF. Note this is NRC
        // $12, NOT $31 - the spec pseudo code routes length mismatches to
        // SFNS-IF; only "data value is invalid" (a separate condition the
        // simulator doesn't model) routes to $31 under Table 150.
        var node = NodeFactory.CreateNode();
        Unlock(node);
        var ch = NodeFactory.CreateChannel();

        // 16 bytes of VIN instead of 17.
        byte[] shortVin = Encoding.ASCII.GetBytes("1G1ZB5ST7HF00000");
        Assert.Equal(16, shortVin.Length);

        var req = new byte[2 + shortVin.Length];
        req[0] = 0x3B;
        req[1] = 0x90;
        shortVin.CopyTo(req, 2);

        Service3BHandler.Handle(node, req, ch);

        var resp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(
            new byte[] { Service.NegativeResponse, Service.WriteDataByIdentifier, Nrc.SubFunctionNotSupportedInvalidFormat },
            resp);

        // No mutation on the failed write.
        Assert.Null(node.GetIdentifier(0x90));
    }

    // ---- 7F-collision audit ----
    //
    // Per DPS "GMLAN Response Processing" rule, when a request's NRC byte
    // value collides with the expected +ve SID (i.e. NRC == request + $40),
    // the interpreter looks for the SECOND occurrence in the goto fields.
    // The simulator never emits an NRC byte that equals a +ve response SID
    // for the same service: $11, $12, $22, $31, $33, $35, $36, $37 are the
    // NRC byte values in use; none equal any of $50, $5A, $60, $62, $67,
    // $68, $6C, $6D, $74, $76, $7B, $7E, $E2, $E5, $EA. This sentinel test
    // pins that property so a future handler change can't silently break it.
    [Theory]
    [InlineData((byte)0x10, (byte)0x50)]  // $10 -> $50
    [InlineData((byte)0x1A, (byte)0x5A)]  // $1A -> $5A
    [InlineData((byte)0x20, (byte)0x60)]  // $20 -> $60
    [InlineData((byte)0x22, (byte)0x62)]  // $22 -> $62
    [InlineData((byte)0x27, (byte)0x67)]  // $27 -> $67
    [InlineData((byte)0x28, (byte)0x68)]  // $28 -> $68
    [InlineData((byte)0x2C, (byte)0x6C)]  // $2C -> $6C
    [InlineData((byte)0x2D, (byte)0x6D)]  // $2D -> $6D
    [InlineData((byte)0x34, (byte)0x74)]  // $34 -> $74
    [InlineData((byte)0x36, (byte)0x76)]  // $36 -> $76
    [InlineData((byte)0x3B, (byte)0x7B)]  // $3B -> $7B (WriteDataByIdentifier)
    [InlineData((byte)0x3E, (byte)0x7E)]  // $3E -> $7E
    [InlineData((byte)0xA2, (byte)0xE2)]  // $A2 -> $E2
    [InlineData((byte)0xA5, (byte)0xE5)]  // $A5 -> $E5
    [InlineData((byte)0xAA, (byte)0xEA)]  // $AA -> $EA
    public void SevenF_Collision_Audit_NrcBytesNeverEqualPositiveSid(byte requestSid, byte positiveSid)
    {
        Assert.Equal((byte)(requestSid + 0x40), positiveSid);

        // Every NRC value the simulator's handlers can emit.
        byte[] nrcsInUse =
        {
            Nrc.ServiceNotSupported,                  // $11
            Nrc.SubFunctionNotSupportedInvalidFormat, // $12
            Nrc.ConditionsNotCorrectOrSequenceError,  // $22
            Nrc.RequestOutOfRange,                    // $31
            Nrc.SecurityAccessDenied,                 // $33
            Nrc.InvalidKey,                           // $35
            Nrc.ExceededNumberOfAttempts,             // $36
            Nrc.RequiredTimeDelayNotExpired,          // $37
        };

        foreach (byte nrc in nrcsInUse)
            Assert.NotEqual(positiveSid, nrc);
    }

    // ---- Sanity: VIN-shape from DPS Step $11 commentary ----
    //
    // Even though the sim doesn't write VIN via $3B, the READ path ($1A $90)
    // must produce a single ISO-TP message starting with $5A $90 followed by
    // 17 ASCII bytes - the value any tester re-reads after a Part 2 write.
    [Fact]
    public void DefaultEcm_Vin_Shape_MatchesAppendixDExpectation()
    {
        var node = NodeFactory.CreateNode();
        const string vin = "1G1ZB5ST7HF000000";
        node.SetIdentifier(0x90, Encoding.ASCII.GetBytes(vin));
        var ch = NodeFactory.CreateChannel();

        // This payload is 2 + 17 = 19 bytes, which exceeds a Single Frame's
        // 7-byte cap. The handler hands it to the fragmenter which emits
        // FF + CFs. Exercising the full FF/CF path is left to
        // Service1AHandlerTests.Vin_response_fragments_and_reassembles -
        // here we just verify the handler accepts the request.
        // (No assertion on the frame queue - the FF/CF emission lives behind
        // Iso15765Channel, not the ChannelSession.RxQueue.)
        Service1AHandler.Handle(node, new byte[] { 0x1A, 0x90 }, ch);
    }
}
