using System.Collections.Concurrent;
using Common.IsoTp;
using Common.PassThru;

namespace Shim.IsoTp;

/// <summary>
/// Per-J2534-channel ISO 15765-2 transport-protocol context. Owns the
/// FlowControl filter table (FLOW_CONTROL_FILTER per §6.3.5 of SAE J2534),
/// the per-N_AI RX/TX state machines, and the reassembled-payload queue
/// that PassThruReadMsgs serves from for ISO15765 channels.
///
/// One <see cref="Iso15765Channel"/> exists for every J2534 channel opened
/// with <see cref="ProtocolID.ISO15765"/>. It does NOT replace the per-frame
/// RxQueue used by the CAN protocol path; that queue stays empty on
/// ISO15765 channels and the host's PassThruReadMsgs reads from
/// <see cref="ReassembledPayloadQueue"/> instead.
///
/// Cascade model: the simulator's bus is in-process and synchronous, so when
/// a host write triggers an inbound FC, we drive the TX state machine and
/// dispatch the resulting CF on the same call stack. <see cref="BusEgress"/>
/// is the wire-level callback the channel uses to put outbound CAN frames on
/// the bus; the dispatcher wires it at channel-create time.
/// </summary>
public sealed class Iso15765Channel
{
    /// <summary>
    /// FLOW_CONTROL_FILTER record - the J2534 surface that maps inbound CAN
    /// frames (mask/pattern) to an outbound CAN ID for FCs and segmented
    /// transmits.
    /// </summary>
    public sealed class IsoFilter
    {
        public required uint Id { get; init; }
        public required uint MaskCanId { get; init; }
        public required uint PatternCanId { get; init; }
        public required uint FlowCtlCanId { get; init; }
        /// <summary>Optional N_AE byte for mixed addressing; 0 if not used.</summary>
        public byte FlowCtlExt { get; init; }
        public AddressFormat Format { get; init; } = AddressFormat.Normal;

        /// <summary>True if <paramref name="canId"/> matches mask/pattern (bitwise: pattern == canId &amp; mask).</summary>
        public bool MatchesInbound(uint canId)
            => (canId & MaskCanId) == (PatternCanId & MaskCanId);
    }

    /// <summary>Per-filter TP machinery (one RX, lazily one TX during outbound transmits).</summary>
    private sealed class FilterContext
    {
        public required IsoFilter Filter { get; init; }
        public required IsoTpRxStateMachine Rx { get; init; }
        public IsoTpTxStateMachine? ActiveTx { get; set; }
        /// <summary>Result of the last completed/aborted ActiveTx; consumed by the dispatcher after the cascade.</summary>
        public NResult? LastTxResult { get; set; }
    }

    private readonly List<FilterContext> filters = new();
    private readonly Lock stateLock = new();
    public IsoTpTimingParameters Timing { get; }

    /// <summary>
    /// Reassembled inbound payloads as they become available. Each entry's
    /// <see cref="PassThruMsg.Data"/> is [4-byte CAN_ID][user payload] - the
    /// J2534 wire shape PassThruReadMsgs returns to the host on an ISO15765
    /// channel.
    /// </summary>
    public ConcurrentQueue<PassThruMsg> ReassembledPayloadQueue { get; } = new();

    /// <summary>Released once per enqueue so a parked PassThruReadMsgs caller wakes up.</summary>
    public SemaphoreSlim ReassembledAvailable { get; } = new(0, int.MaxValue);

    /// <summary>
    /// Bus egress callback. Wired by the dispatcher when the channel is
    /// created so the channel can push outbound CAN frames (FCs and CFs
    /// generated mid-cascade) onto the bus without depending directly on the
    /// VirtualBus type.
    /// </summary>
    public Action<byte[]>? BusEgress { get; set; }

    public Iso15765Channel(IsoTpTimingParameters timing) { Timing = timing; }

    // =========================================================================
    // Filter management (FLOW_CONTROL_FILTER)
    // =========================================================================

    public void AddFilter(IsoFilter filter)
    {
        lock (stateLock)
        {
            filters.RemoveAll(f => f.Filter.Id == filter.Id);
            filters.Add(new FilterContext
            {
                Filter = filter,
                Rx = new IsoTpRxStateMachine(Timing),
            });
        }
    }

    public bool RemoveFilter(uint id)
    {
        lock (stateLock) return filters.RemoveAll(f => f.Filter.Id == id) > 0;
    }

    public void ClearFilters() { lock (stateLock) filters.Clear(); }

    public IReadOnlyList<IsoFilter> Filters
    {
        get { lock (stateLock) return filters.Select(f => f.Filter).ToArray(); }
    }

    // =========================================================================
    // Inbound: drive the RX (or active TX) state machine for the matching filter
    // =========================================================================

    /// <summary>
    /// Process one inbound CAN frame from the simulator's bus. FC frames
    /// inbound from the bus are routed to the matching filter's active TX
    /// machine - if that produces a next CF, this method dispatches it via
    /// <see cref="BusEgress"/> immediately, recursing through the cascade
    /// until the message completes or pauses on STmin/N_Bs. SF/FF/CF frames
    /// are routed to the RX machine; reassembled payloads land on
    /// <see cref="ReassembledPayloadQueue"/>; FCs the receiver wants to emit
    /// are dispatched via <see cref="BusEgress"/>.
    ///
    /// Returns true if a filter matched (frame was consumed by the TP layer);
    /// false if no filter matched (caller may handle the frame elsewhere).
    /// </summary>
    public bool OnInboundCanFrame(uint canId, ReadOnlySpan<byte> canData)
    {
        FilterContext? matched = null;
        lock (stateLock)
        {
            foreach (var fc in filters)
            {
                if (fc.Filter.MatchesInbound(canId)) { matched = fc; break; }
            }
        }
        if (matched == null) return false;

        var stripped = AddressFormatLayout.StripAddressPrefix(canData, matched.Filter.Format, out _);

        // Dispatch by PCI type: FC -> TX side; SF/FF/CF -> RX side.
        if (NPci.TryDecode(stripped, out var hdr) && hdr.Type == NPciType.FlowControl)
        {
            HandleInboundFc(matched, hdr);
            return true;
        }

        // SF/FF/CF -> RX side.
        var rxOutcome = matched.Rx.Feed(stripped, out var payload, out var fcOut, out var rxResult);

        if (payload != null && (rxOutcome == RxOutcome.MessageReady || rxOutcome == RxOutcome.Error))
            EnqueueReassembled(canId, payload);

        if (fcOut.HasValue && BusEgress != null)
        {
            var fc = fcOut.Value;
            var pciBuf = new byte[3];
            NPci.EncodeFlowControl(pciBuf, fc.Status, fc.BlockSize, fc.StMinRaw);
            var canFrame = BuildCanFrame(matched.Filter.FlowCtlCanId, matched.Filter.Format, matched.Filter.FlowCtlExt, pciBuf);
            BusEgress(canFrame);
        }

        return true;
    }

    private void HandleInboundFc(FilterContext matched, in NPciHeader fcHdr)
    {
        IsoTpTxStateMachine? tx;
        lock (stateLock) tx = matched.ActiveTx;
        if (tx == null) return;       // stray FC, no TX awaits it

        var txr = tx.OnFlowControl(fcHdr.FlowStatus, fcHdr.BlockSize, fcHdr.STminRaw);

        // Latch terminal results so the dispatcher can observe them.
        if (tx.State == TxState.Done || tx.State == TxState.Aborted)
        {
            lock (stateLock) matched.LastTxResult = txr.Result;
        }

        // CF produced -> dispatch on bus and (if more CFs in the block) drain
        // them back-to-back. STmin pacing isn't enforced in this build; senders
        // that need sub-frame pacing must wire a TimerOnDelay around the
        // OnSeparationTimeElapsed loop. The wire-level state machine is correct;
        // only the timing is approximate.
        EmitTxFrameAndDrain(matched, txr);
    }

    private void EmitTxFrameAndDrain(FilterContext matched, TxFcResult initial)
    {
        var current = initial;
        while (current.FrameToSend != null && BusEgress != null)
        {
            var canFrame = BuildCanFrame(matched.Filter.FlowCtlCanId, matched.Filter.Format, matched.Filter.FlowCtlExt, current.FrameToSend);
            BusEgress(canFrame);

            // The bus dispatch may itself trigger an inbound FC (BS > 0) which
            // arrives via OnInboundCanFrame on this same call stack and updates
            // the TX state machine. After the dispatch returns, decide what's next.
            switch (current.Next)
            {
                case NextStep.WaitForSeparationTime:
                    // No real timer in this build - immediately produce the next CF.
                    current = matched.ActiveTx!.OnSeparationTimeElapsed();
                    if (matched.ActiveTx.State == TxState.Done || matched.ActiveTx.State == TxState.Aborted)
                        lock (stateLock) matched.LastTxResult = current.Result;
                    break;
                case NextStep.WaitForFlowControl:
                case NextStep.Done:
                case NextStep.None:
                default:
                    return;
            }
        }
    }

    private void EnqueueReassembled(uint canId, byte[] userPayload)
    {
        var data = new byte[4 + userPayload.Length];
        data[0] = (byte)((canId >> 24) & 0xFF);
        data[1] = (byte)((canId >> 16) & 0xFF);
        data[2] = (byte)((canId >> 8) & 0xFF);
        data[3] = (byte)(canId & 0xFF);
        userPayload.CopyTo(data.AsSpan(4));
        var msg = new PassThruMsg { ProtocolID = ProtocolID.ISO15765, Data = data };
        ReassembledPayloadQueue.Enqueue(msg);
        ReassembledAvailable.Release();
    }

    // =========================================================================
    // Outbound: TX state machine driver
    // =========================================================================

    /// <summary>
    /// Begin outbound transmission of <paramref name="userPayload"/> targeted at
    /// <paramref name="requestCanId"/>. Returns the first frame (SF or FF) wrapped
    /// for the bus, or <c>null</c> if no FlowControl filter has been installed for
    /// this address pair (the dispatcher should report ERR_NO_FLOW_CONTROL).
    ///
    /// After SF: TX state is Done immediately.
    /// After FF: TX is in WaitingForFc; the cascade in <see cref="OnInboundCanFrame"/>
    /// drives the rest as the ECU's FC arrives.
    /// </summary>
    public TxBeginResult BeginTransmit(uint requestCanId, ReadOnlySpan<byte> userPayload)
    {
        FilterContext? matched = null;
        lock (stateLock)
        {
            foreach (var fc in filters)
            {
                if (fc.Filter.FlowCtlCanId == requestCanId) { matched = fc; break; }
            }
        }
        if (matched == null) return TxBeginResult.NoFilter();

        int prefixBytes = AddressFormatLayout.AddressPrefixBytes(matched.Filter.Format);
        int dataFieldBytes = 8 - prefixBytes;

        var tx = new IsoTpTxStateMachine(dataFieldBytes, Timing);
        var startResult = tx.Begin(userPayload);

        lock (stateLock)
        {
            matched.ActiveTx = tx;
            matched.LastTxResult = tx.State == TxState.Done ? NResult.N_OK : null;
        }

        var canFrame = BuildCanFrame(requestCanId, matched.Filter.Format, matched.Filter.FlowCtlExt, startResult.FrameToSend);
        return new TxBeginResult
        {
            Filter = matched.Filter,
            Tx = tx,
            CanFrame = canFrame,
            Next = startResult.Next,
        };
    }

    /// <summary>
    /// Inspects the terminal result of the most recent transmit on the
    /// matching filter. Returns N_OK on success, an N_TIMEOUT_/N_BUFFER_/etc.
    /// value on failure, or <c>null</c> if the transmit is still in progress.
    /// </summary>
    public NResult? GetTxResult(IsoFilter filter)
    {
        lock (stateLock)
        {
            foreach (var fc in filters)
                if (ReferenceEquals(fc.Filter, filter)) return fc.LastTxResult;
        }
        return null;
    }

    /// <summary>Releases the TX context after the message completes (success or error).</summary>
    public void EndTransmit(IsoFilter filter)
    {
        lock (stateLock)
        {
            foreach (var fc in filters)
                if (ReferenceEquals(fc.Filter, filter))
                {
                    fc.ActiveTx = null;
                    fc.LastTxResult = null;
                }
        }
    }

    // =========================================================================
    // Frame helpers
    // =========================================================================

    /// <summary>
    /// Wrap a state-machine-produced frame data field (PCI + N_Data) into a
    /// full bus-format CAN frame: [4-byte CAN_ID][optional N_TA/N_AE byte][PCI+N_Data].
    /// </summary>
    public static byte[] BuildCanFrame(uint canId, AddressFormat fmt, byte addrExt, ReadOnlySpan<byte> pciAndData)
    {
        int prefixBytes = AddressFormatLayout.AddressPrefixBytes(fmt);
        var buf = new byte[4 + prefixBytes + pciAndData.Length];
        buf[0] = (byte)((canId >> 24) & 0xFF);
        buf[1] = (byte)((canId >> 16) & 0xFF);
        buf[2] = (byte)((canId >> 8) & 0xFF);
        buf[3] = (byte)(canId & 0xFF);
        if (prefixBytes == 1) buf[4] = addrExt;
        pciAndData.CopyTo(buf.AsSpan(4 + prefixBytes));
        return buf;
    }

    public sealed class TxBeginResult
    {
        public IsoFilter? Filter { get; init; }
        public IsoTpTxStateMachine? Tx { get; init; }
        public byte[]? CanFrame { get; init; }
        public NextStep Next { get; init; }
        public bool Started => Filter != null && CanFrame != null;

        public static TxBeginResult NoFilter() => new() { Next = NextStep.None };
    }
}
