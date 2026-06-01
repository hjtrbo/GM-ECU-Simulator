namespace Common;

// Top-level operational mode for the simulator. Each mode scopes how many
// ECUs the user can configure, whether ECU state persists across restarts,
// and which tabs / fields surface in the editor pane.
//
// EcuSimulator is the original behaviour: multiple ECUs, full editor pane,
// state persists to ecu_simulator.mode.json. DpsSimulator is the single-ECU
// DPS programming workflow - prime from an archive, drive a target ECU through
// a programming session. It folds together the former separate DpsWrite /
// DpsRead modes into one persona. OBD-II Mode $01 emulation is supported inside
// EcuSimulator via per-PID PidMode selection - no separate top-level mode.
public enum AppMode
{
    EcuSimulator = 0,
    DpsSimulator = 1,
}

public static class AppModeExtensions
{
    public static bool AllowsMultipleEcus(this AppMode mode)
        => mode is AppMode.EcuSimulator;

    // DPS sessions are intended to be clean per-run: the user primes from an
    // archive, exercises a flow, and the ECU evaporates on exit. Everything
    // else persists its ECU config to disk.
    public static bool PersistsConfig(this AppMode mode) =>
        mode is AppMode.EcuSimulator;

    // Per-mode config files use the ".mode.json" suffix - a catch-all that reads naturally for every mode (the ECU
    // Simulator setup AND the DPS programming session are all mode-scoped state), and sits alongside the per-ECU
    // ".ecu.json" and per-PID-list ".pids.json" artefacts so the kinds are distinguishable at a glance. Drives both
    // the implicit auto-load/auto-save path (ConfigStore.PathForMode) and the default name in the Save As dialog.
    public static string ConfigFileName(this AppMode mode) => mode switch
    {
        AppMode.EcuSimulator => "ecu_simulator.mode.json",
        AppMode.DpsSimulator => "dps_simulator.mode.json",
        _ => "default.mode.json",
    };

    public static string DisplayName(this AppMode mode) => mode switch
    {
        AppMode.EcuSimulator => "ECU Simulator",
        AppMode.DpsSimulator => "DPS Simulator",
        _ => mode.ToString(),
    };
}
