using Common.PassThru;
using Common.Protocol;
using Core.Bus;
using Core.Ecu;
using Core.Transport;
using Core.Utilities;

namespace Core.Scheduler;

// Three-band periodic DPID scheduler. Each scheduled (node, dpid, channel)
// triple owns its own TimerOnDelay configured with AutoRestart and a
// Slow/Med/Fast period preset. All TimerOnDelay instances share the single
// high-priority polling thread inside TimerScheduler — sub-millisecond
// jitter regardless of how many DPIDs are active.
//
// Periods follow GMW3110 §8.20 default timing and the DataLogger convention
// in `Gm Data Logger_v5_Wpf_WIP/Core/Simulator/ServiceAAHandler.cs:64,84,104`.
public sealed class DpidScheduler : IDisposable
{
    public const int SlowPeriodMs = 1000;
    public const int MediumPeriodMs = 100;
    public const int FastPeriodMs = 40;

    private readonly VirtualBus bus;
    private readonly List<Entry> entries = new();
    private readonly Lock entriesLock = new();

    public DpidScheduler(VirtualBus bus) { this.bus = bus; }

    /// <summary>The bus this scheduler belongs to. Lets handlers reachable only
    /// via the scheduler argument (e.g. EcuExitLogic) access bus-level state
    /// such as CaptureSettings without an extra parameter on every entry point.</summary>
    public VirtualBus Bus => bus;

    // No-op now — kept so callers don't have to change. Each entry's timer
    // starts itself when added; the global TimerScheduler thread is lazy.
    public void Start() { }

    public void Add(EcuNode node, Dpid dpid, ChannelSession ch, DpidRate rate)
    {
        // Update-or-add: if already scheduled, drop the existing entry (any rate)
        // before installing the new one. Matches GMW3110 §8.20 "a re-issued $AA
        // can change a DPID's rate".
        RemoveEntry(node, dpid.Id);

        int periodMs = rate switch
        {
            DpidRate.Slow => SlowPeriodMs,
            DpidRate.Medium => MediumPeriodMs,
            DpidRate.Fast => FastPeriodMs,
            _ => 0,
        };
        if (periodMs == 0) return;

        var entry = new Entry(node, dpid, ch, bus, periodMs);
        lock (entriesLock) entries.Add(entry);
        entry.Start();
    }

    // Stops periodic delivery of the given DPIDs. Empty list = stop ALL on
    // this node.
    public void Stop(EcuNode node, IReadOnlyList<byte> dpidIds)
    {
        Entry[] toStop;
        lock (entriesLock)
        {
            toStop = dpidIds.Count == 0
                ? entries.Where(e => e.Node == node).ToArray()
                : entries.Where(e => e.Node == node && dpidIds.Contains(e.Dpid.Id)).ToArray();
            foreach (var e in toStop) entries.Remove(e);
        }
        foreach (var e in toStop) e.Stop();
    }

    public void StopAllForChannel(ChannelSession ch)
    {
        Entry[] toStop;
        lock (entriesLock)
        {
            toStop = entries.Where(e => e.Channel == ch).ToArray();
            foreach (var e in toStop) entries.Remove(e);
        }
        foreach (var e in toStop) e.Stop();
    }

    private void RemoveEntry(EcuNode node, byte dpidId)
    {
        Entry? toStop = null;
        lock (entriesLock)
        {
            int i = entries.FindIndex(e => e.Node == node && e.Dpid.Id == dpidId);
            if (i >= 0)
            {
                toStop = entries[i];
                entries.RemoveAt(i);
            }
        }
        toStop?.Stop();
    }

    public void Dispose()
    {
        Entry[] toStop;
        lock (entriesLock) { toStop = entries.ToArray(); entries.Clear(); }
        foreach (var e in toStop) e.Stop();
    }

    // ---------------- per-entry timer wrapper ----------------

    private sealed class Entry
    {
        public EcuNode Node { get; }
        public Dpid Dpid { get; }
        public ChannelSession Channel { get; }
        private readonly VirtualBus bus;
        private readonly TimerOnDelay timer;
        private readonly int periodMs;

        public Entry(EcuNode node, Dpid dpid, ChannelSession ch, VirtualBus bus, int periodMs)
        {
            Node = node;
            Dpid = dpid;
            Channel = ch;
            this.bus = bus;
            this.periodMs = periodMs;
            timer = new TimerOnDelay
            {
                Preset = periodMs,
                AutoRestart = true,
                DebugInstanceName = $"DpidScheduler[{node.Name}]",
                DebugTimerName = $"DPID 0x{dpid.Id:X2} @{periodMs}ms",
            };
            timer.OnTimingDone += (_, e) => Tick(e);
        }

        public void Start() => timer.Start();
        public void Stop() => timer.Stop();

        private void Tick(TimerDoneEventArgs e)
        {
            // STALL DETECTOR -- KEEP THIS. Tracks an intermittent host-stall bug
            // first observed 2026-05-10 where every DPID stream visible to the
            // J2534 host (e.g. the sibling DataLogger) flat-lines simultaneously
            // for ~0.5-1 s at roughly 5 s intervals, but only when the simulator
            // window has focus. Cause not yet identified; repro is intermittent.
            // The 3x-period threshold catches both the timer-thread side (this
            // line will fire) and rules it out (this line stays silent during a
            // glitch -> stall is downstream of DPID emission, i.e. the IPC pipe
            // write path). DO NOT remove until the root cause is identified.
            if (e.ElapsedMsDelta > 3L * periodMs)
                bus.LogDiagnostic?.Invoke(
                    $"[stall] DPID 0x{Dpid.Id:X2} on '{Node.Name}' fired {e.ElapsedMsDelta} ms after previous (period={periodMs} ms)");

            var frame = BuildUudtFrame(Node, Dpid, bus.NowMs);
            Channel.EnqueueRx(new PassThruMsg
            {
                ProtocolID = ProtocolID.CAN,
                Data = frame,
            });
        }
    }

    // Build a single UUDT frame: bytes [0..3] = UUDT response CAN ID,
    // byte [4] = DPID id, bytes [5..] = concatenated PID values (big-endian).
    public static byte[] BuildUudtFrame(EcuNode node, Dpid dpid, double timeMs)
    {
        int valueBytes = 0;
        foreach (var p in dpid.Pids) valueBytes += (int)p.Size;
        var buf = new byte[CanFrame.IdBytes + 1 + valueBytes];
        CanFrame.WriteId(buf, node.UudtResponseCanId);
        buf[CanFrame.IdBytes] = dpid.Id;
        int pos = CanFrame.IdBytes + 1;
        foreach (var p in dpid.Pids)
        {
            ValueCodec.Encode(
                p.Waveform.Sample(timeMs),
                p.Scalar, p.Offset, p.DataType, (int)p.Size,
                buf.AsSpan(pos, (int)p.Size));
            pos += (int)p.Size;
        }
        return buf;
    }

    public void SendOnce(EcuNode node, Dpid dpid, ChannelSession ch)
    {
        ch.EnqueueRx(new PassThruMsg
        {
            ProtocolID = ProtocolID.CAN,
            Data = BuildUudtFrame(node, dpid, bus.NowMs),
        });
    }
}
