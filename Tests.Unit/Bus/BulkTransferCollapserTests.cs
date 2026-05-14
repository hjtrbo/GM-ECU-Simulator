using Core.Bus;
using Xunit;

namespace EcuSimulator.Tests.Bus;

// State-machine coverage for the bulk-transfer collapser. Each test
// constructs a synthetic frame sequence, runs it through Process, and
// inspects which lines came out.
public sealed class BulkTransferCollapserTests
{
    // Helper: feed (canId, payload, label) tuples through the collapser and
    // collect the emitted pretty lines (the csv column is not inspected here;
    // tests that care about the marker text check the pretty value).
    private static List<string> Run(BulkTransferCollapser c, IEnumerable<(uint CanId, byte[] Payload, string Label)> frames, uint chId = 1)
    {
        var output = new List<string>();
        foreach (var (canId, payload, label) in frames)
            c.Process(chId, canId, payload, label, label, (pretty, _) => output.Add(pretty));
        return output;
    }

    // Build a CF frame with the given sequence nibble and 7 data bytes (filler).
    private static byte[] Cf(int seq) =>
        new byte[] { (byte)(0x20 | (seq & 0x0F)), 0, 0, 0, 0, 0, 0, 0 };

    // FF whose total length forces ceil((len - 6)/7) CFs.
    private static byte[] Ff(int totalLen) =>
        new byte[] { (byte)(0x10 | ((totalLen >> 8) & 0x0F)), (byte)(totalLen & 0xFF), 0, 0, 0, 0, 0, 0 };

    [Fact]
    public void ShortTransferBelowThreshold_PassesThroughUnchanged()
    {
        // FF of 50 bytes → ceil(44/7) = 7 CFs, below threshold of 10.
        var c = new BulkTransferCollapser();
        var frames = new List<(uint, byte[], string)>
        {
            (0x7E2u, Ff(50), "FF"),
        };
        for (int i = 1; i <= 7; i++)
            frames.Add((0x7E2u, Cf(i), $"CF#{i}"));

        var output = Run(c, frames);
        Assert.Equal(8, output.Count);   // 1 FF + 7 CFs
        Assert.Equal("FF", output[0]);
        Assert.Equal("CF#1", output[1]);
        Assert.Equal("CF#7", output[7]);
    }

    [Fact]
    public void LongTransfer_KeepsHeadTail_ReplacesMiddle()
    {
        // FF of 3110 bytes → ceil(3104/7) = 444 CFs. Far above threshold.
        var c = new BulkTransferCollapser();
        var frames = new List<(uint, byte[], string)>
        {
            (0x7E2u, Ff(3110), "FF"),
        };
        for (int i = 1; i <= 444; i++)
            frames.Add((0x7E2u, Cf(i & 0x0F), $"CF#{i}"));

        var output = Run(c, frames);
        // Expected: FF + 3 head CFs + 1 marker + 3 tail CFs = 8 lines.
        Assert.Equal(8, output.Count);
        Assert.Equal("FF", output[0]);
        Assert.Equal("CF#1", output[1]);
        Assert.Equal("CF#2", output[2]);
        Assert.Equal("CF#3", output[3]);
        Assert.Contains("bulk transfer collapsed", output[4]);
        Assert.Contains("438 frames hidden", output[4]);   // 444 - 3 - 3 = 438
        Assert.Equal("CF#442", output[5]);
        Assert.Equal("CF#443", output[6]);
        Assert.Equal("CF#444", output[7]);
    }

    [Fact]
    public void NoisyTraffic_DuringCollapseWindow_AlsoSuppressed()
    {
        // FF + 3 head CFs + (TP, FC, CF) repeating, ending with tail CFs.
        // The TP and FC in the suppressed middle should be hidden too.
        var c = new BulkTransferCollapser();
        var frames = new List<(uint, byte[], string)>
        {
            (0x7E2u, Ff(3110), "FF"),
        };
        for (int i = 1; i <= 3; i++)
            frames.Add((0x7E2u, Cf(i & 0x0F), $"CF#{i}"));

        // Middle: alternate (TesterPresent functional, FC from $7EA, CF from $7E2)
        // CFs 4..441 are middle. Insert noise every few CFs.
        for (int i = 4; i <= 441; i++)
        {
            if (i % 20 == 0)
            {
                frames.Add((0x101u, new byte[] { 0xFE, 0x01, 0x3E, 0, 0, 0, 0, 0 }, $"TP@{i}"));
                frames.Add((0x7EAu, new byte[] { 0x30, 0x01, 0x00, 0, 0, 0, 0, 0 }, $"FC@{i}"));
            }
            frames.Add((0x7E2u, Cf(i & 0x0F), $"CF#{i}"));
        }
        for (int i = 442; i <= 444; i++)
            frames.Add((0x7E2u, Cf(i & 0x0F), $"CF#{i}"));

        var output = Run(c, frames);
        // Should still be FF + 3 head + marker + 3 tail = 8 lines. The TP
        // and FC noise in the middle must not leak through.
        Assert.Equal(8, output.Count);
        Assert.DoesNotContain(output, l => l.StartsWith("TP@"));
        Assert.DoesNotContain(output, l => l.StartsWith("FC@"));
        // Marker count includes the suppressed CFs + TPs + FCs.
        // 438 middle CFs + 22 TPs (i=20,40,...,440 = 22 values) + 22 FCs = 482.
        var marker = output[4];
        Assert.Contains("bulk transfer collapsed", marker);
        Assert.Contains("482 frames hidden", marker);
    }

    [Fact]
    public void HeadWindowTrafficPassesThrough()
    {
        // FC and TP between FF and first CF should be visible.
        var c = new BulkTransferCollapser();
        var frames = new List<(uint, byte[], string)>
        {
            (0x7E2u, Ff(3110), "FF"),
            (0x7EAu, new byte[] { 0x30, 0x01, 0x00, 0, 0, 0, 0, 0 }, "FC-after-FF"),
            (0x101u, new byte[] { 0xFE, 0x01, 0x3E, 0, 0, 0, 0, 0 }, "TP-during-head"),
            (0x7E2u, Cf(1), "CF#1"),
            (0x7E2u, Cf(2), "CF#2"),
            (0x7E2u, Cf(3), "CF#3"),
        };
        for (int i = 4; i <= 444; i++)
            frames.Add((0x7E2u, Cf(i & 0x0F), $"CF#{i}"));

        var output = Run(c, frames);
        // FF + FC + TP + 3 head CFs + marker + 3 tail CFs = 10 lines.
        Assert.Equal("FF",              output[0]);
        Assert.Equal("FC-after-FF",     output[1]);
        Assert.Equal("TP-during-head",  output[2]);
        Assert.Equal("CF#1",            output[3]);
        Assert.Equal("CF#2",            output[4]);
        Assert.Equal("CF#3",            output[5]);
        Assert.Contains("bulk transfer collapsed", output[6]);
        Assert.Equal("CF#442",          output[7]);
        Assert.Equal("CF#443",          output[8]);
        Assert.Equal("CF#444",          output[9]);
    }

    [Fact]
    public void FunctionalBroadcastFf_DoesNotArmCollapser()
    {
        // FF on $101 (functional). Even with a huge declared length, we
        // shouldn't arm - nobody actually multi-frames to all-nodes.
        var c = new BulkTransferCollapser();
        var frames = new List<(uint, byte[], string)>
        {
            // $101 + $FE (all-nodes) + FF nibble
            (0x101u, new byte[] { 0xFE, 0x1C, 0x26, 0, 0, 0, 0, 0 }, "FF-functional"),
            (0x7E2u, Cf(1), "CF-on-physical"),
        };
        var output = Run(c, frames);
        Assert.Equal(2, output.Count);
        Assert.Equal("FF-functional",     output[0]);
        Assert.Equal("CF-on-physical",    output[1]);
    }

    [Fact]
    public void SequentialFfs_EachArmedIndependently()
    {
        // Two long transfers back-to-back. Each should collapse independently.
        var c = new BulkTransferCollapser();
        var frames = new List<(uint, byte[], string)>();
        frames.Add((0x7E2u, Ff(100), "FF-1"));   // 14 CFs
        for (int i = 1; i <= 14; i++)
            frames.Add((0x7E2u, Cf(i & 0x0F), $"A-CF#{i}"));
        frames.Add((0x7E2u, Ff(100), "FF-2"));
        for (int i = 1; i <= 14; i++)
            frames.Add((0x7E2u, Cf(i & 0x0F), $"B-CF#{i}"));

        var output = Run(c, frames);
        // Per transfer: FF + 3 head + marker + 3 tail = 8 lines. Two transfers = 16.
        Assert.Equal(16, output.Count);
        Assert.Equal("FF-1",   output[0]);
        Assert.Equal("A-CF#1", output[1]);
        Assert.Contains("hidden", output[4]);
        Assert.Equal("A-CF#14", output[7]);
        Assert.Equal("FF-2",   output[8]);
        Assert.Equal("B-CF#1", output[9]);
        Assert.Equal("B-CF#14", output[15]);
    }

    [Fact]
    public void InterruptingSfOnSource_CancelsCollapse()
    {
        // FF arms, head CFs go through, then host suddenly sends an SF
        // (e.g. a NRC or a new request) on the same CAN ID. Collapse should
        // cancel - subsequent traffic logs normally.
        var c = new BulkTransferCollapser();
        var frames = new List<(uint, byte[], string)>
        {
            (0x7E2u, Ff(3110), "FF"),
            (0x7E2u, Cf(1), "CF#1"),
            (0x7E2u, Cf(2), "CF#2"),
            (0x7E2u, Cf(3), "CF#3"),
            (0x7E2u, new byte[] { 0x02, 0x10, 0x02, 0, 0, 0, 0, 0 }, "SF-on-source"),
            // After the SF, the channel is in idle mode. A few more CFs
            // arrive (continuation? bug? doesn't matter) and they should
            // pass through unmolested.
            (0x7E2u, Cf(4), "CF-post-interrupt-1"),
            (0x7E2u, Cf(5), "CF-post-interrupt-2"),
        };
        var output = Run(c, frames);
        Assert.Equal("FF",                       output[0]);
        Assert.Equal("CF#1",                     output[1]);
        Assert.Equal("CF#2",                     output[2]);
        Assert.Equal("CF#3",                     output[3]);
        Assert.Equal("SF-on-source",             output[4]);
        Assert.Equal("CF-post-interrupt-1",      output[5]);
        Assert.Equal("CF-post-interrupt-2",      output[6]);
        Assert.Equal(7, output.Count);
    }

    [Fact]
    public void Reset_ClearsInFlightState()
    {
        var c = new BulkTransferCollapser();
        var frames1 = new List<(uint, byte[], string)>
        {
            (0x7E2u, Ff(3110), "FF"),
            (0x7E2u, Cf(1), "CF#1"),
            (0x7E2u, Cf(2), "CF#2"),
            (0x7E2u, Cf(3), "CF#3"),
        };
        var output1 = Run(c, frames1);
        Assert.Equal(4, output1.Count);

        // Reset. Now the next CFs should NOT be suppressed - state is gone.
        c.Reset();
        var frames2 = new List<(uint, byte[], string)>
        {
            (0x7E2u, Cf(4), "CF#4-after-reset"),
            (0x7E2u, Cf(5), "CF#5-after-reset"),
        };
        var output2 = Run(c, frames2);
        Assert.Equal(2, output2.Count);
        Assert.Equal("CF#4-after-reset", output2[0]);
        Assert.Equal("CF#5-after-reset", output2[1]);
    }

    [Fact]
    public void SeparateChannels_HaveIndependentState()
    {
        // Channel 1 starts a long transfer. Channel 2 receives normal traffic.
        // Channel 2's traffic should pass through regardless of channel 1's state.
        var c = new BulkTransferCollapser();
        var output = new List<string>();
        c.Process(1, 0x7E2, Ff(3110), "ch1-FF",   "ch1-FF",   (p, _) => output.Add(p));
        c.Process(1, 0x7E2, Cf(1),    "ch1-CF#1", "ch1-CF#1", (p, _) => output.Add(p));
        c.Process(2, 0x7E2, new byte[] { 0x02, 0x3E, 0x00, 0, 0, 0, 0, 0 }, "ch2-SF", "ch2-SF", (p, _) => output.Add(p));
        c.Process(1, 0x7E2, Cf(2),    "ch1-CF#2", "ch1-CF#2", (p, _) => output.Add(p));

        Assert.Contains("ch1-FF",   output);
        Assert.Contains("ch1-CF#1", output);
        Assert.Contains("ch2-SF",   output);
        Assert.Contains("ch1-CF#2", output);
    }
}
