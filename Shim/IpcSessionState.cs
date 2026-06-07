using Common.PassThru;
using Core.Bus;
using System.Collections.Concurrent;

namespace Shim.Ipc;

// Per-pipe-connection state: handle ID allocator + open J2534 channels for
// the host on the other end of this pipe. Each PassThru shim instance gets
// its own IpcSessionState so handle IDs don't collide across hosts.
//
// Also implements IFrameBroadcaster so Core-side handlers can shove raw
// CAN frames at every channel without needing a direct reference to the
// channel collection (Core can't reference Shim). Used by FordUdsPersona
// to emit UUDT broadcasts on 0x6A0/0x6A1 - PCMTec's PASS_FILTER for those
// IDs lives on a different J2534 channel than the diagnostic request,
// so the request channel's EnqueueRx doesn't reach it. BroadcastFrame
// offers the frame to every channel and each channel applies its own
// filter table.
public sealed class IpcSessionState : IDisposable, IFrameBroadcaster
{
    public VirtualBus Bus { get; }

    private uint nextDevice = 1;
    private uint nextChannel = 1;
    private uint nextFilter = 1;
    private uint nextPeriodic = 1;
    private readonly Lock alloc = new();

    private readonly ConcurrentDictionary<uint, ChannelSession> channels = new();

    // J2534 PassThruStartPeriodicMsg timers. Key = periodic ID allocated by
    // AllocatePeriodicId. Timer fires at the host-registered interval and
    // dispatches the registered frame through the bus as if WriteMsgs sent it.
    private readonly ConcurrentDictionary<uint, (uint channelId, Timer timer)> periodicTimers = new();

    public IpcSessionState(VirtualBus bus)
    {
        Bus = bus;
        // Subscription kept for the explicit force-idle path
        // (IdleBusSupervisor.DoReset). The time-based supervisor that used
        // to fire this on a schedule was stubbed 2026-05-15 - pipe drop
        // (Dispose, below) is now the authoritative "host is gone" signal
        // and runs the same periodic-timer teardown.
        Bus.IdleReset += OnBusIdleReset;

        // Wire the cross-channel broadcaster. Ford UDS persona's UUDT
        // emitter uses this to push frames at every channel; each channel's
        // filter table decides delivery.
        Bus.Broadcaster = this;
    }

    /// <summary>
    /// IFrameBroadcaster: offer a raw frame to every channel. Each channel
    /// applies its own PASS / BLOCK filter table inside EnqueueRx, so a
    /// 0x6A0-shaped frame only lands on channels that have a matching
    /// PASS_FILTER (which PCMTec installed on its UUDT listener channel).
    /// Safe to call concurrently with channel allocation - the underlying
    /// ConcurrentDictionary tolerates iteration during writes.
    /// </summary>
    public void BroadcastFrame(byte[] frame)
    {
        foreach (var ch in channels.Values)
        {
            ch.EnqueueRx(new PassThruMsg
            {
                ProtocolID = ProtocolID.CAN,
                Data = frame,
            });
        }
    }

    private void OnBusIdleReset()
    {
        // Cancel host-driven periodic timers - there's no host left to receive
        // their output.
        foreach (var kv in periodicTimers.ToArray())
            RemovePeriodicTimer(kv.Key);

        // Drain every channel's RxQueue. Anything still sitting unread is
        // stale - most commonly the unsolicited $60 emitted by the per-ECU
        // P3C ticker just before the host vanished. Leaving these in place
        // causes them to be returned FIFO to the next ReadMsgs call when a
        // new session starts, which the host interprets as "session
        // terminated" and aborts the new session start.
        int drainedTotal = 0;
        foreach (var ch in channels.Values)
        {
            int before = ch.RxQueue.Count;
            ch.ClearRxQueue();
            drainedTotal += before;
        }
        if (drainedTotal > 0)
            Bus.LogJ2534?.Invoke($"[idle] drained {drainedTotal} stale Rx frame(s) from channel queues");
    }

    public void Dispose()
    {
        Bus.IdleReset -= OnBusIdleReset;
        // Tear down the broadcaster registration so a subsequent session
        // (next host connect) rebinds against a live IpcSessionState; the
        // old one would be calling EnqueueRx on disposed channels otherwise.
        if (ReferenceEquals(Bus.Broadcaster, this))
            Bus.Broadcaster = null;
        // Dispose any remaining periodic timers (channel disconnect path
        // should have already cleared them, but belt-and-braces).
        foreach (var kv in periodicTimers.ToArray())
            RemovePeriodicTimer(kv.Key);
    }

    public uint AllocateDeviceId()   { lock (alloc) return nextDevice++; }
    public uint AllocateFilterId()   { lock (alloc) return nextFilter++; }
    public uint AllocatePeriodicId() { lock (alloc) return nextPeriodic++; }

    public ChannelSession AllocateChannel(ProtocolID proto, uint baud, uint connectFlags = 0)
    {
        uint id;
        lock (alloc) id = nextChannel++;
        var ch = new ChannelSession { Id = id, Protocol = proto, Baud = baud, ConnectFlags = connectFlags, Bus = Bus };
        channels[id] = ch;
        return ch;
    }

    public bool TryGetChannel(uint id, out ChannelSession ch)
    {
        if (channels.TryGetValue(id, out var got)) { ch = got; return true; }
        ch = null!;
        return false;
    }

    public bool RemoveChannel(uint id)
    {
        if (!channels.TryRemove(id, out var ch)) return false;
        // Drop any periodic DPID schedule entries that targeted this channel.
        Bus.Scheduler.StopAllForChannel(ch);
        // Cancel any J2534 periodic messages registered against this channel.
        StopAllPeriodicForChannel(id);
        // Per-node cleanup. Two distinct concerns:
        //   1. LastEnhancedChannel - so a later P3C timeout doesn't enqueue an
        //      unsolicited $60 onto an orphaned channel no host is reading from.
        //   2. Per-node IsoTpFragmenter - if the host vanished mid-multi-frame,
        //      the fragmenter is holding `activeChannel == ch` and a pending
        //      N_Bs / STmin TimerOnDelay that would otherwise fire EnqueueRx
        //      onto this disposed channel.
        foreach (var node in Bus.Nodes)
        {
            node.State.ClearLastEnhancedChannelIf(ch);
            node.State.Fragmenter.AbortIfActiveOn(ch);
        }
        return true;
    }

    public void AddPeriodicTimer(uint periodicId, uint channelId, Timer timer)
        => periodicTimers[periodicId] = (channelId, timer);

    public void RemovePeriodicTimer(uint periodicId)
    {
        if (periodicTimers.TryRemove(periodicId, out var entry))
            entry.timer.Dispose();
    }

    public void StopAllPeriodicForChannel(uint channelId)
    {
        foreach (var kv in periodicTimers.ToArray())
            if (kv.Value.channelId == channelId)
                RemovePeriodicTimer(kv.Key);
    }
}
