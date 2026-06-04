using Common.Persistence;
using Common.Pids;
using Common.Protocol;
using Core.Ecu;

namespace Core.Identification;

// Primary "Add ECU" entry point. Hand it a path to a full GM flash readback
// (E38 / E67 / T43) and it returns a detached EcuNode wired with the bin's
// $1A identifiers, the family's $22 PID table (E38 only today), and the
// AlwaysDynamicPids overlay so RPM / MAP / ECT / etc. stay live signals
// rather than static zeros pulled from the bin table.
//
// Detached on purpose: the caller (MainViewModel) decides when to add the
// node to the bus. A parse failure mid-extraction throws inside Create,
// and because we never added a partial node to the bus the caller is left
// with nothing to clean up.
//
// Architectural peer to ArchivePrimer.BuildEcuNode - both call
// EcuNodeFactory.CreatePrimed for their foundation, then populate from
// different data sources (utility-file solver vs flash readback).
public static class BinEcuFactory
{
    /// <summary>
    /// Thrown when the bin file can't be classified as E38/E67/T43, or when
    /// family detection succeeded but the PowerPC dispatcher walk did not -
    /// the bin is probably truncated, mis-aligned, or one of the rare
    /// outlier images that needs hand-tuning before extraction works. The
    /// caller (MainViewModel) catches this and shows a themed dialog naming
    /// the supported families; no partial ECU is created.
    /// </summary>
    public sealed class UnsupportedBinException : Exception
    {
        public UnsupportedBinException(string message) : base(message) { }
        public UnsupportedBinException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// Per-call summary - what came back from each extractor stage, so the
    /// status bar can report "added ECU1 from bin (E38, N DIDs, M $22 PIDs,
    /// 10 live)" without the caller running its own diff.
    /// </summary>
    public sealed record CreateResult(
        EcuNode Node,
        BinFamilyClassifier.Family Family,
        Mode1ADidBinExtractor.BinIdentification Mode1A,
        Mode22DidBinExtractor.Mode22Scan? Mode22,
        IReadOnlyList<ushort> AlwaysDynamicApplied,
        IReadOnlyList<string> Warnings);

    /// <summary>
    /// Build a primed <see cref="EcuNode"/> from a full GM flash readback.
    /// The returned node is detached from any bus - the caller adds it.
    /// </summary>
    /// <param name="binPath">Path to a .bin readback of an E38 / E67 / T43
    /// ECM/TCM. Archive OS modules unpacked from a DPS .zip are NOT
    /// supported here - they omit the boot region where the $1A dispatcher
    /// lives. Use <see cref="Core.Dps.ArchivePrimer"/> for archive flows.</param>
    /// <param name="canIds">CAN-ID + diagnostic-address tuple for the new ECU,
    /// typically from <see cref="EcuNodeFactory.NextObd2EcmTripleFor"/>.</param>
    /// <param name="name">Display name shown in the ECU list.</param>
    public static CreateResult Create(string binPath, EcuNodeFactory.CanIds canIds, string name)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(binPath); }
        catch (Exception ex)
        {
            throw new UnsupportedBinException(
                $"Could not read bin file '{binPath}': {ex.Message}", ex);
        }

        // Step 1: family classification. Bail early on Unknown - the
        // dispatcher walker would also fail, but a typed exception with
        // the right family list is more useful than a generic parse error.
        var family = BinFamilyClassifier.Classify(bytes);
        if (family == BinFamilyClassifier.Family.Unknown)
        {
            throw new UnsupportedBinException(
                "This bin file is not recognised as a GM E38, E67, or T43 readback. " +
                "Bin-driven ECU creation requires a full flash readback from one of " +
                "these families; DPS archive OS modules and other ECUs are not supported.");
        }

        // Step 2: foundation - bare primed node, detached from bus. Security
        // module is hardcoded gm-bypass-2byte for v1 (any tester running $27
        // SecurityAccess gets the bypass cipher). Extracting the real seed/
        // key algorithm from the bin is a follow-up.
        var node = EcuNodeFactory.CreatePrimed(
            name: name,
            ids: canIds,
            securityModuleId: "gm-bypass-2byte",
            securityConfig: null);

        // Step 3: $1A dispatcher walk + segment-derived identity. The walk
        // succeeds when family != Unknown for well-formed bins; a failure
        // here means the bin classified but is truncated / corrupted.
        var mode1A = Mode1ADidBinExtractor.Parse(bytes);
        if (mode1A is null)
        {
            throw new UnsupportedBinException(
                $"Bin classified as {BinFamilyClassifier.Name(family)} but the $1A " +
                "dispatcher walk failed - the file may be truncated, mis-aligned, " +
                "or an outlier of the family that needs hand-tuning. No ECU was created.");
        }

        // Step 4: apply identity to the node. Reuses the existing applier
        // (LoadMode.ReplaceAll matches the "fresh ECU, bin is the truth"
        // semantics of creation-time use).
        BinIdentificationApplier.Apply(node, mode1A, BinIdentificationApplier.LoadMode.ReplaceAll);

        // Step 5: $22 PID table extraction. E38-only today; the extractor's
        // signature scan returns null silently on other families and that's
        // not an error - the AlwaysDynamicPids overlay still runs.
        Mode22DidBinExtractor.Mode22Scan? mode22 = null;
        if (family == BinFamilyClassifier.Family.E38)
        {
            mode22 = Mode22DidBinExtractor.Parse(bytes);
            if (mode22 is not null)
            {
                var catalogue = Mode22Catalogue();
                foreach (var pid in mode22.Pids)
                    node.AddPid(BuildBinPid(pid, catalogue));
            }
        }

        // Step 6: AlwaysDynamicPids overlay - MUST run after step 5. AddPid
        // upserts by (Mode, Address), so a library entry at PID 0x000C
        // replaces any static row planted in step 5 with the live waveform.
        // That's the whole point of the library: bin gives identity +
        // shape, but RPM/MAP/ECT/etc. keep their live demo feel.
        var applied = new List<ushort>(AlwaysDynamicPids.ById.Count);
        foreach (var (pid, entry) in AlwaysDynamicPids.ById)
        {
            node.AddPid(BuildDynamicPid(entry));
            applied.Add(pid);
        }

        var warnings = new List<string>(mode1A.Warnings);
        if (family == BinFamilyClassifier.Family.E38 && mode22 is null)
            warnings.Add("E38 bin classified but the $22 PID table signature was not found - " +
                         "static PID set will be limited to the AlwaysDynamicPids library.");
        if (family != BinFamilyClassifier.Family.E38)
            warnings.Add($"$22 PID table extraction is E38-only today; {BinFamilyClassifier.Name(family)} " +
                         "bins receive identity DIDs plus the AlwaysDynamicPids library only.");

        return new CreateResult(
            Node: node,
            Family: family,
            Mode1A: mode1A,
            Mode22: mode22,
            AlwaysDynamicApplied: applied,
            Warnings: warnings);
    }

    /// <summary>
    /// Build the Pid that represents one bin-extracted $22 record. WireBytes
    /// is typically empty today (the data-pointer high-half lives in a
    /// per-record `lis r12, hi` that needs disassembly to resolve), so we
    /// fall back to a zero-filled payload of the correct length. The bin's
    /// LengthBytes is the wire size a real ECU would emit - getting that
    /// right matters even when the data is zero, otherwise the tester sees
    /// the wrong number of bytes. The flash $22 table holds no name strings,
    /// so the display name is borrowed from the A2L catalogue via
    /// <see cref="ResolveName"/>; PIDs the catalogue doesn't list keep the
    /// "Bin-extracted 0xNNNN" placeholder.
    /// </summary>
    private static Pid BuildBinPid(
        Mode22DidBinExtractor.PidExtraction pid,
        IReadOnlyDictionary<ushort, PidLibraryEntry> catalogue)
    {
        var staticBytes = pid.WireBytes.Length == pid.LengthBytes
            ? pid.WireBytes
            : new byte[pid.LengthBytes];
        return new Pid
        {
            Mode        = PidMode.Mode22,
            Address     = pid.Pid,
            Name        = ResolveName(pid.Pid, catalogue),
            Size        = PidSize.Byte,           // unused when LengthBytes is set
            LengthBytes = pid.LengthBytes,
            StaticBytes = staticBytes,
            DataType    = PidDataType.Unsigned,
            Unit        = "",
        };
    }

    /// <summary>
    /// The A2L-derived $22 catalogue (<see cref="PidLibrary.Mode22"/>), keyed
    /// by 2-byte PID. Loaded once per Create and threaded into BuildBinPid so
    /// every bin record can borrow the programmer-facing name the flash table
    /// itself doesn't carry. Guarded: the catalogue is an embedded encrypted
    /// resource, and a decrypt/parse fault must degrade to synthetic names
    /// rather than abort the whole bin import - so a load failure yields the
    /// empty map and the "Bin-extracted 0xNNNN" fallback stands.
    /// </summary>
    private static IReadOnlyDictionary<ushort, PidLibraryEntry> Mode22Catalogue()
    {
        try { return PidLibrary.Mode22; }
        catch { return EmptyCatalogue; }
    }

    private static readonly IReadOnlyDictionary<ushort, PidLibraryEntry> EmptyCatalogue
        = new Dictionary<ushort, PidLibraryEntry>();

    /// <summary>
    /// Borrow the display name for a bin-extracted $22 PID from the A2L
    /// catalogue: the curated <see cref="PidLibraryEntry.FriendlyName"/>
    /// ("Engine Coolant Temperature") when present, else the verbatim A2L
    /// symbol <see cref="PidLibraryEntry.A2lName"/> ("SfECTI_T_EngCoolCvrtd"),
    /// else the "Bin-extracted 0xNNNN" placeholder for PIDs the catalogue does
    /// not list. The flash $22 table carries no name strings, so this is the
    /// only place a bin row acquires one.
    /// </summary>
    private static string ResolveName(
        ushort pid,
        IReadOnlyDictionary<ushort, PidLibraryEntry> catalogue)
    {
        if (catalogue.TryGetValue(pid, out var entry))
        {
            if (!string.IsNullOrWhiteSpace(entry.FriendlyName)) return entry.FriendlyName;
            if (!string.IsNullOrWhiteSpace(entry.A2lName)) return entry.A2lName;
        }
        return $"Bin-extracted 0x{pid:X4}";
    }

    /// <summary>
    /// Build the Pid for one AlwaysDynamicPids entry. StaticBytes stays null
    /// so the $22 handler routes through the waveform pipeline - WaveformConfig
    /// drives the engineering-unit sample and Scalar/Offset/DataType encode
    /// to raw counts on the wire (OBD-II conventions baked into the library).
    /// </summary>
    private static Pid BuildDynamicPid(AlwaysDynamicPids.Entry entry)
    {
        return new Pid
        {
            Mode           = PidMode.Mode22,
            Address        = entry.Pid,
            Name           = entry.Name,
            Size           = PidSize.Byte,
            LengthBytes    = entry.LengthBytes,
            StaticBytes    = null,
            DataType       = entry.DataType,
            Scalar         = entry.Scalar,
            Offset         = entry.Offset,
            Unit           = entry.Unit,
            WaveformConfig = entry.Waveform,
        };
    }
}
