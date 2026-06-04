using Common;
using Common.Persistence;
using Common.Protocol;
using Common.Replay;
using Common.Signals;
using Common.Signals.Engines;
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

    /// <summary>Filename for the rolling auto-load / auto-save state.</summary>
    public const string LastUsedFileName = "lastused.mode.json";

    /// <summary>
    /// Auto-load / auto-save path under %LocalAppData%\GmEcuSimulator\config\.
    /// All modes share a single "last used" file: only EcuSimulator actually
    /// reads / writes it (DPS modes don't PersistsConfig, so the App lifecycle
    /// skips the auto path for them entirely), so there's no cross-mode clash.
    /// The <paramref name="mode"/> argument is retained for call-site clarity
    /// even though the filename no longer varies by mode. Manual File > Save As
    /// still defaults to the per-mode <see cref="AppModeExtensions.ConfigFileName"/>.
    /// </summary>
    public static string PathForMode(AppMode mode)
    {
        Directory.CreateDirectory(ConfigDirectory);
        return Path.Combine(ConfigDirectory, LastUsedFileName);
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
    /// File > Save / Save As. When <paramref name="replay"/> is
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
            cfg.Ecus.Add(EcuDtoFrom(node));
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

    /// <summary>
    /// Builds an <see cref="EcuDto"/> snapshot of a single <see cref="EcuNode"/>.
    /// Used by the per-ECU Save command (sidebar dropdown) and by
    /// <see cref="Snapshot"/> for the whole-config save path. Primed ECUs are
    /// not skipped here - that's a policy decision the caller owns.
    /// </summary>
    public static EcuDto EcuDtoFrom(EcuNode node) => new()
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
        // Persist persona id only when it diverges from the default
        // (gmw3110). Saves a noisy "PersonaId": "gmw3110" line on every
        // ECU in the standard config and keeps diffs stable.
        PersonaId = node.Persona.Id == "gmw3110" ? null : node.Persona.Id,
        // FlashBinPath is per-persona (Ford-capture only) and EcuNode
        // doesn't carry the path back (LoadFlashBin replaces the static
        // bytes on the persona, not on the node). We DO persist the
        // node's user-set FlashBinPath via the side-channel below so a
        // round-trip through the editor doesn't drop the field. Without
        // this, the auto-save path the WPF runs would strip the field
        // and the next launch would fail Service $23 with NRC $22.
        FlashBinPath = node.FlashBinPath,
        // AllPids unions every mode-keyed store with deterministic ordering
        // (Mode22 -> Mode2D -> Mode1A -> Mode1, each by key) so saved-config
        // diffs stay stable across runs.
        Pids = node.AllPids.Select(PidDtoFrom).ToList(),
        // Persist the boot operating point only when it diverges from the default Idle (keeps standard configs quiet).
        Scenario = node.EngineModel.ActiveScenario == ScenarioId.Idle ? null : node.EngineModel.ActiveScenario,
        // Persist the engine character only when it diverges from the NA default (keeps standard configs quiet).
        EngineModelId = node.EngineModel.Character.Id == EngineCharacterRegistry.DefaultId ? null : node.EngineModel.Character.Id,
        // AccelDecelSweep timing is no longer persisted - it is fixed at SweepProfile.Default for every ECU.
        // Persist only the $01 PIDs the user has turned OFF relative to the
        // built-in E38/E67 default subset (a delta, not the whole map). null
        // when nothing is disabled keeps standard configs quiet.
        Mode1Disabled = ComputeMode1Disabled(node),
        // Persist the DBC broadcast set only when non-empty (keeps standard configs quiet).
        Broadcasts = node.Broadcasts.Count > 0
            ? node.Broadcasts.Select(BroadcastMessageDtoFrom).ToList()
            : null,
    };

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
        // Persona resolution. Missing / unknown -> Gmw3110Persona (the
        // standard default for every GM ECU). The Ford-capture preset uses
        // PersonaId = "ford-capture" to swap in the logging dispatcher.
        node.Persona = Core.Ecu.Personas.PersonaRegistry.Resolve(dto.PersonaId);
        // Ford-capture only: load the flash bin if a path was supplied.
        // Other personas ignore FlashBinPath. We throw on missing / unreadable
        // so config-load failures are loud - $23 silently NRC-ing against a
        // typo'd path would be confusing on a re-test.
        node.FlashBinPath = dto.FlashBinPath;
        if (dto.PersonaId == "ford-capture" && !string.IsNullOrWhiteSpace(dto.FlashBinPath))
        {
            Core.Ecu.Personas.FordCapturePersona.LoadFlashBin(dto.FlashBinPath!);
        }
        foreach (var pidDto in dto.Pids)
        {
            // AddPid routes by pid.Mode into the appropriate per-mode store.
            node.AddPid(PidFrom(pidDto));
        }
        // Restore the DBC broadcast set (absent -> none).
        if (dto.Broadcasts is { } broadcasts)
            node.ReplaceBroadcasts(broadcasts.Select(BroadcastMessageFrom));
        // AccelDecelSweep timing is fixed at SweepProfile.Default (the EngineModel's field initializer) - no per-ECU
        // override is read from config. Old configs carrying the retired Sweep* fields load fine; the values are ignored.
        // Restore the engine character (absent / unknown -> the NA default via the registry's fallback).
        node.EngineModel.Character = EngineCharacterRegistry.Create(dto.EngineModelId);
        // Restore the boot operating point for the live signal model (absent -> the engine model's default Idle).
        if (dto.Scenario is { } scenario) node.EngineModel.SetScenario(scenario, 0);
        // Re-apply the saved $01 supported-PID delta: start from the built-in
        // default subset and remove the PIDs the user turned off. Absent /
        // empty -> the full default subset stands.
        if (dto.Mode1Disabled is { Count: > 0 } disabled)
            node.Mode1Supported = new HashSet<byte>(J1979Catalogue.DefaultSupported.Except(disabled));
        return node;
    }

    // The $01 supported set is stored as a delta off the built-in default
    // subset: which default PIDs has the user disabled? Returns null when the
    // node still advertises the full default set (the common case).
    private static List<byte>? ComputeMode1Disabled(EcuNode node)
    {
        var disabled = J1979Catalogue.DefaultSupported
            .Where(p => !node.Mode1Supported.Contains(p))
            .OrderBy(p => p)
            .ToList();
        return disabled.Count == 0 ? null : disabled;
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
        Signal = dto.Signal,
        WaveformConfig = dto.Waveform.ToWaveformConfig(),
        LengthBytes = dto.LengthBytes,
        StaticBytes = HexStringToBytes(dto.StaticBytes),
        // Explicit source when the config carries one; otherwise infer for pre-v17 files: a signal-backed row stays
        // signal-backed, everything else keeps the old null-signal-means-waveform behaviour. Assigned after Signal so
        // it wins over the setter's auto-select.
        ValueSource = dto.ValueSource ?? (dto.Signal.HasValue ? PidValueSource.Signal : PidValueSource.Waveform),
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
        Signal = pid.Signal,
        ValueSource = pid.ValueSource,
        Waveform = WaveformDto.From(pid.WaveformConfig),
        LengthBytes = pid.LengthBytes,
        StaticBytes = BytesToHexString(pid.StaticBytes),
    };

    // ---- Broadcast (DBC) round-trip. Also the *.dbc.json shape (a flat List<BroadcastMessageDto>). ----

    public static BroadcastMessage BroadcastMessageFrom(BroadcastMessageDto dto)
    {
        var msg = new BroadcastMessage
        {
            CanId = dto.CanId,
            Extended = dto.Extended,
            Name = dto.Name,
            Dlc = dto.Dlc,
            PeriodMs = dto.PeriodMs,
            Enabled = dto.Enabled,
        };
        foreach (var s in dto.Signals) msg.Signals.Add(BroadcastSignalFrom(s));
        return msg;
    }

    public static BroadcastMessageDto BroadcastMessageDtoFrom(BroadcastMessage msg) => new()
    {
        CanId = msg.CanId,
        Extended = msg.Extended,
        Name = msg.Name,
        Dlc = msg.Dlc,
        PeriodMs = msg.PeriodMs,
        Enabled = msg.Enabled,
        Signals = msg.Signals.Select(BroadcastSignalDtoFrom).ToList(),
    };

    private static BroadcastSignal BroadcastSignalFrom(BroadcastSignalDto dto) => new()
    {
        Name = dto.Name,
        StartBit = dto.StartBit,
        Length = dto.Length,
        ByteOrder = dto.ByteOrder,
        Signed = dto.Signed,
        Scale = dto.Scale,
        Offset = dto.Offset,
        Unit = dto.Unit,
        Min = dto.Min,
        Max = dto.Max,
        Signal = dto.Signal,
        Constant = dto.Constant,
        // Explicit source when present; infer otherwise (a signal-backed field stays signal-backed,
        // else None) so a hand-written .dbc.json without ValueSource still behaves sensibly.
        ValueSource = dto.ValueSource ?? (dto.Signal.HasValue
            ? Common.Protocol.BroadcastValueSource.Signal
            : Common.Protocol.BroadcastValueSource.None),
    };

    private static BroadcastSignalDto BroadcastSignalDtoFrom(BroadcastSignal s) => new()
    {
        Name = s.Name,
        StartBit = s.StartBit,
        Length = s.Length,
        ByteOrder = s.ByteOrder,
        Signed = s.Signed,
        Scale = s.Scale,
        Offset = s.Offset,
        Unit = s.Unit,
        Min = s.Min,
        Max = s.Max,
        Signal = s.Signal,
        ValueSource = s.ValueSource,
        Constant = s.Constant,
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
