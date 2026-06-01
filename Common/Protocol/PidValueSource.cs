namespace Common.Protocol;

// Where a live PID row ($22 / $2D) draws its wire value from. The editor's Signal column is the single selector for
// this: the row is no longer implicitly wired to its waveform when no signal is chosen. A static-payload row ($1A
// identity, bin-extracted bytes) still carries StaticBytes independently - those take precedence over Waveform but not
// over an attached Signal (see Pid.WriteResponseBytes).
//
//   None     - the row has no live source; it reads a flat 0 (unless it carries StaticBytes). The default for a
//              freshly added editor row, so "(none)" means 0, not a hidden oscillation.
//   Waveform - the row's value comes from its own WaveformConfig generator (sin / triangle / square / file-stream /
//              constant). The explicit, opt-in form of the old null-signal fallback.
//   Signal   - the row's value comes from the owning ECU's EngineModel signal named by Pid.Signal, encoded with the
//              row's Scalar/Offset/DataType. Assigning Pid.Signal a non-null value selects this automatically.
public enum PidValueSource
{
    None = 0,
    Waveform = 1,
    Signal = 2,
}
