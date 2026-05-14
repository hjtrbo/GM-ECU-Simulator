using Common.Protocol;

namespace Core.Bus;

// Condenses long ISO-TP segmented transfers in the bus log. A typical
// flash session sends thousands of $36 TransferData consecutive frames -
// scrolling past those by hand to find the few interesting frames at the
// start/end of each block is hopeless. This filter sits between
// VirtualBus.LogTx/LogRx and the sink: when a First Frame declares more
// CFs than CollapseThreshold can absorb, we let the first HeadCfCount CFs
// through, suppress the middle (CFs + every other frame on that channel),
// then let the last TailCfCount CFs through preceded by a single marker
// line ("[chan 1] -- bulk transfer collapsed: 438 frames hidden --").
//
// State scope is per-channel. We key the source CAN ID inside the state
// so that during the suppression window we can still count CFs from the
// active sender while suppressing noise from other CAN IDs on the same
// channel (functional TesterPresent at $101, FC frames from the receiver
// at $7EA, etc.).
//
// Thread safety: VirtualBus.LogTx/Rx are called off the IPC + scheduler
// threads. All state mutation happens inside a single lock; sink invocation
// is always OUTSIDE the lock so a slow sink can't stall the bus.
//
// Stateful between frames. Reset() clears in-flight state if the user
// toggles the feature off mid-transfer or if a channel closes.
public sealed class BulkTransferCollapser
{
    /// <summary>Number of CFs at the head of the transfer that pass through unchanged.</summary>
    public const int HeadCfCount = 3;

    /// <summary>Number of CFs at the tail of the transfer that pass through unchanged.</summary>
    public const int TailCfCount = 3;

    /// <summary>
    /// Minimum expected CF count for collapsing to engage. Below this we
    /// let the whole transfer through - condensing a 5-CF transfer saves
    /// nothing.
    /// </summary>
    public const int CollapseThreshold = 10;

    // Per-(channel) active collapse state. SourceCanId is the FF sender's
    // CAN ID; only CFs on that ID count against ExpectedCfCount. Suppressed
    // includes both middle-window CFs and any other frame that arrives on
    // the same channel during the suppression band.
    private sealed class ChannelState
    {
        public required uint SourceCanId { get; init; }
        public required int ExpectedCfCount { get; init; }
        public int CfSeen;
        public int SuppressedCount;
    }

    private readonly Dictionary<uint, ChannelState> states = new();
    private readonly object lockObj = new();

    /// <summary>
    /// Decide what to emit for this frame. The collapser may call sink zero
    /// times (frame suppressed), once (frame passed through, OR replaced
    /// with a marker), or twice (marker line followed by the original line
    /// at the head-to-tail boundary).
    /// </summary>
    /// <remarks>
    /// payload is the CAN data bytes only (no ID). prettyLine and csvLine are
    /// the already-formatted representations that the caller would have emitted
    /// directly. The sink receives both so the caller can route each to the
    /// appropriate destination (textbox vs. file) without re-formatting.
    /// </remarks>
    public void Process(uint chId, uint canId, ReadOnlySpan<byte> payload, string prettyLine, string csvLine, Action<string, string> sink)
    {
        // Classify the frame at the ISO-TP layer. Strip the GMLAN extended-
        // addressing byte ($FE all-nodes / $FD gateway on $101) before
        // looking at the PCI nibble. Functional broadcasts are excluded
        // from arming because nobody sends a multi-frame programming load
        // to "all nodes".
        int offset = 0;
        if (canId == GmlanCanId.AllNodesRequest && payload.Length > 0
            && (payload[0] == GmlanCanId.AllNodesExtAddr || payload[0] == GmlanCanId.GatewayExtAddr))
        {
            offset = 1;
        }

        bool isFf = false, isCf = false;
        int ffTotalLen = 0;
        if (payload.Length > offset)
        {
            byte pciNibble = (byte)(payload[offset] & 0xF0);
            if (pciNibble == 0x10 && payload.Length > offset + 1)
            {
                isFf = true;
                ffTotalLen = ((payload[offset] & 0x0F) << 8) | payload[offset + 1];
            }
            else if (pciNibble == 0x20)
            {
                isCf = true;
            }
        }

        bool isFunctional = canId == GmlanCanId.AllNodesRequest
                         || canId == GmlanCanId.Obd2FunctionalRequest;

        string? markerPretty = null;
        string? markerCsv    = null;
        bool emitLine = true;

        lock (lockObj)
        {
            states.TryGetValue(chId, out var state);

            // A new FF on the source CAN ID closes the previous transfer
            // and starts a fresh one. A non-CF/non-FF on the source CAN ID
            // (e.g. an SF carrying a NRC, or a service response) cancels
            // the in-flight collapse so subsequent traffic logs normally.
            if (state != null && canId == state.SourceCanId && !isCf && !isFf)
            {
                states.Remove(chId);
                state = null;
            }

            if (state == null)
            {
                // Idle. Maybe arm on a long FF from a physical addressee.
                if (isFf && !isFunctional)
                {
                    int expectedCfs = ComputeExpectedCfCount(ffTotalLen);
                    if (expectedCfs > CollapseThreshold)
                    {
                        states[chId] = new ChannelState
                        {
                            SourceCanId = canId,
                            ExpectedCfCount = expectedCfs,
                        };
                    }
                }
                // Emit the FF (or whatever this frame is) untouched.
            }
            else if (canId == state.SourceCanId && isFf)
            {
                // FF replacing in-flight transfer (rare - typically the
                // previous one ran to completion and was cleared). Re-arm
                // if the new payload is large enough.
                states.Remove(chId);
                int expectedCfs = ComputeExpectedCfCount(ffTotalLen);
                if (expectedCfs > CollapseThreshold)
                {
                    states[chId] = new ChannelState
                    {
                        SourceCanId = canId,
                        ExpectedCfCount = expectedCfs,
                    };
                }
            }
            else if (canId == state.SourceCanId && isCf)
            {
                state.CfSeen++;
                bool inHead = state.CfSeen <= HeadCfCount;
                int firstTailIndex = state.ExpectedCfCount - TailCfCount + 1;
                bool inTail = state.CfSeen >= firstTailIndex;

                if (!inHead && !inTail)
                {
                    state.SuppressedCount++;
                    emitLine = false;
                }
                else if (inTail && state.CfSeen == firstTailIndex && state.SuppressedCount > 0)
                {
                    // Crossing the suppression-to-tail boundary. Emit a marker in
                    // both formats before the first tail frame.
                    //   pretty: readable summary line for the on-screen textbox
                    //   csv:    same column layout as normal frame rows so the
                    //           spreadsheet stays aligned (empty dir/bytes columns)
                    var hidden = state.SuppressedCount;
                    markerPretty = $"[chan {chId}] -- bulk transfer collapsed: {hidden} frames hidden --";
                    markerCsv    = $"[chan {chId}],,,-- bulk transfer collapsed: {hidden} frames hidden --";
                }

                // Transition out when the predicted CF count is reached.
                if (state.CfSeen >= state.ExpectedCfCount)
                    states.Remove(chId);
            }
            else
            {
                // Non-CF frame on a different CAN ID (or an FF on a different
                // CAN ID) while collapse is active. Pass through if we're in
                // the head or tail window; suppress in the middle.
                bool inHeadWindow = state.CfSeen < HeadCfCount;
                bool inTailWindow = state.CfSeen >= state.ExpectedCfCount - TailCfCount + 1;
                if (!inHeadWindow && !inTailWindow)
                {
                    state.SuppressedCount++;
                    emitLine = false;
                }
            }
        }

        if (markerPretty != null) sink(markerPretty, markerCsv!);
        if (emitLine) sink(prettyLine, csvLine);
    }

    /// <summary>
    /// Drop all in-flight state. Call when the feature is toggled off
    /// mid-transfer so the next time it turns on we don't carry stale
    /// expectations.
    /// </summary>
    public void Reset()
    {
        lock (lockObj) states.Clear();
    }

    // FF declares the total USDT length. The FF frame itself carries 6 bytes
    // of that payload (data bytes after the 2-byte PCI). Each subsequent CF
    // carries 7 bytes (one PCI byte). ceil((total - 6) / 7) is the CF count.
    private static int ComputeExpectedCfCount(int ffTotalLen)
    {
        int remaining = Math.Max(ffTotalLen - 6, 0);
        return (remaining + 6) / 7;  // ceiling division
    }
}
