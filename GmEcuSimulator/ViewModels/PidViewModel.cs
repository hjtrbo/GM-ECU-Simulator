using Common.Protocol;
using Common.Waveforms;
using Core.Ecu;

namespace GmEcuSimulator.ViewModels;

// Editable view of one Pid. Address / Name / Size / DataType / scaling /
// unit are pushed straight into the model. Address changes trigger the
// owning EcuViewModel to update its lookup so subsequent $22 requests
// route to the right entry.
public sealed class PidViewModel : NotifyPropertyChangedBase
{
    public Pid Model { get; }
    private readonly EcuViewModel parent;
    private string liveValue = "—";

    public PidViewModel(Pid pid, EcuViewModel parent)
    {
        Model = pid;
        this.parent = parent;
        Waveform = new WaveformViewModel(pid);

        // Re-evaluate the aliasing warning whenever the user changes the
        // waveform's frequency or shape — those are the only inputs that
        // affect Nyquist analysis. Other waveform tweaks (amplitude, offset,
        // phase, file path) don't change whether sampling will alias.
        Waveform.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WaveformViewModel.FrequencyHz)
             || e.PropertyName == nameof(WaveformViewModel.Shape))
            {
                OnPropertyChanged(nameof(AliasWarning));
                OnPropertyChanged(nameof(AliasWarningTooltip));
                OnPropertyChanged(nameof(HasAliasWarning));
            }
        };
    }

    public WaveformViewModel Waveform { get; }

    public uint Address
    {
        get => Model.Address;
        set
        {
            if (Model.Address == value) return;
            Model.Address = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AddressHex));
            parent.RaisePidsChanged();
        }
    }

    // 32-bit-capable hex display. Pads to 4 hex digits for the common $22-PID
    // case (≤ 0xFFFF) and to 8 hex digits once a memory address (e.g.
    // 0x002C0000) overflows the 16-bit space.
    public string AddressHex
    {
        get => Model.Address <= 0xFFFF ? $"0x{Model.Address:X4}" : $"0x{Model.Address:X8}";
        set
        {
            var trimmed = value?.Trim() ?? "";
            if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[2..];
            else if (trimmed.EndsWith('h') || trimmed.EndsWith('H')) trimmed = trimmed[..^1];
            if (uint.TryParse(trimmed, System.Globalization.NumberStyles.HexNumber,
                              System.Globalization.CultureInfo.InvariantCulture, out var v))
                Address = v;
        }
    }

    public string Name
    {
        get => Model.Name;
        set { if (Model.Name != value) { Model.Name = value; OnPropertyChanged(); parent.RaisePidsChanged(); } }
    }

    public PidSize Size
    {
        get => Model.Size;
        set { if (Model.Size != value) { Model.Size = value; OnPropertyChanged(); parent.RaisePidsChanged(); } }
    }

    public PidDataType DataType
    {
        get => Model.DataType;
        set { if (Model.DataType != value) { Model.DataType = value; OnPropertyChanged(); parent.RaisePidsChanged(); } }
    }

    public double Scalar
    {
        get => Model.Scalar;
        set { if (Model.Scalar != value) { Model.Scalar = value; OnPropertyChanged(); parent.RaisePidsChanged(); } }
    }

    public double Offset
    {
        get => Model.Offset;
        set { if (Model.Offset != value) { Model.Offset = value; OnPropertyChanged(); parent.RaisePidsChanged(); } }
    }

    public string Unit
    {
        get => Model.Unit;
        set { if (Model.Unit != value) { Model.Unit = value; OnPropertyChanged(); parent.RaisePidsChanged(); } }
    }

    public string LiveValue
    {
        get => liveValue;
        set => SetField(ref liveValue, value);
    }

    public void RefreshLive(double timeMs)
        => LiveValue = Model.Waveform.Sample(timeMs).ToString("F2");

    // ---------------- Aliasing warning ----------------
    //
    // Flags the dramatic case: the waveform frequency is within 2 % of an
    // integer multiple of a DPID band's sample rate. Sin(2π·f·t) at sample
    // times t = 0, 1/r, 2/r, ... lands on the same phase point every sample
    // when f / r is an integer, producing a perfectly DC value on the wire.
    // Frequencies near (but not at) that ratio still produce a near-DC
    // value with very slow drift — equally surprising to a user expecting
    // to see their cycle, so worth flagging.
    //
    // The 2 % window is intentionally narrow. Frequencies between integer
    // multiples (e.g. 0.7 Hz at the Slow band, or 1.5 Hz which folds down
    // to 0.5 Hz) DO alias in the Nyquist sense, but the host sees motion —
    // a slower-than-expected cycle — rather than a frozen value. Those
    // aren't flagged because the host can usually tell something is moving;
    // it's the frozen cases that fool the user into thinking the simulator
    // or host is broken.
    //
    // DPID bands (defined in Core/Scheduler/DpidScheduler.cs):
    //   Slow   1 Hz sample rate
    //   Medium 10 Hz
    //   Fast   25 Hz
    //
    // Constant and FileStream waveforms have no inherent frequency so the warning is suppressed for them
    // regardless of the FrequencyHz value. FileStream means "stream from the loaded bin replay" - sample timing
    // is driven by the bin's row cadence, not FrequencyHz, so the Nyquist check doesn't apply. If the user
    // flips a FileStream PID to a synthetic shape the check re-engages on the next property-change.

    /// <summary>
    /// Short, comma-separated list of DPID rate bands at which the
    /// configured waveform will alias to a near-constant (e.g. "aliases
    /// Slow", "aliases Slow, Med"). Returns null when there's no aliasing
    /// risk — bind to that null state to keep the cell empty for non-warning
    /// rows.
    /// </summary>
    public string? AliasWarning
    {
        get
        {
            var bands = AliasingBands();
            return bands.Count == 0 ? null : "aliases " + string.Join(", ", bands);
        }
    }

    /// <summary>
    /// True when this PID's waveform aliases at any DPID rate band — bound
    /// to by the DataGrid's RowStyle DataTrigger to paint a red left-border
    /// stripe on the row.
    /// </summary>
    public bool HasAliasWarning => AliasingBands().Count > 0;

    /// <summary>
    /// Long-form explanation suitable for a tooltip. Describes which bands
    /// alias, what the host will see, and the two remedies (offset the
    /// frequency, or schedule on a different band). Null when no warning.
    /// </summary>
    public string? AliasWarningTooltip
    {
        get
        {
            var bands = AliasingBands();
            if (bands.Count == 0) return null;
            return $"Waveform frequency {Waveform.FrequencyHz:0.###} Hz is within " +
                   $"2 % of an integer multiple of the {string.Join(", ", bands)} " +
                   $"DPID band's sample rate (Slow=1 Hz, Med=10 Hz, Fast=25 Hz). " +
                   $"The host will sample a near-constant value rather than the " +
                   $"cycling waveform — at exact multiples the value is perfectly DC. " +
                   $"Offset the frequency away from these multiples (e.g. 0.7 Hz, " +
                   $"1.3 Hz) or schedule the PID on a band whose sample rate isn't " +
                   $"a near-divisor of the frequency.";
        }
    }

    private List<string> AliasingBands()
    {
        var bands = new List<string>();
        if (!ShapeUsesFrequency(Waveform.Shape)) return bands;
        double f = Waveform.FrequencyHz;
        if (AliasesAtSampleRate(f, 1.0))  bands.Add("Slow");
        if (AliasesAtSampleRate(f, 10.0)) bands.Add("Med");
        if (AliasesAtSampleRate(f, 25.0)) bands.Add("Fast");
        return bands;
    }

    // True when freq is within 2 % of any integer multiple (n ≥ 1) of
    // sampleRate. Uses relative-error around the nearest multiple so the
    // window scales with the multiple — at the 1 Hz band the flagged ranges
    // are [0.98, 1.02], [1.96, 2.04], [2.94, 3.06], … rather than a fixed
    // ±0.02 Hz across all multiples.
    private static bool AliasesAtSampleRate(double freq, double sampleRate)
    {
        if (freq <= 0) return false;
        double ratio = freq / sampleRate;
        double n = Math.Round(ratio);
        if (n < 1) return false;            // freq below the first multiple — not flagged
        double error = Math.Abs(ratio - n) / n;
        return error <= 0.02;
    }

    private static bool ShapeUsesFrequency(WaveformShape shape)
        => shape != WaveformShape.Constant && shape != WaveformShape.FileStream;
}
