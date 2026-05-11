using Common.Protocol;
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

    private WaveformConfig waveformConfig = new();
    private IWaveformGenerator waveform;

    // Provided by BinChannelToPid when this PID was built from a bin channel - returns a fresh ReplayWaveform
    // bound to the coordinator + channel index. Null when no bin is loaded for this PID, in which case Shape ==
    // FileStream falls back to a constant-zero generator so the dropdown selection never crashes.
    private Func<IWaveformGenerator>? replayWaveformFactory;

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
