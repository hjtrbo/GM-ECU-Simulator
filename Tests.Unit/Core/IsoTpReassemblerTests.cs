using Core.Transport;

namespace EcuSimulator.Tests.Core;

public class IsoTpReassemblerTests
{
    [Fact]
    public void SingleFrame_ReturnedImmediately()
    {
        var r = new IsoTpReassembler();
        var frame = new byte[] { 0x03, 0x22, 0x12, 0x34 };          // SF len 3
        var assembled = r.Feed(frame, emitFc: null);
        Assert.NotNull(assembled);
        Assert.Equal(new byte[] { 0x22, 0x12, 0x34 }, assembled);
    }

    [Fact]
    public void FirstFrame_TriggersFlowControlAndBuffers()
    {
        var r = new IsoTpReassembler();
        bool fcSent = false;
        byte fcBs = 0xFF, fcSt = 0xFF;
        var ff = new byte[] { 0x10, 0x09, 0x62, 0x12, 0x34, 0x0A, 0x71, 0x56 };
        var partial = r.Feed(ff, (bs, st) => { fcSent = true; fcBs = bs; fcSt = st; });
        Assert.Null(partial);
        Assert.True(fcSent);
        Assert.Equal(0, fcBs);
        Assert.Equal(0, fcSt);
    }

    [Fact]
    public void FirstFrame_PlusConsecutive_AssemblesFullPayload()
    {
        var r = new IsoTpReassembler();
        // Total length 9 bytes: $62 0x1234 = 0xAA71  0x5678 = 0x7303 (synthetic)
        var ff = new byte[] { 0x10, 0x09, 0x62, 0x12, 0x34, 0x0A, 0x71, 0x56 };
        var cf = new byte[] { 0x21, 0x78, 0x73, 0x03 };
        Assert.Null(r.Feed(ff, (_, _) => { }));
        var done = r.Feed(cf, null);
        Assert.NotNull(done);
        Assert.Equal(new byte[] { 0x62, 0x12, 0x34, 0x0A, 0x71, 0x56, 0x78, 0x73, 0x03 }, done);
    }

    [Fact]
    public void OutOfOrderConsecutive_ResetsAndDiscards()
    {
        var r = new IsoTpReassembler();
        var ff = new byte[] { 0x10, 0x09, 0x62, 0x12, 0x34, 0x0A, 0x71, 0x56 };
        var cfWrongSeq = new byte[] { 0x22, 0x78, 0x73, 0x03 };       // expected seq 1, got seq 2
        r.Feed(ff, (_, _) => { });
        var got = r.Feed(cfWrongSeq, null);
        Assert.Null(got);
        // After reset, a new SF should still work.
        var done = r.Feed(new byte[] { 0x02, 0x6C, 0xFE }, null);
        Assert.Equal(new byte[] { 0x6C, 0xFE }, done);
    }

    [Fact]
    public void StrayConsecutive_WithoutFirstFrame_IsIgnored()
    {
        var r = new IsoTpReassembler();
        var stray = new byte[] { 0x21, 0xAA, 0xBB };
        Assert.Null(r.Feed(stray, null));
    }
}
