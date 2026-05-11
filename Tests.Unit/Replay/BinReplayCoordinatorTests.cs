using Common.Replay;
using Core.Replay;

namespace EcuSimulator.Tests.Replay;

public class BinReplayCoordinatorTests
{
    [Fact]
    public void NoBin_SampleReturnsZero_StateIsNoBin()
    {
        var c = new BinReplayCoordinator();
        Assert.Equal(BinReplayState.NoBin, c.State);
        Assert.Equal(0.0, c.Sample(0, 1234));
    }

    [Fact]
    public void Armed_SampleReturnsRow0_RegardlessOfBusTime()
    {
        var c = new BinReplayCoordinator();
        c.Load(FakeBinSource.Default());
        Assert.Equal(BinReplayState.Armed, c.State);
        // Row 0 of channel 0 in the default fixture is 0 * 1000 + 0 = 0.
        // Use channel 1 for a non-zero proof: 1 * 1000 + 0 = 1000.
        Assert.Equal(1000.0, c.Sample(1, 999_999));
    }

    [Fact]
    public void MaybeStart_LatchesOnce_StateGoesRunning()
    {
        var c = new BinReplayCoordinator();
        c.Load(FakeBinSource.Default());
        c.MaybeStart(500);
        Assert.Equal(BinReplayState.Running, c.State);
        // Second MaybeStart with a different time must NOT overwrite the latch.
        c.MaybeStart(900);
        // At busNowMs=510, replayMs = 510 - 500 = 10 -> row 1 of channel 1 = 1001.
        Assert.Equal(1001.0, c.Sample(1, 510));
    }

    [Fact]
    public void Running_SampleTracksBusTime()
    {
        var c = new BinReplayCoordinator();
        c.Load(FakeBinSource.Default());
        c.MaybeStart(1000);
        // replayMs = busNow - 1000. At busNow=1050 -> replayMs=50 -> row 5 (50ms).
        // Channel 0 row 5 = 0*1000 + 5 = 5.
        Assert.Equal(5.0, c.Sample(0, 1050));
        // Channel 1 row 5 = 1*1000 + 5 = 1005.
        Assert.Equal(1005.0, c.Sample(1, 1050));
    }

    [Fact]
    public void Sample_BinarySearchPicksRowAtOrBefore()
    {
        // Sparse elapsed times so we can verify the at-or-before behavior.
        var elapsed = new long[] { 0, 50, 200, 500, 1000 };
        var headers = new[]
        {
            new BinChannelHeader(1, "x", "u", 0x10, 2, 0x7E8, 2, 1.0, 0),
        };
        var src = new FakeBinSource(elapsed, headers, (ch, row) => row * 10);
        var c = new BinReplayCoordinator();
        c.Load(src);
        c.MaybeStart(0);

        // replayMs=49 -> row 0 (elapsed=0)
        Assert.Equal(0.0, c.Sample(0, 49));
        // replayMs=50 -> row 1 (elapsed=50)
        Assert.Equal(10.0, c.Sample(0, 50));
        // replayMs=199 -> row 1
        Assert.Equal(10.0, c.Sample(0, 199));
        // replayMs=200 -> row 2
        Assert.Equal(20.0, c.Sample(0, 200));
        // replayMs=999 -> row 3 (elapsed=500)
        Assert.Equal(30.0, c.Sample(0, 999));
        // replayMs=1000 -> row 4 (last)
        Assert.Equal(40.0, c.Sample(0, 1000));
    }

    [Fact]
    public void HoldLast_PastDuration_ReturnsLastRow()
    {
        var c = new BinReplayCoordinator { LoopMode = BinReplayLoopMode.HoldLast };
        c.Load(FakeBinSource.Default());            // 100 rows, last elapsed = 990 ms
        c.MaybeStart(0);
        // replayMs = 5000 (well past 990) -> clamped to row 99 (last). Channel 0 row 99 = 99.
        Assert.Equal(99.0, c.Sample(0, 5000));
    }

    [Fact]
    public void Loop_PastDuration_WrapsByDuration()
    {
        var c = new BinReplayCoordinator { LoopMode = BinReplayLoopMode.Loop };
        c.Load(FakeBinSource.Default());            // last elapsed = 990
        c.MaybeStart(0);
        // replayMs = 1500 -> 1500 % 990 = 510 -> row 51 (510ms). ch0 row 51 = 51.
        Assert.Equal(51.0, c.Sample(0, 1500));
    }

    [Fact]
    public void StopLoopMode_PastDuration_TransitionsToStopped()
    {
        var c = new BinReplayCoordinator { LoopMode = BinReplayLoopMode.Stop };
        c.Load(FakeBinSource.Default());            // last elapsed = 990
        c.MaybeStart(0);
        // First sample past the end: still returns lastRow but transitions Stopped.
        var v = c.Sample(0, 5000);
        Assert.Equal(99.0, v);
        Assert.Equal(BinReplayState.Stopped, c.State);
        // Subsequent samples: still the last row, regardless of busNow.
        Assert.Equal(99.0, c.Sample(0, 9000));
    }

    [Fact]
    public void Stopped_NextMaybeStart_ReArmsAndRestartsFromBinT0()
    {
        // Disconnect / reconnect cycle: after a stop, the next $22 / $AA
        // (i.e. MaybeStart from DispatchUsdt) must clear the stop latch
        // and start a fresh playback clock so replay resumes from row 0,
        // not freeze at the stopped offset.
        var c = new BinReplayCoordinator();
        c.Load(FakeBinSource.Default());     // 100 rows at 10 ms; ch0 row r returns r
        c.MaybeStart(0);
        Assert.Equal(BinReplayState.Running, c.State);

        // Run for a while, then host disconnects.
        Assert.Equal(50.0, c.Sample(0, 500));
        c.MaybeStop(700);
        Assert.Equal(BinReplayState.Stopped, c.State);
        Assert.Equal(70.0, c.Sample(0, 5000));     // frozen at row 70

        // Host comes back and sends $22 at busNowMs=10000. MaybeStart must
        // re-arm, latch a new start time, transition to Running, and
        // produce sample t=0 of the bin (= row 0 = ch0 row 0 = 0).
        c.MaybeStart(10000);
        Assert.Equal(BinReplayState.Running, c.State);
        Assert.Equal(0.0, c.Sample(0, 10000));     // replayMs = 0 -> row 0

        // Subsequent samples track from the new start time.
        Assert.Equal(15.0, c.Sample(0, 10150));    // replayMs = 150 -> row 15
    }

    [Fact]
    public void MaybeStop_FreezesValueAtStopTime()
    {
        var c = new BinReplayCoordinator();
        c.Load(FakeBinSource.Default());
        c.MaybeStart(0);
        // At busNow=300, replayMs=300 -> row 30 -> ch0 = 30.
        Assert.Equal(30.0, c.Sample(0, 300));
        c.MaybeStop(300);                // freeze at row 30
        Assert.Equal(BinReplayState.Stopped, c.State);
        // Bus time advances but Sample stays frozen at row 30.
        Assert.Equal(30.0, c.Sample(0, 999));
        Assert.Equal(30.0, c.Sample(0, 5000));
    }

    [Fact]
    public void Unload_ReturnsToNoBin_DisposesSourceAfterDelay()
    {
        var c = new BinReplayCoordinator();
        var src = FakeBinSource.Default();
        c.Load(src);
        c.MaybeStart(0);
        c.Unload();
        Assert.Equal(BinReplayState.NoBin, c.State);
        Assert.Equal(0.0, c.Sample(0, 100));
        // Late dispose runs ~2s later. We don't wait — just confirm immediate
        // unload doesn't touch the previous source while in-flight readers
        // might still hold it.
        Assert.False(src.Disposed);
    }

    [Fact]
    public void Reload_ReplacesSourceAndResetsLatches()
    {
        var c = new BinReplayCoordinator();
        c.Load(FakeBinSource.Default());
        c.MaybeStart(100);
        Assert.Equal(BinReplayState.Running, c.State);

        // Load a different source -> back to Armed.
        var elapsed2 = new long[] { 0, 500 };
        var headers2 = new[]
        {
            new BinChannelHeader(1, "y", "u", 0x99, 2, 0x7E8, 2, 1.0, 0),
        };
        c.Load(new FakeBinSource(elapsed2, headers2, (ch, row) => 7 + row));
        Assert.Equal(BinReplayState.Armed, c.State);
        // Armed -> row 0 -> 7.
        Assert.Equal(7.0, c.Sample(0, 999_999));
    }

    [Fact]
    public void StateChanged_FiresOnLoadStartStopUnload()
    {
        var c = new BinReplayCoordinator();
        var states = new List<BinReplayState>();
        c.StateChanged += s => states.Add(s);
        c.Load(FakeBinSource.Default());
        c.MaybeStart(0);
        c.MaybeStop(100);
        c.Unload();
        Assert.Equal(
            new[] { BinReplayState.Armed, BinReplayState.Running, BinReplayState.Stopped, BinReplayState.NoBin },
            states);
    }

    [Fact]
    public void MaybeStart_NoBin_IsNoOp()
    {
        var c = new BinReplayCoordinator();
        c.MaybeStart(0);
        Assert.Equal(BinReplayState.NoBin, c.State);
    }

    [Fact]
    public void MaybeStop_BeforeStart_IsNoOp()
    {
        var c = new BinReplayCoordinator();
        c.Load(FakeBinSource.Default());
        c.MaybeStop(0);
        Assert.Equal(BinReplayState.Armed, c.State);
    }

    [Fact]
    public void ConcurrentMaybeStart_ExactlyOneWins()
    {
        // Race the MaybeStart latch from N threads. After they all return,
        // the start time must equal exactly one of the supplied values.
        var c = new BinReplayCoordinator();
        c.Load(FakeBinSource.Default());            // 100 rows, last elapsed = 990 ms

        var times = new double[] { 10, 20, 30, 40, 50, 60, 70, 80 };
        using var barrier = new System.Threading.Barrier(times.Length);
        var threads = times.Select(t => new System.Threading.Thread(() =>
        {
            barrier.SignalAndWait();
            c.MaybeStart(t);
        })).ToArray();
        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();

        Assert.Equal(BinReplayState.Running, c.State);
        // Pick a busNow that keeps replayMs inside the bin's duration so the
        // HoldLast clamp doesn't collapse all winners to the same row.
        // busNow=500 -> replayMs in [420..490] -> row in [42..49] -> ch0 value matches.
        long busNow = 500;
        double sample = c.Sample(0, busNow);
        var validRows = times.Select(t => (long)((busNow - (long)t) / 10)).ToHashSet();
        Assert.Contains((long)sample, validRows);
    }

    [Fact]
    public void ConcurrentSample_NoExceptions_AcrossLifecycleEvents()
    {
        // Stress test: many threads Sample()ing while Load/Unload churn happens.
        // No locking on the hot path means we need to be sure there's no NRE
        // window between the source-null-check and the GetValue call.
        var c = new BinReplayCoordinator();
        c.Load(FakeBinSource.Default());
        c.MaybeStart(0);

        var stop = false;
        var readers = Enumerable.Range(0, 4).Select(_ => new System.Threading.Thread(() =>
        {
            var rng = new Random(Environment.CurrentManagedThreadId);
            while (!Volatile.Read(ref stop))
            {
                double v = c.Sample(rng.Next(0, 2), rng.Next(0, 2000));
                if (double.IsNaN(v)) throw new InvalidOperationException("NaN");
            }
        })).ToArray();
        foreach (var r in readers) r.Start();

        for (int i = 0; i < 20; i++)
        {
            c.Load(FakeBinSource.Default());
            c.MaybeStart(0);
            System.Threading.Thread.Sleep(2);
        }
        Volatile.Write(ref stop, true);
        foreach (var r in readers) r.Join();
        // Pass if no exception thrown above.
    }
}
