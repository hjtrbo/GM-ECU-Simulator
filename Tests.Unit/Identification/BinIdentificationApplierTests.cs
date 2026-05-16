using Core.Ecu;
using Core.Identification;
using EcuSimulator.Tests.TestHelpers;
using Xunit;
using static Core.Identification.BinIdentificationReader;

namespace EcuSimulator.Tests.Identification;

// Two-mode bin-load applier driven by the "Replace all" vs "Add only if blank"
// dialog in the editor. The Core helper is the testable unit: the WPF
// view-model just picks the mode and forwards.
public class BinIdentificationApplierTests
{
    private static BinIdentification FakeResult(
        string? vin = null,
        string? supplierHwNumber = null,
        string? supplierHwVersion = null,
        string? baseModelPartNumber = null,
        string? broadcastCode = null,
        string? programmingDate = null,
        string? traceCode = null,
        DidExtraction? c1 = null)
    {
        var dids = c1 != null ? new[] { c1 } : Array.Empty<DidExtraction>();
        return new BinIdentification(
            Family: "T43",
            ServiceDispatcherOffset: 0,
            Service1AHandlerOffset: 0,
            DidDispatcherOffset: 0,
            SupportedSids: Array.Empty<byte>(),
            Dids: dids,
            Vin: vin,
            SupplierHardwareNumber: supplierHwNumber,
            SupplierHardwareVersion: supplierHwVersion,
            EndModelPartNumber: null,
            BaseModelPartNumber: baseModelPartNumber,
            CalibrationPartNumber: null,
            BroadcastCode: broadcastCode,
            ProgrammingDate: programmingDate,
            ProgrammingTool: null,
            TraceCode: traceCode,
            Warnings: Array.Empty<string>());
    }

    [Fact]
    public void Merge_keeps_existing_DIDs_and_only_fills_blanks()
    {
        // Pre-populate $90 (the user wants to keep their VIN). $92 is blank.
        var node = NodeFactory.CreateNode();
        node.SetIdentifier(0x90, System.Text.Encoding.ASCII.GetBytes("USER-EDITED-VIN--"));

        var result = FakeResult(vin: "BIN-EXTRACTED-VIN", supplierHwNumber: "BIN-HW-001");

        var outcome = BinIdentificationApplier.Apply(node, result, BinIdentificationApplier.LoadMode.Merge);

        // $90 stays as user's value.
        Assert.Equal("USER-EDITED-VIN--",
            System.Text.Encoding.ASCII.GetString(node.GetIdentifier(0x90)!));
        // $92 was blank, so the bin value lands.
        Assert.Equal("BIN-HW-001",
            System.Text.Encoding.ASCII.GetString(node.GetIdentifier(0x92)!));
        // Summary lists both correctly. Labels come from Gmw3110DidNames.
        Assert.Contains("$92 System Supplier ID", outcome.Applied);
        Assert.Contains("$90 VIN", outcome.Skipped);
    }

    [Fact]
    public void ReplaceAll_clears_every_DID_first()
    {
        // Pre-populate a mix of DIDs (well-known and custom).
        var node = NodeFactory.CreateNode();
        node.SetIdentifier(0x90, System.Text.Encoding.ASCII.GetBytes("OLD-VIN"));
        node.SetIdentifier(0xCC, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
        node.SetIdentifier(0x77, new byte[] { 0x77 });   // not in well-known set

        var result = FakeResult(vin: "NEW-VIN");

        var outcome = BinIdentificationApplier.Apply(node, result, BinIdentificationApplier.LoadMode.ReplaceAll);

        // Old DIDs gone.
        Assert.Null(node.GetIdentifier(0xCC));
        Assert.Null(node.GetIdentifier(0x77));
        // New VIN landed (bin value).
        Assert.Equal("NEW-VIN",
            System.Text.Encoding.ASCII.GetString(node.GetIdentifier(0x90)!));
        // Skipped is empty in replace-all mode - precedence guard doesn't apply.
        Assert.Empty(outcome.Skipped);
        Assert.Contains("$90 VIN", outcome.Applied);
    }

    [Fact]
    public void ReplaceAll_leaves_DIDs_unconfigured_when_bin_has_nothing_for_them()
    {
        // User had $90 populated; bin produces nothing. Replace-all wipes it
        // and there's no bin VIN to land - $90 ends up unconfigured.
        var node = NodeFactory.CreateNode();
        node.SetIdentifier(0x90, System.Text.Encoding.ASCII.GetBytes("WILL-BE-WIPED"));

        var result = FakeResult();    // no fields populated

        var outcome = BinIdentificationApplier.Apply(node, result, BinIdentificationApplier.LoadMode.ReplaceAll);

        Assert.Null(node.GetIdentifier(0x90));
        Assert.Empty(outcome.Applied);
        Assert.Empty(outcome.Skipped);
    }

    [Fact]
    public void Merge_with_c1_FlashUInt32BE_writes_raw_bytes()
    {
        // The $C1 special-case path goes through the same precedence guard.
        var node = NodeFactory.CreateNode();
        var c1Extraction = new DidExtraction(
            Did: 0xC1,
            Kind: DidSourceKind.FlashUInt32BE,
            FlashAddress: 0x60005,
            WireBytes: new byte[] { 0x01, 0x72, 0x40, 0xDB },
            DecodedValue: "24264923");

        var result = FakeResult(c1: c1Extraction);

        var outcome = BinIdentificationApplier.Apply(node, result, BinIdentificationApplier.LoadMode.Merge);

        Assert.Equal(new byte[] { 0x01, 0x72, 0x40, 0xDB }, node.GetIdentifier(0xC1));
        Assert.Contains("$C1 End Model Part Number", outcome.Applied);
    }

    [Fact]
    public void Merge_with_existing_C1_skips_FlashUInt32BE_extraction()
    {
        // User already entered $C1; bin would overwrite it in ReplaceAll, but
        // Merge has to skip.
        var node = NodeFactory.CreateNode();
        node.SetIdentifier(0xC1, new byte[] { 0xAA, 0xBB, 0xCC, 0xDD });
        var c1Extraction = new DidExtraction(
            Did: 0xC1,
            Kind: DidSourceKind.FlashUInt32BE,
            FlashAddress: 0x60005,
            WireBytes: new byte[] { 0x01, 0x72, 0x40, 0xDB },
            DecodedValue: "24264923");

        var result = FakeResult(c1: c1Extraction);

        var outcome = BinIdentificationApplier.Apply(node, result, BinIdentificationApplier.LoadMode.Merge);

        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }, node.GetIdentifier(0xC1));
        Assert.Contains("$C1 End Model Part Number", outcome.Skipped);
    }

    [Fact]
    public void Eeprom_block_fields_route_to_matching_dids_b5_99_b4()
    {
        // BCC -> $B5, ProgrammingDate -> $99, TraceCode -> $B4. Sourced from
        // the SegmentReader / Stage-2 extraction in the real bin loader.
        var node = NodeFactory.CreateNode();
        var result = FakeResult(broadcastCode: "ABCD",
                                programmingDate: "20260516",
                                traceCode: "TRACE-STAMP-1234");

        var outcome = BinIdentificationApplier.Apply(node, result, BinIdentificationApplier.LoadMode.Merge);

        Assert.Equal("ABCD",            System.Text.Encoding.ASCII.GetString(node.GetIdentifier(0xB5)!));
        Assert.Equal("20260516",        System.Text.Encoding.ASCII.GetString(node.GetIdentifier(0x99)!));
        Assert.Equal("TRACE-STAMP-1234",System.Text.Encoding.ASCII.GetString(node.GetIdentifier(0xB4)!));
        Assert.Contains("$B5 Broadcast Code",         outcome.Applied);
        Assert.Contains("$99 Programming Date",       outcome.Applied);
        Assert.Contains("$B4 Mfg Traceability Chars", outcome.Applied);
    }

    [Fact]
    public void Walks_synthetic_segment_derived_dids_from_result_dids_list()
    {
        // The parser can synthesise SegmentDerived DIDs (VIN, $28 partial VIN,
        // $C0 cal PN, etc) by promoting structural fields to first-class
        // entries in result.Dids. The Applier should walk those uniformly
        // alongside any FlashUInt32BE entries from the $1A wire path.
        var node = NodeFactory.CreateNode();
        var dids = new[]
        {
            new DidExtraction(0x90, DidSourceKind.SegmentDerived,
                FlashAddress: null,
                WireBytes: System.Text.Encoding.ASCII.GetBytes("6G1FK5EP6GL206970"),
                DecodedValue: "6G1FK5EP6GL206970"),
            new DidExtraction(0x28, DidSourceKind.SegmentDerived,
                FlashAddress: null,
                WireBytes: System.Text.Encoding.ASCII.GetBytes("206970"),
                DecodedValue: "206970"),
            new DidExtraction(0xC0, DidSourceKind.SegmentDerived,
                FlashAddress: null,
                WireBytes: System.Text.Encoding.ASCII.GetBytes("24265053"),
                DecodedValue: "24265053"),
        };
        var result = new BinIdentification(
            Family: "T43",
            ServiceDispatcherOffset: 0,
            Service1AHandlerOffset: 0,
            DidDispatcherOffset: 0,
            SupportedSids: Array.Empty<byte>(),
            Dids: dids,
            Vin: null, SupplierHardwareNumber: null, SupplierHardwareVersion: null,
            EndModelPartNumber: null, BaseModelPartNumber: null,
            CalibrationPartNumber: null, BroadcastCode: null, ProgrammingDate: null,
            ProgrammingTool: null, TraceCode: null,
            Warnings: Array.Empty<string>());

        var outcome = BinIdentificationApplier.Apply(node, result, BinIdentificationApplier.LoadMode.Merge);

        Assert.Equal("6G1FK5EP6GL206970", System.Text.Encoding.ASCII.GetString(node.GetIdentifier(0x90)!));
        Assert.Equal("206970",            System.Text.Encoding.ASCII.GetString(node.GetIdentifier(0x28)!));
        Assert.Equal("24265053",          System.Text.Encoding.ASCII.GetString(node.GetIdentifier(0xC0)!));
        Assert.Contains("$90 VIN",                       outcome.Applied);
        Assert.Contains("$28 Partial VIN",               outcome.Applied);
        Assert.Contains("$C0 Operating Software ID",     outcome.Applied);
    }

    [Fact]
    public void Top_level_field_does_not_double_write_when_did_already_in_dids_list()
    {
        // Regression guard: in the field-and-Dids overlap case (the parser's
        // SynthesiseSegmentDerivedDids path puts $90 into Dids AND leaves
        // result.Vin populated), the Applier must not log $90 twice.
        var node = NodeFactory.CreateNode();
        var dids = new[]
        {
            new DidExtraction(0x90, DidSourceKind.SegmentDerived,
                FlashAddress: null,
                WireBytes: System.Text.Encoding.ASCII.GetBytes("6G1FK5EP6GL206970"),
                DecodedValue: "6G1FK5EP6GL206970"),
        };
        var result = new BinIdentification(
            Family: "T43", 0, 0, 0,
            SupportedSids: Array.Empty<byte>(),
            Dids: dids,
            Vin: "6G1FK5EP6GL206970",
            SupplierHardwareNumber: null, SupplierHardwareVersion: null,
            EndModelPartNumber: null, BaseModelPartNumber: null,
            CalibrationPartNumber: null, BroadcastCode: null, ProgrammingDate: null,
            ProgrammingTool: null, TraceCode: null,
            Warnings: Array.Empty<string>());

        var outcome = BinIdentificationApplier.Apply(node, result, BinIdentificationApplier.LoadMode.Merge);

        Assert.Equal(1, outcome.Applied.Count(l => l == "$90 VIN"));
    }
}
