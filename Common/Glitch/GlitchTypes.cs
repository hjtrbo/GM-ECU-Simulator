namespace Common.Glitch;

// Glitch-injection configuration for stress-testing host applications.
//
// At runtime (when wired up — currently NOT wired) the simulator's service
// dispatcher consults each ECU's GlitchConfig before processing a request:
// if Enabled is true and a uniform random draw on [0,1) falls below the
// service-specific Probability, the dispatcher applies the configured Action
// instead of the normal response.
//
// This file defines the configuration shape only. The injection logic lives
// in Core/Services (when implemented). Keeping the types in Common means
// both runtime code and JSON persistence (Common.Persistence) reference the
// same definitions — no DTO/model duplication for plain settings.

public enum GlitchAction
{
    /// <summary>Normal response — no glitch even if Probability roll succeeds.</summary>
    None = 0,

    /// <summary>Return $7F SID NRC, where NRC is randomly chosen from <see cref="GlitchConfig.NrcPool"/>.</summary>
    EmitNrc = 1,

    /// <summary>Silently drop the request — no response is sent. Forces the host to hit its read timeout.</summary>
    Drop = 2,

    /// <summary>Flip a random byte in the outgoing response payload before enqueue.</summary>
    CorruptByte = 3,

    /// <summary>
    /// Randomly choose between <see cref="EmitNrc"/>, <see cref="Drop"/>, and
    /// <see cref="CorruptByte"/> on each glitch firing. The choice is made
    /// per-glitch, not per-session — every time the probability roll succeeds
    /// the dispatcher draws a fresh action from this trio. <see cref="None"/>
    /// is excluded from the random pool (a None pick would be indistinguishable
    /// from the probability roll having failed).
    /// </summary>
    Random = 4,
}

/// <summary>
/// Per-service glitch settings. ServiceId is the GMW3110 SID byte
/// (e.g. 0x22 for ReadDataByPid). Probability is on [0,1].
/// </summary>
public sealed class GlitchServiceSetting
{
    public byte ServiceId { get; set; }
    public double Probability { get; set; } = 0.0;
    public GlitchAction Action { get; set; } = GlitchAction.EmitNrc;
}

/// <summary>
/// Per-ECU glitch configuration. <see cref="Enabled"/> is the master gate.
/// When false the dispatcher skips all glitch logic regardless of per-service
/// probabilities, so configured-but-disabled glitches are effectively zero-cost.
/// </summary>
public sealed class GlitchConfig
{
    public bool Enabled { get; set; } = false;

    /// <summary>One entry per known service. Default-populated by <see cref="CreateDefault"/>.</summary>
    public List<GlitchServiceSetting> Services { get; set; } = new();

    /// <summary>
    /// NRC bytes the dispatcher randomly picks from when an EmitNrc action fires.
    /// Real testers must handle several NRC variants; including a mix exercises
    /// host paths that single-NRC simulators wouldn't.
    /// </summary>
    public List<byte> NrcPool { get; set; } = new();

    /// <summary>
    /// Returns a fresh GlitchConfig with all known services pre-populated at
    /// Probability=0 (off) and a sensible default NRC pool. Used as the default
    /// initializer for new ECUs and for older saved configs that don't include
    /// glitch settings.
    /// </summary>
    public static GlitchConfig CreateDefault() => new()
    {
        Enabled = false,
        Services = new List<GlitchServiceSetting>
        {
            new() { ServiceId = 0x22, Action = GlitchAction.EmitNrc },  // ReadDataByPid
            new() { ServiceId = 0x2C, Action = GlitchAction.EmitNrc },  // DynamicallyDefineMessage
            new() { ServiceId = 0x2D, Action = GlitchAction.EmitNrc },  // DefinePidByAddress
            new() { ServiceId = 0xAA, Action = GlitchAction.EmitNrc },  // ReadDataByPacketIdentifier
            new() { ServiceId = 0x3E, Action = GlitchAction.EmitNrc },  // TesterPresent
            new() { ServiceId = 0x20, Action = GlitchAction.EmitNrc },  // ReturnToNormalMode
            new() { ServiceId = 0x10, Action = GlitchAction.EmitNrc },  // InitiateDiagnosticOperation
        },
        NrcPool = new List<byte> { 0x11, 0x12, 0x22, 0x31 },
    };

    /// <summary>Friendly name for a known GMW3110 service SID. Used by the editor UI.</summary>
    public static string ServiceName(byte sid) => sid switch
    {
        0x22 => "ReadDataByPid",
        0x2C => "DynamicallyDefineMessage",
        0x2D => "DefinePidByAddress",
        0xAA => "ReadDataByPacketIdentifier",
        0x3E => "TesterPresent",
        0x20 => "ReturnToNormalMode",
        0x10 => "InitiateDiagnosticOperation",
        _    => "Unknown",
    };

    /// <summary>Friendly name for a known GMW3110 NRC byte. Used by the editor UI.</summary>
    public static string NrcName(byte nrc) => nrc switch
    {
        0x10 => "generalReject",
        0x11 => "serviceNotSupported",
        0x12 => "subFunctionNotSupported",
        0x22 => "conditionsNotCorrect",
        0x31 => "requestOutOfRange",
        0x78 => "busyResponsePending",
        _    => "Unknown",
    };
}
