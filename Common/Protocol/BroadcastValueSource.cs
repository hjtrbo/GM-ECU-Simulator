namespace Common.Protocol;

// Where a DBC broadcast signal draws its value at emit time. Parallel to PidValueSource, but a
// broadcast field has no per-row waveform generator - instead it can carry a fixed Constant. Kept
// separate from PidValueSource precisely so the PID value pipeline (None/Waveform/Signal) stays
// unchanged.
//
//   None     - the field reads a flat 0.
//   Constant - the field reads BroadcastSignal.Constant (a fixed engineering value).
//   Signal   - the field is sampled live from the owning ECU's EngineModel signal, encoded with the
//              DBC layout's Scale/Offset.
public enum BroadcastValueSource
{
    None = 0,
    Constant = 1,
    Signal = 2,
}
