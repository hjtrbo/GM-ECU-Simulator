namespace Common.Replay;

// What Sample() returns once playback has run past the end of the bin's
// recorded duration. Default is HoldLast — most natural when the user has
// "left the simulator running with a finished log loaded".
public enum BinReplayLoopMode
{
    HoldLast,
    Loop,
    Stop,
}
