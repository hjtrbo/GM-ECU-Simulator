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
//
// v4 added per-ECU Identifiers (list of IdentifierDto) for $1A
// ReadDataByIdentifier responses. v1/v2/v3 files load with Identifiers == null
// -> $1A returns NRC $31 RequestOutOfRange for every DID, as before.
//
// v5 added per-ECU BypassSecurity flag and per-ECU ISO-TP FlowControl bytes
// (FlowControlBlockSize / FlowControlSeparationTime). v1-v4 files load with
// BypassSecurity false and FC bytes 0/0, preserving spec-correct $27 +
// most-permissive ISO-TP behaviour.
//
// v6 added the BootloaderCapture section (Enabled flag + optional directory
// override). v1-v5 files load with BootloaderCapture == null - the Bootloader
// tab toggle stays at its default (off) and the spec-correct NRC $31 path
// runs in Service36Handler.
public sealed class SimulatorConfig
{
    public const int CurrentVersion = 6;
    public const int MinSupportedVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public string? Description { get; set; }
    public List<EcuDto> Ecus { get; set; } = new();

    // Null for users who have never loaded a bin. The coordinator
    // owns the runtime state; this DTO only carries the persisted hints.
    public BinReplayConfig? BinReplay { get; set; }

    // Null until the user has touched the Bootloader-capture toggle. When
    // present, ConfigStore.ApplyTo restores the Enabled flag (and optional
    // directory override) to bus.Capture so the toggle survives a restart.
    public BootloaderCaptureConfig? BootloaderCapture { get; set; }
}

// Persisted bootloader-capture configuration. Slots into
// SimulatorConfig.BootloaderCapture. Directory is null when the user is happy
// with the default (%LOCALAPPDATA%\GmEcuSimulator\captures); a non-null value
// overrides CaptureSettings.CaptureDirectory at load time.
public sealed class BootloaderCaptureConfig
{
    public bool Enabled { get; set; }
    public string? Directory { get; set; }
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
    // Each module deserialises its own shape from this blob - ConfigSchema
    // doesn't need to know about any of them. Null is valid (module gets
    // defaults).
    public JsonElement? SecurityModuleConfig { get; set; }

    // When true the security module short-circuits $27 to positive responses
    // regardless of seed/key validation. Models real ECUs whose $27 level is
    // a stub (T43 TCM is the canonical example). Defaults to false so older
    // configs and a freshly-created ECU keep the spec-correct challenge.
    public bool BypassSecurity { get; set; }

    // ISO 15765-2 Flow Control BS/STmin emitted on First Frame reception.
    // Defaults 0/0 (most permissive). Override to mimic real silicon - e.g.
    // 6Speed.T43 tester needs BS=1 in the FC tail to recognise the response.
    public byte FlowControlBlockSize { get; set; }
    public byte FlowControlSeparationTime { get; set; }

    // GMW3110-2010 §8.16 ReportProgrammedState ($A2) byte. Default 0x00
    // (FullyProgrammed) matches a normal running ECU.
    public byte ProgrammedState { get; set; }

    public List<PidDto> Pids { get; set; } = new();

    // GMW3110 §8.3 ReadDataByIdentifier ($1A) values. Null when the user has
    // not configured any identifiers (or when loading a v1-v3 config). Each
    // entry pairs a DID byte with its raw value bytes; the handler echoes
    // those bytes verbatim in the positive response.
    public List<IdentifierDto>? Identifiers { get; set; }
}

// One $1A data identifier and the bytes to return for it. Bytes are
// expressed as either an ASCII string (for printable values like VIN) or
// a hex string ("01 02 03" / "0102 03" / "0x01 0x02"). The loader prefers
// Ascii when present; otherwise it parses Hex. Both null = empty value.
public sealed class IdentifierDto
{
    [JsonConverter(typeof(HexByteConverter))]
    public required byte Did { get; set; }

    // Optional human-readable label ("VIN", "Calibration ID"). Not used by
    // the protocol — it just makes the JSON self-documenting.
    public string? Name { get; set; }

    /// <summary>Printable-ASCII shorthand. Mutually exclusive with Hex.</summary>
    public string? Ascii { get; set; }

    /// <summary>Whitespace-separated hex byte list. Mutually exclusive with Ascii.</summary>
    public string? Hex { get; set; }
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
