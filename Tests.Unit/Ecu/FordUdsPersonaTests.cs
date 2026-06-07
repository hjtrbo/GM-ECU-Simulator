using Common.Protocol;
using Core.Bus;
using Core.Ecu.Personas;
using Core.Scheduler;
using Core.Utilities;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Ecu;

// FordUdsPersona covers:
//   - Every USDT request → NRC $7F SID $11 ServiceNotSupported on the wire.
//   - Persona logs to a file (best-effort, validated by checking that the
//     file path is non-null after Dispatch).
//   - Functional broadcasts produce no response (spec).
//   - The persona claims every SID so VirtualBus's fallback ServiceNotSupported
//     never fires; we verify by dispatching a $99 (unknown) and confirming
//     exactly one NRC frame, not two.
//
// Test pattern matches Service22HandlerTests: build a node + channel, call
// Dispatch directly, drain the response queue.
[Collection(FordUdsPersonaCollection.Name)]
public sealed class FordUdsPersonaTests
{
    private static (Core.Ecu.EcuNode node, ChannelSession ch) MakeNodeWithPersona()
    {
        var node = NodeFactory.CreateNode();
        node.Persona = FordUdsPersona.Instance;
        var ch = NodeFactory.CreateChannel();
        return (node, ch);
    }

    public static IEnumerable<object[]> PhysicalCases() => new[]
    {
        new object[] { new byte[] { 0x22, 0xF1, 0x90 } }, // ReadDataByIdentifier VIN
        new object[] { new byte[] { 0x10, 0x01 } },       // DiagSessionControl default
        new object[] { new byte[] { 0x21, 0x01 } },       // Ford Mode 21 ReadDataByLocalId
        new object[] { new byte[] { 0x99 } },             // Wholly unknown SID
        // Note: 0x3E, 0x09, $23, $27, $A0, $A1 deliberately omitted - those have
        // canned / handled responses now, covered by dedicated tests below.
    };

    [Theory]
    [MemberData(nameof(PhysicalCases))]
    public void Physical_AnyRequest_RepliesWithServiceNotSupported(byte[] usdt)
    {
        var (node, ch) = MakeNodeWithPersona();
        var sid = usdt[0];

        bool claimed = node.Persona.Dispatch(
            node, usdt, ch,
            isFunctional: false, sid: sid, nowMs: 0,
            scheduler: new DpidScheduler(new VirtualBus()),
            stack: DiagnosticStack.Uds);

        Assert.True(claimed, "ford-uds must claim every SID so the bus fallback never fires");
        var resp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(new byte[] { 0x7F, sid, 0x11 }, resp);
        TestFrame.AssertEmpty(ch);
    }

    [Fact]
    public void Functional_DoesNotEmitResponse()
    {
        var (node, ch) = MakeNodeWithPersona();
        byte[] usdt = { 0x22, 0xF1, 0x90 };

        bool claimed = node.Persona.Dispatch(
            node, usdt, ch,
            isFunctional: true, sid: 0x22, nowMs: 0,
            scheduler: new DpidScheduler(new VirtualBus()),
            stack: DiagnosticStack.Uds);

        Assert.True(claimed);
        TestFrame.AssertEmpty(ch);
    }

    [Fact]
    public void Mode09Pid02_NoBin_EmitsFirstFrameWithVinFallbackPrefix()
    {
        // 49 02 01 + 17-byte VIN = 20 bytes total. The Fragmenter emits a
        // First Frame (announcing total length 0x014, carrying 6 payload
        // bytes), then waits for FlowControl from the receiver before
        // emitting Consecutive Frames. We only verify the First Frame
        // contents - multi-frame reassembly with the FC handshake is
        // covered exhaustively in IsoTpTxStateMachineTests.
        // No bin loaded -> the VinFallback string is used.
        FordUdsPersona.LoadFlashBin((byte[]?)null);
        var (node, ch) = MakeNodeWithPersona();
        byte[] usdt = { 0x09, 0x02 };

        bool claimed = node.Persona.Dispatch(
            node, usdt, ch,
            isFunctional: false, sid: 0x09, nowMs: 0,
            scheduler: new DpidScheduler(new VirtualBus()),
            stack: DiagnosticStack.Uds);

        Assert.True(claimed);
        Assert.True(ch.RxQueue.TryDequeue(out var msg), "expected first frame");
        var data = msg!.Data;
        // First 4 bytes are CAN ID, then ISO-TP PCI. FF nibble = 1.
        const int IdBytes = 4;
        Assert.Equal(0x10, data[IdBytes] & 0xF0);           // FF PCI nibble
        int totalLen = ((data[IdBytes] & 0x0F) << 8) | data[IdBytes + 1];
        Assert.Equal(20, totalLen);                          // 3-byte header + 17-byte VIN
        Assert.Equal(0x49, data[IdBytes + 2]);               // positive-response SID
        Assert.Equal(0x02, data[IdBytes + 3]);               // PID echo
        Assert.Equal(0x01, data[IdBytes + 4]);               // NODI
        // First Frame carries 6 bytes of payload, of which 3 are the
        // header and 3 are the start of the VIN ("6FP").
        var vinStart = System.Text.Encoding.ASCII.GetString(data, IdBytes + 5, 3);
        Assert.Equal("6FP", vinStart);
    }

    [Fact]
    public void Mode09Pid04_NoBin_EmitsFirstFrameWithCalIdFallbackPrefix()
    {
        // 49 04 01 + 16-byte CalID = 19 bytes total. No bin loaded -> the
        // CalIdFallback string ("HAEE4UY") is used.
        FordUdsPersona.LoadFlashBin((byte[]?)null);
        var (node, ch) = MakeNodeWithPersona();
        byte[] usdt = { 0x09, 0x04 };

        bool claimed = node.Persona.Dispatch(
            node, usdt, ch,
            isFunctional: false, sid: 0x09, nowMs: 0,
            scheduler: new DpidScheduler(new VirtualBus()),
            stack: DiagnosticStack.Uds);

        Assert.True(claimed);
        Assert.True(ch.RxQueue.TryDequeue(out var msg), "expected first frame");
        var data = msg!.Data;
        const int IdBytes = 4;
        Assert.Equal(0x10, data[IdBytes] & 0xF0);
        int totalLen = ((data[IdBytes] & 0x0F) << 8) | data[IdBytes + 1];
        Assert.Equal(19, totalLen);
        Assert.Equal(0x49, data[IdBytes + 2]);
        Assert.Equal(0x04, data[IdBytes + 3]);
        Assert.Equal(0x01, data[IdBytes + 4]);
        // First 3 chars of "HAEE4UY" start at offset 5 in the FF
        var calIdStart = System.Text.Encoding.ASCII.GetString(data, IdBytes + 5, 3);
        Assert.Equal("HAE", calIdStart);
    }

    [Fact]
    public void Mode09Pid02_SourcesVinFromLoadedBin()
    {
        // With a bin loaded, the VIN must come from 0x000100C0 - NOT the
        // fallback - so it agrees with what PCMTec reads via $23 at the same
        // address. Plant a distinct 17-char VIN and assert the First Frame
        // carries its prefix ("WX0"), proving the canned string isn't used.
        var fakeBin = new byte[0x20000];
        System.Text.Encoding.ASCII.GetBytes("WX0TESTVIN1234567", 0, 17, fakeBin, 0x100C0);
        FordUdsPersona.LoadFlashBin(fakeBin);

        var (node, ch) = MakeNodeWithPersona();
        byte[] usdt = { 0x09, 0x02 };

        node.Persona.Dispatch(
            node, usdt, ch,
            isFunctional: false, sid: 0x09, nowMs: 0,
            scheduler: new DpidScheduler(new VirtualBus()),
            stack: DiagnosticStack.Uds);

        Assert.True(ch.RxQueue.TryDequeue(out var msg), "expected first frame");
        var data = msg!.Data;
        const int IdBytes = 4;
        Assert.Equal(20, ((data[IdBytes] & 0x0F) << 8) | data[IdBytes + 1]);
        Assert.Equal(0x49, data[IdBytes + 2]);
        Assert.Equal(0x02, data[IdBytes + 3]);
        var vinStart = System.Text.Encoding.ASCII.GetString(data, IdBytes + 5, 3);
        Assert.Equal("WX0", vinStart);

        FordUdsPersona.LoadFlashBin((byte[]?)null);
    }

    [Fact]
    public void Mode09Pid04_SourcesCalIdFromLoadedBin_TruncatedAtDot()
    {
        // CalID comes from 0x00010046 and is truncated at the first '.' (the
        // bin stores "<strategy>.HEX"). Plant "ZZ9CAL2.HEX" and assert the
        // First Frame carries the strategy prefix ("ZZ9"), not the fallback.
        var fakeBin = new byte[0x20000];
        System.Text.Encoding.ASCII.GetBytes("ZZ9CAL2.HEX", 0, 11, fakeBin, 0x10046);
        FordUdsPersona.LoadFlashBin(fakeBin);

        var (node, ch) = MakeNodeWithPersona();
        byte[] usdt = { 0x09, 0x04 };

        node.Persona.Dispatch(
            node, usdt, ch,
            isFunctional: false, sid: 0x09, nowMs: 0,
            scheduler: new DpidScheduler(new VirtualBus()),
            stack: DiagnosticStack.Uds);

        Assert.True(ch.RxQueue.TryDequeue(out var msg), "expected first frame");
        var data = msg!.Data;
        const int IdBytes = 4;
        // 49 04 01 + 16-byte zero-padded CalID = 19 bytes total.
        Assert.Equal(19, ((data[IdBytes] & 0x0F) << 8) | data[IdBytes + 1]);
        Assert.Equal(0x49, data[IdBytes + 2]);
        Assert.Equal(0x04, data[IdBytes + 3]);
        var calIdStart = System.Text.Encoding.ASCII.GetString(data, IdBytes + 5, 3);
        Assert.Equal("ZZ9", calIdStart);

        FordUdsPersona.LoadFlashBin((byte[]?)null);
    }

    [Fact]
    public void Service23_BinNotLoaded_NrcsConditionsNotCorrect()
    {
        var (node, ch) = MakeNodeWithPersona();
        FordUdsPersona.LoadFlashBin((byte[]?)null);
        byte[] usdt = { 0x23, 0x00, 0x01, 0x00, 0xC0, 0x00, 0x04 };

        node.Persona.Dispatch(
            node, usdt, ch,
            isFunctional: false, sid: 0x23, nowMs: 0,
            scheduler: new DpidScheduler(new VirtualBus()),
            stack: DiagnosticStack.Uds);

        var resp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(new byte[] { 0x7F, 0x23, 0x22 }, resp); // CNCRSE
    }

    [Fact]
    public void Service23_ServesFromLoadedBin()
    {
        // PCMTec's observed request: read 4 bytes at 0x000100C0 - the VIN
        // anchor in the HAEE4UY bin. We mock the bin with a fake 64KB array
        // where 0x100C0 holds "6FPA" (the first 4 chars of the canned VIN).
        var fakeBin = new byte[0x20000];
        System.Text.Encoding.ASCII.GetBytes("6FPA", 0, 4, fakeBin, 0x100C0);
        FordUdsPersona.LoadFlashBin(fakeBin);

        var (node, ch) = MakeNodeWithPersona();
        byte[] usdt = { 0x23, 0x00, 0x01, 0x00, 0xC0, 0x00, 0x04 };

        node.Persona.Dispatch(
            node, usdt, ch,
            isFunctional: false, sid: 0x23, nowMs: 0,
            scheduler: new DpidScheduler(new VirtualBus()),
            stack: DiagnosticStack.Uds);

        // Response is 5 bytes: 0x63 + "6FPA" - fits a single ISO-TP frame.
        var resp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(new byte[] { 0x63, 0x36, 0x46, 0x50, 0x41 }, resp);

        FordUdsPersona.LoadFlashBin((byte[]?)null); // tidy up
    }

    [Fact]
    public void Service23_VerbatimPcmtecBytes_ServesFromBin()
    {
        // Regression: this is the EXACT 7-byte USDT PCMTec sends after Mode
        // 09 VIN/CalID succeed (observed 2026-05-23 15:47). The first
        // iteration of the parser assumed an ALFI byte and required 8 bytes,
        // silently falling through to NRC $11. The bin holds "6FPA" at
        // offset 0x100C0 - the VIN anchor PCMTec is cross-checking.
        var fakeBin = new byte[0x20000];
        System.Text.Encoding.ASCII.GetBytes("6FPA", 0, 4, fakeBin, 0x100C0);
        FordUdsPersona.LoadFlashBin(fakeBin);

        var (node, ch) = MakeNodeWithPersona();
        // VERBATIM from the PCMTec capture, no extra ALFI byte.
        byte[] usdt = { 0x23, 0x00, 0x01, 0x00, 0xC0, 0x00, 0x04 };

        node.Persona.Dispatch(
            node, usdt, ch,
            isFunctional: false, sid: 0x23, nowMs: 0,
            scheduler: new DpidScheduler(new VirtualBus()),
            stack: DiagnosticStack.Uds);

        // 0x63 + "6FPA" - single ISO-TP frame.
        var resp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(new byte[] { 0x63, 0x36, 0x46, 0x50, 0x41 }, resp);

        FordUdsPersona.LoadFlashBin((byte[]?)null);
    }

    [Fact]
    public void Service23_OutOfRange_Nrcs()
    {
        var fakeBin = new byte[16];
        FordUdsPersona.LoadFlashBin(fakeBin);

        var (node, ch) = MakeNodeWithPersona();
        // Request 4 bytes from 0x100C0 against a 16-byte bin -> overflow.
        byte[] usdt = { 0x23, 0x00, 0x01, 0x00, 0xC0, 0x00, 0x04 };
        node.Persona.Dispatch(
            node, usdt, ch,
            isFunctional: false, sid: 0x23, nowMs: 0,
            scheduler: new DpidScheduler(new VirtualBus()),
            stack: DiagnosticStack.Uds);

        var resp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(new byte[] { 0x7F, 0x23, 0x31 }, resp); // ROOR
        FordUdsPersona.LoadFlashBin((byte[]?)null);
    }

    [Fact]
    public void ServiceA1_RepliesWithE1AndIndexOnly_AndCapturesMapping()
    {
        // Verbatim PCMTec request from the 2026-05-23 16:30 capture:
        //   A1 01 8C 00 3F 90 B8
        // Phase 7: reply is JUST {E1, index} per the PCMTec dev blog
        // wire-format spec; sending the full 7-byte verbatim echo
        // (Phase 5 behaviour) triggered PCMTec's downstream NRE.
        FordUdsPersona.ResetDmrSlotMap();
        var (node, ch) = MakeNodeWithPersona();
        byte[] usdt = { 0xA1, 0x01, 0x8C, 0x00, 0x3F, 0x90, 0xB8 };

        node.Persona.Dispatch(
            node, usdt, ch,
            isFunctional: false, sid: 0xA1, nowMs: 0,
            scheduler: new DpidScheduler(new VirtualBus()),
            stack: DiagnosticStack.Uds);

        var resp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(new byte[] { 0xE1, 0x01 }, resp);
        Assert.Equal((uint)0x003F90B8, FordUdsPersona.DmrSlotMap[0x01]);
    }

    [Fact]
    public void ServiceA1_AcceptsAlternativeMagicBytes()
    {
        // Per the PCMTec dev blog the magic byte at offset 2 can be any of
        // {89, 8A, 8B, 8C, 91, 92, 93, 94, 99, 9A, 9B, A1, A2, A9}; each
        // represents a different memory access mode. The handler must not
        // filter on 0x8C.
        FordUdsPersona.ResetDmrSlotMap();
        var (node, ch) = MakeNodeWithPersona();
        var scheduler = new DpidScheduler(new VirtualBus());

        byte[] alt = { 0xA1, 0x02, 0x91, 0x00, 0x3F, 0x9B, 0x70 };
        node.Persona.Dispatch(node, alt, ch, false, 0xA1, 0, scheduler, DiagnosticStack.Uds);
        var resp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(new byte[] { 0xE1, 0x02 }, resp);
        Assert.Equal((uint)0x003F9B70, FordUdsPersona.DmrSlotMap[0x02]);
    }

    [Fact]
    public void ServiceA1_MultipleSlots_AllCaptured()
    {
        FordUdsPersona.ResetDmrSlotMap();
        var (node, ch) = MakeNodeWithPersona();
        var scheduler = new DpidScheduler(new VirtualBus());

        // Verbatim subsequence from the 16:10 PCMTec capture.
        byte[][] requests =
        {
            new byte[] { 0xA1, 0x01, 0x8C, 0x00, 0x3F, 0x90, 0xB8 },
            new byte[] { 0xA1, 0x08, 0x8C, 0x00, 0x3F, 0x86, 0xEC },
            new byte[] { 0xA1, 0x02, 0x8C, 0x00, 0x3F, 0x9B, 0x70 },
            new byte[] { 0xA1, 0x09, 0x8C, 0x00, 0x3F, 0x7B, 0x28 },
        };
        var expectedAcks = new[] { (byte)0x01, (byte)0x08, (byte)0x02, (byte)0x09 };
        foreach (var (r, expected) in requests.Zip(expectedAcks))
        {
            node.Persona.Dispatch(node, r, ch, false, 0xA1, 0, scheduler, DiagnosticStack.Uds);
            var resp = TestFrame.DequeueSingleFrameUsdt(ch);
            Assert.Equal(new byte[] { 0xE1, expected }, resp);
        }

        Assert.Equal((uint)0x003F90B8, FordUdsPersona.DmrSlotMap[0x01]);
        Assert.Equal((uint)0x003F86EC, FordUdsPersona.DmrSlotMap[0x08]);
        Assert.Equal((uint)0x003F9B70, FordUdsPersona.DmrSlotMap[0x02]);
        Assert.Equal((uint)0x003F7B28, FordUdsPersona.DmrSlotMap[0x09]);
    }

    [Fact]
    public void Service3E_NonSuppressedSubFunction_RepliesPositive()
    {
        var (node, ch) = MakeNodeWithPersona();
        byte[] usdt = { 0x3E, 0x02 };

        node.Persona.Dispatch(node, usdt, ch, false, 0x3E, 0,
            new DpidScheduler(new VirtualBus()), DiagnosticStack.Uds);

        var resp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(new byte[] { 0x7E, 0x02 }, resp);
    }

    [Fact]
    public void Service3E_SuppressedSubFunction_Silent()
    {
        var (node, ch) = MakeNodeWithPersona();
        byte[] usdt = { 0x3E, 0x80 };

        node.Persona.Dispatch(node, usdt, ch, false, 0x3E, 0,
            new DpidScheduler(new VirtualBus()), DiagnosticStack.Uds);

        TestFrame.AssertEmpty(ch);
    }

    [Fact]
    public void ServiceA0_EchoesRequestBodyWithPositiveSid()
    {
        // Observed PCMTec request: A0 0A
        var (node, ch) = MakeNodeWithPersona();
        byte[] usdt = { 0xA0, 0x0A };

        node.Persona.Dispatch(
            node, usdt, ch,
            isFunctional: false, sid: 0xA0, nowMs: 0,
            scheduler: new DpidScheduler(new VirtualBus()),
            stack: DiagnosticStack.Uds);

        var resp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(new byte[] { 0xE0, 0x0A }, resp);
    }

    [Fact]
    public void Mode09_UnknownPid_FallsBackToNrc()
    {
        // PID 0x06 (CVN) is not in the whitelist - it must still log + NRC,
        // i.e. the canned-response table is opt-in, not greedy.
        var (node, ch) = MakeNodeWithPersona();
        byte[] usdt = { 0x09, 0x06 };

        node.Persona.Dispatch(
            node, usdt, ch,
            isFunctional: false, sid: 0x09, nowMs: 0,
            scheduler: new DpidScheduler(new VirtualBus()),
            stack: DiagnosticStack.Uds);

        var resp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(new byte[] { 0x7F, 0x09, 0x11 }, resp);
    }

    [Fact]
    public void Service27_RequestSeed_ReturnsNonZeroSeedAndInstallsFallbackModule()
    {
        // The motivating bus log: PCMTec sends $27 and the ford-uds persona
        // used to NRC $11, stalling the flash. Now the persona installs the Ford
        // accept-any-key module (config left SecurityModuleId null here) and
        // answers with a real seed.
        var (node, ch) = MakeNodeWithPersona();
        Assert.Null(node.SecurityModule);

        bool claimed = node.Persona.Dispatch(
            node, new byte[] { 0x27, 0x01 }, ch,
            isFunctional: false, sid: 0x27, nowMs: 0,
            scheduler: new DpidScheduler(new VirtualBus()),
            stack: DiagnosticStack.Uds);

        Assert.True(claimed);
        Assert.NotNull(node.SecurityModule);
        var resp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(0x67, resp[0]);                       // positive SID
        Assert.Equal(0x01, resp[1]);                       // sub echo
        Assert.Equal(5, resp.Length);                      // 0x67 + sub + 3-byte seed (FG PCM default)
        // The 3-byte seed must not be all-zero (zero means "already unlocked").
        bool anyNonZero = false;
        for (int i = 2; i < resp.Length; i++) if (resp[i] != 0) { anyNonZero = true; break; }
        Assert.True(anyNonZero, "issued seed must be non-zero");
    }

    [Fact]
    public void Service27_RequestSeedThenSendKey_UnlocksAcceptingAnyKey()
    {
        var (node, ch) = MakeNodeWithPersona();
        var scheduler = new DpidScheduler(new VirtualBus());

        node.Persona.Dispatch(node, new byte[] { 0x27, 0x01 }, ch, false, 0x27, 0,
            scheduler, DiagnosticStack.Uds);
        var seedResp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(0x67, seedResp[0]);

        // sendKey with an ARBITRARY key - the accept-any module never checks it.
        node.Persona.Dispatch(node, new byte[] { 0x27, 0x02, 0xDE, 0xAD }, ch, false, 0x27, 0,
            scheduler, DiagnosticStack.Uds);
        var keyResp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(new byte[] { 0x67, 0x02 }, keyResp);
        Assert.Equal(1, (int)node.State.SecurityUnlockedLevel);
    }

    [Fact]
    public void Service27_SendKeyWithoutSeed_NrcsConditionsNotCorrect()
    {
        var (node, ch) = MakeNodeWithPersona();
        node.Persona.Dispatch(node, new byte[] { 0x27, 0x02, 0x11, 0x22 }, ch, false, 0x27, 0,
            new DpidScheduler(new VirtualBus()), DiagnosticStack.Uds);

        var resp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(new byte[] { 0x7F, 0x27, 0x22 }, resp); // CNCRSE
        Assert.Equal(0, (int)node.State.SecurityUnlockedLevel);
    }

    [Fact]
    public void Service27_Level2RequestSeed_AlsoSucceeds()
    {
        // The bus log shows PCMTec trying sub $03 (level 2) before falling back
        // to sub $01; the accept-any module must serve any odd subfunction.
        var (node, ch) = MakeNodeWithPersona();
        node.Persona.Dispatch(node, new byte[] { 0x27, 0x03 }, ch, false, 0x27, 0,
            new DpidScheduler(new VirtualBus()), DiagnosticStack.Uds);

        var resp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(0x67, resp[0]);
        Assert.Equal(0x03, resp[1]);
        Assert.Equal(2, (int)node.State.SecurityPendingSeedLevel);
    }

    [Fact]
    public void Service11_EcuReset_AcksAndClearsSecurityState()
    {
        // The flash tool sends 11 01 (hardReset) mid-unlock; we ack 51 01 and
        // reset like a real PCM rather than NRC $11.
        var (node, ch) = MakeNodeWithPersona();
        node.State.SecurityUnlockedLevel = 2;
        node.State.NormalCommunicationDisabled = true;

        bool claimed = node.Persona.Dispatch(
            node, new byte[] { 0x11, 0x01 }, ch,
            isFunctional: false, sid: 0x11, nowMs: 0,
            scheduler: new DpidScheduler(new VirtualBus()),
            stack: DiagnosticStack.Uds);

        Assert.True(claimed);
        var resp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(new byte[] { 0x51, 0x01 }, resp);
        Assert.Equal(0, (int)node.State.SecurityUnlockedLevel);
        Assert.False(node.State.NormalCommunicationDisabled);
    }

    [Fact]
    public void Service11_EcuReset_SuppressedSubFunction_Silent()
    {
        var (node, ch) = MakeNodeWithPersona();
        node.Persona.Dispatch(node, new byte[] { 0x11, 0x81 }, ch, false, 0x11, 0,
            new DpidScheduler(new VirtualBus()), DiagnosticStack.Uds);

        TestFrame.AssertEmpty(ch); // suppress-positive-response bit set
    }

    [Fact]
    public void ServiceB1_DiagnosticCommand_AcksF1()
    {
        // Flash erase `B1 00 B2 AA` -> F1 echoing the command (>= 2 bytes; a bare
        // F1 was rejected by PCMTec's J2534 stack with ERR_INVALID_MSG).
        var (node, ch) = MakeNodeWithPersona();
        node.Persona.Dispatch(node, new byte[] { 0xB1, 0x00, 0xB2, 0xAA }, ch, false, 0xB1, 0,
            new DpidScheduler(new VirtualBus()), DiagnosticStack.Uds);

        Assert.Equal(new byte[] { 0xF1, 0x00, 0xB2, 0xAA }, TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void Service34_RequestDownload_Acks74AndStartsCapture()
    {
        var (node, ch) = MakeNodeWithPersona();
        byte[] req = { 0x34, 0x00, 0x01, 0x00, 0x00, 0x00, 0x0F, 0x00, 0x00 }; // spanishOak
        node.Persona.Dispatch(node, req, ch, false, 0x34, 0,
            new DpidScheduler(new VirtualBus()), DiagnosticStack.Uds);

        Assert.Equal(new byte[] { 0x74, 0x20, 0x04, 0x00 }, TestFrame.DequeueSingleFrameUsdt(ch));
        Assert.True(node.State.DownloadActive);
        Assert.NotNull(node.State.DownloadBuffer);
        Assert.Equal(0u, node.State.DownloadBytesReceived);
    }

    [Fact]
    public void Service36_AfterDownload_Acks76AndBuffersData()
    {
        var (node, ch) = MakeNodeWithPersona();
        var sched = new DpidScheduler(new VirtualBus());
        node.Persona.Dispatch(node, new byte[] { 0x34, 0x00, 0x01, 0x00, 0x00, 0x00, 0x0F, 0x00, 0x00 },
            ch, false, 0x34, 0, sched, DiagnosticStack.Uds);
        TestFrame.DequeueSingleFrameUsdt(ch); // drain 74

        node.Persona.Dispatch(node, new byte[] { 0x36, 0xDE, 0xAD, 0xBE, 0xEF },
            ch, false, 0x36, 0, sched, DiagnosticStack.Uds);

        Assert.Equal(new byte[] { 0x76, 0x00 }, TestFrame.DequeueSingleFrameUsdt(ch));
        Assert.Equal(4u, node.State.DownloadBytesReceived);
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF },
            node.State.DownloadBuffer!.AsSpan(0, 4).ToArray());
    }

    [Fact]
    public void Service36_WithoutDownload_NrcsConditionsNotCorrect()
    {
        var (node, ch) = MakeNodeWithPersona();
        node.State.DownloadActive = false;
        node.Persona.Dispatch(node, new byte[] { 0x36, 0x01, 0x02 }, ch, false, 0x36, 0,
            new DpidScheduler(new VirtualBus()), DiagnosticStack.Uds);

        Assert.Equal(new byte[] { 0x7F, 0x36, 0x22 }, TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void FlashWriteSequence_CapturesImageWithBlock0PreservedAndOverlayAt0x10000()
    {
        // End-to-end: B1 erase -> 34 -> two 36 chunks -> 37. The flushed image must
        // keep the loaded bin's block 0 and overlay the $36 stream at 0x10000.
        var bin = new byte[0x20000];
        bin[0x0000] = 0xB0;       // block-0 markers that must survive
        bin[0xFFFF] = 0xB1;
        FordUdsPersona.LoadFlashBin(bin);
        try
        {
            var (node, ch) = MakeNodeWithPersona();
            var sched = new DpidScheduler(new VirtualBus());

            node.Persona.Dispatch(node, new byte[] { 0xB1, 0x00, 0xB2, 0xAA }, ch, false, 0xB1, 0, sched, DiagnosticStack.Uds);
            Assert.Equal(new byte[] { 0xF1, 0x00, 0xB2, 0xAA }, TestFrame.DequeueSingleFrameUsdt(ch));

            node.Persona.Dispatch(node, new byte[] { 0x34, 0x00, 0x01, 0x00, 0x00, 0x00, 0x0F, 0x00, 0x00 }, ch, false, 0x34, 0, sched, DiagnosticStack.Uds);
            Assert.Equal(new byte[] { 0x74, 0x20, 0x04, 0x00 }, TestFrame.DequeueSingleFrameUsdt(ch));

            node.Persona.Dispatch(node, new byte[] { 0x36, 0x11, 0x22, 0x33, 0x44 }, ch, false, 0x36, 0, sched, DiagnosticStack.Uds);
            Assert.Equal(new byte[] { 0x76, 0x00 }, TestFrame.DequeueSingleFrameUsdt(ch));
            node.Persona.Dispatch(node, new byte[] { 0x36, 0x55, 0x66, 0x77, 0x88 }, ch, false, 0x36, 0, sched, DiagnosticStack.Uds);
            Assert.Equal(new byte[] { 0x76, 0x00 }, TestFrame.DequeueSingleFrameUsdt(ch));

            node.Persona.Dispatch(node, new byte[] { 0x37 }, ch, false, 0x37, 0, sched, DiagnosticStack.Uds);
            Assert.Equal(new byte[] { 0x77, 0x00 }, TestFrame.DequeueSingleFrameUsdt(ch));
            Assert.False(node.State.DownloadActive);

            var path = FordUdsPersona.LastFlashWritePath;
            Assert.False(string.IsNullOrEmpty(path));
            Assert.True(File.Exists(path!));
            var image = File.ReadAllBytes(path!);
            Assert.Equal(0x20000, image.Length);                 // full 128 KiB test image
            Assert.Equal(0xB0, image[0x0000]);                   // block 0 preserved
            Assert.Equal(0xB1, image[0xFFFF]);
            Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88 },
                image.AsSpan(0x10000, 8).ToArray());             // both chunks overlaid at 0x10000
        }
        finally
        {
            FordUdsPersona.LoadFlashBin((byte[]?)null);
        }
    }

    [Fact]
    public void Dispatch_OpensLogFile()
    {
        var (node, ch) = MakeNodeWithPersona();
        FordUdsPersona.EndSession(); // clean slate
        Assert.Null(FordUdsPersona.CurrentLogPath);

        node.Persona.Dispatch(
            node, new byte[] { 0x22, 0x01, 0x00 }, ch,
            isFunctional: false, sid: 0x22, nowMs: 0,
            scheduler: new DpidScheduler(new VirtualBus()),
            stack: DiagnosticStack.Uds);

        var path = FordUdsPersona.CurrentLogPath;
        Assert.False(string.IsNullOrEmpty(path), "lazy-open should have created a log file path");
        Assert.True(File.Exists(path!), $"log file should exist at {path}");
    }
}
