using Common.Pids;

namespace Common.Protocol;

// Catalogue of preconfigured identifiers the PID editor offers in the
// Identifier dropdown for $01 / $1A / $22 mode rows. Picking an entry
// stamps the library-defined Size / Type / Scalar / Offset / Unit + display
// name onto the row; the user can then OVERRIDE any of those fields (and
// the waveform) afterwards to customise the wire response. Only the
// Identifier itself stays locked - the picker is the only editor for it,
// so the override never drifts off the chosen library entry.
//
// The editor picker is how a DID gets a curated row on the ECU: an ECU
// only answers $1A / $22 DIDs it has a configured row for, and NRCs $31
// for anything else. The picker is also the override mechanism when a
// user wants non-default wire bytes or a custom waveform for one DID.
//
// $2D mode rows do NOT pull from this catalogue - the user types a memory
// address freehand and edits every other field by hand, because $2D is the
// "custom dynamic PID" case.
//
// Source data: the Mode$01, $1A, and $22 lists are built from the embedded
// A2L-derived libraries in Common.Pids.PidLibrary (decrypted + decompressed
// at first access). Mode$1A is a union of the GMW3110-2010 §8.3.2 Table 25
// spec DIDs and the library entries, library metadata winning when both
// sources cover the same DID.
public sealed record PidCatalogueEntry(
    PidMode Mode,
    uint Identifier,
    string Name,
    PidSize Size,
    PidDataType DataType,
    double Scalar,
    double Offset,
    string Unit,
    int? LengthBytes = null)
{
    // Display string the ComboBox renders. All catalogue-driven modes
    // (Mode 01 / Mode 1A / Mode 22) use the "$" hex prefix so the dropdown
    // reads uniformly - same convention the spec uses ("$22 ReadDataByPid").
    // Mode 2D rows aren't catalogue-driven (they hand-roll a memory address)
    // so this fall-through never fires for them.
    public string Display => Mode switch
    {
        PidMode.Mode1A => $"${Identifier:X2} - {Name}",
        PidMode.Mode22 => $"${Identifier:X4} - {Name}",
        _              => Name,
    };
}

public static class PidCatalogue
{
    /// <summary>$1A DID catalogue. Union of the GMW3110-2010 §8.3.2 Table 25
    /// DIDs (<see cref="Gmw3110DidNames.KnownDids"/>) and the A2L-derived
    /// library; for DIDs present in both, library metadata wins.</summary>
    public static readonly IReadOnlyList<PidCatalogueEntry> Mode1A = BuildMode1A();

    /// <summary>$22 PID catalogue, sourced verbatim from the embedded library.</summary>
    public static readonly IReadOnlyList<PidCatalogueEntry> Mode22 = BuildFromLibrary(PidLibrary.Mode22, PidMode.Mode22);

    /// <summary>Returns the catalogue list appropriate for <paramref name="mode"/>;
    /// empty for $2D (which is hand-rolled).</summary>
    public static IReadOnlyList<PidCatalogueEntry> For(PidMode mode) => mode switch
    {
        PidMode.Mode1A => Mode1A,
        PidMode.Mode22 => Mode22,
        _              => Array.Empty<PidCatalogueEntry>(),
    };

    private static PidCatalogueEntry[] BuildFromLibrary(
        IReadOnlyDictionary<ushort, PidLibraryEntry> library,
        PidMode mode)
    {
        return library.Values
            .OrderBy(e => e.Did)
            .Select(e => FromLibrary(e, mode))
            .ToArray();
    }

    private static PidCatalogueEntry[] BuildMode1A()
    {
        // Union by DID. Spec DIDs without a library row get a placeholder
        // shape (Byte / Unsigned / unity scaling) so the user can still pick
        // them and edit by hand; the spec table is authoritative about what
        // GMW3110 exposes regardless of which A2L rows the donor bin had.
        var library = PidLibrary.Mode1A;
        var allDids = new SortedSet<byte>(Gmw3110DidNames.KnownDids);
        foreach (var did in library.Keys) allDids.Add((byte)did);

        var list = new List<PidCatalogueEntry>(allDids.Count);
        foreach (var did in allDids)
        {
            if (library.TryGetValue(did, out var lib))
            {
                list.Add(FromLibrary(lib, PidMode.Mode1A));
            }
            else
            {
                list.Add(new PidCatalogueEntry(
                    Mode: PidMode.Mode1A,
                    Identifier: did,
                    Name: Gmw3110DidNames.NameOf(did) ?? $"DID {did:X2}",
                    Size: PidSize.Byte,
                    DataType: PidDataType.Unsigned,
                    Scalar: 1.0, Offset: 0.0, Unit: ""));
            }
        }
        return list.ToArray();
    }

    private static PidCatalogueEntry FromLibrary(PidLibraryEntry e, PidMode mode)
    {
        // Display label precedence: FriendlyName (hand-curated, usually
        // empty), then A2lName (always present for library-sourced rows),
        // then a stable PID-id fallback for rows missing every label.
        string name = !string.IsNullOrWhiteSpace(e.FriendlyName) ? e.FriendlyName
                    : !string.IsNullOrWhiteSpace(e.A2lName)      ? e.A2lName
                    : mode == PidMode.Mode22 ? $"PID 0x{e.Did:X4}" : $"PID ${(byte)e.Did:X2}";

        // PidSize tops out at DWord (4). Larger library entries (e.g. PID
        // 0x155B is 17 bytes on E38) are represented via the LengthBytes
        // override on Pid; the catalogue carries the same hint so the
        // SelectedCatalogueEntry setter can preserve fidelity end-to-end.
        var (size, length) = e.Size switch
        {
            1 => (PidSize.Byte, null),
            2 => (PidSize.Word, null),
            4 => (PidSize.DWord, null),
            _ => (PidSize.DWord, (int?)e.Size),
        };

        return new PidCatalogueEntry(
            Mode:        mode,
            Identifier:  e.Did,
            Name:        name,
            Size:        size,
            DataType:    MapDataType(e.DataType),
            Scalar:      e.Slope  ?? 1.0,
            Offset:      e.Offset ?? 0.0,
            Unit:        (e.Unit ?? "").TrimEnd(),
            LengthBytes: length);
    }

    // A2L datatype tokens -> the simulator's PidDataType. The simulator has
    // no Float, so FLOAT32_IEEE collapses to Unsigned (the row's Scalar +
    // raw-bytes plumbing still moves the right number of bytes; the user
    // sees the raw underlying integer in the live monitor).
    private static PidDataType MapDataType(string a2lDataType) => a2lDataType?.ToUpperInvariant() switch
    {
        "UBYTE" or "UWORD" or "ULONG" or "UINT32" => PidDataType.Unsigned,
        "SBYTE" or "SWORD" or "SLONG" or "INT32"  => PidDataType.Signed,
        _ => PidDataType.Unsigned,
    };
}
