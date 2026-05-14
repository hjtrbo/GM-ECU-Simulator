using Common.IsoTp;
using Common.PassThru;
using Core.Bus;
using Core.Transport;
using Xunit;

namespace EcuSimulator.Tests.IsoTp;

// FC-aware Core/Transport/IsoTpFragmenter (per-EcuNode sender). Tests drive
// the fragmenter directly with a stand-in ChannelSession that captures the
// emitted CAN frames in its RxQueue.
public class IsoTpFragmenterTests
{
    private static ChannelSession MakeChannel() => new()
    {
        Id = 1,
        Protocol = ProtocolID.CAN,    // raw CAN so EnqueueRx pushes to RxQueue (no IsoChannel routing)
        Baud = 500_000,
    };

    private const uint Resp = 0x7E8;

    private static byte[] FrameDataField(PassThruMsg msg) => msg.Data.AsSpan(4).ToArray();

    private static bool TryDequeue(ChannelSession ch, out PassThruMsg msg)
        => ch.RxQueue.TryDequeue(out msg!);

    // ------------------------------------------------------------------------
    // SF: short payload sends as SF immediately, no FC handshake required
    // ------------------------------------------------------------------------

    [Fact]
    public void Short_payload_sends_SF_immediately()
    {
        var ch = MakeChannel();
        var f = new IsoTpFragmenter();

        f.EnqueueResponse(ch, Resp, new byte[] { 0x62, 0xF1, 0x90 });

        Assert.True(TryDequeue(ch, out var msg));
        Assert.Equal(new byte[] { 0x03, 0x62, 0xF1, 0x90 }, FrameDataField(msg));
        Assert.False(TryDequeue(ch, out _));
        Assert.False(f.InProgress);
        Assert.Equal(NResult.N_OK, f.LastResult);
    }

    // ------------------------------------------------------------------------
    // FF + waits for FC; CFs only flow once FC.CTS arrives
    // ------------------------------------------------------------------------

    [Fact]
    public void Long_payload_sends_FF_only_until_FC_arrives()
    {
        var ch = MakeChannel();
        var f = new IsoTpFragmenter();
        var payload = Enumerable.Range(1, 20).Select(i => (byte)i).ToArray();

        f.EnqueueResponse(ch, Resp, payload);

        // FF only, no CFs yet.
        Assert.True(TryDequeue(ch, out var ff));
        Assert.Equal(0x10, ff.Data[4]);
        Assert.Equal(20, ff.Data[5]);
        Assert.False(TryDequeue(ch, out _));
        Assert.True(f.InProgress);

        // Inbound FC.CTS, BS=0 (no further FC), STmin=0 (no pacing).
        f.OnFlowControl(FlowStatus.ContinueToSend, 0, 0);

        Assert.True(TryDequeue(ch, out var cf1));
        Assert.Equal(0x21, cf1.Data[4]);
        Assert.True(TryDequeue(ch, out var cf2));
        Assert.Equal(0x22, cf2.Data[4]);
        Assert.False(TryDequeue(ch, out _));
        Assert.False(f.InProgress);
        Assert.Equal(NResult.N_OK, f.LastResult);
    }

    // ------------------------------------------------------------------------
    // BS > 0 pauses at block boundary; next FC drains the next block
    // ------------------------------------------------------------------------

    [Fact]
    public void BS_2_pauses_at_block_boundary_and_resumes_on_next_FC()
    {
        var ch = MakeChannel();
        var f = new IsoTpFragmenter();
        // 22 bytes -> FF=6, CF1=7, CF2=7, CF3=2 (3 CFs total)
        var payload = Enumerable.Range(1, 22).Select(i => (byte)i).ToArray();

        f.EnqueueResponse(ch, Resp, payload);
        TryDequeue(ch, out _);     // FF

        f.OnFlowControl(FlowStatus.ContinueToSend, 2, 0);

        Assert.True(TryDequeue(ch, out var cf1));
        Assert.Equal(0x21, cf1.Data[4]);
        Assert.True(TryDequeue(ch, out var cf2));
        Assert.Equal(0x22, cf2.Data[4]);
        Assert.False(TryDequeue(ch, out _));     // BS=2 reached, awaiting next FC
        Assert.True(f.InProgress);

        f.OnFlowControl(FlowStatus.ContinueToSend, 2, 0);

        Assert.True(TryDequeue(ch, out var cf3));
        Assert.Equal(0x23, cf3.Data[4]);
        Assert.False(f.InProgress);
        Assert.Equal(NResult.N_OK, f.LastResult);
    }

    // ------------------------------------------------------------------------
    // FC.WAIT keeps the sender waiting; a subsequent CTS resumes
    // ------------------------------------------------------------------------

    [Fact]
    public void FC_WAIT_then_CTS_resumes_transmission()
    {
        var ch = MakeChannel();
        var f = new IsoTpFragmenter();

        f.EnqueueResponse(ch, Resp, new byte[20]);
        TryDequeue(ch, out _);     // FF

        f.OnFlowControl(FlowStatus.Wait, 0, 0);
        Assert.False(TryDequeue(ch, out _));
        Assert.True(f.InProgress);

        f.OnFlowControl(FlowStatus.ContinueToSend, 0, 0);

        Assert.True(TryDequeue(ch, out var cf1));
        Assert.Equal(0x21, cf1.Data[4]);
        Assert.True(TryDequeue(ch, out var cf2));
        Assert.Equal(0x22, cf2.Data[4]);
        Assert.False(f.InProgress);
    }

    // ------------------------------------------------------------------------
    // FC.OVFLW aborts with N_BUFFER_OVFLW
    // ------------------------------------------------------------------------

    [Fact]
    public void FC_OVFLW_aborts_with_N_BUFFER_OVFLW()
    {
        var ch = MakeChannel();
        var f = new IsoTpFragmenter();
        f.EnqueueResponse(ch, Resp, new byte[100]);
        TryDequeue(ch, out _);     // FF

        f.OnFlowControl(FlowStatus.Overflow, 0, 0);

        Assert.False(TryDequeue(ch, out _));
        Assert.False(f.InProgress);
        Assert.Equal(NResult.N_BUFFER_OVFLW, f.LastResult);
    }

    // ------------------------------------------------------------------------
    // Reserved FlowStatus aborts with N_INVALID_FS (§9.6.5.2)
    // ------------------------------------------------------------------------

    [Theory]
    [InlineData(0x3)]
    [InlineData(0xF)]
    public void Reserved_FlowStatus_aborts_with_N_INVALID_FS(byte reservedFs)
    {
        var ch = MakeChannel();
        var f = new IsoTpFragmenter();
        f.EnqueueResponse(ch, Resp, new byte[20]);
        TryDequeue(ch, out _);

        f.OnFlowControl((FlowStatus)reservedFs, 0, 0);

        Assert.Equal(NResult.N_INVALID_FS, f.LastResult);
        Assert.False(f.InProgress);
    }

    // ------------------------------------------------------------------------
    // N_Bs timeout: with no inbound FC, fragmenter aborts after the configured wait
    // ------------------------------------------------------------------------

    [Fact]
    public void N_Bs_timeout_aborts_with_N_TIMEOUT_Bs_after_configured_delay()
    {
        var ch = MakeChannel();
        var f = new IsoTpFragmenter(new IsoTpTimingParameters { NBsMs = 50 });

        f.EnqueueResponse(ch, Resp, new byte[20]);
        TryDequeue(ch, out _);     // FF
        Assert.True(f.InProgress);

        // Wait long enough for the N_Bs timer to fire; the simulator's
        // TimerOnDelay polling thread runs at ThreadPriority.Highest so jitter
        // is small but not zero - 200 ms is comfortably above the 50 ms budget.
        var deadline = DateTime.UtcNow.AddMilliseconds(500);
        while (f.InProgress && DateTime.UtcNow < deadline) Thread.Sleep(10);

        Assert.False(f.InProgress);
        Assert.Equal(NResult.N_TIMEOUT_Bs, f.LastResult);
    }

    // ------------------------------------------------------------------------
    // STmin > 0 paces CFs - first CF lands immediately, subsequent CFs need delay
    // ------------------------------------------------------------------------

    [Fact]
    public void STmin_paces_consecutive_frames()
    {
        var ch = MakeChannel();
        var f = new IsoTpFragmenter();
        // 22-byte payload -> FF + 3 CFs (SN 1..3); BS=0 means all sent without further FC.
        var payload = Enumerable.Range(1, 22).Select(i => (byte)i).ToArray();
        f.EnqueueResponse(ch, Resp, payload);
        TryDequeue(ch, out _);     // FF

        // STmin = 50 ms - enough to be observable on any scheduler but not slow.
        f.OnFlowControl(FlowStatus.ContinueToSend, 0, 50);

        // First CF emitted immediately; subsequent CFs paced by STmin.
        Assert.True(TryDequeue(ch, out var cf1));
        Assert.Equal(0x21, cf1.Data[4]);
        Assert.False(TryDequeue(ch, out _));     // CF2 not yet (STmin pending)

        // Wait for both CFs to drain (50ms + 50ms + slack).
        var deadline = DateTime.UtcNow.AddMilliseconds(500);
        while (f.InProgress && DateTime.UtcNow < deadline) Thread.Sleep(10);

        Assert.True(TryDequeue(ch, out var cf2));
        Assert.Equal(0x22, cf2.Data[4]);
        Assert.True(TryDequeue(ch, out var cf3));
        Assert.Equal(0x23, cf3.Data[4]);
        Assert.False(f.InProgress);
        Assert.Equal(NResult.N_OK, f.LastResult);
    }

    // ------------------------------------------------------------------------
    // Stray FC outside an in-flight transmit is silently ignored
    // ------------------------------------------------------------------------

    [Fact]
    public void Stray_FC_when_idle_is_ignored()
    {
        var ch = MakeChannel();
        var f = new IsoTpFragmenter();

        f.OnFlowControl(FlowStatus.ContinueToSend, 0, 0);
        Assert.False(f.InProgress);
        Assert.False(TryDequeue(ch, out _));
    }

    // ------------------------------------------------------------------------
    // Empty payload: cannot send (§9.6.2.1: no SF_DL = 0)
    // ------------------------------------------------------------------------

    [Fact]
    public void Empty_payload_throws()
    {
        var ch = MakeChannel();
        var f = new IsoTpFragmenter();
        Assert.Throws<ArgumentException>(() => f.EnqueueResponse(ch, Resp, ReadOnlySpan<byte>.Empty));
    }
}
