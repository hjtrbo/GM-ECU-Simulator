using System.Collections.Concurrent;
using Common.PassThru;

namespace Core.Bus;

// Per-J2534-channel state owned by an IPC session. Holds the pending Rx
// queue (frames the simulator is delivering UP to the host) and the
// channel's filter table.
public sealed class ChannelSession
{
    public required uint Id { get; init; }
    public required ProtocolID Protocol { get; init; }
    public required uint Baud { get; init; }

    /// <summary>
    /// Per-channel ISO 15765-2 transport context. Set by the dispatcher when
    /// the channel is opened with <see cref="ProtocolID.ISO15765"/>; <c>null</c>
    /// for raw CAN channels. When set, <see cref="EnqueueRx"/> routes inbound
    /// frames through the TP state machines instead of pushing them onto
    /// <see cref="RxQueue"/> for the host to read raw.
    ///
    /// Typed as <c>object</c> here because Core does not depend on Shim;
    /// Shim's IsoChannel-aware EnqueueRx delegates to <see cref="IsoChannelInbound"/>
    /// when set. Shim wires that delegate at channel-create time.
    /// </summary>
    public object? IsoChannel { get; set; }

    /// <summary>
    /// Delegate the dispatcher wires when it constructs an ISO15765 channel.
    /// Returns true if the inbound CAN frame was consumed by the TP layer
    /// (do NOT also push onto RxQueue); false to fall through to the normal
    /// CAN-channel path.
    /// </summary>
    public Func<uint, byte[], bool>? IsoChannelInbound { get; set; }

    // J2534 v04.04 PassThruConnect flags. Bit definitions (subset):
    //   0x0100  CAN_29BIT_ID            — channel uses 29-bit extended IDs
    //   0x0200  ISO9141_NO_CHECKSUM     — n/a for CAN
    //   0x0800  CAN_ID_BOTH             — accept both 11- and 29-bit IDs
    // Captured at PassThruConnect time. The bus does NOT yet consult these
    // for routing — EcuNode CAN ID fields are 16-bit and TX flag forwarding
    // is not wired. Storing the value lets a host-flag-aware future change
    // build on it without another wire-format bump.
    public uint ConnectFlags { get; init; }
    public bool Use29BitId => (ConnectFlags & 0x0100) != 0;

    // Optional back-reference to the bus so EnqueueRx can route frames
    // through bus.LogFrame for the UI's "Bus log" tab. Tests construct
    // standalone ChannelSession instances without setting this and the
    // logging is silently skipped.
    public VirtualBus? Bus { get; init; }

    public ConcurrentQueue<PassThruMsg> RxQueue { get; } = new();
    public List<ChannelFilter> Filters { get; } = new();

    // Released once per EnqueueRx so a parked ReadMsgs caller wakes up at
    // most one extra time. Initial count 0; max int.MaxValue (queue grows
    // freely under load — readers will drain whatever is queued).
    public SemaphoreSlim RxAvailable { get; } = new(0, int.MaxValue);

    // J2534 SET_CONFIG / GET_CONFIG storage (per-channel parameter map).
    // Parameter ID is the J2534 ConfigParameter enum value (CAN_TIMEOUT, etc.).
    private readonly ConcurrentDictionary<uint, uint> sConfigValues = new();

    private readonly Lock filtersLock = new();

    public void AddFilter(ChannelFilter f) { lock (filtersLock) Filters.Add(f); }
    public bool RemoveFilter(uint id)
    {
        lock (filtersLock) return Filters.RemoveAll(f => f.Id == id) > 0;
    }
    public void ClearFilters() { lock (filtersLock) Filters.Clear(); }

    public void ClearRxQueue()
    {
        while (RxQueue.TryDequeue(out _)) { }
    }

    public uint GetConfig(uint paramId)
        => sConfigValues.TryGetValue(paramId, out var v) ? v : 0;
    public void SetConfig(uint paramId, uint value)
        => sConfigValues[paramId] = value;

    // Enqueue an Rx frame for delivery up to the J2534 host. If filters are
    // configured, the frame must pass at least one PASS_FILTER (or have no
    // BLOCK_FILTER match) to be delivered.
    //
    // ISO15765 routing: if an Iso15765Channel is attached, the raw CAN frame
    // is offered to the TP layer first. When the TP layer consumes it (i.e.
    // it matches a FlowControl filter), we do NOT also push the raw frame
    // onto the user-visible RxQueue - the host on an ISO15765 channel reads
    // reassembled payloads from the IsoChannel's queue, not raw CAN frames.
    public void EnqueueRx(PassThruMsg msg)
    {
        if (Protocol == ProtocolID.ISO15765 && IsoChannelInbound != null && msg.Data.Length >= 4)
        {
            uint canId = ((uint)msg.Data[0] << 24) | ((uint)msg.Data[1] << 16)
                       | ((uint)msg.Data[2] << 8)  | msg.Data[3];
            if (IsoChannelInbound(canId, msg.Data))
            {
                Bus?.LogRx(Id, msg.Data);
                return;
            }
            // Unmatched: fall through to the raw CAN path (uncommon for
            // ISO15765, but keeps stray frames visible if the host enabled
            // diagnostic listening without a FlowControl filter).
        }

        if (!ShouldDeliver(msg.Data))
        {
            Bus?.LogRxFiltered(Id, msg.Data);
            return;
        }
        Bus?.LogRx(Id, msg.Data);
        RxQueue.Enqueue(msg);
        RxAvailable.Release();      // wake any ReadMsgs caller parked on this channel
    }

    private bool ShouldDeliver(ReadOnlySpan<byte> frame)
    {
        bool anyPass = false, anyPassMatch = false;
        lock (filtersLock)
        {
            foreach (var f in Filters)
            {
                if (f.Type == FilterType.BLOCK_FILTER && f.Matches(frame))
                    return false;
                if (f.Type == FilterType.PASS_FILTER || f.Type == FilterType.FLOW_CONTROL_FILTER)
                {
                    anyPass = true;
                    if (f.Matches(frame)) anyPassMatch = true;
                }
            }
        }
        // No PASS filter at all -> deliver everything (matching DataLogger expectations
        // until it sets one). PASS filter present -> require a match.
        return !anyPass || anyPassMatch;
    }
}
