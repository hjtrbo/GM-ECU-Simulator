using Common.IsoTp;
using Xunit;

namespace EcuSimulator.Tests.IsoTp;

// ISO 15765-2:2016 RX state machine. Each test mirrors a clause or table.
public class IsoTpRxStateMachineTests
{
    private static IsoTpRxStateMachine Make(byte bs = 0, byte stMin = 0, int bufCap = 4096) =>
        new(new IsoTpTimingParameters { BlockSizeSend = bs, StMinSendRaw = stMin }, bufCap);

    // -----------------------------------------------------------------------
    // SF (single-frame) - happy path
    // -----------------------------------------------------------------------

    [Fact]
    public void SF_delivers_payload_immediately_no_FC()
    {
        var sm = Make();
        // SF with SF_DL=3, payload {22, F1, 90}
        var frame = new byte[] { 0x03, 0x22, 0xF1, 0x90 };

        var outcome = sm.Feed(frame, out var payload, out var fc, out var result);

        Assert.Equal(RxOutcome.MessageReady, outcome);
        Assert.Equal(new byte[] { 0x22, 0xF1, 0x90 }, payload);
        Assert.Null(fc);
        Assert.Equal(NResult.N_OK, result);
        Assert.False(sm.InProgress);
    }

    [Fact]
    public void SF_with_truncated_data_is_ignored_per_9_6_2_2()
    {
        var sm = Make();
        // SF claims SF_DL=5 but only 2 data bytes follow.
        var frame = new byte[] { 0x05, 0xAA, 0xBB };

        var outcome = sm.Feed(frame, out var payload, out _, out _);

        Assert.Equal(RxOutcome.Idle, outcome);
        Assert.Null(payload);
    }

    // -----------------------------------------------------------------------
    // FF + CF reassembly (BS = 0 -> all CFs without further FC)
    // -----------------------------------------------------------------------

    [Fact]
    public void FF_emits_FC_CTS_with_configured_BS_and_STmin()
    {
        var sm = Make(bs: 4, stMin: 0x0A);
        // FF FF_DL=20, first 6 data bytes
        var ff = new byte[] { 0x10, 0x14, 1, 2, 3, 4, 5, 6 };

        var outcome = sm.Feed(ff, out var payload, out var fc, out var result);

        Assert.Equal(RxOutcome.SendFlowControl, outcome);
        Assert.Null(payload);
        Assert.NotNull(fc);
        Assert.Equal(FlowStatus.ContinueToSend, fc!.Value.Status);
        Assert.Equal((byte)4, fc.Value.BlockSize);
        Assert.Equal((byte)0x0A, fc.Value.StMinRaw);
        Assert.Equal(NResult.N_OK, result);
        Assert.True(sm.InProgress);
    }

    [Fact]
    public void Full_reassembly_with_BS_zero_returns_payload_after_last_CF()
    {
        // 20-byte payload: FF carries 6, then CFs 1..3 carry 7+7+0 = nope.
        //   FF: 6 bytes
        //   CF1: 7 bytes (bytes 7..13)
        //   CF2: 7 bytes (bytes 14..20) - actually 14..19, last CF carries 6
        // We need 20 bytes total: FF=6, CF1=7, CF2=7 -> 20 total. CF2 is the last
        // and carries 7 of which all fit. Done.
        var sm = Make();
        sm.Feed(new byte[] { 0x10, 0x14, 1, 2, 3, 4, 5, 6 }, out _, out _, out _);
        sm.Feed(new byte[] { 0x21, 7, 8, 9, 10, 11, 12, 13 }, out _, out _, out _);

        var outcome = sm.Feed(
            new byte[] { 0x22, 14, 15, 16, 17, 18, 19, 20 },
            out var payload, out var fc, out var result);

        Assert.Equal(RxOutcome.MessageReady, outcome);
        Assert.Null(fc);
        Assert.Equal(NResult.N_OK, result);
        Assert.NotNull(payload);
        Assert.Equal(20, payload!.Length);
        for (int i = 0; i < 20; i++) Assert.Equal((byte)(i + 1), payload[i]);
        Assert.False(sm.InProgress);
    }

    // -----------------------------------------------------------------------
    // FF + CF reassembly with BS > 0
    // -----------------------------------------------------------------------

    [Fact]
    public void FC_re_emitted_after_BS_consecutive_frames()
    {
        // FF_DL=22; FF=6, CF1=7, CF2=7, CF3=2 (last)
        // BS=2 -> after CF1+CF2 we should see another FC.
        var sm = Make(bs: 2);
        sm.Feed(new byte[] { 0x10, 22, 1, 2, 3, 4, 5, 6 }, out _, out _, out _);
        // CF1 - no FC yet
        var o1 = sm.Feed(new byte[] { 0x21, 7, 8, 9, 10, 11, 12, 13 }, out _, out var fc1, out _);
        Assert.Equal(RxOutcome.Idle, o1);
        Assert.Null(fc1);

        // CF2 - block boundary; FC emitted
        var o2 = sm.Feed(new byte[] { 0x22, 14, 15, 16, 17, 18, 19, 20 }, out _, out var fc2, out _);
        Assert.Equal(RxOutcome.SendFlowControl, o2);
        Assert.NotNull(fc2);
        Assert.Equal(FlowStatus.ContinueToSend, fc2!.Value.Status);

        // CF3 - last, completes message
        var o3 = sm.Feed(new byte[] { 0x23, 21, 22, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC }, out var p3, out _, out _);
        Assert.Equal(RxOutcome.MessageReady, o3);
        Assert.Equal(22, p3!.Length);
    }

    // -----------------------------------------------------------------------
    // SequenceNumber wraparound (§9.6.4.3 Table 16)
    // -----------------------------------------------------------------------

    [Fact]
    public void SN_wraps_after_F_back_to_0()
    {
        // FF=6, CF1..CFn enough to cover SN 0xF then 0x0 etc.
        // To exercise wraparound we need >= 16 CFs. FF carries 6; each CF
        // carries 7. 16 CFs * 7 = 112 bytes plus the FF's 6 = 118 bytes.
        // FF_DL = 118.
        var sm = Make();
        var ff = new byte[8];
        ff[0] = 0x10;
        ff[1] = 118;
        for (int i = 0; i < 6; i++) ff[2 + i] = (byte)i;
        var outcome = sm.Feed(ff, out _, out _, out _);
        Assert.Equal(RxOutcome.SendFlowControl, outcome);

        int produced = 6;
        for (int seq = 1; produced < 118; seq = (seq + 1) & 0x0F)
        {
            int chunk = Math.Min(7, 118 - produced);
            var cf = new byte[1 + chunk];
            cf[0] = (byte)(0x20 | seq);
            for (int i = 0; i < chunk; i++) cf[1 + i] = (byte)(produced + i);
            produced += chunk;

            var o = sm.Feed(cf, out var p, out _, out _);
            if (produced < 118) Assert.NotEqual(RxOutcome.Error, o);
            else
            {
                Assert.Equal(RxOutcome.MessageReady, o);
                Assert.Equal(118, p!.Length);
            }
        }
    }

    // -----------------------------------------------------------------------
    // Wrong SN -> N_WRONG_SN (§9.6.4.4)
    // -----------------------------------------------------------------------

    [Fact]
    public void Wrong_SN_aborts_with_N_WRONG_SN()
    {
        var sm = Make();
        sm.Feed(new byte[] { 0x10, 20, 1, 2, 3, 4, 5, 6 }, out _, out _, out _);

        // Skip SN=1; send SN=2 directly. Receiver should abort.
        var outcome = sm.Feed(new byte[] { 0x22, 14, 15, 16, 17, 18, 19, 20 },
            out var payload, out var fc, out var result);

        Assert.Equal(RxOutcome.Error, outcome);
        Assert.Null(payload);
        Assert.Null(fc);
        Assert.Equal(NResult.N_WRONG_SN, result);
        Assert.False(sm.InProgress);
    }

    // -----------------------------------------------------------------------
    // FF_DL > buffer cap -> FC.OVFLW (§9.6.5.1)
    // -----------------------------------------------------------------------

    [Fact]
    public void FF_with_FF_DL_over_buffer_emits_OVFLW_and_does_not_start_reception()
    {
        var sm = Make(bufCap: 100);
        // FF_DL = 200 (> 100)
        var ff = new byte[] { 0x10, 0xC8, 1, 2, 3, 4, 5, 6 };

        var outcome = sm.Feed(ff, out var payload, out var fc, out var result);

        Assert.Equal(RxOutcome.SendFlowControl, outcome);
        Assert.Null(payload);
        Assert.NotNull(fc);
        Assert.Equal(FlowStatus.Overflow, fc!.Value.Status);
        Assert.Equal(NResult.N_OK, result);     // OVFLW is the FC, not an upper-layer error
        Assert.False(sm.InProgress);
    }

    // -----------------------------------------------------------------------
    // §9.8.3: unexpected SF/FF during reception
    // -----------------------------------------------------------------------

    [Fact]
    public void SF_during_reception_aborts_with_N_UNEXP_PDU_and_delivers_new_SF()
    {
        var sm = Make();
        sm.Feed(new byte[] { 0x10, 20, 1, 2, 3, 4, 5, 6 }, out _, out _, out _);

        var outcome = sm.Feed(new byte[] { 0x03, 0xAA, 0xBB, 0xCC },
            out var payload, out _, out var result);

        Assert.Equal(RxOutcome.Error, outcome);
        Assert.Equal(NResult.N_UNEXP_PDU, result);
        // §9.8.3 example: process the SF as the start of a new reception. Since SF is
        // self-contained, we expose its payload too so the upper layer doesn't lose it.
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, payload);
        Assert.False(sm.InProgress);
    }

    [Fact]
    public void FF_during_reception_aborts_with_N_UNEXP_PDU_and_starts_new_reception()
    {
        var sm = Make(bs: 4);
        sm.Feed(new byte[] { 0x10, 20, 1, 2, 3, 4, 5, 6 }, out _, out _, out _);

        var outcome = sm.Feed(new byte[] { 0x10, 30, 9, 9, 9, 9, 9, 9 },
            out _, out var fc, out var result);

        Assert.Equal(RxOutcome.Error, outcome);
        Assert.Equal(NResult.N_UNEXP_PDU, result);
        Assert.NotNull(fc);
        Assert.Equal(FlowStatus.ContinueToSend, fc!.Value.Status);
        Assert.Equal((byte)4, fc.Value.BlockSize);   // BS for the *new* reception
        Assert.True(sm.InProgress);
        Assert.Equal(30, sm.ExpectedTotal);
    }

    // -----------------------------------------------------------------------
    // §9.8.3: idle + CF / idle + FC -> ignore
    // -----------------------------------------------------------------------

    [Fact]
    public void Idle_state_ignores_stray_CF()
    {
        var sm = Make();
        var outcome = sm.Feed(new byte[] { 0x21, 1, 2, 3, 4, 5, 6, 7 }, out var p, out var fc, out var r);
        Assert.Equal(RxOutcome.Idle, outcome);
        Assert.Null(p);
        Assert.Null(fc);
        Assert.Equal(NResult.N_OK, r);
    }

    [Fact]
    public void Idle_state_ignores_stray_FC()
    {
        var sm = Make();
        var outcome = sm.Feed(new byte[] { 0x30, 0x00, 0x00 }, out var p, out var fc, out var r);
        Assert.Equal(RxOutcome.Idle, outcome);
        Assert.Null(p);
        Assert.Null(fc);
        Assert.Equal(NResult.N_OK, r);
    }

    [Fact]
    public void Reception_in_progress_ignores_FC()
    {
        var sm = Make();
        sm.Feed(new byte[] { 0x10, 20, 1, 2, 3, 4, 5, 6 }, out _, out _, out _);
        var outcome = sm.Feed(new byte[] { 0x30, 0x00, 0x00 }, out _, out _, out _);
        Assert.Equal(RxOutcome.Idle, outcome);
        Assert.True(sm.InProgress, "reception state must survive a stray FC");
    }

    // -----------------------------------------------------------------------
    // N_Cr timeout
    // -----------------------------------------------------------------------

    [Fact]
    public void OnNcrTimeout_aborts_with_N_TIMEOUT_Cr_when_in_progress()
    {
        var sm = Make();
        sm.Feed(new byte[] { 0x10, 20, 1, 2, 3, 4, 5, 6 }, out _, out _, out _);
        Assert.True(sm.InProgress);

        var result = sm.OnNcrTimeout();

        Assert.Equal(NResult.N_TIMEOUT_Cr, result);
        Assert.False(sm.InProgress);
    }

    [Fact]
    public void OnNcrTimeout_returns_null_when_idle()
    {
        var sm = Make();
        Assert.Null(sm.OnNcrTimeout());
    }

    // -----------------------------------------------------------------------
    // Escape FF with large FF_DL within buffer cap
    // -----------------------------------------------------------------------

    [Fact]
    public void Escape_FF_within_cap_starts_reception_normally()
    {
        // FF_DL = 5000 (escape; > 4095 so escape required).
        var sm = Make(bufCap: 8000);
        var ff = new byte[8];
        ff[0] = 0x10; ff[1] = 0x00;
        // 5000 = 0x00001388 BE
        ff[2] = 0x00; ff[3] = 0x00; ff[4] = 0x13; ff[5] = 0x88;
        // Escape FF carries 2 data bytes after the 6-byte PCI.
        ff[6] = 0xAA; ff[7] = 0xBB;

        var outcome = sm.Feed(ff, out _, out var fc, out _);

        Assert.Equal(RxOutcome.SendFlowControl, outcome);
        Assert.NotNull(fc);
        Assert.True(sm.InProgress);
        Assert.Equal(5000, sm.ExpectedTotal);
        Assert.Equal(2, sm.BytesReceived);
    }

    // -----------------------------------------------------------------------
    // Two SF messages back-to-back
    // -----------------------------------------------------------------------

    [Fact]
    public void Two_SF_messages_back_to_back_both_delivered()
    {
        var sm = Make();
        var o1 = sm.Feed(new byte[] { 0x02, 0x01, 0x02 }, out var p1, out _, out _);
        var o2 = sm.Feed(new byte[] { 0x03, 0x03, 0x04, 0x05 }, out var p2, out _, out _);

        Assert.Equal(RxOutcome.MessageReady, o1);
        Assert.Equal(new byte[] { 0x01, 0x02 }, p1);
        Assert.Equal(RxOutcome.MessageReady, o2);
        Assert.Equal(new byte[] { 0x03, 0x04, 0x05 }, p2);
    }
}
