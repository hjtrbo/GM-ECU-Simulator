namespace Common.IsoTp;

/// <summary>
/// ISO 15765-2:2016 transmit-side network-layer state machine for a single
/// outbound message. Stateful, single-threaded by design.
///
/// Design contract:
///   * The caller has chosen the addressing format and tells the machine how
///     many bytes of CAN frame data field are available for N_PCI + N_Data.
///     For classical CAN that is 8 - addressPrefixBytes (7 or 8 depending on
///     address format).
///   * The machine produces "frame data field" bytes; the caller wraps them
///     with the addressing prefix (N_TA / N_AE) and CAN ID before transmit.
///   * The machine owns no timers. The caller must arm an N_Bs timer after
///     emitting an FF or the last CF of a block, and call <see cref="OnNbsTimeout"/>
///     when it fires. The caller must also enforce STmin between CFs (the
///     machine reports the required separation in microseconds via
///     <see cref="EffectiveStMinUs"/>).
///
/// Behaviour traceable to the spec:
///   §9.2-9.3        : SF vs FF/CF segmentation
///   §9.6.5.1 OVFLW  : sender aborts with N_BUFFER_OVFLW
///   §9.6.5.1 WAIT   : sender restarts N_Bs and waits for next FC
///   §9.6.5.2        : reserved FlowStatus -> N_INVALID_FS
///   §9.6.5.6        : dynamic BS/STmin honoured per FC
///   §9.8.2 N_Bs     : N_Bs timeout -> N_TIMEOUT_Bs
/// </summary>
public sealed class IsoTpTxStateMachine
{
    private readonly int dataFieldBytes;
    private readonly IsoTpTimingParameters timing;

    private byte[] payload = [];
    private int written;
    private byte sn;                    // next SN to emit (1..15, wraps after 15 -> 0)
    private byte effectiveBs;
    private int effectiveStMinUs;
    private int cfsInBlockEmitted;
    private TxState state;

    public TxState State => state;
    public int EffectiveStMinUs => effectiveStMinUs;
    public byte EffectiveBlockSize => effectiveBs;

    /// <summary>
    /// Creates a TX state machine.
    /// </summary>
    /// <param name="dataFieldBytes">
    /// Number of CAN frame data-field bytes available for N_PCI + N_Data
    /// (after the addressing prefix is consumed). 8 for normal/normal-fixed
    /// addressing on classical CAN; 7 for extended/mixed addressing on
    /// classical CAN; up to 64 for CAN-FD.
    /// </param>
    public IsoTpTxStateMachine(int dataFieldBytes, IsoTpTimingParameters timing)
    {
        if (dataFieldBytes < 2)
            throw new ArgumentOutOfRangeException(nameof(dataFieldBytes),
                "ISO-TP requires at least 2 data-field bytes (PCI byte + 1 data byte for SF_DL=1)");
        this.dataFieldBytes = dataFieldBytes;
        this.timing = timing;
    }

    // ========================================================================
    // Public API
    // ========================================================================

    /// <summary>
    /// Begin transmission of <paramref name="userPayload"/>. Returns the data
    /// field of the first frame (SF or FF) the caller should put on the wire.
    /// After an SF, <see cref="State"/> goes to <see cref="TxState.Done"/>;
    /// after an FF, it goes to <see cref="TxState.WaitingForFc"/> and the
    /// caller should arm the N_Bs timer.
    /// </summary>
    public TxStartResult Begin(ReadOnlySpan<byte> userPayload)
    {
        if (state != TxState.Idle)
            throw new InvalidOperationException("TX state machine is busy");

        if (userPayload.Length == 0)
            throw new ArgumentException("Cannot transmit an empty payload (no SF_DL=0 in §9.6.2.1)");

        // Decide SF vs FF using the payload length and the available data-field bytes.
        // SF (classical) needs 1 PCI byte + payload <= dataFieldBytes;
        // SF (escape) needs 2 PCI bytes + payload <= dataFieldBytes (only for CAN-FD).
        int classicalSfMax = dataFieldBytes - 1;
        if (userPayload.Length <= classicalSfMax && userPayload.Length <= NPci.MaxClassicalSfDlNormal)
        {
            // SingleFrame, no segmentation needed.
            var buf = new byte[1 + userPayload.Length];
            NPci.EncodeSingleFrameClassical(buf, userPayload);
            state = TxState.Done;
            return new TxStartResult(buf, NextStep.Done);
        }

        // FirstFrame path. Decide short vs escape FF_DL encoding.
        payload = userPayload.ToArray();
        written = 0;
        sn = 1;
        cfsInBlockEmitted = 0;
        effectiveBs = 0;
        effectiveStMinUs = 0;
        state = TxState.WaitingForFc;

        if (userPayload.Length <= NPci.MaxShortFfDl)
        {
            // FF (12-bit length): 2 PCI bytes + (dataFieldBytes - 2) data bytes.
            int firstChunk = Math.Min(userPayload.Length, dataFieldBytes - 2);
            var buf = new byte[2 + firstChunk];
            NPci.EncodeFirstFrameShort(buf, (ushort)userPayload.Length);
            userPayload.Slice(0, firstChunk).CopyTo(buf.AsSpan(2));
            written = firstChunk;
            return new TxStartResult(buf, NextStep.WaitForFlowControl);
        }
        else
        {
            // Escape FF (32-bit length): 6 PCI bytes + (dataFieldBytes - 6) data bytes.
            int firstChunk = Math.Min(userPayload.Length, dataFieldBytes - 6);
            if (firstChunk < 0) firstChunk = 0;
            var buf = new byte[6 + firstChunk];
            NPci.EncodeFirstFrameEscape(buf, (uint)userPayload.Length);
            if (firstChunk > 0)
                userPayload.Slice(0, firstChunk).CopyTo(buf.AsSpan(6));
            written = firstChunk;
            return new TxStartResult(buf, NextStep.WaitForFlowControl);
        }
    }

    /// <summary>
    /// Process an inbound FC frame. Returns the data field of the next CF the
    /// caller should send (or null if the FC was WAIT or aborted), the
    /// effective STmin (us) the caller must wait before subsequent CFs, and
    /// any error raised.
    /// </summary>
    public TxFcResult OnFlowControl(FlowStatus fs, byte bs, byte stMinRaw)
    {
        if (state != TxState.WaitingForFc)
            // §9.8.3 sender state: stray FC not awaited is ignored at this layer.
            // The channel can drop it.
            return new TxFcResult(NextStep.None, null, NResult.N_OK);

        switch (fs)
        {
            case FlowStatus.Wait:
                // §9.6.5.1 WAIT: stay in WaitingForFc; caller restarts N_Bs.
                // BS/STmin in this FC are not relevant.
                return new TxFcResult(NextStep.WaitForFlowControl, null, NResult.N_OK);

            case FlowStatus.Overflow:
                // §9.6.5.1 OVFLW: abort immediately with N_BUFFER_OVFLW.
                state = TxState.Aborted;
                return new TxFcResult(NextStep.Done, null, NResult.N_BUFFER_OVFLW);

            case FlowStatus.ContinueToSend:
                effectiveBs = bs;
                effectiveStMinUs = STminCodec.DecodeMicroseconds(stMinRaw);
                cfsInBlockEmitted = 0;
                // Switch state then emit the first CF in the new block (no STmin
                // delay before the first CF after FC; STmin starts measuring AFTER
                // a CF transmission).
                state = TxState.SendingCfs;
                return EmitCfAfterCtsOrSeparation();

            default:
                // §9.6.5.2: reserved FS values trigger N_INVALID_FS abort.
                state = TxState.Aborted;
                return new TxFcResult(NextStep.Done, null, NResult.N_INVALID_FS);
        }
    }

    /// <summary>
    /// After STmin has elapsed, emit the next CF. Caller must call this only
    /// when the state is <see cref="TxState.SendingCfs"/>; calling it in other
    /// states throws <see cref="InvalidOperationException"/>.
    /// </summary>
    public TxFcResult OnSeparationTimeElapsed()
    {
        if (state != TxState.SendingCfs)
            throw new InvalidOperationException(
                "OnSeparationTimeElapsed valid only while sending CFs in a block");
        return EmitCfAfterCtsOrSeparation();
    }

    /// <summary>
    /// Driven by the channel when the N_Bs timer fires (§9.8.2). Aborts the
    /// in-flight transmission with <see cref="NResult.N_TIMEOUT_Bs"/>.
    /// </summary>
    public NResult? OnNbsTimeout()
    {
        if (state != TxState.WaitingForFc) return null;
        state = TxState.Aborted;
        return NResult.N_TIMEOUT_Bs;
    }

    // ========================================================================
    // Internals
    // ========================================================================

    private TxFcResult EmitCfAfterCtsOrSeparation()
    {
        int remaining = payload.Length - written;
        int chunk = Math.Min(dataFieldBytes - 1, remaining);

        var buf = new byte[1 + chunk];
        NPci.EncodeConsecutiveFrame(buf, sn);
        payload.AsSpan(written, chunk).CopyTo(buf.AsSpan(1));
        written += chunk;
        sn = (byte)((sn + 1) & 0x0F);
        cfsInBlockEmitted++;

        if (written >= payload.Length)
        {
            // Final CF of the message.
            state = TxState.Done;
            return new TxFcResult(NextStep.Done, buf, NResult.N_OK);
        }

        if (effectiveBs > 0 && cfsInBlockEmitted >= effectiveBs)
        {
            // End of this block; await the next FC.
            state = TxState.WaitingForFc;
            return new TxFcResult(NextStep.WaitForFlowControl, buf, NResult.N_OK);
        }

        // More CFs in this block; caller observes EffectiveStMinUs and calls
        // OnSeparationTimeElapsed() when ready.
        return new TxFcResult(NextStep.WaitForSeparationTime, buf, NResult.N_OK);
    }
}

/// <summary>High-level TX state.</summary>
public enum TxState
{
    Idle = 0,
    /// <summary>Sent FF or end-of-block CF; waiting for FC from the receiver.</summary>
    WaitingForFc = 1,
    /// <summary>Inside a block, pacing CFs by STmin.</summary>
    SendingCfs = 2,
    /// <summary>Message fully transmitted (success) or aborted (error).</summary>
    Done = 3,
    /// <summary>Aborted by an FC.OVFLW, FS.Reserved, or N_Bs timeout.</summary>
    Aborted = 4,
}

/// <summary>What the channel is expected to do next after a state-machine call returns.</summary>
public enum NextStep
{
    /// <summary>Nothing to do (or the call had no relevant effect).</summary>
    None = 0,
    /// <summary>Caller arms / re-arms N_Bs and waits for FC.</summary>
    WaitForFlowControl = 1,
    /// <summary>Caller waits EffectiveStMinUs then calls OnSeparationTimeElapsed.</summary>
    WaitForSeparationTime = 2,
    /// <summary>Transmission complete (success or aborted).</summary>
    Done = 3,
}

public readonly struct TxStartResult
{
    public byte[] FrameToSend { get; }
    public NextStep Next { get; }
    public TxStartResult(byte[] frameToSend, NextStep next) { FrameToSend = frameToSend; Next = next; }
}

public readonly struct TxFcResult
{
    public NextStep Next { get; }
    /// <summary>The CF data-field to put on the wire, or null when no frame is emitted.</summary>
    public byte[]? FrameToSend { get; }
    public NResult Result { get; }
    public TxFcResult(NextStep next, byte[]? frameToSend, NResult result)
    { Next = next; FrameToSend = frameToSend; Result = result; }
}
