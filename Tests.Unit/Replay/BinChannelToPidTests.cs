using Core.Replay;

namespace EcuSimulator.Tests.Replay;

public class BinChannelToPidTests
{
    [Fact]
    public void GroupsChannelsByNodeType_OneEcuPerType()
    {
        var headers = new[]
        {
            new BinChannelHeader(1, "RPM",  "RPM", 0x000C, 2, 0x7E8, 2, 1.0,  0),
            new BinChannelHeader(2, "MAP",  "kPa", 0x000B, 1, 0x7E8, 2, 1.0,  0),
            new BinChannelHeader(3, "Gear", "",    0x000F, 1, 0x7E9, 2, 1.0,  0),
        };
        var c = new BinReplayCoordinator();
        c.Load(new FakeBinSource(new long[] { 0, 100 }, headers));

        var build = BinChannelToPid.BuildEcus(headers, c);
        Assert.Equal(2, build.Nodes.Count);
        Assert.Equal(0, build.SkippedChannels);

        var ecm = build.Nodes.Single(n => n.PhysicalRequestCanId == 0x7E0);
        Assert.Equal("ECM", ecm.Name);
        Assert.Equal(2, ecm.Pids.Count);
        Assert.NotNull(ecm.GetPid(0x000C));
        Assert.NotNull(ecm.GetPid(0x000B));

        var tcm = build.Nodes.Single(n => n.PhysicalRequestCanId == 0x7E1);
        Assert.Equal("TCM", tcm.Name);
        Assert.Single(tcm.Pids);
    }

    [Fact]
    public void NoneNodeType_SkippedWithCount()
    {
        var headers = new[]
        {
            new BinChannelHeader(1, "x", "u", 0x10, 2, 0x7E8, 2, 1.0, 0),
            new BinChannelHeader(2, "y", "u", 0x11, 2, 0,     2, 1.0, 0),  // unmapped
            new BinChannelHeader(3, "z", "u", 0x12, 2, 0,     2, 1.0, 0),  // unmapped
        };
        var c = new BinReplayCoordinator();
        c.Load(new FakeBinSource(new long[] { 0 }, headers));
        var build = BinChannelToPid.BuildEcus(headers, c);
        Assert.Single(build.Nodes);
        Assert.Equal(2, build.SkippedChannels);
    }

    [Fact]
    public void BuiltPid_SamplesThroughCoordinator()
    {
        var elapsed = new long[] { 0, 50, 100 };
        var headers = new[]
        {
            new BinChannelHeader(1, "RPM", "RPM", 0x000C, 2, 0x7E8, 2, 1.0, 0),
        };
        // Channel 0 row r returns 1234 + r so we can identify which row.
        var src = new FakeBinSource(elapsed, headers, (ch, row) => 1234 + row);
        var c = new BinReplayCoordinator();
        c.Load(src);
        c.MaybeStart(0);

        var build = BinChannelToPid.BuildEcus(headers, c);
        var pid = build.Nodes[0].GetPid(0x000C)!;

        // At busNow=50 -> row 1 -> 1235.
        Assert.Equal(1235.0, pid.Waveform.Sample(50));
        // At busNow=100 -> row 2 -> 1236.
        Assert.Equal(1236.0, pid.Waveform.Sample(100));
    }
}
