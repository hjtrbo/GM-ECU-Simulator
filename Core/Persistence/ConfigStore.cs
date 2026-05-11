using Common.Persistence;
using Common.Protocol;
using Common.Replay;
using Common.Waveforms;
using Core.Bus;
using Core.Ecu;
using Core.Replay;

namespace Core.Persistence;

// Translates between the plain-data SimulatorConfig (what's on disk)
// and the live VirtualBus / EcuNode / Pid model (what runs in memory).
// Caller is responsible for stopping anything pinned to the old model
// before swapping; ApplyTo replaces VirtualBus.Nodes wholesale.
public static class ConfigStore
{
    /// <summary>
    /// Default location for the auto-loaded / auto-saved config:
    /// %LocalAppData%\GmEcuSimulator\config.json. Used when the user
    /// hasn't picked an explicit file via File > Open.
    /// </summary>
    public static string DefaultPath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GmEcuSimulator");
            return Path.Combine(dir, "config.json");
        }
    }

    public static void Save(SimulatorConfig cfg, string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, ConfigSerializer.Serialize(cfg));
    }

    public static SimulatorConfig Load(string path)
        => ConfigSerializer.Deserialize(File.ReadAllText(path));

    /// <summary>
    /// Builds a SimulatorConfig snapshot of the current bus state — for
    /// File > Save and File > Export. When <paramref name="replay"/> is
    /// supplied and has a loaded bin (or a path the user wants auto-loaded),
    /// the BinReplay section is populated from it.
    /// </summary>
    public static SimulatorConfig Snapshot(
        VirtualBus bus, string? description = null, BinReplayCoordinator? replay = null)
    {
        var cfg = new SimulatorConfig
        {
            Version = SimulatorConfig.CurrentVersion,
            Description = description,
        };
        foreach (var node in bus.Nodes)
        {
            cfg.Ecus.Add(new EcuDto
            {
                Name = node.Name,
                PhysicalRequestCanId = node.PhysicalRequestCanId,
                UsdtResponseCanId = node.UsdtResponseCanId,
                UudtResponseCanId = node.UudtResponseCanId,
                AllowPeriodicTesterPresent = node.AllowPeriodicTesterPresent,
                Glitch = node.Glitch,
                Pids = node.Pids.Select(PidDtoFrom).ToList(),
            });
        }
        if (replay?.FilePath != null)
        {
            cfg.BinReplay = new BinReplayConfig
            {
                FilePath = replay.FilePath,
                LoopMode = replay.LoopMode,
                AutoLoadOnStart = replay.PersistedAutoLoadOnStart,
            };
        }
        return cfg;
    }

    /// <summary>
    /// Replaces the bus's current ECU set with the one described by
    /// <paramref name="cfg"/>. Active periodic schedules are stopped
    /// for any ECU that is removed; remaining ECUs keep running.
    /// </summary>
    public static void ApplyTo(SimulatorConfig cfg, VirtualBus bus)
    {
        // Stop any periodic schedules tied to ECUs we're about to drop.
        foreach (var oldNode in bus.Nodes.ToArray())
            bus.Scheduler.Stop(oldNode, Array.Empty<byte>());

        bus.ReplaceNodes(cfg.Ecus.Select(EcuNodeFrom));
    }

    public static EcuNode EcuNodeFrom(EcuDto dto)
    {
        var node = new EcuNode
        {
            Name = dto.Name,
            PhysicalRequestCanId = dto.PhysicalRequestCanId,
            UsdtResponseCanId = dto.UsdtResponseCanId,
            UudtResponseCanId = dto.UudtResponseCanId,
            AllowPeriodicTesterPresent = dto.AllowPeriodicTesterPresent,
            Glitch = dto.Glitch ?? Common.Glitch.GlitchConfig.CreateDefault(),
        };
        foreach (var pidDto in dto.Pids) node.AddPid(PidFrom(pidDto));
        return node;
    }

    public static Pid PidFrom(PidDto dto) => new()
    {
        Address = dto.Address,
        Name = dto.Name,
        Size = dto.Size,
        DataType = dto.DataType,
        Scalar = dto.Scalar,
        Offset = dto.Offset,
        Unit = dto.Unit,
        WaveformConfig = dto.Waveform.ToWaveformConfig(),
    };

    private static PidDto PidDtoFrom(Pid pid) => new()
    {
        Address = pid.Address,
        Name = pid.Name,
        Size = pid.Size,
        DataType = pid.DataType,
        Scalar = pid.Scalar,
        Offset = pid.Offset,
        Unit = pid.Unit,
        Waveform = WaveformDto.From(pid.WaveformConfig),
    };
}
