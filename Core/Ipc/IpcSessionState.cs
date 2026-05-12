using System.Collections.Concurrent;
using Common.PassThru;
using Core.Bus;

namespace Core.Ipc;

// Per-pipe-connection state: handle ID allocator + open J2534 channels for
// the host on the other end of this pipe. Each PassThru shim instance gets
// its own IpcSessionState so handle IDs don't collide across hosts.
public sealed class IpcSessionState : IDisposable
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
        // When the bus supervisor flags an idle reset, cancel every periodic
        // timer this session owns. The host is gone; the synthetic frames the
        // timers would otherwise emit have nowhere to go.
        Bus.IdleReset += OnBusIdleReset;
    }

    private void OnBusIdleReset()
    {
        // Cancel host-driven periodic timers — there's no host left to receive
        // their output.
        foreach (var kv in periodicTimers.ToArray())
            RemovePeriodicTimer(kv.Key);

        // Drain every channel's RxQueue. Anything still sitting unread after
        // 10s of host silence is stale — most commonly the unsolicited $60
        // emitted by the regular P3C ticker just before the host vanished.
        // Leaving these in place causes them to be returned FIFO to the next
        // ReadMsgs call when a new session starts, which the host interprets
        // as "session terminated" and aborts the new session start.
        int drainedTotal = 0;
        foreach (var ch in channels.Values)
        {
            int before = ch.RxQueue.Count;
            ch.ClearRxQueue();
            drainedTotal += before;
        }
        if (drainedTotal > 0)
            Bus.LogDiagnostic?.Invoke($"[idle] drained {drainedTotal} stale Rx frame(s) from channel queues");
    }

    public void Dispose()
    {
        Bus.IdleReset -= OnBusIdleReset;
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
        // Clear LastEnhancedChannel on every node where it pointed at this
        // session — otherwise a later P3C timeout would enqueue an unsolicited
        // $60 onto a now-orphaned channel that no host is reading from.
        foreach (var node in Bus.Nodes)
            node.State.ClearLastEnhancedChannelIf(ch);
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
