using System.Text.Json;
using System.Text.Json.Serialization;
using Common.Glitch;
using Common.Protocol;
using Common.Replay;
using Common.Waveforms;

namespace Common.Persistence;

// JSON-serialised configuration. Hex-string CAN IDs follow the
// Tester-Emu (https://github.com/jakka351/Tester-Emu) convention so a
// human can hand-edit. PidDto and WaveformDto stay flat - easier to
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
// ReadDataByIdentifier responses. (Dropped in v12 - see below.)
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
//
// v7 added per-ECU DownloadAddressByteCount (number of bytes in the $36
// startingAddress field, 2..4). v1-v6 files load with the field absent ->
// the loader defaults it to 4 to match GMW3110-2010-era ECUs like T43;
// older 3-byte ECUs need an explicit override in the saved config.
//
// v8 dropped per-ECU AllowPeriodicTesterPresent. The setting hoisted to a
// simulator-wide preference (AppSettings.AllowPeriodicTesterPresent, exposed
// through the ECU menu). v1-v7 configs still load: STJ silently ignores the
// now-unknown property. A per-ECU `false` in an old config is NOT migrated
// into the global - users who had it disabled on any ECU need to toggle the
// menu item off after upgrading.
//
// v9 added per-ECU DiagnosticAddress (returned by $1A $B0). Defaults to 0
// for v1-v8 configs that lack the field; users set it to match the low byte
// of PhysicalRequestCanId. (v9 also briefly carried a SpsType enum for the
// blank-ECU activation flow; that was removed once we collapsed the persona
// to always-on Type-A behaviour - the field is silently dropped on load.)
//
// v10 added IdentifierDto.Source per-row provenance tracking. (Dropped
// in v12 along with the rest of IdentifierDto.)
//
// v11 dropped per-ECU BypassSecurity. The per-ECU security module dropdown
// covers the same use case (select gm-programming-bypass for stub-security
// ECUs). v1-v10 configs still load: STJ silently ignores the now-unknown
// property. Users who had it enabled need to switch the affected ECU's
// security module to gm-programming-bypass after upgrading.
//
// v12 dropped per-ECU Identifiers. DIDs are now seeded at runtime only
// (Bin menu -> Load info from BIN... / Auto-populate DIDs, or File ->
// Prime from DPS archive). v1-v11 configs still load: STJ silently
// ignores the now-unknown property, so any persisted DIDs are not carried
// over and the user has to re-seed via the Bin menu or a primed archive.
//
// v13 dropped per-ECU DownloadAddressByteCount (the v7 field). The $36
// startingAddress is fixed at 4 bytes in production to match T43-era and
// later GM ECUs (kernel destinations like 0x003FAFE0 don't fit in 3 bytes,
// and tools like 6Speed.T43 always send the full 4). Tests that need to
// exercise 2/3-byte address layouts override NodeState.DownloadAddressByteCount
// directly. v1-v12 configs still load: STJ silently ignores the now-unknown
// property.
//
// v14 dropped the FC.STmin half of the v5 FlowControl pair. FC.BS stays
// (6Speed.T43 needs BS=1); FC.STmin was never needed in practice and the
// most-permissive 0 is always correct. v1-v13 configs still load: STJ
// silently ignores the now-unknown FlowControlSeparationTime property.
//
// v15 added PidDto.Mode for multi-mode PID rows ($1A / $22 / $2D in the
// same grid). Absent in v1-v14 configs - the loader defaults it to
// PidMode.Mode22, which is the legacy single-mode behaviour, so older
// configs round-trip identically. Mode2D rows store the 32-bit memory
// address in PidDto.Address; the wire PID id is derived at runtime as
// 0xF000 | (addr & 0x0FFF) and never persisted. Mode1A rows store the
// DID in the low 8 bits of Address; they superseded the v12-dropped
// IdentifierDto for user-editable DIDs.
public sealed class SimulatorConfig
{
    public const int CurrentVersion = 15;
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

    // Path to a DPS programming archive (.zip) the simulator should auto-
    // ingest on startup via Core.Dps.ArchivePrimer.ApplyTo. Null = no
    // prime-from-archive behaviour. Set by the File -> Prime from DPS
    // archive... menu item.
    public string? PrimeArchivePath { get; set; }

    // Optional path to a full 2 MiB ECU flash readback (.bin) whose boot
    // block (0x000000-0x00FFFF) is spliced with the archive OS module to
    // form a synthetic full binary for Mode1ADidBinExtractor. Null = no
    // donor (archive-only prime, walker returns null as before).
    public string? DonorBinPath { get; set; }
}

// Persisted bootloader-capture configuration. Slots into
// SimulatorConfig.BootloaderCapture. Directory is null when the user is happy
// with the default (%LOCALAPPDATA%\GmEcuSimulator\logs\captures); a non-null value
// overrides CaptureSettings.CaptureDirectory at load time. The legacy
// Enabled flag from earlier versions is silently dropped on load - capture
// writes are unconditional now (controlled by directory presence in
// CaptureSettings).
public sealed class BootloaderCaptureConfig
{
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

    // ISO 15765-2 Flow Control BS byte emitted on First Frame reception.
    // Default 0 (no further FC; send all CFs in one burst). Override to
    // mimic real silicon - e.g. 6Speed.T43 tester needs BS=1 in the FC
    // tail to recognise the response.
    public byte FlowControlBlockSize { get; set; }

    // GMW3110-2010 §8.16 ReportProgrammedState ($A2) byte. Default 0x00
    // (FullyProgrammed) matches a normal running ECU.
    public byte ProgrammedState { get; set; }

    // 8-bit diagnostic address returned by $1A $B0 (Read ECU Diagnostic
    // Address). Typically equals the low byte of PhysicalRequestCanId, e.g.
    // PhysicalRequestCanId = $7E0 -> DiagnosticAddress = $11. Default 0.
    [JsonConverter(typeof(HexByteConverter))]
    public byte DiagnosticAddress { get; set; }

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
    // the protocol - it just makes the JSON self-documenting.
    public string? Name { get; set; }

    /// <summary>Printable-ASCII shorthand. Mutually exclusive with Hex.</summary>
    public string? Ascii { get; set; }

    /// <summary>Whitespace-separated hex byte list. Mutually exclusive with Ascii.</summary>
    public string? Hex { get; set; }

    /// <summary>
    /// Provenance tag - "user" (hand-typed), "bin" (Load Info From Bin),
    /// "auto" (Auto-populate), or "blank" (deliberately empty, sticky).
    /// Optional in v1-v9 configs; absent -> User (the old default before
    /// source tracking existed).
    /// </summary>
    public DidSource Source { get; set; } = DidSource.User;
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

    /// <summary>
    /// Which service this row serves on the wire. See <see cref="PidMode"/>
    /// for the per-mode meaning of <see cref="Address"/>. Defaults to
    /// <see cref="PidMode.Mode22"/> so pre-v15 configs (which had no Mode
    /// field) load with the legacy single-mode behaviour.
    /// </summary>
    public PidMode Mode { get; set; } = PidMode.Mode22;

    /// <summary>
    /// Optional explicit response length in bytes. Overrides <see cref="Size"/>
    /// (which caps at 4) for PIDs longer than 4 bytes. Real GM ECUs expose $22
    /// PIDs of arbitrary byte length (e.g. E38 PID 0x155B is 17 bytes); bin-
    /// auto-extracted PID entries carry this field with the real-ECU length.
    /// </summary>
    public int? LengthBytes { get; set; }

    /// <summary>
    /// Optional verbatim response bytes as a contiguous hex string (e.g.
    /// <c>"0000..00"</c> for zero-fill). When present, the $22 handler returns
    /// these bytes directly and skips the waveform-encoding path. Length must
    /// match <see cref="LengthBytes"/> (or <see cref="Size"/> when <c>LengthBytes</c>
    /// is null) - padded with zeros if shorter. Lowercase, no spaces, no <c>0x</c>
    /// prefix; <c>null</c> means "use waveform" as before.
    /// </summary>
    public string? StaticBytes { get; set; }
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
