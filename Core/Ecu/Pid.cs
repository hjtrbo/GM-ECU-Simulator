using Common.Protocol;
using Common.Signals;
using Common.Waveforms;

namespace Core.Ecu;

// One simulated parameter on an ECU. All properties are mutable so the
// editor UI can edit them live; the WaveformConfig setter rebuilds the
// underlying IWaveformGenerator so the next scheduler tick picks up the
// new shape automatically.
//
// Address is `uint` (32-bit) so memory addresses like 0x002C0000 — typical
// for ECU RAM/flash — fit. Wire-protocol-readable PIDs ($22) only see the
// low 16 bits since the spec defines a 2-byte PID id; longer addresses can
// only be reached by mapping them via $2D first (which assigns a 16-bit
// short PID that mirrors the 32-bit address).
public sealed class Pid
{
    public uint Address { get; set; }
    public string Name { get; set; } = "";
    public PidSize Size { get; set; } = PidSize.Word;
    public PidDataType DataType { get; set; } = PidDataType.Unsigned;
    public double Scalar { get; set; } = 1.0;
    public double Offset { get; set; } = 0.0;
    public string Unit { get; set; } = "";

    // Which service this row serves on the wire. The Address field's meaning
    // depends on this: Mode22 = 2-byte wire PID id; Mode1A = 1-byte DID in
    // the low 8 bits; Mode2D = 32-bit memory address (wire PID id is derived
    // via WireLookupId). Defaults to Mode22 so legacy configs round-trip.
    public PidMode Mode { get; set; } = PidMode.Mode22;

    // 2-byte wire PID id the $22 dispatcher matches against. Mode22 echoes
    // Address verbatim; Mode2D derives the alias as 0xF000 | (addr & 0x0FFF)
    // (GM's dynamic-PID range, deterministic so the DataLogger profile stays
    // valid across reloads without persisting the alias). Mode1A rows aren't
    // reachable through $22 - returns null.
    public ushort? WireLookupId => Mode switch
    {
        PidMode.Mode22 => (ushort)(Address & 0xFFFF),
        PidMode.Mode2D => (ushort)(0xF000 | (Address & 0x0FFF)),
        _ => null,
    };

    // The key this PID occupies in its per-mode store (see EcuNode.AddPid): the 1-byte DID for $1A, the 2-byte wire
    // PID for $22, the full 32-bit address for $2D. Two rows in the same mode that share this key collide - the store
    // keeps only the last one added and silently shadows the rest on the wire - so the editor uses it to keep each
    // identifier unique within a mode.
    public uint StoreKey => StoreKeyFor(Mode, Address);

    public static uint StoreKeyFor(PidMode mode, uint address) => mode switch
    {
        PidMode.Mode1A => address & 0xFF,
        PidMode.Mode22 => address & 0xFFFF,
        _              => address,
    };

    // 1-byte DID this row serves on $1A; null for Mode22/Mode2D rows. The
    // $1A handler uses this to short-circuit the identifier-table lookup so
    // a user-edited row in the PID grid takes precedence over the bin- or
    // archive-seeded identifier value.
    public byte? Mode1ADid => Mode == PidMode.Mode1A ? (byte)(Address & 0xFF) : null;

    /// <summary>
    /// Optional override for <see cref="Size"/> to express PIDs longer than
    /// 4 bytes. The legacy <see cref="PidSize"/> enum caps at <c>DWord</c>
    /// (4 bytes) because synthetic-waveform PIDs are scalar values; real GM
    /// ECUs expose PIDs of arbitrary byte length via $22 (e.g. PID 0x155B is
    /// 17 bytes on the E38 family). When non-null, the $22 handler responds
    /// with exactly this many bytes regardless of <see cref="Size"/>.
    /// </summary>
    public int? LengthBytes { get; set; }

    /// <summary>
    /// Optional verbatim response payload. When set, the $22 handler returns
    /// these bytes directly (truncated/padded to <see cref="ResponseLength"/>)
    /// and ignores the waveform-encoding pipeline. Used by bin-extracted
    /// PID placeholders that have a known correct size but no live data
    /// source - zero-filled is the default. Existing synthetic-waveform PIDs
    /// (Engine RPM etc.) leave this null and go through the waveform path.
    /// </summary>
    public byte[]? StaticBytes { get; set; }

    /// <summary>Effective wire response length in bytes: <see cref="LengthBytes"/>
    /// when explicit, otherwise the value of the legacy <see cref="Size"/> enum.</summary>
    public int ResponseLength => LengthBytes ?? (int)Size;

    // Where this row draws its live value from (None / Waveform / Signal). The editor's Signal column is the single
    // selector for this - a row is no longer implicitly wired to its waveform just because no signal is chosen. Defaults
    // to Waveform so a bare `new Pid { WaveformConfig = ... }` and every pre-v17 config (which had no source field)
    // behave exactly as before; the editor's Add button overrides this to None so a fresh row reads 0 until sourced.
    private PidValueSource valueSource = PidValueSource.Waveform;
    public PidValueSource ValueSource
    {
        get => valueSource;
        set => valueSource = value;
    }

    // When set, this PID is signal-backed: its value comes from the owning ECU's EngineModel (live, scenario-driven)
    // rather than its waveform or StaticBytes. Scalar/Offset/DataType still define the wire encoding, so the same signal
    // can carry GM A2L scaling here while Mode $01 carries the legislated J1979 formula for it. Assigning a non-null
    // signal selects ValueSource.Signal automatically (the common "this row reads signal X" intent); clear the source
    // explicitly via ValueSource to go back to Waveform/None. Resolving it needs the engine attached via AttachEngine
    // (EcuNode.AddPid does so).
    private SignalId? signal;
    public SignalId? Signal
    {
        get => signal;
        set
        {
            signal = value;
            if (value is not null) valueSource = PidValueSource.Signal;
        }
    }

    /// <summary>
    /// Fill <paramref name="dest"/> with this PID's response payload bytes. Precedence:
    /// an attached <see cref="ValueSource.Signal"/> row samples the live EngineModel; otherwise <see cref="StaticBytes"/>
    /// verbatim (zero-padded if shorter) when present; otherwise a <see cref="ValueSource.Waveform"/> row encodes its
    /// waveform; otherwise (<see cref="PidValueSource.None"/> with no static payload) the response is all zeros.
    /// All numeric paths encode through <see cref="ValueCodec.Encode"/> with this PID's Scalar/Offset/DataType.
    /// </summary>
    public void WriteResponseBytes(double timeMs, Span<byte> dest)
    {
        if (valueSource == PidValueSource.Signal && Signal is { } sig && engine is { } eng)
        {
            ValueCodec.Encode(eng.Sample(sig, timeMs), Scalar, Offset, DataType, dest.Length, dest);
            return;
        }
        if (StaticBytes is not null)
        {
            int n = Math.Min(StaticBytes.Length, dest.Length);
            StaticBytes.AsSpan(0, n).CopyTo(dest);
            if (n < dest.Length) dest.Slice(n).Clear();
            return;
        }
        if (valueSource == PidValueSource.Waveform)
        {
            ValueCodec.Encode(waveform.Sample(timeMs), Scalar, Offset, DataType, dest.Length, dest);
            return;
        }
        dest.Clear();   // None, no static payload -> a flat zero response (the value must come from a chosen source)
    }

    // The PID's current engineering value (pre-encoding): the live signal when signal-backed, the waveform sample when
    // waveform-sourced, otherwise 0 (a None row, or a static-byte row whose bytes the editor surfaces directly). Used
    // by the editor's live readout - the wire path goes through WriteResponseBytes, which applies Scalar/Offset on top.
    public double SampleValue(double timeMs)
    {
        if (valueSource == PidValueSource.Signal && Signal is { } sig && engine is { } eng) return eng.Sample(sig, timeMs);
        if (StaticBytes is not null) return 0;
        return valueSource == PidValueSource.Waveform ? waveform.Sample(timeMs) : 0;
    }

    private WaveformConfig waveformConfig = new();
    private IWaveformGenerator waveform;

    // Provided by BinChannelToPid when this PID was built from a bin channel - returns a fresh ReplayWaveform
    // bound to the coordinator + channel index. Null when no bin is loaded for this PID, in which case Shape ==
    // FileStream falls back to a constant-zero generator so the dropdown selection never crashes.
    private Func<IWaveformGenerator>? replayWaveformFactory;

    // The owning ECU's engine model, attached by EcuNode.AddPid so a signal-backed PID resolves its value live. Null
    // until the PID is added to a node (e.g. a bare Pid in a test): a signal-backed PID with no engine falls through
    // to the StaticBytes / waveform path.
    private EngineModel? engine;

    // Bind this PID to its owning ECU's engine model. Called by EcuNode.AddPid; lets WriteResponseBytes resolve Signal.
    internal void AttachEngine(EngineModel engineModel) => engine = engineModel;

    public Pid()
    {
        waveform = BuildWaveform();
    }

    // The user-editable shape configuration. Setting this rebuilds the derived Waveform so live samples switch
    // immediately. When Shape == FileStream the rebuild routes through the replay-waveform factory instead of
    // WaveformFactory.
    public WaveformConfig WaveformConfig
    {
        get => waveformConfig;
        set
        {
            waveformConfig = value ?? throw new ArgumentNullException(nameof(value));
            waveform = BuildWaveform();
        }
    }

    public IWaveformGenerator Waveform => waveform;

    // Wires this PID to the bin replay coordinator so Shape == FileStream produces a live ReplayWaveform.
    // BinChannelToPid calls this when building PIDs from a loaded bin's channel headers; pass null to detach
    // (the FileStream selection will then fall back to a constant zero on the next rebuild). If the current
    // Shape is FileStream the active generator is rebuilt immediately so the new factory takes effect.
    public void SetReplayWaveformFactory(Func<IWaveformGenerator>? factory)
    {
        replayWaveformFactory = factory;
        if (waveformConfig.Shape == WaveformShape.FileStream)
            waveform = BuildWaveform();
    }

    public bool HasReplayWaveform => replayWaveformFactory != null;

    // Read-only accessor exposed so $2D DefinePidByAddress can propagate the
    // factory when it clones an existing Pid - without this, a dynamic PID
    // aliasing a bin-replay address resolves to ConstantWaveform(0) on the
    // wire even though the underlying Pid has live bin samples.
    public Func<IWaveformGenerator>? ReplayWaveformFactory => replayWaveformFactory;

    private IWaveformGenerator BuildWaveform()
    {
        if (waveformConfig.Shape == WaveformShape.FileStream)
            return replayWaveformFactory?.Invoke() ?? new ConstantWaveform(0);
        return WaveformFactory.Create(waveformConfig);
    }
}
