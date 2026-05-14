namespace Common.IsoTp;

/// <summary>
/// ISO 15765-2:2016 receive-side network-layer state machine for a single
/// N_AI (one peer pair). Stateful, single-threaded by design - the owning
/// channel must serialise <see cref="Feed"/> and <see cref="OnNcrTimeout"/>
/// calls.
///
/// Design contract:
///   * The caller has already stripped the addressing prefix (Extended N_TA
///     or Mixed N_AE byte) per §10.3 - <paramref name="frameDataField"/>
///     starts at the first N_PCI byte.
///   * Decisions to emit a FC or hand a payload up are returned through the
///     out parameters; the caller dispatches them on the wire.
///   * The state machine itself owns no timers. The caller must arm an N_Cr
///     timer (timing.NCrMs) after a FF or each accepted CF, and call
///     <see cref="OnNcrTimeout"/> on expiry.
///
/// Behaviour traceable to the spec:
///   §9.3            : segmentation/reassembly + FC mechanism
///   §9.6.2.2        : SF malformed-length handling -> ignore
///   §9.6.3.2        : FF malformed-length / over-buffer handling
///   §9.6.4.4        : CF wrong-SN -> N_WRONG_SN
///   §9.6.5.1 OVFLW  : FF_DL exceeds buffer -> emit FC.OVFLW
///   §9.7  N_WFTmax  : we don't emit WAIT in this build (planned receivers
///                     can opt in via timing.NWftMax > 0 in a follow-up)
///   §9.8.2 N_Cr     : §9.8.2 N_TIMEOUT_Cr on no CF in time
///   §9.8.3          : unexpected SF/FF during reception -> N_UNEXP_PDU on
///                     the in-flight reception, then start the new one
/// </summary>
public sealed class IsoTpRxStateMachine
{
    private readonly IsoTpTimingParameters timing;
    private readonly int rxBufferCap;

    private byte[]? buffer;
    private int totalLen;
    private int written;
    private byte expectedSn;            // next SN we expect on a CF (1..F, wraps after F to 0)
    private int cfReceivedInBlock;      // CFs accepted since the last FC we emitted
    private bool inProgress;

    /// <summary>
    /// Creates a receiver bound to the given timing parameters. The buffer
    /// cap is the largest message the upper layer is willing to assemble;
    /// FF_DLs above this cap are rejected with FC.OVFLW per §9.6.5.1.
    /// </summary>
    public IsoTpRxStateMachine(IsoTpTimingParameters timing, int rxBufferCap = 4 * 1024)
    {
        this.timing = timing;
        this.rxBufferCap = rxBufferCap;
    }

    /// <summary>True when reception of a segmented message is in progress.</summary>
    public bool InProgress => inProgress;

    /// <summary>Snapshot of bytes accumulated so far (for diagnostics; not the eventual buffer).</summary>
    public int BytesReceived => written;

    /// <summary>Snapshot of FF_DL for the in-progress message.</summary>
    public int ExpectedTotal => totalLen;

    /// <summary>
    /// Process one CAN frame's data field. Returns the result of this feed:
    ///   <see cref="RxOutcome.Idle"/>      - nothing to do
    ///   <see cref="RxOutcome.MessageReady"/> - <paramref name="completedPayload"/> populated
    ///   <see cref="RxOutcome.SendFlowControl"/> - <paramref name="fcOutput"/> populated
    ///   <see cref="RxOutcome.Error"/>     - <paramref name="result"/> set; reception aborted
    ///
    /// Multiple outputs can co-occur with one feed (e.g. an FF that triggers both
    /// a FC.CTS emission and (if the buffer is too small) an OVFLW abort). When
    /// that happens, the strongest signal wins:
    ///   Error &gt; MessageReady &gt; SendFlowControl &gt; Idle.
    /// </summary>
    public RxOutcome Feed(
        ReadOnlySpan<byte> frameDataField,
        out byte[]? completedPayload,
        out FcOutput? fcOutput,
        out NResult result)
    {
        completedPayload = null;
        fcOutput = null;
        result = NResult.N_OK;

        if (!NPci.TryDecode(frameDataField, out var hdr))
        {
            // §9.6.x error handling: malformed PCI -> ignore (no upper-layer notification).
            return RxOutcome.Idle;
        }

        switch (hdr.Type)
        {
            case NPciType.SingleFrame:
                return HandleSingleFrame(frameDataField, hdr, out completedPayload, out result);

            case NPciType.FirstFrame:
                return HandleFirstFrame(frameDataField, hdr, out completedPayload, out fcOutput, out result);

            case NPciType.ConsecutiveFrame:
                return HandleConsecutiveFrame(frameDataField, hdr, out completedPayload, out fcOutput, out result);

            case NPciType.FlowControl:
                // §9.8.3 receive-in-progress + FC -> ignore (FC is a sender-side concern).
                // Idle + FC -> ignore. Either way we do nothing here; the channel routes
                // FC frames to the TX state machine separately.
                return RxOutcome.Idle;

            default:
                return RxOutcome.Idle;
        }
    }

    private RxOutcome HandleSingleFrame(
        ReadOnlySpan<byte> frameDataField, in NPciHeader hdr,
        out byte[]? completedPayload, out NResult result)
    {
        completedPayload = null;
        result = NResult.N_OK;

        int sfDl = hdr.Length;
        // §9.6.2.2: SF data must fit in (CAN_DL - PCI byte count). Caller has
        // already verified frameDataField is the post-address-prefix span.
        if (frameDataField.Length < hdr.PciByteCount + sfDl)
            return RxOutcome.Idle;

        var payload = frameDataField.Slice(hdr.PciByteCount, sfDl).ToArray();

        if (inProgress)
        {
            // §9.8.3: SF arriving during a segmented receive -> abort current with
            // N_UNEXP_PDU, then process the new SF as a fresh reception.
            ResetState();
            // Two outputs: error AND the payload of the SF. We surface the error
            // first per the strongest-signal rule; the payload is delivered too,
            // but the caller observes the error first and can decide whether to
            // also pass the new payload up. We keep this simple: deliver the
            // payload via completedPayload alongside the error so the caller
            // doesn't lose the new message.
            completedPayload = payload;
            result = NResult.N_UNEXP_PDU;
            return RxOutcome.Error;
        }

        completedPayload = payload;
        return RxOutcome.MessageReady;
    }

    private RxOutcome HandleFirstFrame(
        ReadOnlySpan<byte> frameDataField, in NPciHeader hdr,
        out byte[]? completedPayload, out FcOutput? fcOutput, out NResult result)
    {
        completedPayload = null;
        fcOutput = null;
        result = NResult.N_OK;

        // Resolve effective length. We use Length32 (uint) so escape-encoded
        // FF_DL > Int32.MaxValue is still bounded properly by the buffer cap.
        uint ffDl = hdr.Length32;

        if (inProgress)
        {
            // §9.8.3: FF arriving during a segmented receive -> abort current,
            // then start a new reception with this FF.
            ResetState();
            result = NResult.N_UNEXP_PDU;
            // Fall through to start the new reception; we'll surface the error
            // outcome at the end so the caller can also dispatch the FC we emit
            // for the new message.
        }

        // §9.6.5.1 OVFLW: if FF_DL exceeds our willing-to-buffer cap, send
        // FC.OVFLW and stay Idle.
        if (ffDl > (uint)rxBufferCap)
        {
            fcOutput = new FcOutput(FlowStatus.Overflow, 0, 0);
            // If we were in-progress, the N_UNEXP_PDU above is the active error;
            // otherwise this is a clean OVFLW outcome with no payload + FC out.
            return result == NResult.N_UNEXP_PDU ? RxOutcome.Error : RxOutcome.SendFlowControl;
        }

        // Begin a new reception.
        buffer = new byte[ffDl];
        totalLen = (int)ffDl;
        written = 0;
        expectedSn = 1;             // first CF is SN=1 (FF is implicitly SN=0)
        cfReceivedInBlock = 0;
        inProgress = true;

        // Copy FF data bytes (everything after the PCI bytes).
        int firstChunk = Math.Min(totalLen, frameDataField.Length - hdr.PciByteCount);
        if (firstChunk > 0)
        {
            frameDataField.Slice(hdr.PciByteCount, firstChunk).CopyTo(buffer);
            written = firstChunk;
        }

        // Emit our FC.CTS (or WAIT/OVFLW) per the configured policy. We default
        // to CTS with the configured BS/STmin; FC.WAIT support requires N_WFTmax
        // tracking and is opt-in via timing.NWftMax (not implemented in this
        // build - planned).
        fcOutput = new FcOutput(FlowStatus.ContinueToSend, timing.BlockSizeSend, timing.StMinSendRaw);

        return result == NResult.N_UNEXP_PDU ? RxOutcome.Error : RxOutcome.SendFlowControl;
    }

    private RxOutcome HandleConsecutiveFrame(
        ReadOnlySpan<byte> frameDataField, in NPciHeader hdr,
        out byte[]? completedPayload, out FcOutput? fcOutput, out NResult result)
    {
        completedPayload = null;
        fcOutput = null;
        result = NResult.N_OK;

        if (!inProgress)
        {
            // §9.8.3 idle + CF -> ignore.
            return RxOutcome.Idle;
        }

        // §9.6.4.4: SN must match the next expected value (mod 16).
        if (hdr.SequenceNumber != (expectedSn & 0x0F))
        {
            ResetState();
            result = NResult.N_WRONG_SN;
            return RxOutcome.Error;
        }

        int chunk = Math.Min(totalLen - written, frameDataField.Length - hdr.PciByteCount);
        if (chunk > 0)
        {
            frameDataField.Slice(hdr.PciByteCount, chunk).CopyTo(buffer.AsSpan(written));
            written += chunk;
        }
        expectedSn = (byte)((expectedSn + 1) & 0x0F);
        cfReceivedInBlock++;

        if (written >= totalLen)
        {
            completedPayload = buffer;
            ResetState();
            return RxOutcome.MessageReady;
        }

        // BlockSize == 0 means "send all remaining CFs without further FC"
        // (§9.6.5.3 Table 19). Otherwise emit a fresh FC after each BS-th CF.
        if (timing.BlockSizeSend > 0 && cfReceivedInBlock >= timing.BlockSizeSend)
        {
            cfReceivedInBlock = 0;
            fcOutput = new FcOutput(FlowStatus.ContinueToSend, timing.BlockSizeSend, timing.StMinSendRaw);
            return RxOutcome.SendFlowControl;
        }

        return RxOutcome.Idle;
    }

    /// <summary>
    /// Driven by the channel layer when the N_Cr timer fires (§9.8.2). Aborts
    /// the in-flight reception with <see cref="NResult.N_TIMEOUT_Cr"/>. No-op
    /// when nothing is in progress.
    /// </summary>
    public NResult? OnNcrTimeout()
    {
        if (!inProgress) return null;
        ResetState();
        return NResult.N_TIMEOUT_Cr;
    }

    private void ResetState()
    {
        buffer = null;
        totalLen = 0;
        written = 0;
        expectedSn = 0;
        cfReceivedInBlock = 0;
        inProgress = false;
    }
}

/// <summary>The high-level outcome of one frame fed into the RX state machine.</summary>
public enum RxOutcome
{
    /// <summary>Nothing to do; frame was either consumed silently or ignored per spec.</summary>
    Idle = 0,
    /// <summary>The state machine wants to emit a FC frame (see fcOutput).</summary>
    SendFlowControl = 1,
    /// <summary>A complete payload is ready for the upper layer (see completedPayload).</summary>
    MessageReady = 2,
    /// <summary>An error occurred and reception was aborted (see result).</summary>
    Error = 3,
}

/// <summary>
/// Body of a FlowControl frame the receiver wants to emit. The channel layer
/// is responsible for prefixing addressing bytes and writing the wire frame
/// via <see cref="NPci.EncodeFlowControl"/>.
/// </summary>
public readonly struct FcOutput
{
    public FlowStatus Status { get; }
    public byte BlockSize { get; }
    public byte StMinRaw { get; }

    public FcOutput(FlowStatus status, byte blockSize, byte stMinRaw)
    {
        Status = status;
        BlockSize = blockSize;
        StMinRaw = stMinRaw;
    }
}
