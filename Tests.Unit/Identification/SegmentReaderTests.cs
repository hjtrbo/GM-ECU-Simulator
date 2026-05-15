using System.Buffers.Binary;
using System.Text;
using Core.Identification.Segments;
using Xunit;

namespace EcuSimulator.Tests.Identification;

// Coverage for the structural-marker SegmentReader (clean-room
// reimplementation of UP's CheckWord/Addresses pattern).
//
// Synthetic tests build minimal byte arrays with a known segment layout
// and confirm: CheckWord matching, segment-base picking, anchored field
// resolution (CWvin+0), segment-relative resolution, text/UIntBE decoding,
// and the multi-candidate fall-through (first matching CheckWord wins).
//
// The real-bin integration tests live in BinIdentificationReaderTests
// because they assert the wired-in behaviour through Parse().
public sealed class SegmentReaderTests
{
    [Fact]
    public void NoSearchAddresses_AndNoCheckWords_UsesZeroBase()
    {
        var bin = new byte[0x100];
        BinaryPrimitives.WriteUInt32BigEndian(bin.AsSpan(0x10, 4), 12345678);
        var seg = new SegmentDefinition(
            "Test", Array.Empty<SearchRange>(), Array.Empty<SegmentCheckWord>(),
            new SegmentField[] { new("PN", new AddressExpression.SegmentRelative(0x10), 4, FieldType.UIntBE) });

        var match = SegmentReader.TryMatch(bin, seg);
        Assert.NotNull(match);
        Assert.Equal(0, match!.SegmentBase);
        Assert.Equal("12345678", match.Fields[0].DecodedValue);
    }

    [Fact]
    public void CheckWord_Fires_AnchorBecomesAvailable()
    {
        // 64 KiB synthetic: base @ 0x4000, marker A5A0 at base+0x10,
        // anchor at base+0x80. VIN bytes "TESTVIN1234567890" at the anchor.
        var bin = new byte[0x10000];
        Array.Fill(bin, (byte)0xFF);
        const int Base = 0x4000;
        const int MarkerOffset = 0x10;
        const int AnchorOffset = 0x80;
        BinaryPrimitives.WriteUInt16BigEndian(bin.AsSpan(Base + MarkerOffset, 2), 0xA5A0);
        Encoding.ASCII.GetBytes("TESTVIN1234567890").CopyTo(bin.AsSpan(Base + AnchorOffset));

        var seg = new SegmentDefinition(
            "Test",
            new SearchRange[] { new(Base, Base + 0xFFF) },
            new SegmentCheckWord[] { new("CWvin", 0xA5A0, 2, MarkerOffset, AnchorOffset) },
            new SegmentField[] { new("VIN", new AddressExpression.CheckWordRef("CWvin", 0), 17, FieldType.Text) });

        var match = SegmentReader.TryMatch(bin, seg);
        Assert.NotNull(match);
        Assert.Equal(Base, match!.SegmentBase);
        Assert.Equal(Base + AnchorOffset, match.CheckWordAnchors["CWvin"]);
        Assert.Equal("TESTVIN1234567890", match.Fields[0].DecodedValue);
    }

    [Fact]
    public void CheckWord_FallsThroughToSecondCandidate_WhenFirstMissesMarker()
    {
        // First CheckWord expects marker at offset 0x10 but we plant it
        // at 0x20 (second CheckWord's offset) - the reader should skip
        // the first and accept the second.
        var bin = new byte[0x10000];
        Array.Fill(bin, (byte)0xFF);
        const int Base = 0x4000;
        BinaryPrimitives.WriteUInt16BigEndian(bin.AsSpan(Base + 0x20, 2), 0xA5A0);
        Encoding.ASCII.GetBytes("SECONDVARIANTVIN1").CopyTo(bin.AsSpan(Base + 0xC0));

        var seg = new SegmentDefinition(
            "Test",
            new SearchRange[] { new(Base, Base + 0xFFF) },
            new SegmentCheckWord[]
            {
                new("CWvin", 0xA5A0, 2, MarkerOffset: 0x10, AnchorOffset: 0x80),  // miss
                new("CWvin", 0xA5A0, 2, MarkerOffset: 0x20, AnchorOffset: 0xC0),  // hit
            },
            new SegmentField[] { new("VIN", new AddressExpression.CheckWordRef("CWvin", 0), 17, FieldType.Text) });

        var match = SegmentReader.TryMatch(bin, seg);
        Assert.NotNull(match);
        Assert.Equal(Base + 0xC0, match!.CheckWordAnchors["CWvin"]);
        Assert.Equal("SECONDVARIANTVIN1", match.Fields[0].DecodedValue);
    }

    [Fact]
    public void NoCheckWord_Fires_SegmentDoesNotMatch()
    {
        // Marker not present anywhere -> no candidate base passes the
        // verification -> TryMatch returns null. Critical for VIN: we
        // never want to surface a "found" VIN when there's no structural
        // anchor for it.
        var bin = new byte[0x10000];
        Array.Fill(bin, (byte)0xFF);  // no A5A0 anywhere

        var seg = new SegmentDefinition(
            "Test",
            new SearchRange[] { new(0x4000, 0x4FFF) },
            new SegmentCheckWord[] { new("CWvin", 0xA5A0, 2, 0x10, 0x80) },
            new SegmentField[] { new("VIN", new AddressExpression.CheckWordRef("CWvin", 0), 17, FieldType.Text) });

        Assert.Null(SegmentReader.TryMatch(bin, seg));
    }

    [Fact]
    public void FirstCandidateBase_TakesPrecedenceOverSecond()
    {
        // Plant the marker at both 0xC000 and 0xE000. The reader iterates
        // candidate bases in order, so 0xC000 should win.
        var bin = new byte[0x10000];
        Array.Fill(bin, (byte)0xFF);
        BinaryPrimitives.WriteUInt16BigEndian(bin.AsSpan(0xC000 + 0x10, 2), 0xA5A0);
        Encoding.ASCII.GetBytes("VINATCBASEXXXXXXX").CopyTo(bin.AsSpan(0xC000 + 0x80));
        BinaryPrimitives.WriteUInt16BigEndian(bin.AsSpan(0xE000 + 0x10, 2), 0xA5A0);
        Encoding.ASCII.GetBytes("VINATEBASEXXXXXXX").CopyTo(bin.AsSpan(0xE000 + 0x80));

        var seg = new SegmentDefinition(
            "Test",
            new SearchRange[] { new(0xC000, 0xDFFF), new(0xE000, 0xFFFF) },
            new SegmentCheckWord[] { new("CWvin", 0xA5A0, 2, 0x10, 0x80) },
            new SegmentField[] { new("VIN", new AddressExpression.CheckWordRef("CWvin", 0), 17, FieldType.Text) });

        var match = SegmentReader.TryMatch(bin, seg);
        Assert.NotNull(match);
        Assert.Equal(0xC000, match!.SegmentBase);
        Assert.Equal("VINATCBASEXXXXXXX", match.Fields[0].DecodedValue);
    }

    [Fact]
    public void UnresolvedCheckWordRef_LeavesFieldAddressNull()
    {
        // The segment requires a CWvin anchor but the marker doesn't
        // fire. Result: TryMatch returns null (CheckWord required for
        // segment to be valid).
        var bin = new byte[0x10000];
        Array.Fill(bin, (byte)0xFF);

        var seg = new SegmentDefinition(
            "Test",
            new SearchRange[] { new(0x4000, 0x4FFF) },
            new SegmentCheckWord[] { new("CWvin", 0xA5A0, 2, 0x10, 0x80) },
            new SegmentField[]
            {
                new("VIN", new AddressExpression.CheckWordRef("CWvin", 0), 17, FieldType.Text),
                new("PN",  new AddressExpression.SegmentRelative(0x28),    4,  FieldType.UIntBE),
            });

        Assert.Null(SegmentReader.TryMatch(bin, seg));
    }

    [Fact]
    public void Indirect_PointerResolution_ReadsTargetAddress()
    {
        // Plant a 4-byte BE pointer at file offset 0x10024 whose value is
        // 0x00010000. A field with SegmentRelative(5) under a segment
        // whose base is loaded via Indirect(0x10024) should land at 0x10005.
        var bin = new byte[0x20000];
        Array.Fill(bin, (byte)0xFF);
        BinaryPrimitives.WriteUInt32BigEndian(bin.AsSpan(0x10024, 4), 0x00010000);
        BinaryPrimitives.WriteUInt32BigEndian(bin.AsSpan(0x10005, 4), 24264923);

        // Two-step: resolve the segment base via Indirect, then read a
        // SegmentRelative offset off it. We test this through a direct
        // call to SegmentReader's resolver since the model doesn't yet
        // support "segment base comes from an Indirect" - that's a Stage
        // 2 feature. For now, verify the Indirect expression works at
        // field level instead.
        var seg = new SegmentDefinition(
            "Test", Array.Empty<SearchRange>(), Array.Empty<SegmentCheckWord>(),
            new SegmentField[]
            {
                new("PN", new AddressExpression.Indirect(0x10024, PointerSize: 4), 4, FieldType.UIntBE),
            });
        var match = SegmentReader.TryMatch(bin, seg);
        Assert.NotNull(match);
        // Indirect resolves to 0x00010000, then read 4 bytes there = the
        // first 4 bytes of the buffer starting at 0x10000. Since we
        // wrote 24264923 at 0x10005, position 0x10000 is still 0xFFs.
        // This test confirms the resolution mechanism, not the data.
        Assert.Equal(0x00010000, match!.Fields[0].FileOffset);
    }

    [Fact]
    public void SearchMarker_DiscoversBaseFromMarkerHit()
    {
        // The T43 EEPROM_DATA approach: scan the SearchAddresses range for
        // a marker that the bootloader places at a known offset relative
        // to the segment base. base = position_of_hit - MarkerOffsetFromBase.
        const int Base = 0x8000;
        const int MarkerOffsetFromBase = 0x284;
        const int VinOffsetFromBase = 0x300;

        var bin = new byte[0x10000];
        Array.Fill(bin, (byte)0xFF);
        BinaryPrimitives.WriteUInt16BigEndian(
            bin.AsSpan(Base + MarkerOffsetFromBase, 2), 0xA5A5);
        Encoding.ASCII.GetBytes("TESTVINFROMSEARCHM").CopyTo(bin.AsSpan(Base + VinOffsetFromBase));

        var seg = new SegmentDefinition(
            "Test",
            new SearchRange[] { new(0x8000, 0x9FFF) },
            CheckWords: Array.Empty<SegmentCheckWord>(),
            Fields: new SegmentField[]
            {
                new("VIN", new AddressExpression.SegmentRelative(VinOffsetFromBase), 17, FieldType.Text),
            },
            SearchMarker: new SegmentSearchMarker(0xA5A5, 2, MarkerOffsetFromBase));

        var match = SegmentReader.TryMatch(bin, seg);
        Assert.NotNull(match);
        Assert.Equal(Base, match!.SegmentBase);
        Assert.Equal("TESTVINFROMSEARCH", match.Fields[0].DecodedValue!.TrimEnd());
    }

    [Fact]
    public void SearchMarker_AcceptsBaseEvenWhenCheckWordsDoNotFire()
    {
        // With a SearchMarker present, the CheckWords list is advisory:
        // a missing marker doesn't reject the segment. This is the T43
        // pattern - the a5a0 CheckWords carried over from UP's t43.xml
        // don't fire on most T43 samples, but the a5a5 SearchMarker does.
        const int Base = 0x8000;
        var bin = new byte[0x10000];
        Array.Fill(bin, (byte)0xFF);
        BinaryPrimitives.WriteUInt16BigEndian(bin.AsSpan(Base + 0x284, 2), 0xA5A5);
        BinaryPrimitives.WriteUInt32BigEndian(bin.AsSpan(Base + 0x280, 4), 24265053);

        var seg = new SegmentDefinition(
            "Test",
            new SearchRange[] { new(0x8000, 0x9FFF) },
            CheckWords: new SegmentCheckWord[]
            {
                // Marker doesn't actually appear at offset 0x326 - this
                // CheckWord won't fire, but the SearchMarker base should
                // still resolve the segment.
                new("CWvin", 0xA5A0, 2, 0x326, 0x1CC),
            },
            Fields: new SegmentField[]
            {
                new("PCM", new AddressExpression.SegmentRelative(0x280), 4, FieldType.UIntBE),
            },
            SearchMarker: new SegmentSearchMarker(0xA5A5, 2, 0x284));

        var match = SegmentReader.TryMatch(bin, seg);
        Assert.NotNull(match);
        Assert.Equal("24265053", match!.Fields[0].DecodedValue);
        Assert.Empty(match.CheckWordAnchors);
    }

    [Fact]
    public void TextDecoding_TrimsTrailingNullsAndErasedFlash()
    {
        // Pattern from real bins: a 17-byte VIN slot may be partially
        // erased (FF padding) for a non-VIN-flashed bin. The decoder
        // should produce a clean prefix rather than "VIN??????????".
        var bin = new byte[0x100];
        Array.Fill(bin, (byte)0xFF);
        Encoding.ASCII.GetBytes("ABC").CopyTo(bin.AsSpan(0x10));
        // bytes 0x13..0x20 stay 0xFF

        var seg = new SegmentDefinition(
            "Test", Array.Empty<SearchRange>(), Array.Empty<SegmentCheckWord>(),
            new SegmentField[]
            {
                new("Trimmed", new AddressExpression.SegmentRelative(0x10), 17, FieldType.Text),
            });

        var match = SegmentReader.TryMatch(bin, seg);
        Assert.NotNull(match);
        Assert.Equal("ABC", match!.Fields[0].DecodedValue);
    }
}
