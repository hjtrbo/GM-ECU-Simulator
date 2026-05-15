namespace Core.Identification.Segments;

// Data model for our segment-definition system. Designed independently
// from UniversalPatcher's XML-driven equivalent - we know GM ECU bins
// follow a "segment + structural marker" layout, so this file expresses
// that domain in plain C# records.
//
// A segment is a contiguous region of flash with a known internal layout
// (offsets of part numbers, VIN, supplier codes, etc). The exact base
// address of the segment varies per bin within a known set of candidate
// ranges - the bin loader is expected to find which candidate fits by
// searching for one of the CheckWords.

/// <summary>
/// A candidate region where a segment might live. We try the start of
/// each range as the segment base, verify with a CheckWord, and use the
/// first match. Real-world examples: E38/E67 EEPROM_DATA lives at one of
/// 0xC000 or 0xE000; T43's same segment can sit anywhere from 0x8000 to
/// 0xFFFF.
/// </summary>
public readonly record struct SearchRange(int Start, int End);

/// <summary>
/// A structural marker plus where it points. When the marker bytes are
/// found at <see cref="MarkerOffset"/> relative to the candidate segment
/// base, the segment base is confirmed and an anchor at
/// <see cref="AnchorOffset"/> becomes available for field lookups (e.g.
/// VIN references CWvin+0 to mean "17 bytes starting at this anchor").
///
/// Multiple CheckWords with the same name may be listed - they're tried
/// in order and the first whose marker matches wins. This lets one
/// segment definition cover several known variants of the same physical
/// layout.
/// </summary>
public sealed record SegmentCheckWord(
    string Name,
    ulong Marker,
    int MarkerSize,      // bytes: 1, 2, 4, or 8
    int MarkerOffset,    // segment-relative offset where the marker must appear
    int AnchorOffset);   // segment-relative offset that becomes the named anchor

/// <summary>
/// Where a field lives, expressed in the language of segments and
/// CheckWord anchors. Four kinds cover everything we've seen in GM bins:
/// </summary>
public abstract record AddressExpression
{
    /// <summary>Literal file offset.</summary>
    public sealed record Absolute(int Offset) : AddressExpression;

    /// <summary>Offset relative to the resolved segment base.</summary>
    public sealed record SegmentRelative(int Offset) : AddressExpression;

    /// <summary>
    /// A signed offset from a named CheckWord anchor. The CheckWord must
    /// have fired for this segment match, else the field is unresolved.
    /// </summary>
    public sealed record CheckWordRef(string CheckWord, int Offset) : AddressExpression;

    /// <summary>
    /// Indirection: read <see cref="PointerSize"/> bytes (big-endian) at
    /// <see cref="PointerAt"/> and use that value as the resolved address.
    /// Used for "segment base lives at file[0x10024]" style entries.
    /// </summary>
    public sealed record Indirect(int PointerAt, int PointerSize = 4) : AddressExpression;
}

/// <summary>
/// How to decode raw bytes once we've resolved their address.
/// </summary>
public enum FieldType
{
    /// <summary>Big-endian unsigned integer; rendered as decimal.</summary>
    UIntBE,
    /// <summary>Printable ASCII; non-printable bytes rendered as '?'.</summary>
    Text,
    /// <summary>Raw bytes; rendered as uppercase hex string.</summary>
    Hex,
}

/// <summary>
/// One named extraction. Maps from "decoded value" the consumer sees back
/// to the raw bytes and their address.
/// </summary>
public sealed record SegmentField(
    string Name,
    AddressExpression Address,
    int SizeBytes,
    FieldType Type);

/// <summary>
/// Optional dynamic base discovery: scan the SearchAddresses ranges for
/// the given marker bytes. When found at position P, the segment base is
/// inferred as <c>P - MarkerOffsetFromBase</c>. Used for segments whose
/// base address shifts between calibrations and isn't pinned by a
/// CheckWord at a fixed offset - the T43 EEPROM_DATA is the canonical
/// example: its $A5A5 stamp sits at base+0x284, so finding the stamp tells
/// us the base. If both <see cref="SegmentDefinition.SearchMarker"/> and
/// CheckWords are specified, the SearchMarker wins for base discovery and
/// CheckWords are informational anchor sources only (a CheckWord that
/// fails to match doesn't reject the segment).
/// </summary>
public sealed record SegmentSearchMarker(
    ulong Marker,
    int MarkerSize,           // bytes: 1, 2, 4, or 8
    int MarkerOffsetFromBase);

/// <summary>
/// One segment: where it might live, how to confirm we found it, and
/// what fields to pull out of it.
/// </summary>
public sealed record SegmentDefinition(
    string Name,
    IReadOnlyList<SearchRange> SearchAddresses,
    IReadOnlyList<SegmentCheckWord> CheckWords,
    IReadOnlyList<SegmentField> Fields,
    SegmentSearchMarker? SearchMarker = null);

/// <summary>
/// All segments associated with an ECU family. The reader walks this in
/// order and returns matches for each segment that resolves.
/// </summary>
public sealed record FamilyDefinition(
    string Name,
    IReadOnlyList<SegmentDefinition> Segments);
