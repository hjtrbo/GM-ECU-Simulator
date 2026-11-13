using Core.Bus;
using Core.Dps;
using Xunit;

namespace EcuSimulator.Tests.Dps;

// End-to-end fixture: parse + scan + compose the real 2011 Silverado E38
// archive into an EcuNode. These tests assume the archive lives at the
// canonical C:\DpsArch path; skip cleanly if absent so a CI box without
// the fixture does not turn red.
public sealed class ArchivePrimerTests
{
    private const string FixtureArchive = @"C:\DpsArch\E38_1GCRKSE36BZ158034.zip";

    private static bool FixturePresent => File.Exists(FixtureArchive);

    [Fact]
    public void Prime_RealE38Archive_BuildsDatasetWithExpectedShape()
    {
        if (!FixturePresent) return;

        var dataset = ArchivePrimer.Prime(FixtureArchive);

        Assert.Equal(121, dataset.UtilityFile.Instructions.Count);
        Assert.Equal(12, dataset.UtilityFile.Routines.Count);
        Assert.Equal(12, dataset.ExpectedValues.Count);
        Assert.InRange(dataset.ExpectedRequests.Count, 20, 121);

        // Family and cipher cannot be inferred from a utility selector alone.
        Assert.Null(dataset.Report.Family);
        Assert.Equal(UtilitySecuritySelection.UnresolvedModuleId, dataset.Report.SecurityModuleId);
        Assert.NotNull(dataset.Report.SecuritySelection);

        Assert.InRange(dataset.KnownPids.Count, 530, 540);

        // Phase 3 manifest is populated from bytecode literals + Empty
        // placeholders. No bin walker contributes anymore.
        Assert.NotEmpty(dataset.Phase3.Rows);
    }

    [Fact]
    public void Prime_RealE38Archive_VinFromFilename()
    {
        if (!FixturePresent) return;

        var dataset = ArchivePrimer.Prime(FixtureArchive);

        Assert.Equal("1GCRKSE36BZ158034", dataset.Report.Vin);
        Assert.Equal(VinSource.ArchiveFilename, dataset.Report.VinSource);
    }

    [Fact]
    public void BuildEcuNode_VinFromFilenameLandsOnDid90()
    {
        if (!FixturePresent) return;

        var dataset = ArchivePrimer.Prime(FixtureArchive);
        var node = ArchivePrimer.BuildEcuNode(dataset);

        Assert.Equal(0x7E0, node.PhysicalRequestCanId);
        Assert.Equal(0x7E8, node.UsdtResponseCanId);
        Assert.Equal(0x5E8, node.UudtResponseCanId);
        Assert.Equal(0x11, node.DiagnosticAddress);
        Assert.Equal(0x00, node.ProgrammedState);
        Assert.NotNull(node.SecurityModule);

        var vinBytes = node.GetIdentifier(0x90);
        Assert.NotNull(vinBytes);
        Assert.Equal("1GCRKSE36BZ158034", System.Text.Encoding.ASCII.GetString(vinBytes!));

        var allPids = node.AllPids.ToList();
        Assert.NotEmpty(allPids);
        Assert.Contains(allPids, p => p.Address == 0x155B);
    }

    [Fact]
    public void ApplyTo_AddsNodeToBus_AndReplacesPriorEcuOnDefaultId()
    {
        if (!FixturePresent) return;

        var bus = new VirtualBus();
        var (node, dataset) = ArchivePrimer.ApplyTo(bus, FixtureArchive);

        Assert.NotNull(dataset);
        Assert.Same(node, bus.FindByRequestId(0x7E0));

        var (node2, _) = ArchivePrimer.ApplyTo(bus, FixtureArchive);
        Assert.NotSame(node, node2);
        Assert.Same(node2, bus.FindByRequestId(0x7E0));
    }

    [Fact]
    public void ParseArchive_ReturnsDescriptorWithMetadata()
    {
        if (!FixturePresent) return;

        var d = ArchivePrimer.ParseArchive(FixtureArchive);
        Assert.Equal(FixtureArchive, d.ArchivePath);
        Assert.False(string.IsNullOrEmpty(d.UtilityFileName));
        Assert.True(d.CalibrationBlockCount > 0);
        Assert.NotNull(d.OsPartNumber);
        Assert.Equal(8, d.OsPartNumber!.Length);
    }
}
