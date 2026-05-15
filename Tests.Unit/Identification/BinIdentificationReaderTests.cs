using System.Buffers.Binary;
using Core.Identification;
using Xunit;

namespace EcuSimulator.Tests.Identification;

// Coverage for BinIdentificationReader.
//
// Synthetic tests build a tiny PowerPC stub that mimics the dispatcher
// pattern seen across T43/E38/E67 (cmpwi/beq chain -> $1A trampoline ->
// DID dispatcher -> indirect call -> fetcher returning a flash address)
// and assert the walker extracts the right SIDs, DIDs, and 4-byte BE
// value. Keeps the parser honest without needing the user's real bins
// committed to the repo.
//
// Integration tests against the user's real bins live in
// RealBinExtractionTests below and are skipped when the files aren't
// present, so CI on a clean checkout still passes.
public sealed class BinIdentificationReaderTests
{
    [Fact]
    public void Parse_TooSmallBin_ReturnsNull()
    {
        var tiny = new byte[0x100];
        Assert.Null(BinIdentificationReader.Parse(tiny));
    }

    [Fact]
    public void Parse_BinWithNoDispatcher_ReturnsNull()
    {
        // 1 MiB of 0xFF erased flash - no PPC code, no dispatcher.
        var blank = new byte[0x100000];
        Array.Fill(blank, (byte)0xFF);
        Assert.Null(BinIdentificationReader.Parse(blank));
    }

    [Fact]
    public void Parse_SyntheticT43LikeImage_ExtractsCorrectC1Value()
    {
        // Build a 2 MiB image that contains:
        //   - A service dispatcher (cmpwi r11, $1A / beq -> $1A handler, plus
        //     enough other SID compares to hit the cluster threshold)
        //   - A $1A trampoline that loads `lbz r3, 3(r31)` and bl's the DID
        //     dispatcher
        //   - A DID dispatcher that cmpwi-compares $C1 / beq -> $C1 handler
        //   - A $C1 handler that loads a function pointer from a flash table
        //     and indirectly calls it
        //   - A fetcher fn that returns flash address 0x60005 via the classic
        //     `lis rD, X ; addi rD, rD, Y ; blr` shape
        //   - 4 bytes at 0x60005 encoding 24264923 as BE uint32
        // The walker should bottom out at exactly that value.
        var bin = new byte[0x200000];

        // Layout of code regions. All offsets are 4-byte aligned (PPC ins).
        const int dispatcher = 0x2BB90;
        const int handler1A  = 0x2BD28;
        const int didDisp    = 0x2B904;
        const int handlerC1  = 0x2B96C;
        const int fetcherFn  = 0x0BA3CC;
        const int tableAddr  = 0x60208;
        const int dataAddr   = 0x60005;
        const uint expectedValue = 24264923u;

        // ----- service dispatcher: lbz r11, 2(r31) ; cmpwi/beq chain -----
        // To satisfy the cluster-of-known-SIDs threshold, plant cmpwi
        // immediates for $1A, $20, $27, $28, $34 in a tight window. The
        // walker's WalkChain ignores any cmpwi whose next-instruction is not
        // a beq, so dud filler bytes between them are harmless.
        WriteInstr(bin, dispatcher - 4, 0x89FF0002);   // lbz r15, 2(r31)
        EmitCmpBeq(bin, dispatcher,      0x1A, handler1A);
        EmitCmpBeq(bin, dispatcher + 8,  0x20, dispatcher + 0x40);
        EmitCmpBeq(bin, dispatcher + 16, 0x27, dispatcher + 0x44);
        EmitCmpBeq(bin, dispatcher + 24, 0x28, dispatcher + 0x48);
        EmitCmpBeq(bin, dispatcher + 32, 0x34, dispatcher + 0x4C);

        // ----- $1A trampoline -----
        // First instruction is a junk flag-write so the lbz isn't the very
        // first thing the walker sees. Then `lbz r3, 3(r31)` and `bl didDisp`.
        WriteInstr(bin, handler1A,     0x3D800030);   // lis r12, 0x30 (filler)
        WriteInstr(bin, handler1A + 4, 0x887F0003);   // lbz r3, 3(r31)
        WriteInstr(bin, handler1A + 8, EncodeBl(handler1A + 8, didDisp));

        // ----- DID dispatcher: cmpwi/beq chain on r3 -----
        EmitCmpBeq(bin, didDisp,      0xB0, didDisp + 0x30);
        EmitCmpBeq(bin, didDisp + 8,  0xC1, handlerC1);
        EmitCmpBeq(bin, didDisp + 16, 0xCB, didDisp + 0x34);
        EmitCmpBeq(bin, didDisp + 24, 0xCC, didDisp + 0x38);

        // ----- $C1 handler: lis r12, 0x6 ; lwz r12, 0x208(r12) ; mtctr ; bctr -----
        WriteInstr(bin, handlerC1,      EncodeLis(12, 0x6));
        WriteInstr(bin, handlerC1 + 4,  EncodeLwz(12, 12, 0x208));
        WriteInstr(bin, handlerC1 + 8,  0x7D8903A6);     // mtctr r12
        WriteInstr(bin, handlerC1 + 12, 0x4E800420);     // bctr

        // ----- function-pointer table -----
        BinaryPrimitives.WriteInt32BigEndian(bin.AsSpan(tableAddr, 4), fetcherFn);

        // ----- fetcher fn: lis r3, X ; addi r3, r3, Y ; blr -----
        WriteInstr(bin, fetcherFn,     EncodeLis(3, dataAddr >> 16));
        WriteInstr(bin, fetcherFn + 4, EncodeAddi(3, 3, dataAddr & 0xFFFF));
        WriteInstr(bin, fetcherFn + 8, 0x4E800020);     // blr

        // ----- $C1 data: 4 bytes BE -----
        BinaryPrimitives.WriteUInt32BigEndian(bin.AsSpan(dataAddr, 4), expectedValue);

        // Act
        var result = BinIdentificationReader.Parse(bin);

        // Assert: walker found everything we planted.
        Assert.NotNull(result);
        Assert.Equal(handler1A, result!.Service1AHandlerOffset);
        Assert.Equal(didDisp,   result.DidDispatcherOffset);
        Assert.Contains((byte)0x1A, result.SupportedSids);
        Assert.Contains((byte)0x20, result.SupportedSids);

        var c1 = result.FindDid(0xC1);
        Assert.NotNull(c1);
        Assert.Equal(BinIdentificationReader.DidSourceKind.FlashUInt32BE, c1!.Kind);
        Assert.Equal(dataAddr, c1.FlashAddress);
        Assert.Equal(expectedValue.ToString(), c1.DecodedValue);
        Assert.Equal(new byte[] { 0x01, 0x72, 0x40, 0xDB }, c1.WireBytes);
    }

    [Fact]
    public void Parse_FetcherWithPrologueReturningRamAddress_TaggedRuntimeComputed()
    {
        // Same shape as the $C1 synthetic test, but the $CB fetcher fn has
        // a function prologue, an inlined helper call (modelled as a `bl`
        // that returns immediately), and the lis/addi pair appears AFTER
        // the helper call. The returned address is in chip RAM (above the
        // bin length), so the walker should tag it RuntimeComputed with
        // the resolved RAM address in the decoded note - matching the
        // shape observed on real $CB/$CC handlers across all three families.
        var bin = new byte[0x200000];

        const int dispatcher = 0x2BB90;
        const int handler1A  = 0x2BD28;
        const int didDisp    = 0x2B904;
        const int handlerCB  = 0x2B9E8;
        const int fetcherFn  = 0x0B9A64;
        const int helperFn   = 0x0B7608;
        const int tableAddr  = 0x6020C;
        const int ramAddr    = 0x302578;  // above 0x200000, intentionally out-of-bin

        // Service dispatcher with a cluster of SIDs.
        WriteInstr(bin, dispatcher - 4, 0x89FF0002);
        EmitCmpBeq(bin, dispatcher,      0x1A, handler1A);
        EmitCmpBeq(bin, dispatcher + 8,  0x20, dispatcher + 0x40);
        EmitCmpBeq(bin, dispatcher + 16, 0x27, dispatcher + 0x44);
        EmitCmpBeq(bin, dispatcher + 24, 0x28, dispatcher + 0x48);
        EmitCmpBeq(bin, dispatcher + 32, 0x34, dispatcher + 0x4C);

        // $1A trampoline.
        WriteInstr(bin, handler1A,     0x3D800030);
        WriteInstr(bin, handler1A + 4, 0x887F0003);     // lbz r3, 3(r31)
        WriteInstr(bin, handler1A + 8, EncodeBl(handler1A + 8, didDisp));

        // DID dispatcher routes $CB to handlerCB.
        EmitCmpBeq(bin, didDisp,      0xB0, didDisp + 0x30);
        EmitCmpBeq(bin, didDisp + 8,  0xCB, handlerCB);

        // $CB handler does the indirect call.
        WriteInstr(bin, handlerCB,      EncodeLis(12, 0x6));
        WriteInstr(bin, handlerCB + 4,  EncodeLwz(12, 12, 0x020C));
        WriteInstr(bin, handlerCB + 8,  0x7D8903A6);
        WriteInstr(bin, handlerCB + 12, 0x4E800420);

        // Flash fn-pointer table entry.
        BinaryPrimitives.WriteInt32BigEndian(bin.AsSpan(tableAddr, 4), fetcherFn);

        // Helper fn: just `blr`. (Modeling the NVM-init helper as a no-op.)
        WriteInstr(bin, helperFn, 0x4E800020);

        // Fetcher fn with a prologue, helper call, then lis/addi return.
        WriteInstr(bin, fetcherFn,      0x9421FFF0);                              // stwu r1, -0x10(r1)
        WriteInstr(bin, fetcherFn + 4,  0x7C0802A6);                              // mflr r0
        WriteInstr(bin, fetcherFn + 8,  0x90010014);                              // stw r0, 0x14(r1)
        WriteInstr(bin, fetcherFn + 12, EncodeLis(12, 0xC));                      // unrelated lis (clobbers r12)
        WriteInstr(bin, fetcherFn + 16, EncodeLwz(3, 12, unchecked((short)0xA288))); // lwz r3, -0x5D78(r12)
        WriteInstr(bin, fetcherFn + 20, EncodeBl(fetcherFn + 20, helperFn));      // bl helperFn
        WriteInstr(bin, fetcherFn + 24, EncodeLis(3, ramAddr >> 16));             // lis r3, 0x30
        WriteInstr(bin, fetcherFn + 28, 0x80010014);                              // lwz r0, 0x14(r1) (epilogue mixed in)
        WriteInstr(bin, fetcherFn + 32, EncodeAddi(3, 3, ramAddr & 0xFFFF));      // addi r3, r3, 0x2578
        WriteInstr(bin, fetcherFn + 36, 0x7C0803A6);                              // mtlr r0
        WriteInstr(bin, fetcherFn + 40, 0x38210010);                              // addi r1, r1, 0x10
        WriteInstr(bin, fetcherFn + 44, 0x4E800020);                              // blr

        var result = BinIdentificationReader.Parse(bin);
        Assert.NotNull(result);
        var cb = result!.FindDid(0xCB);
        Assert.NotNull(cb);
        Assert.Equal(BinIdentificationReader.DidSourceKind.RuntimeComputed, cb!.Kind);
        Assert.Null(cb.FlashAddress);
        Assert.Empty(cb.WireBytes);
        Assert.Contains($"0x{ramAddr:X6}", cb.DecodedValue);
        Assert.Contains($"0x{fetcherFn:X6}", cb.DecodedValue);
    }

    // ---------------------------- PPC encoder helpers ----------------------------

    private static void WriteInstr(byte[] d, int off, uint w)
        => BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(off, 4), w);

    // cmpwi rA, simm @ off, then beq +disp (target = off + 4 + disp)
    private static void EmitCmpBeq(byte[] d, int off, byte imm, int target)
    {
        // cmpwi cr0, r11, imm  (RA=11 used by the dispatchers we observed)
        uint cmpwi = (11u << 26) | (0u << 23) | (0u << 22) | (0u << 21) | (11u << 16) | imm;
        WriteInstr(d, off, cmpwi);
        int disp = target - (off + 4);
        Assert.InRange(disp, -0x8000, 0x7FFF);
        // bc 12, 2, +disp  (BO=12, BI=2 -> beq cr0)
        uint bc = (16u << 26) | (12u << 21) | (2u << 16) | ((uint)(disp & 0xFFFC));
        WriteInstr(d, off + 4, bc);
    }

    // bl target
    private static uint EncodeBl(int insAddr, int target)
    {
        int rel = target - insAddr;
        Assert.InRange(rel, -0x02000000, 0x01FFFFFC);
        uint li = (uint)(rel & 0x03FFFFFC);
        return (18u << 26) | li | 1u;  // LK=1
    }

    private static uint EncodeLis(int rt, int imm16)
        => (15u << 26) | ((uint)rt << 21) | (0u << 16) | ((uint)imm16 & 0xFFFF);

    private static uint EncodeAddi(int rt, int ra, int simm16)
        => (14u << 26) | ((uint)rt << 21) | ((uint)ra << 16) | ((uint)simm16 & 0xFFFF);

    private static uint EncodeLwz(int rt, int ra, int simm16)
        => (32u << 26) | ((uint)rt << 21) | ((uint)ra << 16) | ((uint)simm16 & 0xFFFF);
}

// Integration tests against the user's real bins. Tests return early
// without asserting when the bin file isn't present, so a clean-checkout
// CI run still passes. On the development box where the bins live, the
// tests run end-to-end and verify the parser's output against known-good
// values - any silent skip is intentional, not a missed assertion.
public sealed class RealBinExtractionTests
{
    private const string T43Stock = @"C:\Users\Nathan\OneDrive\Cars\VF2 HSV R8 LSA\GM Programming\PCMHacking\TCM\My T43\Bins\Stock_24264923.bin";
    private const string E67Lsa   = @"C:\Users\Nathan\Downloads\2016 HSV R8 LSA 12656942.bin";

    [Fact]
    public void T43StockBin_DetectsFamilyAndExtractsC1()
    {
        if (!File.Exists(T43Stock)) return;

        var bin = File.ReadAllBytes(T43Stock);
        var r = BinIdentificationReader.Parse(bin);
        Assert.NotNull(r);
        Assert.Equal("T43", r!.Family);
        var c1 = r.FindDid(0xC1);
        Assert.NotNull(c1);
        Assert.Equal(0x060005, c1!.FlashAddress);
        Assert.Equal("24264923", c1.DecodedValue);
        // Bonus: VIN, Bosch trace, base-model PN should also be extracted from
        // the flash-metadata sweep.
        Assert.Equal("6G1FK5EP6GL206970", r.Vin);
        Assert.Equal("DV5053Q103619515", r.SupplierHardwareNumber);
        Assert.Equal("24246947", r.BaseModelPartNumber);

        // $CB/$CC fetchers return RAM cache addresses (0x302578/0x302528 on
        // the T43 family). The data lives in chip RAM, populated at runtime
        // from NVM - there's no fixed flash literal we can extract. The
        // walker correctly surfaces this with RuntimeComputed plus the
        // resolved RAM address in the decoded-value note.
        var cb = r.FindDid(0xCB);
        Assert.NotNull(cb);
        Assert.Equal(BinIdentificationReader.DidSourceKind.RuntimeComputed, cb!.Kind);
        Assert.Null(cb.FlashAddress);
        Assert.Contains("0x302578", cb.DecodedValue);

        var cc = r.FindDid(0xCC);
        Assert.NotNull(cc);
        Assert.Equal(BinIdentificationReader.DidSourceKind.RuntimeComputed, cc!.Kind);
        Assert.Null(cc.FlashAddress);
        Assert.Contains("0x302528", cc.DecodedValue);

        // Stage 2: T43 EEPROM_DATA segment surfaces calibration metadata
        // that's distinct from the $1A wire identity. The segment base is
        // discovered by scanning for the 0xA5A5 marker (Stage 2's
        // SearchMarker feature) since the 0xA5A0 CheckWords don't fire on
        // T43 bins. Values reverse-engineered from the bin in earlier
        // sessions.
        Assert.Equal("24265053", r.CalibrationPartNumber);
        Assert.Equal("5053",     r.BroadcastCode);
        Assert.Equal("20220326", r.ProgrammingDate);
        Assert.Equal("DV5053Q103619515", r.TraceCode);
    }

    [Fact]
    public void E67LsaBin_DetectsFamilyAndExtractsC1()
    {
        if (!File.Exists(E67Lsa)) return;

        var bin = File.ReadAllBytes(E67Lsa);
        var r = BinIdentificationReader.Parse(bin);
        Assert.NotNull(r);
        Assert.Equal("E67", r!.Family);
        var c1 = r.FindDid(0xC1);
        Assert.NotNull(c1);
        Assert.Equal(0x010005, c1!.FlashAddress);
        Assert.Equal("12656942", c1.DecodedValue);
        Assert.Equal("6G1FK5EP6GL206970", r.Vin);

        // E67 LSA cal puts the $CB/$CC RAM cache at 0x3F7948/0x3F7940. The
        // address differs from the 2009 CTSV cal because the calibration
        // layout shifted; the walker resolves it from the bin's own code,
        // not a per-family constant.
        var cb = r.FindDid(0xCB);
        Assert.NotNull(cb);
        Assert.Equal(BinIdentificationReader.DidSourceKind.RuntimeComputed, cb!.Kind);
        Assert.Contains("0x3F7948", cb.DecodedValue);

        var cc = r.FindDid(0xCC);
        Assert.NotNull(cc);
        Assert.Equal(BinIdentificationReader.DidSourceKind.RuntimeComputed, cc!.Kind);
        Assert.Contains("0x3F7940", cc.DecodedValue);
    }
}
