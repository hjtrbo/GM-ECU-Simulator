using Common.Protocol;
using Core.Dps;
using Core.Ecu;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Dps;

// Fixture-free regression tests for ArchivePrimer.ApplyPhase3Row - the manifest-row dispatch that turns a primed
// archive's Phase 3 reads into per-mode Pid rows. A $1A read MUST become a Mode1A Pid row (not just a raw identifier-
// dictionary entry): the editor's $1A grid, the $1A handler's preferred lookup, and File -> Save persistence all read
// from the Mode1A store, so storing only in the dict left primed $1A PIDs invisible in the editor. A $22 read must
// land in the Mode22 store. These pin both halves so the two modes can't silently swap stores again.
public sealed class ArchivePrimerPhase3RowTests
{
    private static Phase3Row Row(byte opCode, ushort didOrPid, byte[] value,
        Phase3RowSource source = Phase3RowSource.Bytecode)
        => new(StepNumber: 1, InstructionIndex: 0, OpCode: opCode, DidOrPid: didOrPid,
               HasCompareDownstream: false, Source: source, ExpectedLength: value.Length, ExpectedValue: value);

    [Fact]
    public void Mode1ARow_BecomesEditorVisibleMode1APidRow()
    {
        var node = NodeFactory.CreateNode();
        var value = new byte[] { 0xAA, 0xBB, 0xCC };

        ArchivePrimer.ApplyPhase3Row(node, Row(0x1A, 0xDF, value));

        // The store the $1A handler reads from carries the value.
        var pid = node.GetMode1APid(0xDF);
        Assert.NotNull(pid);
        Assert.Equal(PidMode.Mode1A, pid!.Mode);
        Assert.Equal(value, pid.StaticBytes);

        // And it is enumerated by AllPids - exactly what the editor's $1A section binds to.
        Assert.Contains(node.AllPids, p => p.Mode == PidMode.Mode1A && (p.Address & 0xFF) == 0xDF);

        // It must NOT leak into the $22 wire namespace (disjoint id spaces).
        Assert.Null(node.GetPidByWireId(0xDF));
    }

    [Fact]
    public void Mode22Row_BecomesMode22PidRow()
    {
        var node = NodeFactory.CreateNode();
        var value = new byte[] { 0x01, 0x02 };

        ArchivePrimer.ApplyPhase3Row(node, Row(0x22, 0x155B, value));

        var pid = node.GetPidByWireId(0x155B);
        Assert.NotNull(pid);
        Assert.Equal(PidMode.Mode22, pid!.Mode);
        Assert.Equal(value, pid.StaticBytes);

        // A $22 PID id is not a $1A DID; the Mode1A store stays empty.
        Assert.Null(node.GetMode1APid(0x5B));
    }

    [Fact]
    public void EmptySourceRow_IsSkipped()
    {
        var node = NodeFactory.CreateNode();

        ArchivePrimer.ApplyPhase3Row(node,
            Row(0x1A, 0xDF, new byte[] { 0x01 }, source: Phase3RowSource.Empty));

        Assert.Null(node.GetMode1APid(0xDF));
        Assert.Empty(node.AllPids);
    }

    [Fact]
    public void ZeroLengthRow_IsSkipped()
    {
        var node = NodeFactory.CreateNode();

        ArchivePrimer.ApplyPhase3Row(node, Row(0x1A, 0xDF, System.Array.Empty<byte>()));

        Assert.Null(node.GetMode1APid(0xDF));
        Assert.Empty(node.AllPids);
    }
}
