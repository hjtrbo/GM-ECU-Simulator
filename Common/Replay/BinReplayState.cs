namespace Common.Replay;

// Coordinator lifecycle. NoBin -> Armed (after Load) -> Running (after the
// first $22/$AA from a connected J2534 host) -> Stopped (host disconnect or
// all-nodes P3C timeout). Stopped is sticky until Unload — playback freezes
// at the bus-time of the stop event so the user can inspect the post-mortem
// state of the simulator.
public enum BinReplayState
{
    NoBin,
    Armed,
    Running,
    Stopped,
}
