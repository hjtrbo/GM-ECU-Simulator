using Common.Glitch;
using Common.Protocol;
using Common.Replay;
using Common.Signals;
using Common.Waveforms;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Common.Persistence;

// JSON-serialised configuration. Hex-string CAN IDs follow the Tester-Emu (https://github.com/jakka351/Tester-Emu)
// convention so a human can hand-edit. PidDto and WaveformDto stay flat - easier to diff in source control than a
// deeply-nested structure.
//
// Schema version is incremented when a change is non-additive; loaders must handle older versions or fail with a clear
// error.
//
// v2 added the optional BinReplay section (path + auto-load + loop mode). v1 files load with BinReplay == null and
// round-trip cleanly.
//
// v3 added per-ECU SecurityModuleId + SecurityModuleConfig for $27 SecurityAccess support. v1/v2 files load with both
// null → $27 returns NRC $11 as before.
//
// v4 added per-ECU Identifiers (list of IdentifierDto) for $1A ReadDataByIdentifier responses. (Dropped in v12 - see
// below.)
//
// v5 added per-ECU BypassSecurity flag and per-ECU ISO-TP FlowControl bytes (FlowControlBlockSize /
// FlowControlSeparationTime). v1-v4 files load with BypassSecurity false and FC bytes 0/0, preserving spec-correct $27
// + most-permissive ISO-TP behaviour.
//
// v6 added the BootloaderCapture section (Enabled flag + optional directory override). v1-v5 files load with
// BootloaderCapture == null - the Bootloader tab toggle stays at its default (off) and the spec-correct NRC $31 path
// runs in Service36Handler.
//
// v7 added per-ECU DownloadAddressByteCount (number of bytes in the $36 startingAddress field, 2..4). v1-v6 files load
// with the field absent -> the loader defaults it to 4 to match GMW3110-2010-era ECUs like T43; older 3-byte ECUs need
// an explicit override in the saved config.
//
// v8 dropped per-ECU AllowPeriodicTesterPresent. The setting hoisted to a simulator-wide preference
// (AppSettings.AllowPeriodicTesterPresent, exposed through the ECU menu). v1-v7 configs still load: STJ silently
// ignores the now-unknown property. A per-ECU `false` in an old config is NOT migrated into the global - users who had
// it disabled on any ECU need to toggle the menu item off after upgrading.
//
// v9 added per-ECU DiagnosticAddress (returned by $1A $B0). Defaults to 0 for v1-v8 configs that lack the field; users
// set it to match the low byte of PhysicalRequestCanId. (v9 also briefly carried a SpsType enum for the blank-ECU
// activation flow; that was removed once we collapsed the persona to always-on Type-A behaviour - the field is silently
// dropped on load.)
//
// v10 added IdentifierDto.Source per-row provenance tracking. (Dropped in v12 along with the rest of IdentifierDto.)
//
// v11 dropped per-ECU BypassSecurity. The per-ECU security module dropdown covers the same use case (select
// gm-programming-bypass for stub-security ECUs). v1-v10 configs still load: STJ silently ignores the now-unknown
// property. Users who had it enabled need to switch the affected ECU's security module to gm-programming-bypass after
// upgrading.
//
// v12 dropped per-ECU Identifiers. DIDs are now seeded at runtime only (Bin menu -> Load info from BIN... /
// Auto-populate DIDs, or File -> Prime from DPS archive). v1-v11 configs still load: STJ silently ignores the
// now-unknown property, so any persisted DIDs are not carried over and the user has to re-seed via the Bin menu or a
// primed archive.
//
// v13 dropped per-ECU DownloadAddressByteCount (the v7 field). The $36 startingAddress is fixed at 4 bytes in
// production to match T43-era and later GM ECUs (kernel destinations like 0x003FAFE0 don't fit in 3 bytes, and tools
// like 6Speed.T43 always send the full 4). Tests that need to exercise 2/3-byte address layouts override
// NodeState.DownloadAddressByteCount directly. v1-v12 configs still load: STJ silently ignores the now-unknown
// property.
//
// v14 dropped the FC.STmin half of the v5 FlowControl pair. FC.BS stays (6Speed.T43 needs BS=1); FC.STmin was never
// needed in practice and the most-permissive 0 is always correct. v1-v13 configs still load: STJ silently ignores the
// now-unknown FlowControlSeparationTime property.
//
// v15 added PidDto.Mode for multi-mode PID rows ($1A / $22 / $2D in the same grid). Absent in v1-v14 configs - the
// loader defaults it to PidMode.Mode22, which is the legacy single-mode behaviour, so older configs round-trip
// identically. Mode2D rows store the 32-bit memory address in PidDto.Address; the wire PID id is derived at runtime as
// 0xF000 | (addr & 0x0FFF) and never persisted. Mode1A rows store the DID in the low 8 bits of Address; they superseded
// the v12-dropped IdentifierDto for user-editable DIDs.
//
// v16 is the clean-break baseline for the signal-centric redesign. MinSupportedVersion is raised to 16, so configs
// written before the redesign are now REJECTED with a clear version error rather than silently losing fields - the
// pre-redesign schema is not migrated. v16 carries PidDto.Signal (the signal-backed source) and EcuDto.Scenario (the
// boot operating point). It also carries EcuDto.Mode1Disabled - the delta of built-in $01 PIDs the user has turned OFF
// (the supported $01 subset is the E38/E67 default minus this list; absent/empty means the full default set stands).
//
// v17 added the optional top-level LiveTiles list - the ordered set of PIDs pinned to the main window's live-tile
// dashboard. Each entry references a PID by (Ecu name, Mode, Address); tiles whose target no longer resolves are
// pruned on load. Cross-ECU, so it lives at the config root rather than under EcuDto. v1-v16 files load with
// LiveTiles == null (empty dashboard) and round-trip cleanly; MinSupportedVersion stays 16.
public sealed class SimulatorConfig
{
    public const int CurrentVersion = 17;
    public const int MinSupportedVersion = 16;

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

    // Ordered set of PIDs pinned to the main window's live-tile dashboard.
    // Cross-ECU (each entry names its owning ECU), so it lives at the config
    // root rather than under EcuDto. Null / omitted -> empty dashboard. The
    // WPF MainViewModel owns reading/writing this; ConfigStore.ApplyTo (which
    // only touches the bus) ignores it. Tiles whose (Ecu, Mode, Address)
    // target no longer resolves are dropped silently on load.
    public List<LiveTileDto>? LiveTiles { get; set; }
}

// One pinned live-tile on the main window's dashboard. References its target
// indirectly (by name/id, not object identity) so it survives a save/load
// round-trip and a PID-list rebuild (Load PIDs). The order of the list IS the
// on-screen tile order.
//
//   Source = Pid  -> an editable $22/$2D/$1A PID row, keyed by (Ecu, Mode, Address).
//   Source = Obd2 -> a built-in $01 (OBD-II / J1979) PID, keyed by (Ecu, Address),
//                    where Address holds the 1-byte $01 PID id (Mode is unused).
//
// Source is absent in the first v17 files written before $01 tiles existed; it
// defaults to Pid so those still resolve.
public sealed class LiveTileDto
{
    public LiveTileSource Source { get; set; } = LiveTileSource.Pid;

    public required string Ecu { get; set; }

    // Only meaningful when Source == Pid; defaults to the legacy Mode22.
    public PidMode Mode { get; set; } = PidMode.Mode22;

    [JsonConverter(typeof(HexUIntConverter))]
    public required uint Address { get; set; }
}

// Which value layer a live-tile is pinned to. Serialised as a camelCase string.
public enum LiveTileSource
{
    Pid,
    Obd2,
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

    // Persona / dispatch table this ECU uses for inbound USDT requests.
    // Null or omitted -> "gmw3110" (the default that every GM ECU starts
    // with). "ford-capture" routes every request through FordCapturePersona,
    // which logs and NRC-replies without consulting any Service*Handler.
    // Resolved via PersonaRegistry on load. New field; absence in older
    // configs is silently treated as "gmw3110".
    public string? PersonaId { get; set; }

    // Optional path to a flash bin file backing Service $23 ReadMemoryByAddress
    // when PersonaId == "ford-capture". The file is loaded once at config-apply
    // time via FordCapturePersona.LoadFlashBin(path); subsequent $23 requests
    // serve directly from those bytes. Use this so PCMTec's flash-cross-check
    // probes (VIN at 0x000100C0, etc.) can complete against the real HAEE4UY
    // contents instead of NRC-ing. Path can be absolute or relative to the
    // config file's directory. Null / missing -> $23 returns NRC $22.
    public string? FlashBinPath { get; set; }

    // The operating point this ECU boots at (drives the live signal model). Null / omitted -> Idle. Persisted only
    // when it differs from Idle so standard configs stay quiet.
    public ScenarioId? Scenario { get; set; }

    // ID of the engine character driving the live signal model's derivation (induction curve, airflow, fuelling). Null
    // (or unknown to EngineCharacterRegistry) -> the naturally-aspirated default, so every config saved before this
    // field existed loads with the original behaviour unchanged. "boosted-gas-v8" selects the forced-induction model.
    public string? EngineModelId { get; set; }

    // Tunable timing of the AccelDecelSweep rev pull, in milliseconds: climb time, rev-limiter hold, coast time, and
    // the entry cross-fade. Each is persisted only when it differs from the SweepProfile default (keeps standard
    // configs quiet); a null falls back to that default on load. Independent of Scenario so a tuned profile survives
    // even while the ECU boots at Idle.
    public double? SweepAccelMs { get; set; }
    public double? SweepLimiterHoldMs { get; set; }
    public double? SweepDecelMs { get; set; }
    public double? SweepCrossfadeMs { get; set; }

    // The +/- rpm the engine oscillates by while bouncing off the rev limiter (the fuel/spark-cut amplitude).
    // Persisted only when it differs from the SweepProfile default; null falls back to that default on load.
    public double? SweepLimiterCutRpm { get; set; }

    // The $01 (OBD-II) PIDs this ECU has turned OFF, as a delta against the
    // built-in E38/E67 default supported subset. The advertised $01 set (and
    // its computed $00/$20/... support bitmask) is DefaultSupported minus this
    // list. Null / empty -> the ECU advertises the full default subset. Stored
    // as a delta so standard configs stay quiet and the default set can evolve
    // without rewriting every saved ECU.
    public List<byte>? Mode1Disabled { get; set; }
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

    // Optional signal-backed source for this PID's value (the redesigned live-signal model). When set, the value comes
    // from the ECU's EngineModel rather than the waveform/StaticBytes, encoded with this row's Scalar/Offset. Null = a
    // legacy waveform / static PID. Serialised as a camelCase string (e.g. "engineRpm").
    public SignalId? Signal { get; set; }

    // Where the row draws its live value: "none" (reads 0), "waveform", or "signal". Null in a pre-v17 config that
    // predates the explicit selector - ConfigStore.PidFrom then infers it (a non-null Signal -> Signal, otherwise the
    // old null-signal-means-waveform fallback) so older files keep behaving exactly as they did. Serialised camelCase.
    public PidValueSource? ValueSource { get; set; }
}

public sealed class WaveformDto
{
    public required WaveformShape Shape { get; set; }
    public double Amplitude { get; set; } = 1.0;
    public double Offset { get; set; } = 0.0;
    public double FrequencyHz { get; set; } = 1.0;
    public double PhaseDeg { get; set; } = 0.0;
    public double DutyCycle { get; set; } = 0.5;

    // Only meaningful when Shape == CsvFile; null / HoldLast for every other
    // shape so the JSON stays minimal on round-trip.
    public string? CsvFilePath { get; set; }
    public CsvLoopMode CsvLoopMode { get; set; } = CsvLoopMode.HoldLast;

    public WaveformConfig ToWaveformConfig() => new()
    {
        Shape       = Shape,
        Amplitude   = Amplitude,
        Offset      = Offset,
        FrequencyHz = FrequencyHz,
        PhaseDeg    = PhaseDeg,
        DutyCycle   = DutyCycle,
        CsvFilePath = CsvFilePath,
        CsvLoopMode = CsvLoopMode,
    };

    public static WaveformDto From(WaveformConfig cfg) => new()
    {
        Shape       = cfg.Shape,
        Amplitude   = cfg.Amplitude,
        Offset      = cfg.Offset,
        FrequencyHz = cfg.FrequencyHz,
        PhaseDeg    = cfg.PhaseDeg,
        DutyCycle   = cfg.DutyCycle,
        CsvFilePath = cfg.CsvFilePath,
        CsvLoopMode = cfg.CsvLoopMode,
    };
}
