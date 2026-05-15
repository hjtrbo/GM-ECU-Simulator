using System.Buffers.Binary;
using System.Text;

namespace Core.Identification.Segments;

// Walks a flash image against a FamilyDefinition and produces one
// SegmentMatch per segment that resolves cleanly. Has no dependency on
// the rest of BinIdentificationReader - this is the structural-marker
// reader, independent of the PowerPC dispatcher-trace logic. The two
// approaches complement each other: dispatcher trace gives on-the-wire
// fidelity, segment reader gives broad flash metadata coverage.
//
// Matching algorithm per segment:
//   1. Iterate candidate bases (SearchAddresses + each fixed Indirect
//      target, if any).
//   2. For each candidate, scan the CheckWords list in order. A CheckWord
//      "fires" when its marker bytes are present at
//      base+MarkerOffset. Record the anchor address. First firing
//      CheckWord wins for a given name; later entries with the same name
//      are ignored. (This matches UP's first-match-wins semantics.)
//   3. With segment_base confirmed, resolve every field's AddressExpression
//      and read the appropriate number of bytes. If a field references a
//      CheckWord that never fired, that field is reported as null.
public static class SegmentReader
{
    /// <summary>
    /// One successful segment resolution. Holds the confirmed base, the
    /// CheckWord anchors that fired, and the resolved fields (Address +
    /// raw bytes + decoded string).
    /// </summary>
    public sealed record SegmentMatch(
        string SegmentName,
        int SegmentBase,
        IReadOnlyDictionary<string, int> CheckWordAnchors,
        IReadOnlyList<FieldExtraction> Fields);

    /// <summary>
    /// One field's resolved location and decoded value.
    /// </summary>
    public sealed record FieldExtraction(
        string Name,
        int? FileOffset,         // null if address couldn't be resolved
        byte[] RawBytes,
        FieldType Type,
        string? DecodedValue);   // null if address couldn't be resolved

    /// <summary>
    /// Run the reader for every segment in the family. Returns one match
    /// per segment that resolved (CheckWord matched, or no CheckWords
    /// required), in segment-definition order. Segments that fail to
    /// match are omitted.
    /// </summary>
    public static IReadOnlyList<SegmentMatch> Read(ReadOnlySpan<byte> bin, FamilyDefinition family)
    {
        var results = new List<SegmentMatch>();
        foreach (var seg in family.Segments)
        {
            var match = TryMatch(bin, seg);
            if (match != null) results.Add(match);
        }
        return results;
    }

    /// <summary>
    /// Try to match a single segment. Used both by Read above and from
    /// tests when only one segment is interesting.
    /// </summary>
    public static SegmentMatch? TryMatch(ReadOnlySpan<byte> bin, SegmentDefinition seg)
    {
        // Build the candidate-base list. Two modes:
        //   1. SearchMarker mode: scan each SearchRange for the marker;
        //      every hit produces one candidate base.
        //   2. Static mode: use the start of each SearchRange. If no
        //      SearchAddresses are listed at all, fall through to a
        //      single zero-base candidate (the segment's fields are then
        //      expected to be absolute or indirect).
        IEnumerable<int> candidates;
        if (seg.SearchMarker != null)
        {
            candidates = FindSearchMarkerBases(bin, seg);
        }
        else if (seg.SearchAddresses.Count > 0)
        {
            candidates = seg.SearchAddresses.Select(r => r.Start);
        }
        else
        {
            candidates = new[] { 0 };
        }

        foreach (var baseAddr in candidates)
        {
            if (baseAddr < 0 || baseAddr >= bin.Length) continue;

            var anchors = ResolveCheckWords(bin, seg, baseAddr);

            // When there's no SearchMarker, the CheckWord is the only
            // base-validation mechanism: require at least one to fire (if
            // any are defined). With a SearchMarker the marker hit already
            // confirmed the base, so CheckWords become advisory anchor
            // sources that may or may not resolve.
            if (seg.SearchMarker == null && seg.CheckWords.Count > 0 && anchors.Count == 0)
                continue;

            var fields = new List<FieldExtraction>(seg.Fields.Count);
            foreach (var f in seg.Fields)
                fields.Add(ResolveField(bin, f, baseAddr, anchors));

            return new SegmentMatch(seg.Name, baseAddr, anchors, fields);
        }
        return null;
    }

    private static IEnumerable<int> FindSearchMarkerBases(ReadOnlySpan<byte> bin, SegmentDefinition seg)
    {
        var marker = seg.SearchMarker!;
        // No ranges declared - scan the whole bin. Otherwise scan each
        // range. Either way, every marker hit at position P yields a
        // candidate base of P - MarkerOffsetFromBase.
        var ranges = seg.SearchAddresses.Count > 0
            ? seg.SearchAddresses.ToArray()
            : new[] { new SearchRange(0, bin.Length - 1) };

        var hits = new List<int>();
        foreach (var r in ranges)
        {
            int start = Math.Max(0, r.Start);
            int end   = Math.Min(bin.Length - marker.MarkerSize, r.End);
            for (int i = start; i <= end; i++)
            {
                if (MarkerMatches(bin, i, marker.Marker, marker.MarkerSize))
                {
                    int candidate = i - marker.MarkerOffsetFromBase;
                    if (candidate >= 0 && candidate < bin.Length)
                        hits.Add(candidate);
                }
            }
        }
        return hits;
    }

    // ---------------------------- helpers ----------------------------

    private static Dictionary<string, int> ResolveCheckWords(
        ReadOnlySpan<byte> bin, SegmentDefinition seg, int baseAddr)
    {
        var anchors = new Dictionary<string, int>();
        foreach (var cw in seg.CheckWords)
        {
            // Skip CheckWords whose name already resolved - first-match-wins.
            if (anchors.ContainsKey(cw.Name)) continue;

            int markerAt = baseAddr + cw.MarkerOffset;
            if (markerAt < 0 || markerAt + cw.MarkerSize > bin.Length) continue;

            if (!MarkerMatches(bin, markerAt, cw.Marker, cw.MarkerSize)) continue;

            anchors[cw.Name] = baseAddr + cw.AnchorOffset;
        }
        return anchors;
    }

    private static bool MarkerMatches(ReadOnlySpan<byte> bin, int at, ulong marker, int size)
    {
        // Big-endian marker compare. UP source stores markers as big-endian
        // hex strings ("a5a0" = bytes A5 A0 in file order) and that's what
        // we observed across the sample bins.
        ulong actual = size switch
        {
            1 => bin[at],
            2 => BinaryPrimitives.ReadUInt16BigEndian(bin.Slice(at, 2)),
            4 => BinaryPrimitives.ReadUInt32BigEndian(bin.Slice(at, 4)),
            8 => BinaryPrimitives.ReadUInt64BigEndian(bin.Slice(at, 8)),
            _ => throw new ArgumentOutOfRangeException(nameof(size),
                "CheckWord marker size must be 1, 2, 4, or 8 bytes."),
        };
        return actual == marker;
    }

    private static FieldExtraction ResolveField(
        ReadOnlySpan<byte> bin, SegmentField field, int baseAddr,
        IReadOnlyDictionary<string, int> anchors)
    {
        int? addr = ResolveAddress(bin, field.Address, baseAddr, anchors);
        if (addr is null || addr.Value < 0 || addr.Value + field.SizeBytes > bin.Length)
        {
            return new FieldExtraction(field.Name, FileOffset: null,
                RawBytes: Array.Empty<byte>(), Type: field.Type, DecodedValue: null);
        }

        var bytes = bin.Slice(addr.Value, field.SizeBytes).ToArray();
        var decoded = DecodeField(bytes, field.Type);
        return new FieldExtraction(field.Name, FileOffset: addr.Value,
            RawBytes: bytes, Type: field.Type, DecodedValue: decoded);
    }

    private static int? ResolveAddress(
        ReadOnlySpan<byte> bin, AddressExpression expr, int baseAddr,
        IReadOnlyDictionary<string, int> anchors)
    {
        switch (expr)
        {
            case AddressExpression.Absolute a:
                return a.Offset;

            case AddressExpression.SegmentRelative sr:
                return baseAddr + sr.Offset;

            case AddressExpression.CheckWordRef cw:
                return anchors.TryGetValue(cw.CheckWord, out var anchor)
                    ? anchor + cw.Offset
                    : (int?)null;

            case AddressExpression.Indirect ind:
                if (ind.PointerAt < 0 || ind.PointerAt + ind.PointerSize > bin.Length) return null;
                return ind.PointerSize switch
                {
                    2 => BinaryPrimitives.ReadUInt16BigEndian(bin.Slice(ind.PointerAt, 2)),
                    4 => (int)BinaryPrimitives.ReadUInt32BigEndian(bin.Slice(ind.PointerAt, 4)),
                    _ => throw new ArgumentOutOfRangeException(nameof(ind.PointerSize),
                        "Indirect pointer size must be 2 or 4 bytes."),
                };

            default:
                throw new ArgumentOutOfRangeException(nameof(expr),
                    "Unknown AddressExpression variant: " + expr.GetType().Name);
        }
    }

    private static string DecodeField(byte[] bytes, FieldType type) => type switch
    {
        FieldType.UIntBE => DecodeUIntBE(bytes).ToString(),
        FieldType.Text   => DecodeText(bytes),
        FieldType.Hex    => Convert.ToHexString(bytes),
        _ => throw new ArgumentOutOfRangeException(nameof(type), "Unknown FieldType: " + type),
    };

    private static ulong DecodeUIntBE(byte[] bytes) => bytes.Length switch
    {
        1 => bytes[0],
        2 => BinaryPrimitives.ReadUInt16BigEndian(bytes),
        4 => BinaryPrimitives.ReadUInt32BigEndian(bytes),
        8 => BinaryPrimitives.ReadUInt64BigEndian(bytes),
        _ => throw new ArgumentOutOfRangeException(nameof(bytes),
            "UIntBE field must be 1, 2, 4, or 8 bytes (got " + bytes.Length + ")."),
    };

    private static string DecodeText(byte[] bytes)
    {
        // Replace non-printable bytes with '?'. Trailing nulls and 0xFFs
        // (typical erased-flash padding) get trimmed off entirely so a
        // 17-byte VIN slot with 4 trailing FF bytes doesn't look like
        // "VIN????".
        var sb = new StringBuilder(bytes.Length);
        foreach (var b in bytes)
        {
            if (b == 0x00 || b == 0xFF) sb.Append('\0');
            else if (b >= 0x20 && b <= 0x7E) sb.Append((char)b);
            else sb.Append('?');
        }
        return sb.ToString().TrimEnd('\0');
    }
}
