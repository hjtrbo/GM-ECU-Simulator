using System.Text.Json;
using System.Text.Json.Serialization;
using Common.Glitch;
using Common.Protocol;
using Common.Replay;
using Common.Waveforms;

namespace Common.Persistence;

// JSON-serialised configuration. Hex-string CAN IDs follow the
// Tester-Emu (https://github.com/jakka351/Tester-Emu) convention so a
// human can hand-edit. PidDto and WaveformDto stay flat — easier to
// diff in source control than a deeply-nested structure.
//
// Schema version is incremented when a change is non-additive; loaders
// must handle older versions or fail with a clear error.
//
// v2 added the optional BinReplay section (path + auto-load + loop mode).
// v1 files load with BinReplay == null and round-trip cleanly.
//
// v3 added per-ECU SecurityModuleId + SecurityModuleConfig for $27 SecurityAccess
// support. v1/v2 files load with both null → $27 returns NRC $11 as before.
public sealed class SimulatorConfig
{
    public const int CurrentVersion = 3;
    public const int MinSupportedVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public string? Description { get; set; }
    public List<EcuDto> Ecus { get; set; } = new();

    // Null for users who have never loaded a bin. The coordinator
    // owns the runtime state; this DTO only carries the persisted hints.
    public BinReplayConfig? BinReplay { get; set; }
}

public sealed class EcuDto
{
    public required string Name { get; set; }

    [JsonConverter(typeof(HexUShortConverter))]
    public required ushort PhysicalRequestCanId { get; set; }

    [JsonConverter(typeof(HexUShortConverter))]
    public required ushort UsdtResponseCanId { get; set; }

    [JsonConverter(typeof(HexUShortConverter))]
    public required ushort UudtResponseCanId { get; set; }

    // When true the simulator drives $3E keepalives via PassThruStartPeriodicMsg
    // registrations. The host delegates and will not send $3E any other way.
    public bool AllowPeriodicTesterPresent { get; set; } = true;

    // Per-ECU glitch-injection settings. Always serialised; defaults to
    // disabled with all probabilities at 0 so saved configs are unaffected
    // until the user opts in via the editor.
    public GlitchConfig Glitch { get; set; } = GlitchConfig.CreateDefault();

    // ID of the security module to instantiate for $27 on this ECU. Null
    // (or unknown to the registry) → $27 returns NRC $11 ServiceNotSupported.
    public string? SecurityModuleId { get; set; }

    // Module-specific configuration handed to ISecurityAccessModule.LoadConfig.
    // Each module deserialises its own shape from this blob — ConfigSchema
    // doesn't need to know about any of them. Null is valid (module gets
    // defaults).
    public JsonElement? SecurityModuleConfig { get; set; }

    public List<PidDto> Pids { get; set; } = new();
}

public sealed class PidDto
{
    [JsonConverter(typeof(HexUIntConverter))]
    public required uint Address { get; set; }

    public required string Name { get; set; }
    public required PidSize Size { get; set; }
    public required PidDataType DataType { get; set; }
    public double Scalar { get; set; } = 1.0;
    public double Offset { get; set; } = 0.0;
    public string Unit { get; set; } = "";
    public required WaveformDto Waveform { get; set; }
}

public sealed class WaveformDto
{
    public required WaveformShape Shape { get; set; }
    public double Amplitude { get; set; } = 1.0;
    public double Offset { get; set; } = 0.0;
    public double FrequencyHz { get; set; } = 1.0;
    public double PhaseDeg { get; set; } = 0.0;
    public double DutyCycle { get; set; } = 0.5;

    public WaveformConfig ToWaveformConfig() => new()
    {
        Shape = Shape,
        Amplitude = Amplitude,
        Offset = Offset,
        FrequencyHz = FrequencyHz,
        PhaseDeg = PhaseDeg,
        DutyCycle = DutyCycle,
    };

    public static WaveformDto From(WaveformConfig cfg) => new()
    {
        Shape = cfg.Shape,
        Amplitude = cfg.Amplitude,
        Offset = cfg.Offset,
        FrequencyHz = cfg.FrequencyHz,
        PhaseDeg = cfg.PhaseDeg,
        DutyCycle = cfg.DutyCycle,
    };
}
