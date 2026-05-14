using Common.IsoTp;
using Xunit;

namespace EcuSimulator.Tests.IsoTp;

// ISO 15765-2:2016 TX state machine tests.
public class IsoTpTxStateMachineTests
{
    private static IsoTpTxStateMachine Make(int dataField = 8) =>
        new(dataField, new IsoTpTimingParameters());

    // -----------------------------------------------------------------------
    // SF (small messages take the SingleFrame path)
    // -----------------------------------------------------------------------

    [Fact]
    public void Begin_with_short_payload_emits_SF_and_finishes()
    {
        var sm = Make();
        var r = sm.Begin(new byte[] { 0x22, 0xF1, 0x90 });

        Assert.Equal(NextStep.Done, r.Next);
        Assert.Equal(TxState.Done, sm.State);
        Assert.Equal(new byte[] { 0x03, 0x22, 0xF1, 0x90 }, r.FrameToSend);
    }

    [Fact]
    public void Begin_with_seven_byte_payload_still_fits_in_SF()
    {
        var sm = Make();
        var payload = new byte[] { 1, 2, 3, 4, 5, 6, 7 };
        var r = sm.Begin(payload);

        Assert.Equal(NextStep.Done, r.Next);
        Assert.Equal(8, r.FrameToSend.Length);
        Assert.Equal(0x07, r.FrameToSend[0]);
    }

    [Fact]
    public void Begin_with_empty_payload_throws()
        => Assert.Throws<ArgumentException>(() => Make().Begin(ReadOnlySpan<byte>.Empty));

    // -----------------------------------------------------------------------
    // FF + CF segmentation, FC.CTS with BS=0 (one block)
    // -----------------------------------------------------------------------

    [Fact]
    public void Begin_with_long_payload_emits_FF_and_waits_for_FC()
    {
        var sm = Make();
        var payload = new byte[20];
        for (int i = 0; i < 20; i++) payload[i] = (byte)(i + 1);

        var r = sm.Begin(payload);

        Assert.Equal(NextStep.WaitForFlowControl, r.Next);
        Assert.Equal(TxState.WaitingForFc, sm.State);
        // FF: 0x10, 0x14, then 6 data bytes
        Assert.Equal(8, r.FrameToSend.Length);
        Assert.Equal(0x10, r.FrameToSend[0]);
        Assert.Equal(0x14, r.FrameToSend[1]);
        for (int i = 0; i < 6; i++) Assert.Equal((byte)(i + 1), r.FrameToSend[2 + i]);
    }

    [Fact]
    public void FC_CTS_with_BS_0_drives_first_CF_and_then_more_via_separation_time()
    {
        var sm = Make();
        var payload = new byte[20];
        for (int i = 0; i < 20; i++) payload[i] = (byte)(i + 1);
        sm.Begin(payload);

        // Receiver: CTS, BS=0 (=> all remaining CFs without further FC), STmin=0.
        var fc1 = sm.OnFlowControl(FlowStatus.ContinueToSend, 0, 0);

        // First CF: SN=1, payload bytes 7..13
        Assert.Equal(NextStep.WaitForSeparationTime, fc1.Next);
        Assert.Equal(NResult.N_OK, fc1.Result);
        Assert.NotNull(fc1.FrameToSend);
        Assert.Equal(0x21, fc1.FrameToSend![0]);
        Assert.Equal(7, fc1.FrameToSend[1]);
        Assert.Equal(13, fc1.FrameToSend[7]);

        // After STmin elapses, next CF: SN=2, payload bytes 14..20 (last)
        var step2 = sm.OnSeparationTimeElapsed();
        Assert.Equal(NextStep.Done, step2.Next);
        Assert.Equal(TxState.Done, sm.State);
        Assert.Equal(0x22, step2.FrameToSend![0]);
        Assert.Equal(14, step2.FrameToSend[1]);
        Assert.Equal(20, step2.FrameToSend[7]);
    }

    // -----------------------------------------------------------------------
    // FC.CTS with BS > 0 (multi-block)
    // -----------------------------------------------------------------------

    [Fact]
    public void FC_CTS_with_BS_2_pauses_at_block_boundary_and_waits_for_next_FC()
    {
        var sm = Make();
        // Need a payload whose CFs > 2 to see the boundary.
        // FF carries 6, each CF carries 7. 22 bytes = 6 + 7 + 7 + 2 -> 3 CFs.
        var payload = new byte[22];
        for (int i = 0; i < 22; i++) payload[i] = (byte)(i + 1);
        sm.Begin(payload);

        // Receiver: BS=2.
        var fc1 = sm.OnFlowControl(FlowStatus.ContinueToSend, 2, 0);
        Assert.Equal(NextStep.WaitForSeparationTime, fc1.Next);
        Assert.Equal(0x21, fc1.FrameToSend![0]);

        // After STmin, second CF -> end of block (BS=2). Next: WaitForFc.
        var step2 = sm.OnSeparationTimeElapsed();
        Assert.Equal(NextStep.WaitForFlowControl, step2.Next);
        Assert.Equal(TxState.WaitingForFc, sm.State);
        Assert.Equal(0x22, step2.FrameToSend![0]);

        // Receiver: another CTS, BS=2 still; only 1 CF remains.
        var fc2 = sm.OnFlowControl(FlowStatus.ContinueToSend, 2, 0);
        Assert.Equal(NextStep.Done, fc2.Next);
        Assert.Equal(TxState.Done, sm.State);
        Assert.Equal(0x23, fc2.FrameToSend![0]);
    }

    // -----------------------------------------------------------------------
    // FC.WAIT keeps us waiting and resets nothing else
    // -----------------------------------------------------------------------

    [Fact]
    public void FC_WAIT_returns_no_frame_and_stays_waiting_for_FC()
    {
        var sm = Make();
        var payload = new byte[20];
        sm.Begin(payload);

        var r = sm.OnFlowControl(FlowStatus.Wait, 0, 0);

        Assert.Equal(NextStep.WaitForFlowControl, r.Next);
        Assert.Null(r.FrameToSend);
        Assert.Equal(NResult.N_OK, r.Result);
        Assert.Equal(TxState.WaitingForFc, sm.State);

        // Subsequent CTS resumes the transmission cleanly.
        var fc = sm.OnFlowControl(FlowStatus.ContinueToSend, 0, 0);
        Assert.NotNull(fc.FrameToSend);
        Assert.Equal(0x21, fc.FrameToSend![0]);
    }

    // -----------------------------------------------------------------------
    // FC.OVFLW aborts with N_BUFFER_OVFLW
    // -----------------------------------------------------------------------

    [Fact]
    public void FC_OVFLW_aborts_with_N_BUFFER_OVFLW()
    {
        var sm = Make();
        sm.Begin(new byte[20]);

        var r = sm.OnFlowControl(FlowStatus.Overflow, 0, 0);

        Assert.Equal(NextStep.Done, r.Next);
        Assert.Null(r.FrameToSend);
        Assert.Equal(NResult.N_BUFFER_OVFLW, r.Result);
        Assert.Equal(TxState.Aborted, sm.State);
    }

    // -----------------------------------------------------------------------
    // Reserved FlowStatus -> N_INVALID_FS (§9.6.5.2)
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(0x3)]
    [InlineData(0xF)]
    public void Reserved_FlowStatus_aborts_with_N_INVALID_FS(byte reservedFs)
    {
        var sm = Make();
        sm.Begin(new byte[20]);

        var r = sm.OnFlowControl((FlowStatus)reservedFs, 0, 0);

        Assert.Equal(NResult.N_INVALID_FS, r.Result);
        Assert.Equal(TxState.Aborted, sm.State);
    }

    // -----------------------------------------------------------------------
    // N_Bs timeout
    // -----------------------------------------------------------------------

    [Fact]
    public void OnNbsTimeout_aborts_with_N_TIMEOUT_Bs_when_waiting_for_FC()
    {
        var sm = Make();
        sm.Begin(new byte[20]);
        Assert.Equal(TxState.WaitingForFc, sm.State);

        var r = sm.OnNbsTimeout();
        Assert.Equal(NResult.N_TIMEOUT_Bs, r);
        Assert.Equal(TxState.Aborted, sm.State);
    }

    [Fact]
    public void OnNbsTimeout_returns_null_when_not_waiting_for_FC()
    {
        var sm = Make();
        // Idle -> nothing.
        Assert.Null(sm.OnNbsTimeout());
    }

    // -----------------------------------------------------------------------
    // Dynamic STmin per FC (§9.6.5.6)
    // -----------------------------------------------------------------------

    [Fact]
    public void Dynamic_STmin_is_observed_per_FC()
    {
        var sm = Make();
        // Need at least 2 blocks so BS triggers another FC. FF=6, CF1=7, CF2=7,
        // CF3=last=2  ->  total = 22 bytes,  BS=1 means each CF needs its own FC.
        var payload = new byte[22];
        sm.Begin(payload);

        // FC1: BS=1, STmin=10ms.
        var fc1 = sm.OnFlowControl(FlowStatus.ContinueToSend, 1, 0x0A);
        Assert.Equal(NextStep.WaitForFlowControl, fc1.Next);  // BS=1 -> only 1 CF, then wait for next FC
        Assert.Equal(10_000, sm.EffectiveStMinUs);

        // FC2: BS=1, STmin=20ms (different).
        var fc2 = sm.OnFlowControl(FlowStatus.ContinueToSend, 1, 0x14);
        Assert.Equal(20_000, sm.EffectiveStMinUs);
    }

    // -----------------------------------------------------------------------
    // Sub-millisecond STmin (§9.6.5.4 0xF1..0xF9)
    // -----------------------------------------------------------------------

    [Fact]
    public void Sub_ms_STmin_decoded_to_microseconds_in_EffectiveStMinUs()
    {
        var sm = Make();
        sm.Begin(new byte[20]);

        sm.OnFlowControl(FlowStatus.ContinueToSend, 0, 0xF5);   // 500 us
        Assert.Equal(500, sm.EffectiveStMinUs);
    }

    // -----------------------------------------------------------------------
    // FF escape (FF_DL > 4095)
    // -----------------------------------------------------------------------

    [Fact]
    public void Begin_with_huge_payload_uses_escape_FF()
    {
        var sm = Make();
        var payload = new byte[5000];
        var r = sm.Begin(payload);

        Assert.Equal(NextStep.WaitForFlowControl, r.Next);
        // Escape FF: byte0=0x10, byte1=0x00, bytes2..5 = 5000 BE = 0x00 00 13 88
        Assert.Equal(0x10, r.FrameToSend[0]);
        Assert.Equal(0x00, r.FrameToSend[1]);
        Assert.Equal(0x00, r.FrameToSend[2]);
        Assert.Equal(0x00, r.FrameToSend[3]);
        Assert.Equal(0x13, r.FrameToSend[4]);
        Assert.Equal(0x88, r.FrameToSend[5]);
        // Frame is 8 bytes total; only 2 data bytes after the 6-byte PCI for classical CAN.
        Assert.Equal(8, r.FrameToSend.Length);
    }

    // -----------------------------------------------------------------------
    // Stray FC outside WaitingForFc state is ignored at this layer
    // -----------------------------------------------------------------------

    [Fact]
    public void FC_received_outside_WaitingForFc_returns_None()
    {
        var sm = Make();
        // Idle: no message in flight.
        var r = sm.OnFlowControl(FlowStatus.ContinueToSend, 0, 0);
        Assert.Equal(NextStep.None, r.Next);
        Assert.Null(r.FrameToSend);
        Assert.Equal(NResult.N_OK, r.Result);
    }

    // -----------------------------------------------------------------------
    // Begin twice in a row throws (state machine is busy)
    // -----------------------------------------------------------------------

    [Fact]
    public void Begin_while_in_flight_throws()
    {
        var sm = Make();
        sm.Begin(new byte[20]);
        Assert.Throws<InvalidOperationException>(() => sm.Begin(new byte[20]));
    }

    // -----------------------------------------------------------------------
    // Extended addressing data-field budget (1 byte for N_TA -> 7 bytes left)
    // -----------------------------------------------------------------------

    [Fact]
    public void Extended_addressing_SF_max_is_six_bytes()
    {
        // dataFieldBytes = 7 simulates extended/mixed (one byte already consumed
        // by N_TA / N_AE before the PCI begins).
        var sm = new IsoTpTxStateMachine(7, new IsoTpTimingParameters());

        // 6 bytes still SF.
        var r6 = sm.Begin(new byte[6]);
        Assert.Equal(NextStep.Done, r6.Next);
        Assert.Equal(7, r6.FrameToSend.Length);

        // 7 bytes -> FF (because SF would require dataFieldBytes - 1 = 6 byte cap).
        sm = new IsoTpTxStateMachine(7, new IsoTpTimingParameters());
        var r7 = sm.Begin(new byte[7]);
        Assert.Equal(NextStep.WaitForFlowControl, r7.Next);
        Assert.Equal(0x10, r7.FrameToSend[0]);
        Assert.Equal(0x07, r7.FrameToSend[1]);
    }
}
