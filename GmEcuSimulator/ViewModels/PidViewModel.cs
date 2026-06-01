using Common.Protocol;
using Common.Signals;
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
    private string liveValue = "-";

    private bool hasAliasCollision;
    private string? aliasCollisionTooltip;

    public PidViewModel(Pid pid, EcuViewModel parent)
    {
        Model = pid;
        this.parent = parent;
        lengthBytesText = Model.ResponseLength.ToString();
        Waveform = new WaveformViewModel(pid);

        // Re-evaluate the aliasing warning whenever the user changes the
        // waveform's frequency or shape - those are the only inputs that
        // affect Nyquist analysis. Other waveform tweaks (amplitude, offset,
        // phase, file path) don't change whether sampling will alias.
        Waveform.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WaveformViewModel.FrequencyHz)
             || e.PropertyName == nameof(WaveformViewModel.Shape))
            {
                RaiseAliasWarningChanged();
            }
        };
    }

    // Re-announce the Nyquist-aliasing warning state. Anything that changes whether the row actually USES its
    // waveform (signal source picked, static bytes set) or its frequency/shape must call this so the row's red-border
    // warning clears/appears immediately.
    private void RaiseAliasWarningChanged()
    {
        OnPropertyChanged(nameof(AliasWarning));
        OnPropertyChanged(nameof(AliasWarningTooltip));
        OnPropertyChanged(nameof(HasAliasWarning));
        OnPropertyChanged(nameof(HasWarning));
    }

    public WaveformViewModel Waveform { get; }

    public uint Address
    {
        get => Model.Address;
        set
        {
            if (Model.Address == value) return;
            // Reject a typed identifier already served by another row in this mode (the $1A DID / $2D address box).
            // The store keeps one row per key, so a duplicate would silently shadow the existing row on the wire. Flag
            // the cell (red border via INotifyDataErrorInfo) and leave the model unchanged; the user's text stays put
            // so they can correct it, matching the Size column's validation.
            if (parent.IsIdentifierTaken(Model, Model.Mode, value))
            {
                string label = Model.Mode == PidMode.Mode2D ? $"address 0x{value:X6}" : $"DID ${value & 0xFF:X2}";
                SetError(nameof(AddressHex), $"Another row in this mode already uses {label}.");
                return;
            }
            SetError(nameof(AddressHex), null);
            var oldAddress = Model.Address;
            Model.Address = value;
            // The per-mode store is keyed by Address, so re-key the entry before notifying. Without this a $2D / $22
            // read against the new address misses the dict and the ECU NRCs RequestOutOfRange.
            parent.OnPidAddressChanged(Model, oldAddress);
            OnPropertyChanged();
            OnPropertyChanged(nameof(AddressHex));
            OnPropertyChanged(nameof(IdentifierLabel));
            parent.RaisePidsChanged();
        }
    }

    public PidMode Mode
    {
        get => Model.Mode;
        set
        {
            if (Model.Mode == value) return;
            var oldMode = Model.Mode;
            Model.Mode = value;
            // The underlying Pid moves between the per-mode stores ($1A / $22 / $2D); EcuNode.RelocatePidMode does the
            // move atomically without churning this collection.
            parent.OnPidModeChanged(Model, oldMode, value);
            OnPropertyChanged();
            // Mode flips swap which columns are live and reshape the
            // identifier display; the cells re-read these on the same tick.
            OnPropertyChanged(nameof(IsMode1A));
            OnPropertyChanged(nameof(IsMode22));
            OnPropertyChanged(nameof(IsMode2D));
            OnPropertyChanged(nameof(IsCatalogueDriven));
            OnPropertyChanged(nameof(IsHandRolled));
            OnPropertyChanged(nameof(IdentifierCatalogue));
            OnPropertyChanged(nameof(SelectedCatalogueEntry));
            OnPropertyChanged(nameof(AddressHex));
            OnPropertyChanged(nameof(IdentifierLabel));
            parent.RaisePidsChanged();
        }
    }

    public bool IsMode1A => Model.Mode == PidMode.Mode1A;
    public bool IsMode22 => Model.Mode == PidMode.Mode22;
    public bool IsMode2D => Model.Mode == PidMode.Mode2D;

    // Spec-defined name for the configured identifier, when known. Surfaced
    // as the Identifier cell tooltip so the user can hover a row to confirm
    // "$90 = VIN" without cross-referencing the spec. Returns null for
    // unknown DIDs / PIDs (no tooltip shown).
    public string? IdentifierLabel => Model.Mode switch
    {
        PidMode.Mode1A => Gmw3110DidNames.NameOf((byte)(Model.Address & 0xFF)),
        _              => null,
    };

    // $1A and $22 rows pull their entire shape (identifier, size, type,
    // scaling, unit) from a static catalogue - the user picks from a
    // dropdown rather than typing values by hand. $2D rows are the inverse:
    // every field is editable because the user is rolling a custom dynamic
    // PID from scratch (typically mirroring a memory-mapped value the real
    // ECU doesn't natively expose).
    // $22 alone uses the catalogue dropdown (a big 2-byte DID library worth picking from). $1A shows just the raw DID
    // hex - the identity DIDs are few and the user thinks in "$90", not a catalogue name - and $2D is a hand-rolled
    // 32-bit address. Both of the latter use the plain hex text box.
    public bool IsCatalogueDriven => Model.Mode == PidMode.Mode22;
    public bool IsHandRolled      => Model.Mode is PidMode.Mode1A or PidMode.Mode2D;

    // The picker list for the current mode. Bound to the Identifier cell's ComboBox.ItemsSource on $22 rows; empty
    // (and the cell collapses to a TextBox) for $1A/$2D. Identifiers another row in this mode already serves are
    // filtered out - each identifier maps to exactly one row in its per-mode store, so offering a taken one would let
    // the user create a row that silently shadows (or is shadowed by) the existing one on the wire. The row's own
    // current identifier is never filtered (IdentifiersInUse excludes self), so the combo can still show its selection.
    public IReadOnlyList<PidCatalogueEntry> IdentifierCatalogue
    {
        get
        {
            var full = PidCatalogue.For(Model.Mode);
            var taken = parent.IdentifiersInUse(Model.Mode, exclude: Model);
            if (taken.Count == 0) return full;
            return full.Where(e => !taken.Contains(Pid.StoreKeyFor(Model.Mode, e.Identifier))).ToList();
        }
    }

    // Re-announce IdentifierCatalogue so the picker re-filters when another row claims or releases an identifier.
    // Called by EcuViewModel after a structural identifier change.
    internal void RefreshIdentifierCatalogue() => OnPropertyChanged(nameof(IdentifierCatalogue));

    // Round-trips the current row through the catalogue: the getter finds
    // the entry whose mode + identifier match the model (or null if the
    // row's identifier isn't in the catalogue - e.g. a config from before
    // the catalogue gained that PID). The setter stamps every shape field
    // onto the model in one shot so the read-only cells reflect the new
    // selection on the next tick.
    public PidCatalogueEntry? SelectedCatalogueEntry
    {
        get => IdentifierCatalogue.FirstOrDefault(e => e.Identifier == Model.Address);
        set
        {
            if (value is null) return;
            // Refuse a duplicate identifier - each DID gets exactly one row per mode (the per-mode store would keep
            // only the last one and shadow the rest on the wire). The picker already hides taken identifiers; this is
            // the backstop for a stale-open dropdown. Snap the combo back to the current selection.
            if (parent.IsIdentifierTaken(Model, Model.Mode, value.Identifier))
            {
                OnPropertyChanged(nameof(SelectedCatalogueEntry));
                return;
            }
            // Apply identifier first so AddressHex / IdentifierLabel raise
            // in the same property change burst as the rest.
            var oldAddress    = Model.Address;
            Model.Address     = value.Identifier;
            // Address is the per-mode store key, so re-key the entry; otherwise the dropdown's new identifier is
            // unreachable on the wire until the next reload.
            parent.OnPidAddressChanged(Model, oldAddress);
            Model.Name        = value.Name;
            Model.Size        = value.Size;
            Model.LengthBytes = value.LengthBytes;
            Model.DataType    = value.DataType;
            Model.Scalar      = value.Scalar;
            Model.Offset      = value.Offset;
            Model.Unit        = value.Unit;
            OnPropertyChanged(nameof(Address));
            OnPropertyChanged(nameof(AddressHex));
            OnPropertyChanged(nameof(IdentifierLabel));
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(Size));
            OnPropertyChanged(nameof(DataType));
            OnPropertyChanged(nameof(Scalar));
            OnPropertyChanged(nameof(Offset));
            OnPropertyChanged(nameof(Unit));
            OnPropertyChanged(nameof(SelectedCatalogueEntry));
            RefreshLengthBytesText();
            parent.RaisePidsChanged();
        }
    }

    // Identifier display: mode-aware hex formatting.
    //   Mode1  -> "$XX"      for the 1-byte OBD-II Service $01 PID id
    //   Mode1A -> "$XX"      for the GMW3110 $1A DID byte (e.g. "$90")
    //   Mode22 -> "$XXXX"    for the GMW3110 / UDS $22 wire PID id
    //   Mode2D -> "0xXXXXXX" for the 24-bit memory address - $2D rows mirror
    //                        a memory-mapped value; the "$" prefix would be
    //                        ambiguous with the 2-byte PID prefix GM uses
    //                        for the dynamically-defined alias, so we keep
    //                        the explicit C-style "0x" here. 6 hex digits
    //                        cover the full GM ECU code/calibration address
    //                        space (typical bins are <= 2 MiB). Addresses
    //                        above $FFFFFF widen automatically.
    // The setter accepts any of those forms regardless of mode so quick
    // edits don't fight the formatter.
    public string AddressHex
    {
        get => Model.Mode switch
        {
            PidMode.Mode1A => $"${(byte)(Model.Address & 0xFF):X2}",
            PidMode.Mode2D => $"0x{Model.Address:X6}",
            _              => Model.Address <= 0xFFFF ? $"${Model.Address:X4}" : $"0x{Model.Address:X6}",
        };
        set
        {
            var trimmed = value?.Trim() ?? "";
            if (trimmed.StartsWith("$"))                                      trimmed = trimmed[1..];
            else if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[2..];
            else if (trimmed.EndsWith('h') || trimmed.EndsWith('H'))          trimmed = trimmed[..^1];
            if (uint.TryParse(trimmed, System.Globalization.NumberStyles.HexNumber,
                              System.Globalization.CultureInfo.InvariantCulture, out var v))
                Address = v;
        }
    }

    // $2D rows derive their wire PID id from Address as 0xF000 | (addr & 0x0FFF).
    // Two rows whose addresses share the low 12 bits collide on the wire - both
    // would respond to the same $22 request. EcuViewModel re-evaluates this
    // whenever a row's mode or address changes; the row border + tooltip in
    // the SetupWindow grid surface the warning to the user.
    public bool HasAliasCollision
    {
        get => hasAliasCollision;
        set
        {
            if (SetField(ref hasAliasCollision, value))
                OnPropertyChanged(nameof(HasWarning));
        }
    }

    public string? AliasCollisionTooltip
    {
        get => aliasCollisionTooltip;
        set => SetField(ref aliasCollisionTooltip, value);
    }

    // Combined warning indicator the row style binds to: alias collision OR
    // Nyquist-band aliasing. Either turns on the red-border row decoration.
    public bool HasWarning => hasAliasCollision || HasAliasWarning;

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

    // Response length in bytes (1-99) as edited in the grid's Size column. Drives Pid.LengthBytes, which overrides the
    // legacy Size enum so arbitrary widths work (a VIN is 17 bytes). The raw text is held so an in-progress invalid
    // entry isn't silently reverted; the model updates only once the text validates (error shown via the row's
    // INotifyDataErrorInfo red border).
    private string lengthBytesText = "";
    public string LengthBytesText
    {
        get => lengthBytesText;
        set
        {
            if (!SetField(ref lengthBytesText, value)) return;
            var error = ValidateLengthBytes(value);
            SetError(nameof(LengthBytesText), error);
            if (error is null)
            {
                int n = int.Parse(value.Trim());
                Model.LengthBytes = n;
                // Keep the legacy Size enum coherent for the common 1/2/4 widths so other readers (catalogue picker,
                // persistence) aren't surprised; wider values fall to DWord but ResponseLength prefers LengthBytes.
                Model.Size = n switch { 1 => PidSize.Byte, 2 => PidSize.Word, _ => PidSize.DWord };
                parent.RaisePidsChanged();
            }
        }
    }

    // The Size field accepts a whole number of bytes, 1..99 (covers single sensors up to long records like VIN).
    private static string? ValidateLengthBytes(string? value)
    {
        var v = (value ?? "").Trim();
        if (!int.TryParse(v, out var n)) return "Size must be a whole number of bytes.";
        if (n < 1 || n > 99) return "Size must be between 1 and 99 bytes.";
        return null;
    }

    // Re-sync the Size text from the model when something other than the user's keystrokes changes the length (e.g.
    // the catalogue picker stamps a library entry's size). Clears any stale validation error.
    private void RefreshLengthBytesText()
    {
        lengthBytesText = Model.ResponseLength.ToString();
        SetError(nameof(LengthBytesText), null);
        OnPropertyChanged(nameof(LengthBytesText));
    }

    // The row's static response payload as human text - the value column in the editor. Identity ($1A) DIDs are
    // usually ASCII (VIN, part numbers, broadcast code), so a fully-printable payload shows as text; anything with a
    // non-printable byte shows as "0x..." hex. This is what a bin load surfaces (the extracted VIN etc.). For a $22 /
    // $2D row with no static bytes it reads blank (those rows are signal/waveform-driven; see WriteResponseBytes
    // precedence Signal > StaticBytes > waveform).
    //
    // Editing mirrors the display convention: a leading "0x" is parsed as hex bytes, otherwise the text is stored
    // verbatim as ASCII. Setting a value resizes the row to match its content length (an identity value's length IS
    // its byte count), keeping the Size column coherent.
    public string ValueText
    {
        get => BytesToDisplay(Model.StaticBytes);
        set
        {
            var bytes = ParseValue(value);
            if (bytes is null) return;                 // unparseable hex - keep the prior value rather than corrupt it
            if (BytesEqual(Model.StaticBytes, bytes)) return;

            Model.StaticBytes = bytes.Length == 0 ? null : bytes;
            if (bytes.Length != 0)
            {
                Model.LengthBytes = bytes.Length;
                // Keep the legacy Size enum coherent for 1/2/4-byte widths (other readers fall back to it).
                Model.Size = bytes.Length switch { 1 => PidSize.Byte, 2 => PidSize.Word, _ => PidSize.DWord };
                RefreshLengthBytesText();
            }
            OnPropertyChanged();
            RaiseAliasWarningChanged();   // gaining/losing static bytes flips whether the waveform (and its Nyquist risk) applies
            parent.RaisePidsChanged();
        }
    }

    // ASCII when every byte is printable; "0x"-prefixed hex otherwise. Empty for no payload.
    private static string BytesToDisplay(byte[]? b)
    {
        if (b is null || b.Length == 0) return "";
        bool printable = b.All(x => x >= 0x20 && x <= 0x7E);
        return printable ? System.Text.Encoding.ASCII.GetString(b) : "0x" + Convert.ToHexString(b);
    }

    // "0x...." -> hex bytes; anything else -> ASCII bytes. Returns null only for malformed hex (odd length / bad
    // digit), which the setter treats as "leave unchanged".
    private static byte[]? ParseValue(string? s)
    {
        var t = (s ?? "").Trim();
        if (t.Length == 0) return Array.Empty<byte>();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            var hex = t[2..].Replace(" ", "");
            if (hex.Length == 0) return Array.Empty<byte>();
            if ((hex.Length & 1) != 0) return null;
            try { return Convert.FromHexString(hex); } catch { return null; }
        }
        return System.Text.Encoding.ASCII.GetBytes(t);
    }

    private static bool BytesEqual(byte[]? a, byte[]? b)
    {
        if (a is null || a.Length == 0) return b is null || b.Length == 0;
        return b is not null && a.AsSpan().SequenceEqual(b);
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

    // The row's live-value source, bound to the Signal column's picker. This is now the single selector for where the
    // value comes from: "(none)" reads 0, "Waveform" runs the row's waveform generator, and any engine signal reads the
    // live model. Picking an option writes both Model.ValueSource and Model.Signal (the latter only for a real signal).
    public SignalOption SelectedSource
    {
        get => SignalOptions.FirstOrDefault(o => o.Matches(Model.ValueSource, Model.Signal)) ?? SignalOptions[0];
        set
        {
            if (value is null || value.Matches(Model.ValueSource, Model.Signal)) return;
            // Signal first (its setter auto-selects ValueSource.Signal), then the explicit source so None/Waveform win.
            Model.Signal = value.Source == PidValueSource.Signal ? value.Signal : null;
            Model.ValueSource = value.Source;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SignalDisplay));
            RaiseAliasWarningChanged();   // switching source flips whether the waveform (and its Nyquist risk) applies
            parent.RaisePidsChanged();
        }
    }

    // Flat text of the current source for the Signal column's filter/sort (the cell itself shows the picker).
    public string SignalDisplay => SelectedSource.Display;

    // Options for the Signal picker: "(none)" and "Waveform" up front, then every catalogue signal by friendly name.
    // Shared across all rows - the list never changes.
    public IReadOnlyList<SignalOption> SignalSourceOptions => SignalOptions;

    private static readonly IReadOnlyList<SignalOption> SignalOptions = BuildSignalOptions();

    private static IReadOnlyList<SignalOption> BuildSignalOptions()
    {
        var list = new List<SignalOption>
        {
            new(PidValueSource.None,     null, "(none)"),
            new(PidValueSource.Waveform, null, "Waveform"),
        };
        foreach (var d in SignalCatalogue.All) list.Add(new SignalOption(PidValueSource.Signal, d.Id, d.Name));
        return list;
    }

    public string LiveValue
    {
        get => liveValue;
        set => SetField(ref liveValue, value);
    }

    public void RefreshLive(double timeMs)
    {
        // Identity DIDs ($1A) and any static-payload PID have no scalar value -
        // their "value" is the response bytes themselves (VIN / codes shown as
        // ASCII, otherwise "0x.." hex via ValueText). SampleValue returns 0 for
        // those, which is what made them read "0.00" on the dashboard. Signal-
        // and waveform-backed PIDs still report the live engineering number.
        if (Model.StaticBytes is { Length: > 0 } || Model.Mode == PidMode.Mode1A)
        {
            var text = ValueText;
            LiveValue = string.IsNullOrEmpty(text) ? "-" : text;
        }
        else
        {
            LiveValue = Model.SampleValue(timeMs).ToString("F2");
        }
    }

    // ---------------- Aliasing warning ----------------
    //
    // Flags the dramatic case: the waveform frequency is within 2 % of an
    // integer multiple of a DPID band's sample rate. Sin(2π·f·t) at sample
    // times t = 0, 1/r, 2/r, ... lands on the same phase point every sample
    // when f / r is an integer, producing a perfectly DC value on the wire.
    // Frequencies near (but not at) that ratio still produce a near-DC
    // value with very slow drift - equally surprising to a user expecting
    // to see their cycle, so worth flagging.
    //
    // The 2 % window is intentionally narrow. Frequencies between integer
    // multiples (e.g. 0.7 Hz at the Slow band, or 1.5 Hz which folds down
    // to 0.5 Hz) DO alias in the Nyquist sense, but the host sees motion -
    // a slower-than-expected cycle - rather than a frozen value. Those
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
    /// risk - bind to that null state to keep the cell empty for non-warning
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
    /// True when this PID's waveform aliases at any DPID rate band - bound
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
                   $"cycling waveform - at exact multiples the value is perfectly DC. " +
                   $"Offset the frequency away from these multiples (e.g. 0.7 Hz, " +
                   $"1.3 Hz) or schedule the PID on a band whose sample rate isn't " +
                   $"a near-divisor of the frequency.";
        }
    }

    // True only when this row's wire value actually comes from the waveform generator - i.e. the user picked "Waveform"
    // as the source. A signal-backed or "(none)" row, and any static-payload row, never samples the waveform, so the
    // Nyquist analysis is meaningless for them. StaticBytes still wins over the waveform on the wire, so a static-payload
    // row is excluded even if its source is Waveform. Mirrors Pid.WriteResponseBytes precedence.
    private bool UsesWaveform => Model.Mode != PidMode.Mode1A
                              && Model.ValueSource == PidValueSource.Waveform
                              && (Model.StaticBytes is null || Model.StaticBytes.Length == 0);

    private List<string> AliasingBands()
    {
        var bands = new List<string>();
        if (!UsesWaveform) return bands;
        if (!ShapeUsesFrequency(Waveform.Shape)) return bands;
        double f = Waveform.FrequencyHz;
        if (AliasesAtSampleRate(f, 1.0))  bands.Add("Slow");
        if (AliasesAtSampleRate(f, 10.0)) bands.Add("Med");
        if (AliasesAtSampleRate(f, 25.0)) bands.Add("Fast");
        return bands;
    }

    // True when freq is within 2 % of any integer multiple (n ≥ 1) of
    // sampleRate. Uses relative-error around the nearest multiple so the
    // window scales with the multiple - at the 1 Hz band the flagged ranges
    // are [0.98, 1.02], [1.96, 2.04], [2.94, 3.06], … rather than a fixed
    // ±0.02 Hz across all multiples.
    private static bool AliasesAtSampleRate(double freq, double sampleRate)
    {
        if (freq <= 0) return false;
        double ratio = freq / sampleRate;
        double n = Math.Round(ratio);
        if (n < 1) return false;            // freq below the first multiple - not flagged
        double error = Math.Abs(ratio - n) / n;
        return error <= 0.02;
    }

    private static bool ShapeUsesFrequency(WaveformShape shape)
        => shape != WaveformShape.Constant
        && shape != WaveformShape.FileStream
        && shape != WaveformShape.CsvFile;
}

// One entry in a PID row's value-source picker. Source is the kind (None / Waveform / Signal); Signal is the engine
// signal id, non-null only when Source == Signal. Display is the friendly name shown in the dropdown.
public sealed record SignalOption(PidValueSource Source, SignalId? Signal, string Display)
{
    // True when this option represents the given model state - used to select the current entry and to detect no-ops.
    public bool Matches(PidValueSource source, SignalId? signal)
        => Source == source && (Source != PidValueSource.Signal || Signal == signal);
}
