using Common.Replay;
using Core.Bus;

namespace Core.Replay;

// Owns an IBinSource for the lifetime of a "loaded bin" session and tracks
// the playback state machine: NoBin -> Armed (after Load) -> Running (after
// the first $22/$AA from a connected J2534 host triggers MaybeStart) ->
// Stopped (host disconnect via VirtualBus.HostDisconnected or .IdleReset,
// or all-nodes P3C timeout via TesterPresentTicker calling MaybeStop).
//
// Thread safety: Sample() runs on the DPID scheduler thread, the IPC pipe
// thread, and the WPF UI thread simultaneously. Hot path uses Volatile.Read
// only - no locks. Lifecycle writes (Load/Unload/MaybeStart/MaybeStop) take
// a private gate, but Sample() never does. The two state latches (startMs
// and stoppedAtMs) are mutated only via Interlocked.CompareExchange, so two
// concurrent first-$22 requests can race harmlessly: exactly one CAS wins.
//
// Lifetime: Unload() and Load() do NOT dispose the previous source
// synchronously. The old reference is parked on a 2-second delayed dispose
// so any in-flight Sample() finishes before the underlying memory-mapped
// view is torn down (reading a disposed MemoryMappedViewAccessor is
// undefined behaviour on Windows).
public sealed class BinReplayCoordinator : IDisposable
{
    private readonly object lifecycleGate = new();
    private IBinSource? source;
    private string? loadedFilePath;
    private BinReplayLoopMode loopMode = BinReplayLoopMode.HoldLast;

    // -1L sentinel = "not started"; any other value = bus.NowMs at the
    // moment the first $22/$AA arrived.
    private long startMsLatch = -1L;

    // -1L sentinel = "not stopped"; any other value = bus.NowMs at stop.
    // While set, Sample() uses this in place of busNowMs so playback
    // freezes at the row at-or-before that offset.
    private long stoppedAtMsLatch = -1L;

    public event Action<BinReplayState>? StateChanged;

    /// <summary>Optional bus reference; coordinator subscribes to both
    /// bus.HostDisconnected (pipe drop / clean PassThruClose - the primary
    /// signal) and bus.IdleReset (only fired now by an explicit
    /// IdleBusSupervisor.DoReset; the time-based supervisor was stubbed
    /// 2026-05-15) so any flavour of host-vanish transitions the state
    /// machine to Stopped automatically. MaybeStop is idempotent so a
    /// double-fire is harmless.</summary>
    public BinReplayCoordinator(VirtualBus? bus = null)
    {
        if (bus != null)
        {
            bus.HostDisconnected += OnHostSessionEnded;
            bus.IdleReset        += OnHostSessionEnded;
        }
    }

    private void OnHostSessionEnded()
        => MaybeStop(double.NaN);   // stop reason: host disconnect - bus time irrelevant, freeze "now"

    public BinReplayState State
    {
        get
        {
            if (Volatile.Read(ref source) == null) return BinReplayState.NoBin;
            if (Volatile.Read(ref stoppedAtMsLatch) >= 0) return BinReplayState.Stopped;
            if (Volatile.Read(ref startMsLatch) < 0) return BinReplayState.Armed;
            return BinReplayState.Running;
        }
    }

    public string? FilePath
    {
        get { lock (lifecycleGate) return loadedFilePath; }
    }

    public IReadOnlyList<BinChannelHeader>? ChannelHeaders
        => Volatile.Read(ref source)?.ChannelHeaders;

    public int RowCount => Volatile.Read(ref source)?.RowCount ?? 0;

    public long DurationMs
    {
        get
        {
            var src = Volatile.Read(ref source);
            return src == null || src.RowCount == 0 ? 0 : src.GetElapsedMs(src.RowCount - 1);
        }
    }

    public BinReplayLoopMode LoopMode
    {
        get { lock (lifecycleGate) return loopMode; }
        set { lock (lifecycleGate) loopMode = value; }
    }

    // Toggle the user has set in the Bin Replay tab. Not consumed by the
    // coordinator itself - it's a hint round-tripped through ecu_config.json
    // so App.OnStartup can decide whether to call Load on next launch.
    public bool PersistedAutoLoadOnStart { get; set; }

    /// <summary>
    /// Replaces the current source with <paramref name="newSource"/>. Resets
    /// the playback state to Armed (the next $22/$AA will start the clock).
    /// The old source, if any, is parked for late disposal.
    /// </summary>
    public void Load(IBinSource newSource, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(newSource);
        IBinSource? old;
        lock (lifecycleGate)
        {
            old = source;
            source = newSource;
            loadedFilePath = path;
            // Reset latches via direct write - under the gate, no readers
            // need a fence (Volatile.Read picks up the new value next call).
            Volatile.Write(ref startMsLatch, -1L);
            Volatile.Write(ref stoppedAtMsLatch, -1L);
        }
        if (old != null) ScheduleLateDispose(old);
        StateChanged?.Invoke(State);
    }

    /// <summary>Drops the current source (if any) and returns the state machine
    /// to NoBin. The dropped source is parked for late disposal.</summary>
    public void Unload()
    {
        IBinSource? old;
        lock (lifecycleGate)
        {
            old = source;
            source = null;
            loadedFilePath = null;
            Volatile.Write(ref startMsLatch, -1L);
            Volatile.Write(ref stoppedAtMsLatch, -1L);
        }
        if (old != null) ScheduleLateDispose(old);
        StateChanged?.Invoke(State);
    }

    /// <summary>
    /// Idempotent latch of the playback start time. Called by VirtualBus on
    /// every $22 / $AA. Three cases:
    ///   - NoBin: no-op.
    ///   - Stopped (host previously disconnected / all-nodes-idle): clear
    ///     the stop latch and the old start latch, then latch the new start
    ///     so playback restarts from t=0 of the bin. This is what makes
    ///     "disconnect / reconnect" cycle work without an explicit Unload.
    ///   - Armed: latch the start time. Running: no-op (already latched).
    /// All transitions go through CAS so concurrent first-requests on
    /// different ECUs cannot both win.
    /// </summary>
    public void MaybeStart(double busNowMs)
    {
        if (Volatile.Read(ref source) == null) return;

        // Stopped -> fresh request: re-arm. CAS the stop latch back to -1;
        // whichever thread wins also clears the start latch so the start
        // CAS below takes against -1.
        long stoppedAt = Volatile.Read(ref stoppedAtMsLatch);
        if (stoppedAt >= 0)
        {
            if (Interlocked.CompareExchange(ref stoppedAtMsLatch, -1L, stoppedAt) == stoppedAt)
                Volatile.Write(ref startMsLatch, -1L);
        }

        long original = Interlocked.CompareExchange(ref startMsLatch, (long)busNowMs, -1L);
        if (original == -1L) StateChanged?.Invoke(State);
    }

    /// <summary>
    /// Idempotent first-write of the stop latch. Called by both stop paths
    /// (VirtualBus.HostDisconnected / IdleReset, and TesterPresentTicker's
    /// all-nodes-idle check). busNowMs may be NaN (host-disconnect path
    /// doesn't have a meaningful time; freeze at the current playback offset
    /// using the bus clock at the time of the first subsequent Sample).
    /// </summary>
    public void MaybeStop(double busNowMs)
    {
        if (Volatile.Read(ref source) == null) return;
        if (Volatile.Read(ref startMsLatch) < 0) return;
        long stopAt = double.IsNaN(busNowMs) ? long.MaxValue : (long)busNowMs;
        long original = Interlocked.CompareExchange(ref stoppedAtMsLatch, stopAt, -1L);
        if (original == -1L) StateChanged?.Invoke(State);
    }

    /// <summary>
    /// Returns the bin sample at the current playback offset. The contract
    /// matches IWaveformGenerator.Sample - safe to call from any thread,
    /// any number of times per millisecond.
    /// </summary>
    public double Sample(int channelIndex, double busNowMs)
    {
        var src = Volatile.Read(ref source);
        if (src == null || src.RowCount == 0) return 0.0;
        if ((uint)channelIndex >= (uint)src.ChannelCount) return 0.0;

        long start = Volatile.Read(ref startMsLatch);
        if (start < 0)
        {
            // Armed: not yet started. Show row 0 in the editor's Live column
            // so the user sees realistic values before the host kicks in.
            return src.GetValue(channelIndex, 0);
        }

        long stoppedAt = Volatile.Read(ref stoppedAtMsLatch);
        long timeRefMs;
        if (stoppedAt == long.MaxValue)
        {
            // Host-disconnect path - latch the freeze offset on the first sample
            // after stop. CompareExchange so concurrent first-samples agree.
            long now = (long)busNowMs;
            Interlocked.CompareExchange(ref stoppedAtMsLatch, now, long.MaxValue);
            timeRefMs = Volatile.Read(ref stoppedAtMsLatch);
        }
        else if (stoppedAt >= 0)
        {
            timeRefMs = stoppedAt;
        }
        else
        {
            timeRefMs = (long)busNowMs;
        }

        long replayMs = timeRefMs - start;
        if (replayMs < 0) replayMs = 0;

        long lastRowMs = src.GetElapsedMs(src.RowCount - 1);
        if (replayMs > lastRowMs)
        {
            switch (loopMode)
            {
                case BinReplayLoopMode.HoldLast:
                    replayMs = lastRowMs;
                    break;
                case BinReplayLoopMode.Loop:
                    long duration = lastRowMs > 0 ? lastRowMs : 1;
                    replayMs = replayMs % duration;
                    break;
                case BinReplayLoopMode.Stop:
                    // Auto-transition to Stopped at the moment we exceeded
                    // the bin's duration. Freeze at lastRow.
                    Interlocked.CompareExchange(ref stoppedAtMsLatch, start + lastRowMs, -1L);
                    replayMs = lastRowMs;
                    break;
            }
        }

        int row = FindRowAtOrBefore(src, replayMs);
        return src.GetValue(channelIndex, row);
    }

    /// <summary>
    /// Returns the playback offset (0..DurationMs) the coordinator would use
    /// for a Sample call right now at <paramref name="busNowMs"/>. Provided
    /// for the UI's "elapsed / total" display - reads only, never mutates state.
    /// </summary>
    public long ElapsedAt(double busNowMs)
    {
        var src = Volatile.Read(ref source);
        if (src == null || src.RowCount == 0) return 0;
        long start = Volatile.Read(ref startMsLatch);
        if (start < 0) return 0;
        long stoppedAt = Volatile.Read(ref stoppedAtMsLatch);
        long timeRefMs = stoppedAt switch
        {
            long.MaxValue => (long)busNowMs,
            >= 0 => stoppedAt,
            _ => (long)busNowMs,
        };
        long replayMs = timeRefMs - start;
        if (replayMs < 0) return 0;
        long lastRowMs = src.GetElapsedMs(src.RowCount - 1);
        return replayMs > lastRowMs ? lastRowMs : replayMs;
    }

    // Pure binary search for the largest row whose elapsed time is at-or-before
    // targetMs. Same algorithm as the DataLogger's App.FindRowAtOrBefore.
    public static int FindRowAtOrBefore(IBinSource src, long targetMs)
    {
        int lo = 0, hi = src.RowCount - 1, ans = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (src.GetElapsedMs(mid) <= targetMs) { ans = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return ans < 0 ? 0 : ans;
    }

    // 2 s is generous - the longest in-flight Sample() takes microseconds.
    private static void ScheduleLateDispose(IBinSource old)
    {
        Task.Delay(2000).ContinueWith(_ =>
        {
            try { old.Dispose(); } catch { /* swallow - best-effort */ }
        });
    }

    public void Dispose() => Unload();
}
