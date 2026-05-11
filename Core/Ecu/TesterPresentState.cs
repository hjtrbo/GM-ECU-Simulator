namespace Core.Ecu;

// Per-ECU TesterPresent / P3C state per GMW3110-2010 §6.2.4 + §8.15.6.2.
//
// State becomes ACTIVE when the ECU receives a service that requires P3C
// (e.g. $AA periodic, $2C, $10, $28, $A5, $AE). It returns to INACTIVE
// when Exit_Diagnostic_Services() runs — either because a $20 was received
// or because the P3Cnom (5000 ms) timeout elapsed without a $3E reset.
//
// On P3C timeout the node clears all enhanced state and emits an unsolicited
// $60 (positive $20 response) — see EcuExitLogic.
//
// Thread-safe: Activate/Reset/Deactivate (called from the IPC dispatch thread)
// and TickAndCheckTimeout (called from the TimerScheduler thread) all share
// a private lock. Without it, a $3E reset that lands between the read and
// write of `TimerMs += delta` is silently dropped — manifests as a spurious
// P3C timeout under heavy host traffic.
public enum TesterPresentTimerState
{
    Inactive,
    Active,
}

public sealed class TesterPresentState
{
    private readonly Lock gate = new();
    private TesterPresentTimerState state = TesterPresentTimerState.Inactive;
    private double timerMs;

    public TesterPresentTimerState State { get { lock (gate) return state; } }
    public double TimerMs
    {
        get { lock (gate) return timerMs; }
        // Setter exists for tests that prime the timer near its threshold.
        // Never call from production code — use Reset() / Activate() / Deactivate().
        set { lock (gate) timerMs = value; }
    }

    public void Activate()
    {
        lock (gate) { state = TesterPresentTimerState.Active; timerMs = 0; }
    }

    public void Reset()
    {
        lock (gate) timerMs = 0;
    }

    public void Deactivate()
    {
        lock (gate) { state = TesterPresentTimerState.Inactive; timerMs = 0; }
    }

    /// <summary>
    /// Called once per ticker tick. Returns true if the timer crossed the
    /// timeout threshold, false otherwise. The transition is consumed under
    /// the lock — only one tick will report a timeout for any given Active
    /// session, even if multiple ticks land near simultaneously.
    /// </summary>
    public bool TickAndCheckTimeout(int deltaMs, double timeoutMs)
    {
        lock (gate)
        {
            if (state != TesterPresentTimerState.Active) return false;
            timerMs += deltaMs;
            if (timerMs < timeoutMs) return false;
            // Edge consumed — leave it Active; EcuExitLogic.Run will
            // Deactivate via its normal path. Reset the counter so a follow-up
            // tick before Deactivate doesn't fire the timeout twice.
            timerMs = 0;
            return true;
        }
    }
}
