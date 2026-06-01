using Common.Protocol;
using Core.Bus;
using Core.Ecu;
using Core.Identification;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Core.Dps;

// Public entry-point of the Prime-From-Archive feature. The prime is split
// into two cheap stages so the wizard can present each on its own page:
//
//   1. ParseArchive(zipPath) -> ArchiveDescriptor
//      Cheap: extracts the zip, reads the utility-file filename, counts
//      cal blocks, parses the OS module's per-module header for the OS PN.
//      No solver. The wizard's archive page calls this.
//
//   2. Prime(zipPath) -> PrimedDataset
//      Re-opens the archive, runs the solver + cal-block pipeline, builds
//      the Phase 3 manifest from the bytecode literals, and assembles the
//      dataset. The wizard's Phase 3 review page uses Prime's output as
//      the editable baseline.
//
//   3. BuildEcuNode(dataset) - turn the dataset into an EcuNode.
//   4. ApplyTo(bus, zipPath) - build the node and register it.
//
// Handler invariants (per Core/Dps/README.md):
//   - No protocol handler is modified. This feature is data-plane only.
//   - When the dataset has no value for a PID, the simulator returns the
//     spec-defined NRC ($31 requestOutOfRange) because the PID is simply
//     not registered. Zero-fill is rejected as not-spec-truthful.
//
// Donor-bin handling is gone (see the donor-free design discussion in the
// session memory). Identifier values now come from three sources, in this
// order: bytecode literal ($53 COMPARE_DATA routine.Data, the value DPS
// will actually verify against) -> wizard-side "Load from bin..." or
// "Auto-populate" buttons mutating the Phase3Manifest -> zero stub. No
// splice, no donor walker, no family detection from a donor.
//
// Security module selection: if the utility-file script's first $27
// instruction carries a non-zero algorithm-id byte (AC1), the prime
// picks gm-e92-5byte with that algoId so the simulator computes the
// algorithmically-correct key. If AC1 is zero (= ones-complement
// default) or the script has no $27 step, the prime falls back to
// gm-bypass-2byte, which emits a random seed and unlocks straight
// through.
public static class ArchivePrimer
{
    // VIN charset is [A-HJ-NPR-Z0-9] (no I/O/Q to avoid confusion with 1/0).
    // Use char-class look-arounds rather than \b because typical DPS archive
    // filenames are shaped "E38_1GCRKSE36BZ158034.zip" - the underscore is a
    // word character so \b would not fire between _ and the VIN.
    private static readonly Regex VinFromFilenameRx =
        new(@"(?<![A-HJ-NPR-Z0-9])([A-HJ-NPR-Z0-9]{17})(?![A-HJ-NPR-Z0-9])",
            RegexOptions.Compiled);

    // ----- Stage 1: cheap parse (wizard page 1) -----

    public static ArchiveDescriptor ParseArchive(string archiveZipPath)
    {
        using var archive = ArchiveExtractor.Extract(archiveZipPath);

        byte[] osCalBytes = Array.Empty<byte>();
        Mode1ADidBinExtractor.ArchiveOsHeader? header = null;
        if (archive.OsCalFilePath is not null)
        {
            osCalBytes = File.ReadAllBytes(archive.OsCalFilePath);
            header = Mode1ADidBinExtractor.ReadArchiveOsHeader(osCalBytes);
        }

        return new ArchiveDescriptor(
            ArchivePath: archiveZipPath,
            UtilityFileName: Path.GetFileName(archive.UtilityFilePath),
            CalibrationBlockCount: archive.CalibrationFilePaths.Count,
            OsPartNumber: header?.OsPartNumber,
            OsAlphaCode: header?.AlphaCode,
            OsCalBytes: osCalBytes);
    }

    // ----- Stage 2: full prime (wizard page 2 baseline) -----

    public static PrimedDataset Prime(string archiveZipPath)
    {
        using var archive = ArchiveExtractor.Extract(archiveZipPath);

        var utility = UtilityFileParser.ParseFile(archive.UtilityFilePath);

        byte[] osCalBytes = Array.Empty<byte>();
        IReadOnlyList<E38PidRecord> pids = Array.Empty<E38PidRecord>();
        Mode1ADidBinExtractor.ArchiveOsHeader? archiveHeader = null;
        if (archive.OsCalFilePath is not null)
        {
            osCalBytes = File.ReadAllBytes(archive.OsCalFilePath);
            archiveHeader = Mode1ADidBinExtractor.ReadArchiveOsHeader(osCalBytes);

            // E38PidExtractor's signature requires ~200 ordered records to
            // anchor. T43 / E67 / non-E38 bins typically don't carry the
            // same table shape; treat InvalidDataException as "this bin
            // doesn't carry a recognisable PID table" rather than failing
            // the whole prime.
            try
            {
                var (recs, _) = E38PidExtractor.Extract(osCalBytes);
                pids = recs;
            }
            catch (InvalidDataException) { /* table not found - leave pids empty */ }
        }

        var vin = ResolveVinFromFilename(archive.ArchivePath);
        var expectedValues = new ExpectedValueTable(utility.Routines);
        var expectedRequests = ExpectedRequestLog.Build(utility.Instructions);
        var flags = BuildFlagList(utility, expectedValues);
        var solverResult = PidResponseSolver.Compute(utility, pids);
        var phase3 = Phase3Extractor.Build(utility, solverResult, pids);

        var calBlocks = BuildCalBlocks(archive.CalibrationFilePaths, utility);

        var (securityModuleId, securityModuleConfig) = PickSecurityModule(utility);

        var report = new PrimeReport(
            ArchivePath: archiveZipPath,
            DonorBinPath: null,                 // donor concept dropped
            UtilityFileName: Path.GetFileName(archive.UtilityFilePath),
            CalFileCount: archive.CalibrationFilePaths.Count,
            Vin: vin,
            VinSource: vin is null ? VinSource.None : VinSource.ArchiveFilename,
            Family: null,                       // donor-free: family unknown
            OsPartNumber: archiveHeader?.OsPartNumber,
            OsAlphaCode: archiveHeader?.AlphaCode,
            SecurityModuleId: securityModuleId,
            SecurityModuleConfig: securityModuleConfig,
            IdentifierDidCount: 0,              // bin walker dropped
            PidsKnownFromBin: pids.Count,
            PidsSatisfiedFromBin: 0,
            PidsSatisfiedFromArchive: solverResult.Responses.Count,
            PidsReturningNrc: Math.Max(0, pids.Count - solverResult.Responses.Count),
            CalBlocks: calBlocks,
            Flags: flags);

        return new PrimedDataset(
            Report: report,
            UtilityFile: utility,
            ExpectedValues: expectedValues,
            ExpectedRequests: expectedRequests,
            BinIdentification: null,            // unused now; kept on the record for back-compat
            KnownPids: pids,
            SolverResult: solverResult,
            OsCalBytes: osCalBytes,
            Phase3: phase3,
            EditedPhase3: null);
    }

    // OBD-II first-ECM CAN-ID triple. EcuNodeFactory owns the canonical
    // constants now - kept here as a local alias so the ApplyTo path below
    // still has a single name to reference when replacing an existing ECU.
    private const ushort DefaultEcmRequestId = EcuNodeFactory.DefaultEcmRequestId;

    public static EcuNode BuildEcuNode(PrimedDataset dataset)
    {
        // Foundation: bare primed node with the OBD-II first-ECM CAN triple
        // and security module installed. Same code path BinEcuFactory uses so
        // both factories produce identically-wired nodes; what differs is the
        // data populated afterwards.
        var node = EcuNodeFactory.CreatePrimed(
            name: "PrimedECU",
            ids: new EcuNodeFactory.CanIds(
                PhysicalRequestId: EcuNodeFactory.DefaultEcmRequestId,
                UsdtResponseId:    EcuNodeFactory.DefaultEcmResponseId,
                UudtResponseId:    EcuNodeFactory.DefaultEcmUudtId,
                DiagnosticAddress: EcuNodeFactory.DefaultEcmDiagAddress),
            securityModuleId: dataset.Report.SecurityModuleId,
            securityConfig:   dataset.Report.SecurityModuleConfig);

        // VIN on DID $90 from the archive filename, if present. Tagged Auto
        // so a later wizard edit (User) takes precedence.
        if (dataset.Report.Vin is { } vin && vin.Length == 17)
            node.SetIdentifier(0x90, Encoding.ASCII.GetBytes(vin), DidSource.Auto);

        // Register every $22 PID the solver computed.
        foreach (var (pid, bytes) in dataset.SolverResult.Responses)
        {
            node.AddPid(new Pid
            {
                Address = pid,
                Name = $"Auto-primed 0x{pid:X4}",
                Size = PidSize.Byte,            // unused when LengthBytes is set
                LengthBytes = bytes.Length,
                StaticBytes = bytes,
                DataType = PidDataType.Unsigned,
                Unit = "",
            });
        }

        // Auto-stub identifiers the utility-file script reads via $1A and
        // writes via $3B. Without a stub, the first $1A would NRC $31. The
        // Phase 3 manifest below overrides these with real values where
        // available.
        var didLengthsFromWrites = ScanWriteDataLengths(dataset.UtilityFile);
        var readDids = ScanReadDids(dataset.UtilityFile);
        foreach (var did in readDids.Concat(didLengthsFromWrites.Keys).Distinct())
        {
            if (node.GetIdentifier(did) is not null) continue;
            int length = didLengthsFromWrites.TryGetValue(did, out var L) ? L : 8;
            node.SetIdentifier(did, new byte[length], DidSource.Auto);
        }

        // Apply the Phase 3 manifest. The wizard mutates EditedPhase3 with
        // user edits (Load-from-bin / Auto-populate / manual) before
        // calling BuildEcuNode; in headless paths EditedPhase3 is null and
        // the initial bytecode-derived Phase3 is applied straight through.
        // Empty rows pass through as zero stubs (won't overwrite anything).
        var manifest = dataset.EditedPhase3 ?? dataset.Phase3;
        foreach (var row in manifest.Rows)
            ApplyPhase3Row(node, row);

        return node;
    }

    // Apply one Phase 3 manifest row to the node. A $1A read becomes a Mode1A Pid row and a $22 read becomes a Mode22
    // Pid row, so BOTH land in the per-mode Pid stores the editor grid, the service handlers (GetMode1APid /
    // GetPidByWireId), and File -> Save persistence read from. The earlier code stored $1A rows only in the raw
    // identifier dictionary, which the wire could still answer from but the editor and persistence never saw - hence
    // primed $1A PIDs were invisible in the $1A section. Empty / zero-length rows are skipped: they would only
    // overwrite a real value (or an auto-stub) with nothing. internal so the dispatch is unit-testable without a
    // full archive fixture (see Tests.Unit/Dps/ArchivePrimerTests).
    internal static void ApplyPhase3Row(EcuNode node, Phase3Row row)
    {
        if (row.Source == Phase3RowSource.Empty) return;
        if (row.ExpectedValue.Length == 0) return;

        if (row.OpCode == 0x1A)
        {
            byte did = (byte)row.DidOrPid;
            node.AddPid(new Pid
            {
                Mode        = PidMode.Mode1A,
                Address     = did,
                Name        = Gmw3110DidNames.NameOf(did) ?? $"DID {did:X2}",
                StaticBytes = row.ExpectedValue,
                LengthBytes = row.ExpectedValue.Length,
                Size        = PidSize.DWord,
                DataType    = PidDataType.Unsigned,
                Unit        = "",
            });
        }
        else if (row.OpCode == 0x22)
        {
            node.AddPid(new Pid
            {
                Mode        = PidMode.Mode22,
                Address     = row.DidOrPid,
                Name        = $"Phase3-primed 0x{row.DidOrPid:X4}",
                Size        = PidSize.Byte,
                LengthBytes = row.ExpectedValue.Length,
                StaticBytes = row.ExpectedValue,
                DataType    = PidDataType.Unsigned,
                Unit        = "",
            });
        }
    }

    // Decide which security module to install for the primed ECU. The
    // utility-file $27 instruction encodes the algorithm-id in Action[1]
    // (per the GM Class 2 Interpreter 1 spec): 0x00 = ones-complement
    // default (no per-family math required), anything else is a real
    // algorithm id. We pick gm-e92-5byte + algoId for the real cases
    // and fall through to gm-bypass-2byte otherwise.
    private static (string ModuleId, JsonElement? Config) PickSecurityModule(UtilityFile uf)
    {
        const string BypassId = "gm-bypass-2byte";
        const string StrictId = "gm-e92-5byte";

        foreach (var instr in uf.Instructions)
        {
            if (instr.OpCode != 0x27) continue;
            if (instr.Action is null || instr.Action.Length < 2) continue;

            byte algoId = instr.Action[1];
            if (algoId == 0) return (BypassId, null);

            var cfg = JsonSerializer.SerializeToElement(new { algoId = $"0x{algoId:X2}" });
            return (StrictId, cfg);
        }

        // No $27 in the script: tester won't request security, so the
        // choice is cosmetic. Keep the bypass for back-compatibility.
        return (BypassId, null);
    }

    private static IEnumerable<byte> ScanReadDids(UtilityFile uf)
    {
        var seen = new HashSet<byte>();
        foreach (var i in uf.Instructions)
            if (i.OpCode == 0x1A && seen.Add(i.Action[0]))
                yield return i.Action[0];
    }

    private static IReadOnlyDictionary<byte, int> ScanWriteDataLengths(UtilityFile uf)
    {
        var lengths = new Dictionary<byte, int>();
        foreach (var i in uf.Instructions)
        {
            if (i.OpCode != 0x3B) continue;
            byte did = i.Action[0];
            byte ac3 = i.Action[3];
            int length = (ac3 & 0xF0) switch
            {
                0x30 => i.Action[2],
                _ => 0,
            };
            if (length > 0 && !lengths.ContainsKey(did))
                lengths[did] = length;
        }
        return lengths;
    }

    public static (EcuNode Node, PrimedDataset Dataset) ApplyTo(VirtualBus bus, string archiveZipPath)
    {
        var dataset = Prime(archiveZipPath);
        return ApplyTo(bus, dataset);
    }

    public static (EcuNode Node, PrimedDataset Dataset) ApplyTo(VirtualBus bus, PrimedDataset dataset)
    {
        var node = BuildEcuNode(dataset);
        var existing = bus.FindByRequestId(DefaultEcmRequestId);
        if (existing is not null) bus.RemoveNode(existing);
        bus.AddNode(node);
        return (node, dataset);
    }

    private static string? ResolveVinFromFilename(string archivePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(archivePath);
        var m = VinFromFilenameRx.Match(fileName);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static IReadOnlyList<CalBlockEntry> BuildCalBlocks(
        IReadOnlyList<string> calFilePaths,
        UtilityFile utility)
    {
        var rawAddresses = new Dictionary<int, uint?>();
        foreach (var inst in utility.Instructions)
        {
            if (inst.OpCode != 0xB0 || inst.Action[0] == 0) continue;
            int calIdx = inst.Action[0] - 1;
            if (rawAddresses.ContainsKey(calIdx)) continue;

            byte ac3 = inst.Action[3];
            if ((ac3 & 0x30) == 0x00)
            {
                int routineIdx = inst.Action[1] - 1;
                rawAddresses[calIdx] = routineIdx >= 0 && routineIdx < utility.Routines.Count
                    ? utility.Routines[routineIdx].Address
                    : null;
            }
            else if ((ac3 & 0x10) == 0x10)
            {
                rawAddresses[calIdx] = utility.Sps.DataAddressInfo;
            }
            else
            {
                rawAddresses[calIdx] = null;
            }
        }

        var distinctResolved = rawAddresses.Values
            .Where(a => a.HasValue)
            .Select(a => a!.Value)
            .Distinct()
            .ToList();
        bool isSequentialBase = distinctResolved.Count == 1;

        var result = new List<CalBlockEntry>(calFilePaths.Count);
        uint cumulativeOffset = 0;
        for (int idx = 0; idx < calFilePaths.Count; idx++)
        {
            string name = Path.GetFileName(calFilePaths[idx]);
            long size = new FileInfo(calFilePaths[idx]).Length;

            uint? addr = rawAddresses.TryGetValue(idx, out var raw) ? raw : null;
            if (isSequentialBase && addr.HasValue)
                addr = addr.Value + cumulativeOffset;

            cumulativeOffset += (uint)size;
            result.Add(new CalBlockEntry(name, size, addr));
        }
        return result;
    }

    private static IReadOnlyList<string> BuildFlagList(UtilityFile uf, ExpectedValueTable values)
    {
        var flags = new List<string>();

        foreach (var i in uf.Instructions)
        {
            if (i.OpCode != 0x53) continue;
            if (i.Action[3] != 1) continue;
            int routineIdx = i.Action[1];
            var blob = values.Get(routineIdx);
            if (blob is null)
            {
                flags.Add($"step {i.Step}: $53 references routine[{routineIdx}] which is not present in the file");
                continue;
            }
            if (blob.Length > 16)
                flags.Add($"step {i.Step}: $53 big-blob compare against routine[{routineIdx}] ({blob.Length} bytes) - likely an expected-fail branch");
        }

        foreach (var i in uf.Instructions)
        {
            if (i.OpCode != 0x53) continue;
            if (i.Action[3] != 0) continue;
            byte vit2Id = i.Action[1];
            if (vit2Id == 0x41) continue;
            flags.Add($"step {i.Step}: $53 references VIT2 record 0x{vit2Id:X2} (DPS-side session state, simulator cannot predict)");
        }

        return flags;
    }
}
