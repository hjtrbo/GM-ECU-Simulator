namespace Core.Identification.Segments;

using Addr = AddressExpression;

// Per-family segment definitions. Each captures the segment offsets we've
// confirmed empirically from sample bins and reverse-engineered $1A
// handlers. The values match UniversalPatcher's XML defaults where they
// agree with our observations; where they diverge (T43 VIN, for instance)
// we trust the bytes in our test bins over UP's spec since UP's XML
// presumably covers a wider variant set than we've seen.
//
// Stage 1 covers E38 and E67 EEPROM_DATA only - that's where the
// CheckWord-based VIN extraction lights up across every E38/E67 sample
// we have. T43, E92, and per-family OS segments are deferred to later
// stages.
public static class GmFamilyDefinitions
{
    /// <summary>
    /// The EEPROM_DATA segment that both E38 PCMs and E67 PCMs carry.
    /// Lives at one of two candidate bases (0xC000 or 0xE000); confirmed
    /// by finding the 0xA5A0 marker at one of three known internal
    /// offsets, each of which implies a different layout variant for the
    /// VIN anchor.
    ///
    /// Field offsets (all relative to segment base unless the field is
    /// anchored on CWvin):
    ///   #0x07 ..  +3   "Eeprom"  - EEPROM type tag (3 chars text)
    ///   #0x20 ..  +4   "PCMid2"  - secondary PN as BE uint32
    ///   #0x28 ..  +4   "PCM"     - primary calibration PN as BE uint32
    ///   #0x2C .. +16   "TraceCode" - Bosch/Delphi supplier trace string
    ///   CWvin+0 .. +17 "VIN"     - 17-char ASCII VIN
    /// </summary>
    public static readonly SegmentDefinition E38E67EepromData = new(
        Name: "EEPROM_DATA",
        SearchAddresses: new SearchRange[]
        {
            new(0xC000, 0xDFFF),
            new(0xE000, 0xFFFF),
        },
        CheckWords: new SegmentCheckWord[]
        {
            // Three known layout variants. Tried in order; first match wins.
            // The marker's job is to confirm both the segment base AND
            // which variant's anchor offset applies.
            new("CWvin", Marker: 0xA5A0, MarkerSize: 2, MarkerOffset: 0x326, AnchorOffset: 0x1CC),
            new("CWvin", Marker: 0xA5A0, MarkerSize: 2, MarkerOffset: 0x1DA, AnchorOffset: 0xB4),
            new("CWvin", Marker: 0xA5A0, MarkerSize: 2, MarkerOffset: 0x1BE, AnchorOffset: 0xB8),
        },
        Fields: new SegmentField[]
        {
            new("Eeprom",    new Addr.SegmentRelative(0x07),    SizeBytes:  3, Type: FieldType.Text),
            new("PCMid2",    new Addr.SegmentRelative(0x20),    SizeBytes:  4, Type: FieldType.UIntBE),
            new("PCM",       new Addr.SegmentRelative(0x28),    SizeBytes:  4, Type: FieldType.UIntBE),
            new("TraceCode", new Addr.SegmentRelative(0x2C),    SizeBytes: 16, Type: FieldType.Text),
            new("VIN",       new Addr.CheckWordRef("CWvin", 0), SizeBytes: 17, Type: FieldType.Text),
        });

    /// <summary>
    /// The OS segment for E38/E67. Base address is held as a 4-byte
    /// big-endian pointer at the fixed file offset 0x10024 - this matches
    /// our PowerPC RE of the $1A handler chain exactly (the handler for
    /// $C1 reads from base+5, and that's where the 4-byte BE service PN
    /// lives). Mainly here as a cross-check against the dispatcher-trace
    /// path: if the two disagree on $C1, something is off.
    /// </summary>
    public static readonly SegmentDefinition E38E67OsSegment = new(
        Name: "OS",
        SearchAddresses: Array.Empty<SearchRange>(),
        CheckWords: Array.Empty<SegmentCheckWord>(),
        Fields: new SegmentField[]
        {
            // PN at indirect[0x10024] + 5, 4 bytes BE int = $C1 wire bytes.
            // We can't express "segment base = indirect" yet, so the field
            // itself is built from indirect; the segment base value here
            // is irrelevant for this single-field segment.
            new("PN_OS",  new Addr.Indirect(0x10024, PointerSize: 4), SizeBytes: 4, Type: FieldType.UIntBE),
        });

    public static readonly FamilyDefinition E38 = new(
        Name: "E38",
        Segments: new[] { E38E67OsSegment, E38E67EepromData });

    public static readonly FamilyDefinition E67 = new(
        Name: "E67",
        Segments: new[] { E38E67OsSegment, E38E67EepromData });

    /// <summary>
    /// The T43 TCM's EEPROM_DATA segment. Base is discovered dynamically
    /// by scanning for a $A5A5 marker that the bootloader places at
    /// base+0x284 in every T43 calibration we've inspected. The
    /// CheckWords list is carried over from UP's t43.xml but none of them
    /// fired on our T43 samples - they're harmless when SearchMarker is
    /// the active base-discovery mode.
    ///
    /// Field offsets (all relative to discovered base):
    ///   #0x002  +3    "PNAddr"     - segment number / version tag
    ///   #0x00E +20    "Bosch HW#"  - 20-char Bosch HW stamp ("PCM1" in UP)
    ///   #0x0CF +27    "PCM2"       - HW + cal-PN combined string
    ///   #0x22C  +4    "BCC"        - broadcast code (last 4 of cal PN)
    ///   #0x280  +4    "PCM_Cal"    - calibration PN (BE uint32)
    ///   #0x298 +16    "TraceCode"  - Bosch DV-series trace stamp
    ///   #0x2A8  +4    "Programdate"- BCD YYYYMMDD
    ///   #0x2C8 +10    "Tool"       - programming tool id
    ///   #0x300 +17    "VIN"        - 17-char ASCII VIN
    /// </summary>
    public static readonly SegmentDefinition T43EepromData = new(
        Name: "EEPROM_DATA",
        SearchAddresses: new SearchRange[]
        {
            new(0x8000, 0x9FFF),
            new(0xA000, 0xBFFF),
            new(0xC000, 0xDFFF),
            new(0xE000, 0xFFFF),
        },
        SearchMarker: new SegmentSearchMarker(
            Marker: 0xA5A5,
            MarkerSize: 2,
            MarkerOffsetFromBase: 0x284),
        CheckWords: new SegmentCheckWord[]
        {
            // Mirrors UP's t43.xml. None of these fire on the T43 samples
            // we have - SearchMarker carries the base - but keep them so
            // bins that do carry the a5a0 marker get a confirmed anchor
            // for the VIN slot too.
            new("CWvin", Marker: 0xA5A0, MarkerSize: 2, MarkerOffset: 0x326, AnchorOffset: 0x1CC),
            new("CWvin", Marker: 0xA5A0, MarkerSize: 2, MarkerOffset: 0x1DA, AnchorOffset: 0xB4),
            new("CWvin", Marker: 0xA5A0, MarkerSize: 2, MarkerOffset: 0x1BE, AnchorOffset: 0xB8),
        },
        Fields: new SegmentField[]
        {
            new("BoschHwStamp", new Addr.SegmentRelative(0x00E), SizeBytes: 20, Type: FieldType.Text),
            new("PCM2",         new Addr.SegmentRelative(0x0CF), SizeBytes: 27, Type: FieldType.Text),
            new("BCC",          new Addr.SegmentRelative(0x22C), SizeBytes:  4, Type: FieldType.Text),
            new("PCM",          new Addr.SegmentRelative(0x280), SizeBytes:  4, Type: FieldType.UIntBE),
            new("TraceCode",    new Addr.SegmentRelative(0x298), SizeBytes: 16, Type: FieldType.Text),
            new("Programdate",  new Addr.SegmentRelative(0x2A8), SizeBytes:  4, Type: FieldType.Hex),
            new("Tool",         new Addr.SegmentRelative(0x2C8), SizeBytes: 10, Type: FieldType.Text),
            new("VIN",          new Addr.SegmentRelative(0x300), SizeBytes: 17, Type: FieldType.Text),
        });

    public static readonly FamilyDefinition T43 = new(
        Name: "T43",
        Segments: new[] { T43EepromData });

    /// <summary>
    /// Find the FamilyDefinition for a label string. Returns null if the
    /// label isn't one of the families we have segment definitions for.
    /// Used by the BinIdentificationReader after its own family-detection
    /// pass to opt in to the segment reader where supported.
    /// </summary>
    public static FamilyDefinition? Lookup(string family) => family switch
    {
        "T43" => T43,
        "E38" => E38,
        "E67" => E67,
        _ => null,
    };
}
