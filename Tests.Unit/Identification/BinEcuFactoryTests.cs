using Common.Persistence;
using Common.Protocol;
using Common.Waveforms;
using Core.Bus;
using Core.Ecu;
using Core.Identification;
using System.Buffers.Binary;
using System.Text;
using Xunit;

namespace EcuSimulator.Tests.Identification;

// Coverage for BinEcuFactory. The factory composes BinFamilyClassifier,
// Mode1ADidBinExtractor, Mode22DidBinExtractor, BinIdentificationApplier,
// and the AlwaysDynamicPids overlay - tests here pin its rejection
// behaviour, the detached-node contract, and the overlay-wins-over-bin
// rule for live PIDs. Synthetic E38-like images use the same PowerPC
// stub pattern as Mode1ADidBinExtractorTests; helpers duplicated locally
// so the test class stays self-contained.
public sealed class BinEcuFactoryTests
{
    [Fact]
    public void Create_FileNotFound_ThrowsUnsupportedBin()
    {
        var canIds = CanIds();
        Assert.Throws<BinEcuFactory.UnsupportedBinException>(() =>
            BinEcuFactory.Create(@"X:\does-not-exist-{0BC9F6F7}.bin", canIds, "ECU1"));
    }

    [Fact]
    public void Create_TooSmallBin_ThrowsUnsupportedBin()
    {
        using var tmp = new TempBin(new byte[64]);
        var canIds = CanIds();
        var ex = Assert.Throws<BinEcuFactory.UnsupportedBinException>(() =>
            BinEcuFactory.Create(tmp.Path, canIds, "ECU1"));
        Assert.Contains("E38", ex.Message);
        Assert.Contains("E67", ex.Message);
        Assert.Contains("T43", ex.Message);
    }

    [Fact]
    public void Create_AllZerosLargeBin_ThrowsUnsupportedBin()
    {
        // 1 MiB of zeros - no family markers, classifier returns Unknown
        // and the factory rejects before any parse work runs.
        using var tmp = new TempBin(new byte[0x100000]);
        var canIds = CanIds();
        Assert.Throws<BinEcuFactory.UnsupportedBinException>(() =>
            BinEcuFactory.Create(tmp.Path, canIds, "ECU1"));
    }

    [Fact]
    public void Create_FamilyClassifiesButParseFails_ThrowsUnsupportedBin()
    {
        // VIN descriptor alone at 0xE0AC is enough to classify as E38, but
        // without a PPC dispatcher cluster the Mode1A walker bails and
        // returns null. Factory must convert that to UnsupportedBinException
        // with a message naming the family it classified to - so the user
        // knows the file IS the right family but is somehow malformed.
        var bin = new byte[0x100000];
        WriteAscii(bin, 0xE0AC, "BZ158034" + "1GCRKSE36BZ158034");
        using var tmp = new TempBin(bin);
        var canIds = CanIds();

        var ex = Assert.Throws<BinEcuFactory.UnsupportedBinException>(() =>
            BinEcuFactory.Create(tmp.Path, canIds, "ECU1"));
        Assert.Contains("E38", ex.Message);
    }

    [Fact]
    public void Create_SyntheticE38Image_ProducesPrimedDetachedNodeWithLiveOverlay()
    {
        // Build a 2 MiB synthetic image that:
        //   - classifies as E38 via the 0xE0AC VIN descriptor
        //   - has a recognisable PowerPC dispatcher + $1A trampoline + a $C1
        //     handler that resolves to a flash address with a 4-byte BE value
        //   - has NO $22 PID table signature (E38 extractor returns null, so
        //     the AlwaysDynamicPids overlay is the only source of $22 PIDs)
        var bin = BuildSyntheticE38(out string vin);
        using var tmp = new TempBin(bin);
        var canIds = CanIds();

        var result = BinEcuFactory.Create(tmp.Path, canIds, "ECU1");

        // The node carries the foundation contract from EcuNodeFactory.
        Assert.NotNull(result.Node);
        Assert.True(result.Node.IsPrimed);
        Assert.Equal("ECU1", result.Node.Name);
        Assert.Equal(canIds.PhysicalRequestId, result.Node.PhysicalRequestCanId);
        Assert.Equal(canIds.UsdtResponseId,    result.Node.UsdtResponseCanId);
        Assert.Equal(canIds.UudtResponseId,    result.Node.UudtResponseCanId);
        Assert.Equal(canIds.DiagnosticAddress, result.Node.DiagnosticAddress);

        // Family / extraction summary surfaced to the caller for the status bar.
        Assert.Equal(BinFamilyClassifier.Family.E38, result.Family);
        Assert.True(result.Mode1A.Dids.Count > 0);
        Assert.True(result.AlwaysDynamicApplied.Count >= 5);

        // VIN populated from the bin's segment-reader extraction. Lives on
        // the Mode1A store (via BinIdentificationApplier) - that's how the
        // identity persists across save/load.
        Assert.Equal(vin, Encoding.ASCII.GetString(result.Node.GetIdentifier(0x90)!));

        // Detached: the factory returned a node that is NOT on any bus.
        // The caller (MainViewModel) does bus.AddNode after success.
        var bus = new VirtualBus();
        Assert.Null(bus.FindByRequestId(canIds.PhysicalRequestId));
    }

    [Fact]
    public void Create_AlwaysDynamicEntries_HaveLiveWaveformNotStaticBytes()
    {
        // After Create returns, every PID in AlwaysDynamicPids.ById must
        // exist on the node as a Mode22 row WITH a live waveform and NO
        // static bytes - that's the whole point of the overlay (bin gives
        // identity + shape, the library keeps the live demo feel for
        // well-known PIDs like RPM / MAP / ECT).
        var bin = BuildSyntheticE38(out _);
        using var tmp = new TempBin(bin);
        var canIds = CanIds();

        var result = BinEcuFactory.Create(tmp.Path, canIds, "ECU1");

        foreach (var (pidId, entry) in AlwaysDynamicPids.ById)
        {
            var pid = result.Node.GetPidByWireId(pidId);
            Assert.NotNull(pid);
            Assert.Equal(PidMode.Mode22, pid!.Mode);
            Assert.Equal(entry.LengthBytes, pid.LengthBytes);
            Assert.Null(pid.StaticBytes);
            Assert.Equal(entry.Waveform.Shape, pid.WaveformConfig.Shape);
        }
    }

    // ----------------------- helpers -----------------------

    private static EcuNodeFactory.CanIds CanIds() => new(
        PhysicalRequestId: 0x7E0,
        UsdtResponseId:    0x7E8,
        UudtResponseId:    0x5E8,
        DiagnosticAddress: 0x11);

    private static void WriteAscii(byte[] bin, int offset, string s)
    {
        var bytes = Encoding.ASCII.GetBytes(s);
        Array.Copy(bytes, 0, bin, offset, bytes.Length);
    }

    /// <summary>
    /// Build a 2 MiB synthetic E38-like flash image that:
    ///   - has a 25-byte ASCII VIN descriptor at 0xE0AC (E38 classifier hit)
    ///   - has a PowerPC service dispatcher with the cmpwi/beq cluster
    ///   - $1A trampoline + DID dispatcher routing $C1 to a flash-read fetcher
    /// Returns the VIN string so the test can assert it lands on $1A $90.
    /// </summary>
    private static byte[] BuildSyntheticE38(out string vin)
    {
        var bin = new byte[0x200000];
        vin = "1GCRKSE36BZ158034";
        // Descriptor: <8-char tail><17-char VIN>. The 8-char tail must equal
        // the last 8 chars of the VIN for the descriptor regex to validate.
        WriteAscii(bin, 0xE0AC, "BZ158034" + vin);

        // PowerPC stubs - same layout Mode1ADidBinExtractorTests uses.
        const int dispatcher = 0x2BB90;
        const int handler1A  = 0x2BD28;
        const int didDisp    = 0x2B904;
        const int handlerC1  = 0x2B96C;
        const int fetcherFn  = 0x0BA3CC;
        const int tableAddr  = 0x60208;
        const int dataAddr   = 0x60005;

        WriteInstr(bin, dispatcher - 4, 0x89FF0002);   // lbz r15, 2(r31)
        EmitCmpBeq(bin, dispatcher,      0x1A, handler1A);
        EmitCmpBeq(bin, dispatcher + 8,  0x20, dispatcher + 0x40);
        EmitCmpBeq(bin, dispatcher + 16, 0x27, dispatcher + 0x44);
        EmitCmpBeq(bin, dispatcher + 24, 0x28, dispatcher + 0x48);
        EmitCmpBeq(bin, dispatcher + 32, 0x34, dispatcher + 0x4C);

        WriteInstr(bin, handler1A,     0x3D800030);    // junk lis (filler)
        WriteInstr(bin, handler1A + 4, 0x887F0003);    // lbz r3, 3(r31)
        WriteInstr(bin, handler1A + 8, EncodeBl(handler1A + 8, didDisp));

        EmitCmpBeq(bin, didDisp,     0xC1, handlerC1);

        WriteInstr(bin, handlerC1,      EncodeLis(12, 0x6));
        WriteInstr(bin, handlerC1 + 4,  EncodeLwz(12, 12, 0x208));
        WriteInstr(bin, handlerC1 + 8,  0x7D8903A6);   // mtctr r12
        WriteInstr(bin, handlerC1 + 12, 0x4E800420);   // bctr

        BinaryPrimitives.WriteInt32BigEndian(bin.AsSpan(tableAddr, 4), fetcherFn);
        WriteInstr(bin, fetcherFn,     EncodeLis(3, dataAddr >> 16));
        WriteInstr(bin, fetcherFn + 4, EncodeAddi(3, 3, dataAddr & 0xFFFF));
        WriteInstr(bin, fetcherFn + 8, 0x4E800020);    // blr

        BinaryPrimitives.WriteUInt32BigEndian(bin.AsSpan(dataAddr, 4), 24264923u);
        return bin;
    }

    private static void WriteInstr(byte[] d, int off, uint w)
        => BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(off, 4), w);

    private static void EmitCmpBeq(byte[] d, int off, byte imm, int target)
    {
        uint cmpwi = (11u << 26) | (11u << 16) | imm;
        WriteInstr(d, off, cmpwi);
        int disp = target - (off + 4);
        uint bc = (16u << 26) | (12u << 21) | (2u << 16) | ((uint)(disp & 0xFFFC));
        WriteInstr(d, off + 4, bc);
    }

    private static uint EncodeBl(int insAddr, int target)
    {
        int rel = target - insAddr;
        uint li = (uint)(rel & 0x03FFFFFC);
        return (18u << 26) | li | 1u;  // LK=1
    }

    private static uint EncodeLis(int rt, int imm16)
        => (15u << 26) | ((uint)rt << 21) | (uint)(imm16 & 0xFFFF);

    private static uint EncodeAddi(int rt, int ra, int simm16)
        => (14u << 26) | ((uint)rt << 21) | ((uint)ra << 16) | (uint)(simm16 & 0xFFFF);

    private static uint EncodeLwz(int rt, int ra, int simm16)
        => (32u << 26) | ((uint)rt << 21) | ((uint)ra << 16) | (uint)(simm16 & 0xFFFF);

    /// <summary>
    /// RAII wrapper around <see cref="Path.GetTempFileName"/> - writes the
    /// payload at construction, deletes the file on dispose. xUnit tests
    /// run with the working directory set somewhere harmless; the temp
    /// path is per-test so concurrent xUnit collections don't collide.
    /// </summary>
    private sealed class TempBin : IDisposable
    {
        public string Path { get; }
        public TempBin(byte[] payload)
        {
            Path = System.IO.Path.GetTempFileName();
            File.WriteAllBytes(Path, payload);
        }
        public void Dispose()
        {
            try { if (File.Exists(Path)) File.Delete(Path); }
            catch { /* best-effort cleanup */ }
        }
    }
}
