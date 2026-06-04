using Core.Bus;
using Core.Ecu;
using Core.Transport;
using Core.Utilities;

namespace Core.Scheduler;

// Emits the ECUs' DBC-driven CAN broadcast messages as unsolicited raw frames - the "background
// traffic" a passive logger expects. Mirrors DpidScheduler: one AutoRestart TimerOnDelay per enabled
// (node, message), all sharing the single high-priority TimerScheduler thread, so free-form periods
// (16 / 48 / 992 ms ...) hold sub-millisecond jitter regardless of how many messages are active.
//
// Unlike DpidScheduler (which enqueues onto one channel's RX queue), broadcast frames go through
// bus.Broadcaster, which offers each frame to EVERY open channel; the host's per-channel filter
// decides delivery. Emission is therefore meaningful only while a host session is open - the
// composition root drives RebuildAndStart on HostConnected and StopAll on HostDisconnected.
public sealed class BroadcastScheduler : IDisposable
{
    private readonly VirtualBus bus;
    private readonly List<Entry> entries = new();
    private readonly Lock entriesLock = new();
    private bool running;

    public BroadcastScheduler(VirtualBus bus) { this.bus = bus; }

    /// <summary>Rebuild every timer from the current node/broadcast config and start them. Idempotent
    /// (stops existing entries first). Call on host-connect, and again whenever the broadcast config
    /// changes while a session is live.</summary>
    public void RebuildAndStart()
    {
        StopAll();
        var fresh = new List<Entry>();
        foreach (var node in bus.Nodes)
            foreach (var msg in node.Broadcasts)
            {
                if (!msg.Enabled || msg.PeriodMs <= 0) continue;   // disabled / event-driven (period 0) -> not scheduled
                fresh.Add(new Entry(node, msg, bus));
            }

        lock (entriesLock)
        {
            running = true;
            entries.AddRange(fresh);
        }
        foreach (var e in fresh) e.Start();
    }

    /// <summary>Stop and drop every timer. Safe to call repeatedly.</summary>
    public void StopAll()
    {
        Entry[] toStop;
        lock (entriesLock)
        {
            running = false;
            toStop = entries.ToArray();
            entries.Clear();
        }
        foreach (var e in toStop) e.Stop();
    }

    /// <summary>Rebuild only when a session is currently live (config edited mid-session); a no-op
    /// otherwise so an editor save doesn't start emitting with no host attached.</summary>
    public void RebuildIfRunning()
    {
        bool r;
        lock (entriesLock) r = running;
        if (r) RebuildAndStart();
    }

    public void Dispose() => StopAll();

    private sealed class Entry
    {
        private readonly EcuNode node;
        private readonly BroadcastMessage msg;
        private readonly VirtualBus bus;
        private readonly TimerOnDelay timer;

        public Entry(EcuNode node, BroadcastMessage msg, VirtualBus bus)
        {
            this.node = node;
            this.msg = msg;
            this.bus = bus;
            timer = new TimerOnDelay
            {
                Preset = msg.PeriodMs,
                AutoRestart = true,
                DebugInstanceName = $"BroadcastScheduler[{node.Name}]",
                DebugTimerName = $"BCAST {msg.CanId:X3} @{msg.PeriodMs}ms",
            };
            timer.OnTimingDone += (_, _) => Tick();
        }

        public void Start() => timer.Start();
        public void Stop() => timer.Stop();

        private void Tick()
        {
            var broadcaster = bus.Broadcaster;
            if (broadcaster == null) return;          // no open channel -> nowhere to deliver
            var payload = msg.BuildPayload(node.EngineModel, bus.NowMs);
            var frame = new byte[CanFrame.IdBytes + payload.Length];
            CanFrame.WriteId(frame, msg.CanId);
            payload.CopyTo(frame, CanFrame.IdBytes);
            broadcaster.BroadcastFrame(frame);
        }
    }
}
