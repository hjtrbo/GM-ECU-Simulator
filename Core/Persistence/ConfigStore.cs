using System.Globalization;
using System.Text;
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
            var idMap = node.Identifiers;
            cfg.Ecus.Add(new EcuDto
            {
                Name = node.Name,
                PhysicalRequestCanId = node.PhysicalRequestCanId,
                UsdtResponseCanId = node.UsdtResponseCanId,
                UudtResponseCanId = node.UudtResponseCanId,
                Glitch = node.Glitch,
                SecurityModuleId = node.SecurityModule?.Id,
                SecurityModuleConfig = node.SecurityModuleConfig,
                BypassSecurity = node.BypassSecurity,
                FlowControlBlockSize = node.FlowControlBlockSize,
                FlowControlSeparationTime = node.FlowControlSeparationTime,
                ProgrammedState = node.ProgrammedState,
                DownloadAddressByteCount = node.DownloadAddressByteCount,
                Pids = node.Pids.Select(PidDtoFrom).ToList(),
                Identifiers = idMap.Count == 0
                    ? null
                    : idMap.OrderBy(kv => kv.Key).Select(kv => IdentifierDtoFrom(kv.Key, kv.Value)).ToList(),
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
        // Bootloader-capture toggle is persisted whenever it's ON, or when the
        // user has overridden the capture directory. Default-state (off, default
        // dir) leaves BootloaderCapture null so v1-v5 configs round-trip cleanly.
        var defaultDir = new Bus.CaptureSettings().CaptureDirectory;
        bool dirOverridden = !string.Equals(bus.Capture.CaptureDirectory, defaultDir,
            StringComparison.OrdinalIgnoreCase);
        if (bus.Capture.BootloaderCaptureEnabled || dirOverridden)
        {
            cfg.BootloaderCapture = new BootloaderCaptureConfig
            {
                Enabled = bus.Capture.BootloaderCaptureEnabled,
                Directory = dirOverridden ? bus.Capture.CaptureDirectory : null,
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

        // Restore the bootloader-capture toggle (v6+). Null leaves the bus at
        // its constructor defaults (off, default directory), which matches the
        // pre-v6 implicit behaviour.
        if (cfg.BootloaderCapture is not null)
        {
            bus.Capture.BootloaderCaptureEnabled = cfg.BootloaderCapture.Enabled;
            if (!string.IsNullOrWhiteSpace(cfg.BootloaderCapture.Directory))
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
            BypassSecurity = dto.BypassSecurity,
            FlowControlBlockSize = dto.FlowControlBlockSize,
            FlowControlSeparationTime = dto.FlowControlSeparationTime,
            ProgrammedState = dto.ProgrammedState,
            // v1-v6 configs lack the field and deserialise it as 0; clamp to
            // the GMW3110-permitted 2..4 range and fall back to the 4-byte
            // default for any out-of-range value (including 0).
            DownloadAddressByteCount = dto.DownloadAddressByteCount is >= 2 and <= 4
                ? dto.DownloadAddressByteCount
                : 4,
        };
        node.SecurityModule = SecurityModuleRegistry.Create(dto.SecurityModuleId);
        node.SecurityModule?.LoadConfig(dto.SecurityModuleConfig);
        foreach (var pidDto in dto.Pids) node.AddPid(PidFrom(pidDto));
        if (dto.Identifiers != null)
        {
            foreach (var idDto in dto.Identifiers)
                node.SetIdentifier(idDto.Did, IdentifierBytesFrom(idDto));
        }
        return node;
    }

    internal static byte[] IdentifierBytesFrom(IdentifierDto dto)
    {
        if (dto.Ascii != null && dto.Hex != null)
            throw new FormatException($"Identifier 0x{dto.Did:X2} has both Ascii and Hex set; pick one.");
        if (dto.Ascii != null) return Encoding.ASCII.GetBytes(dto.Ascii);
        if (dto.Hex != null) return ParseHexBytes(dto.Hex);
        return Array.Empty<byte>();
    }

    internal static IdentifierDto IdentifierDtoFrom(byte did, byte[] data)
    {
        var dto = new IdentifierDto { Did = did };
        if (data.Length > 0 && data.All(IsPrintableAscii))
            dto.Ascii = Encoding.ASCII.GetString(data);
        else
            dto.Hex = FormatHexBytes(data);
        return dto;
    }

    private static bool IsPrintableAscii(byte b) => b >= 0x20 && b < 0x7F;

    private static byte[] ParseHexBytes(string s)
    {
        var tokens = s.Split(new[] { ' ', '\t', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        var bytes = new byte[tokens.Length];
        for (int i = 0; i < tokens.Length; i++)
        {
            var t = tokens[i];
            if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
            else if (t.EndsWith('h') || t.EndsWith('H')) t = t[..^1];
            if (!byte.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
                throw new FormatException($"Invalid hex byte '{tokens[i]}' in identifier value");
            bytes[i] = v;
        }
        return bytes;
    }

    private static string FormatHexBytes(byte[] data)
    {
        if (data.Length == 0) return "";
        var sb = new StringBuilder(data.Length * 3 - 1);
        for (int i = 0; i < data.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:X2}", data[i]);
        }
        return sb.ToString();
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
