using System.Globalization;
using System.Text;
using Common;
using Common.Persistence;
using Common.Protocol;
using Common.Replay;
using Common.Waveforms;
using Core.Bus;
using Core.Ecu;
using Core.Replay;
using Core.Security;

namespace Core.Persistence;

// Translates between the plain-data SimulatorConfig (what's on disk)
// and the live VirtualBus / EcuNode / Pid model (what runs in memory).
// Caller is responsible for stopping anything pinned to the old model
// before swapping; ApplyTo replaces VirtualBus.Nodes wholesale.
public static class ConfigStore
{
    private static string ConfigDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GmEcuSimulator", "config");

    /// <summary>
    /// Per-mode auto-load / auto-save path under
    /// %LocalAppData%\GmEcuSimulator\config\. Each persistable mode owns its
    /// own file so DPS, Flash-Tool, and ECU Simulator state stay separate
    /// worlds. DPS modes get a path too, but the App lifecycle skips reading
    /// / auto-writing it - the path exists only so manual File > Save has a
    /// target.
    /// </summary>
    public static string PathForMode(AppMode mode)
    {
        Directory.CreateDirectory(ConfigDirectory);
        return Path.Combine(ConfigDirectory, mode.ConfigFileName());
    }

    /// <summary>
    /// Idempotent startup migration. Covers two upgrades:
    /// <list type="bullet">
    /// <item>The original single-file <c>config.json</c> at the
    /// <c>%LocalAppData%\GmEcuSimulator\</c> root, renamed to the per-mode
    /// <c>ecu_simulator_config.json</c> when the multi-mode layout shipped.</item>
    /// <item>The flat per-mode + <c>settings.json</c> + <c>layout.xml</c>
    /// files at the same root, relocated into the new
    /// <c>%LocalAppData%\GmEcuSimulator\config\</c> subfolder so all config
    /// state sits together. Sibling of the <c>logs\</c> subfolder.</item>
    /// </list>
    /// Skips any move whose target already exists - that preserves whichever
    /// the user has been writing to most recently and never overwrites.
    /// </summary>
    public static void MigrateLegacyConfigFile()
    {
        try
        {
            var oldRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GmEcuSimulator");
            Directory.CreateDirectory(ConfigDirectory);

            // 1. Original single-file legacy rename, kept for installs that
            //    still have config.json sitting around. Target is the new
            //    per-mode filename inside config\.
            var legacy = Path.Combine(oldRoot, "config.json");
            var target = PathForMode(AppMode.EcuSimulator);
            if (File.Exists(legacy) && !File.Exists(target))
                File.Move(legacy, target);

            // 2. Flat-root -> config\ relocation. Every config-shaped file
            //    that used to live directly under GmEcuSimulator\ moves
            //    into config\ keeping its filename. Safe to run on every
            //    startup: the move is skipped when source is absent or
            //    destination already exists.
            string[] flatRootConfigs = {
                "settings.json",
                "layout.xml",
                "ecu_simulator_config.json",
                "dps_write_config.json",
                "dps_read_config.json",
                "flash_write_config.json",
                "flash_read_config.json",
            };
            foreach (var name in flatRootConfigs)
            {
                var src = Path.Combine(oldRoot, name);
                var dst = Path.Combine(ConfigDirectory, name);
                if (File.Exists(src) && !File.Exists(dst))
                    File.Move(src, dst);
            }
        }
        catch
        {
            // Migration failures are non-fatal - the user falls back to
            // defaults and re-saves. Logging would need an injected sink.
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
    /// Builds a SimulatorConfig snapshot of the current bus state - for
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
            // Primed ECUs are reconstructed at startup from PrimeArchivePath;
            // writing them to the config would persist stale static-byte dumps
            // that the next prime would overwrite anyway.
            if (node.IsPrimed) continue;
            cfg.Ecus.Add(new EcuDto
            {
                Name = node.Name,
                PhysicalRequestCanId = node.PhysicalRequestCanId,
                UsdtResponseCanId = node.UsdtResponseCanId,
                UudtResponseCanId = node.UudtResponseCanId,
                Glitch = node.Glitch,
                SecurityModuleId = node.SecurityModule?.Id,
                SecurityModuleConfig = node.SecurityModuleConfig,
                FlowControlBlockSize = node.FlowControlBlockSize,
                ProgrammedState = node.ProgrammedState,
                DiagnosticAddress = node.DiagnosticAddress,
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
        // Persist a CaptureDirectory override only when the user has pointed
        // it somewhere other than WPF's default. The default is set on every
        // launch by App.OnStartup so a null saved value still resolves to
        // the right path next time. Pre-toggle-removal configs (which
        // carried an Enabled flag in this section) load fine - the
        // serialiser silently drops the now-unknown field.
        var defaultDir = Bus.CaptureSettings.DefaultDirectory();
        if (!string.IsNullOrEmpty(bus.Capture.CaptureDirectory)
            && !string.Equals(bus.Capture.CaptureDirectory, defaultDir,
                              StringComparison.OrdinalIgnoreCase))
        {
            cfg.BootloaderCapture = new BootloaderCaptureConfig
            {
                Directory = bus.Capture.CaptureDirectory,
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

        // Restore any user-set CaptureDirectory override. Null means "use
        // whatever was set at startup" (WPF defaults it; tests leave it null
        // and intentionally don't write).
        if (cfg.BootloaderCapture is not null
            && !string.IsNullOrWhiteSpace(cfg.BootloaderCapture.Directory))
        {
            bus.Capture.CaptureDirectory = cfg.BootloaderCapture.Directory!;
        }
    }

    public static EcuNode EcuNodeFrom(EcuDto dto)
    {
        var node = new EcuNode
        {
            Name = dto.Name,
            PhysicalRequestCanId = dto.PhysicalRequestCanId,
            UsdtResponseCanId = dto.UsdtResponseCanId,
            UudtResponseCanId = dto.UudtResponseCanId,
            Glitch = dto.Glitch ?? Common.Glitch.GlitchConfig.CreateDefault(),
            SecurityModuleConfig = dto.SecurityModuleConfig,
            FlowControlBlockSize = dto.FlowControlBlockSize,
            ProgrammedState = dto.ProgrammedState,
            DiagnosticAddress = dto.DiagnosticAddress,
        };
        node.SecurityModule = SecurityModuleRegistry.Create(dto.SecurityModuleId);
        node.SecurityModule?.LoadConfig(dto.SecurityModuleConfig);
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
        Mode = dto.Mode,
        WaveformConfig = dto.Waveform.ToWaveformConfig(),
        LengthBytes = dto.LengthBytes,
        StaticBytes = HexStringToBytes(dto.StaticBytes),
    };

    public static PidDto PidDtoFrom(Pid pid) => new()
    {
        Address = pid.Address,
        Name = pid.Name,
        Size = pid.Size,
        DataType = pid.DataType,
        Scalar = pid.Scalar,
        Offset = pid.Offset,
        Unit = pid.Unit,
        Mode = pid.Mode,
        Waveform = WaveformDto.From(pid.WaveformConfig),
        LengthBytes = pid.LengthBytes,
        StaticBytes = BytesToHexString(pid.StaticBytes),
    };

    private static byte[]? HexStringToBytes(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return null;
        var s = hex.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        if ((s.Length & 1) != 0)
            throw new FormatException($"PID staticBytes hex length must be even, got {s.Length}: {hex}");
        var bytes = new byte[s.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = byte.Parse(s.AsSpan(i * 2, 2),
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture);
        return bytes;
    }

    private static string? BytesToHexString(byte[]? bytes)
    {
        if (bytes is null) return null;
        var sb = new System.Text.StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        return sb.ToString();
    }
}
