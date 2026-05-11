// Ported verbatim from `Gm Data Logger_v5_Wpf_WIP/Core/Utilities/Timers.cs`.
// Single shared high-priority polling thread with a min-heap of pending
// deadlines — sub-millisecond accuracy regardless of how many timers are
// active. Used here by DpidScheduler and TesterPresentTicker so we don't
// need our own per-band threads.
using System.Diagnostics;

namespace Core.Utilities;

/// <summary>
/// A software on-delay timer with sub-millisecond accuracy, supporting one-shot,
/// auto-restart (endless), and auto-stop (counted-restart) modes.
/// </summary>
public class TimerOnDelay
{
    public int Preset { get; set; }

    public int Accumulator
    {
        get
        {
            if (!_running) return 0;
            long remainingUs = _nextDeadlineUs - TimerScheduler.Instance.NowMicros;
            return remainingUs > 0 ? (int)(remainingUs / 1000) : 0;
        }
    }

    private volatile bool _timerTiming;
    public bool TimerTiming => _timerTiming;
    public bool TimerDone => !_timerTiming;
    public bool TimerDoneOnce { get; set; }

    private volatile bool _running;
    public bool Running => _running;

    public bool AutoRestart
    {
        get => _autoRestart;
        set { ThrowIfRunning(nameof(AutoRestart)); _autoRestart = value; }
    }
    private bool _autoRestart;

    private volatile bool _autoRestartInhibit;
    public bool AutoRestartInhibit
    {
        get => _autoRestartInhibit;
        set => _autoRestartInhibit = value;
    }

    public bool AutoStop
    {
        get => _autoStop;
        set { ThrowIfRunning(nameof(AutoStop)); _autoStop = value; }
    }
    private bool _autoStop;

    public int AutoStopPreset
    {
        get => _autoStopPreset;
        set { ThrowIfRunning(nameof(AutoStopPreset)); _autoStopPreset = value; }
    }
    private int _autoStopPreset;

    public int AutoStopAccumulator { get; private set; }

    public string DebugInstanceName { get; set; } = string.Empty;
    public string DebugTimerName { get; set; } = string.Empty;
    public SynchronizationContext? SyncContext { get; set; }

    public long IgnoreIfLateByMs { get; set; }

    public event EventHandler<TimerDoneEventArgs>? OnTimingDone;

    private long _nextDeadlineUs;
    private long _lastFireAbsoluteUs;
    private long _lastFireDeltaUs;
    private bool _autoRestartActive;
    private bool _autoStopActive;
    private readonly object _sync = new();

    public TimerOnDelay() { }

    public void Start(SynchronizationContext syncContext)
    {
        SyncContext = syncContext;
        _Start();
    }

    public void Start() => _Start();

    private void _Start()
    {
        long firstDeadlineUs;

        lock (_sync)
        {
            if (Preset <= 0)
                throw new ArgumentOutOfRangeException(nameof(Preset), "Preset must be > 0 ms");
            if (_autoStop && _autoRestart)
                throw new InvalidOperationException("Cannot have AutoStop and AutoRestart set true at the same time");
            if (_autoStop && _autoStopPreset <= 0)
                throw new ArgumentOutOfRangeException(nameof(AutoStopPreset), "AutoStopPreset must be > 0");
            if (_autoRestartInhibit && !_autoRestart)
                throw new InvalidOperationException("AutoRestartInhibit requires AutoRestart");

            if (_running)
            {
                if (_autoStopActive) AutoStopAccumulator = _autoStopPreset;
                return;
            }

            _autoRestartActive = _autoRestart;
            _autoStopActive    = _autoStop;
            if (_autoStopActive) AutoStopAccumulator = _autoStopPreset;

            long now = TimerScheduler.Instance.NowMicros;
            firstDeadlineUs     = now + (long)Preset * 1000;
            _nextDeadlineUs     = firstDeadlineUs;
            _lastFireAbsoluteUs = now;
            _lastFireDeltaUs    = 0;

            _running     = true;
            _timerTiming = true;
        }

        TimerScheduler.Instance.Schedule(this, firstDeadlineUs);
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (!_running) return;
            _running             = false;
            _timerTiming         = false;
            AutoStopAccumulator  = 0;
            _nextDeadlineUs      = 0;
            _lastFireAbsoluteUs  = 0;
            _lastFireDeltaUs     = 0;
        }
    }

    internal void OnSchedulerFire(long fireMomentUs)
    {
        TimerDoneEventArgs? args         = null;
        long                rescheduleAt = 0;
        bool                willStop     = false;
        bool                lateSkip;
        long                lateByUs;

        lock (_sync)
        {
            if (!_running) return;

            lateByUs = fireMomentUs - _nextDeadlineUs;
            if (lateByUs < 0) lateByUs = 0;
            long ignoreUs = IgnoreIfLateByMs * 1000;

            lateSkip         = ignoreUs > 0 && lateByUs >= ignoreUs;
            bool inhibitMode = _autoRestartInhibit && _autoRestartActive;
            bool raiseEvent  = !lateSkip && !inhibitMode;

            long deltaUs = _lastFireAbsoluteUs == 0 ? 0 : fireMomentUs - _lastFireAbsoluteUs;
            if (deltaUs < 0) deltaUs = 0;

            _lastFireAbsoluteUs = fireMomentUs;
            _lastFireDeltaUs    = deltaUs;
            _timerTiming        = false;

            if (raiseEvent)
            {
                TimerDoneOnce = true;
                args = new TimerDoneEventArgs
                {
                    ElapsedMs      = fireMomentUs / 1000,
                    ElapsedMsDelta = deltaUs / 1000,
                };
            }

            if (_autoStopActive)
            {
                if (!lateSkip) AutoStopAccumulator--;
                if (AutoStopAccumulator <= 0) willStop = true;
                else
                {
                    rescheduleAt    = fireMomentUs + (long)Preset * 1000;
                    _nextDeadlineUs = rescheduleAt;
                    _timerTiming    = true;
                }
            }
            else if (_autoRestartActive)
            {
                rescheduleAt    = fireMomentUs + (long)Preset * 1000;
                _nextDeadlineUs = rescheduleAt;
                _timerTiming    = true;
            }
            else
            {
                willStop = true;
            }
        }

        if (lateSkip)
        {
            Trace.WriteLine(
                $"{DebugInstanceName} - {DebugTimerName} - Late fire skipped (lateBy = {lateByUs / 1000} ms; budget = {IgnoreIfLateByMs} ms)");
        }

        if (args != null) RaiseOnTimingDoneEvent(args);

        if (rescheduleAt != 0) TimerScheduler.Instance.Schedule(this, rescheduleAt);
        else if (willStop) Stop();
    }

    protected virtual void RaiseOnTimingDoneEvent(TimerDoneEventArgs e)
    {
        var handler = OnTimingDone;
        if (handler == null) return;
        var syncContext = SyncContext;

        if (syncContext == null)
        {
            try { handler(this, e); }
            catch (Exception ex)
            {
                Trace.WriteLine(
                    $"{DebugInstanceName} - {DebugTimerName} - OnTimingDone handler threw: {ex.GetType().Name}: {ex.Message}");
            }
        }
        else
        {
            syncContext.Post(_ =>
            {
                try { handler(this, e); }
                catch (Exception ex)
                {
                    Trace.WriteLine(
                        $"{DebugInstanceName} - {DebugTimerName} - OnTimingDone handler threw: {ex.GetType().Name}: {ex.Message}");
                }
            }, null);
        }
    }

    private void ThrowIfRunning(string memberName)
    {
        if (_running)
            throw new InvalidOperationException($"{memberName} cannot be changed while the timer is running");
    }

    public override string ToString()
        => $"Preset = {((double)Preset / 1000):0.000}s, Actual = {((double)_lastFireDeltaUs / 1_000_000):0.000}s";
}

public class TimerDoneEventArgs
{
    public long ElapsedMsDelta { get; set; }
    public long ElapsedMs { get; set; }
    public TimerDoneEventArgs() { }
}

internal sealed class TimerScheduler
{
    private static readonly Lazy<TimerScheduler> _lazy =
        new(() => new TimerScheduler(), LazyThreadSafetyMode.ExecutionAndPublication);

    public static TimerScheduler Instance => _lazy.Value;

    private readonly PriorityQueue<TimerOnDelay, long> _heap = new();
    private readonly object _heapLock = new();
    private readonly ManualResetEventSlim _wake = new(initialState: false);
    private volatile bool _heapDirty;
    private volatile bool _shuttingDown;
    private readonly MicroStopwatch _stopwatch = new();

    private Thread? _thread;
    private readonly object _threadLock = new();

    private TimerScheduler()
    {
        _stopwatch.Start();
        // Tell the spin loop to exit when the host process is shutting down
        // — without this, the high-priority polling thread can prevent xUnit's
        // testhost from ending cleanly and the run is reported as aborted.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RequestShutdown();
        AppDomain.CurrentDomain.DomainUnload += (_, _) => RequestShutdown();
    }

    private void RequestShutdown()
    {
        _shuttingDown = true;
        _heapDirty = true;
        _wake.Set();
    }

    public long NowMicros => _stopwatch.ElapsedMicroseconds;

    internal void Schedule(TimerOnDelay timer, long deadlineUs)
    {
        EnsureThreadStarted();
        lock (_heapLock) _heap.Enqueue(timer, deadlineUs);
        _heapDirty = true;
        _wake.Set();
    }

    private void EnsureThreadStarted()
    {
        if (_thread != null) return;
        lock (_threadLock)
        {
            if (_thread != null) return;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Priority     = ThreadPriority.Highest,
                Name         = "High Precision Timer",
            };
            _thread.Start();
        }
    }

    private void Run()
    {
        while (!_shuttingDown)
        {
            TimerOnDelay? activeTimer;
            long deadlineUs = 0;

            lock (_heapLock)
            {
                while (_heap.TryPeek(out var head, out _) && !head.Running) _heap.Dequeue();
                _heapDirty = false;
                activeTimer = _heap.TryPeek(out var t, out var d) ? t : null;
                if (activeTimer != null) deadlineUs = d;
            }

            if (activeTimer == null)
            {
                _wake.Wait();
                _wake.Reset();
                continue;
            }

            while (!_heapDirty && !_shuttingDown)
            {
                long now = _stopwatch.ElapsedMicroseconds;
                if (now >= deadlineUs) break;
                Thread.SpinWait(10);
            }

            if (_heapDirty || _shuttingDown) continue;

            TimerOnDelay? toFire = null;
            long fireMoment = _stopwatch.ElapsedMicroseconds;

            lock (_heapLock)
            {
                if (_heap.TryPeek(out var head, out _) && ReferenceEquals(head, activeTimer))
                {
                    _heap.Dequeue();
                    toFire = activeTimer;
                }
            }

            if (toFire == null) continue;
            if (!toFire.Running) continue;
            toFire.OnSchedulerFire(fireMoment);
        }
    }
}

internal class MicroStopwatch : Stopwatch
{
    readonly double _microSecPerTick = 1_000_000D / Frequency;

    public MicroStopwatch()
    {
        if (!IsHighResolution)
            throw new Exception("On this system the high-resolution performance counter is not available");
    }

    public long ElapsedMicroseconds => (long)(ElapsedTicks * _microSecPerTick);
}

// Test-only hook so test assemblies can force the singleton scheduler to
// stop its polling thread before the testhost shuts down. Internal so it
// isn't part of the production surface; the test project gets at it via
// InternalsVisibleTo (configured in Core.csproj).
public static class TimerSchedulerTestHooks
{
    public static void Shutdown()
    {
        // Setting _shuttingDown via the singleton's internal RequestShutdown
        // method — accessed through reflection since TimerScheduler is internal.
        var scheduler = TimerScheduler.Instance;
        var method = typeof(TimerScheduler).GetMethod(
            "RequestShutdown",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        method?.Invoke(scheduler, null);
    }
}
