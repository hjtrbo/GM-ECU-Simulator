namespace Common.Replay;

// Persisted bin-replay configuration. Slots into SimulatorConfig.BinReplay.
// FilePath is null when no bin has ever been loaded or the user explicitly
// cleared it. AutoLoadOnStart re-runs Load on next app launch.
public sealed class BinReplayConfig
{
    public string? FilePath { get; set; }
    public bool AutoLoadOnStart { get; set; } = false;
    public BinReplayLoopMode LoopMode { get; set; } = BinReplayLoopMode.HoldLast;
}
