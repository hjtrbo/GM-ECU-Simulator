using Common.Dbc;
using Common.Protocol;
using Common.Signals;
using Core.Ecu;
using System.Collections.ObjectModel;
using System.Globalization;

namespace GmEcuSimulator.ViewModels;

// Editable view of one BroadcastMessage and its child signals. Mirrors PidViewModel's shape: setters
// push straight into the model and notify the parent so the BroadcastScheduler rebuilds its timers if
// a host session is live.
public sealed class BroadcastMessageViewModel : NotifyPropertyChangedBase
{
    public BroadcastMessage Model { get; }
    private readonly EcuViewModel parent;

    // The message's signal set is owned by the DBC - rows are not added or deleted in the editor
    // (only their value mapping / scaling is editable). Import a different DBC to change the set.
    public ObservableCollection<BroadcastSignalViewModel> Signals { get; } = new();

    public BroadcastMessageViewModel(BroadcastMessage model, EcuViewModel parent)
    {
        Model = model;
        this.parent = parent;
        foreach (var s in model.Signals) Signals.Add(new BroadcastSignalViewModel(s, this));
        ToggleExpandedCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    // Per-row chevron: opens / re-collapses this message's signals grid (row details).
    public RelayCommand ToggleExpandedCommand { get; }

    // CAN ID as hex. Accepts "0x"/"$"/"h" forms on input; shows 0xNNN (11-bit) or 0xNNNNNNNN (extended).
    public string CanIdHex
    {
        get => Model.Extended || Model.CanId > 0x7FF ? $"0x{Model.CanId:X8}" : $"0x{Model.CanId:X3}";
        set
        {
            var t = (value ?? "").Trim();
            if (t.StartsWith("$")) t = t[1..];
            else if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
            else if (t.EndsWith('h') || t.EndsWith('H')) t = t[..^1];
            if (uint.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) && v != Model.CanId)
            {
                Model.CanId = v;
                OnPropertyChanged();
                parent.OnBroadcastEdited();
            }
        }
    }

    public string Name
    {
        get => Model.Name;
        set { if (Model.Name != value) { Model.Name = value; OnPropertyChanged(); parent.OnBroadcastEdited(); } }
    }

    public int Dlc
    {
        get => Model.Dlc;
        set
        {
            int v = value < 0 ? 0 : value > 64 ? 64 : value;
            if (Model.Dlc != v) { Model.Dlc = v; OnPropertyChanged(); parent.OnBroadcastEdited(); }
        }
    }

    public int PeriodMs
    {
        get => Model.PeriodMs;
        set { if (Model.PeriodMs != value) { Model.PeriodMs = value; OnPropertyChanged(); parent.OnBroadcastEdited(); } }
    }

    public bool Enabled
    {
        get => Model.Enabled;
        set { if (Model.Enabled != value) { Model.Enabled = value; OnPropertyChanged(); parent.OnBroadcastEdited(); } }
    }

    public int SignalCount => Model.Signals.Count;

    // Whether this message's signals grid (row details) is open. Toggled by the per-row chevron so a
    // row can be collapsed again after viewing - independent of grid selection. UI-only, not persisted.
    private bool isExpanded;
    public bool IsExpanded
    {
        get => isExpanded;
        set { if (SetField(ref isExpanded, value)) OnPropertyChanged(nameof(ChevronGlyph)); }
    }

    // Segoe MDL2 chevron: down when open, right when collapsed.
    public string ChevronGlyph => isExpanded ? "" : "";

    // Called by child signal VMs when a value-mapping / scaling field changes, so the scheduler rebuilds.
    internal void OnSignalEdited() => parent.OnBroadcastEdited();

    public void RefreshLive(EngineModel engine, double timeMs)
    {
        foreach (var s in Signals) s.RefreshLive(engine, timeMs);
    }
}

// Editable view of one bit-packed broadcast signal. The DBC layout fields are editable (so a manual
// row can be hand-built); the headline control is the source dropdown that maps the field to a live
// engine signal / constant.
public sealed class BroadcastSignalViewModel : NotifyPropertyChangedBase
{
    public BroadcastSignal Model { get; }
    private readonly BroadcastMessageViewModel parent;
    private string liveValue = "-";

    public BroadcastSignalViewModel(BroadcastSignal model, BroadcastMessageViewModel parent)
    {
        Model = model;
        this.parent = parent;
    }

    public string Name
    {
        get => Model.Name;
        set { if (Model.Name != value) { Model.Name = value; OnPropertyChanged(); parent.OnSignalEdited(); } }
    }

    public int StartBit
    {
        get => Model.StartBit;
        set { if (Model.StartBit != value) { Model.StartBit = value; OnPropertyChanged(); parent.OnSignalEdited(); } }
    }

    public int Length
    {
        get => Model.Length;
        set { if (Model.Length != value) { Model.Length = value; OnPropertyChanged(); parent.OnSignalEdited(); } }
    }

    // Byte order as a short label ("M" Motorola / "I" Intel). Two-way via the boolean IsIntel toggle
    // for manual rows; DBC-imported rows already carry the correct order.
    public bool IsIntel
    {
        get => Model.ByteOrder == DbcByteOrder.Intel;
        set
        {
            var order = value ? DbcByteOrder.Intel : DbcByteOrder.Motorola;
            if (Model.ByteOrder != order) { Model.ByteOrder = order; OnPropertyChanged(); OnPropertyChanged(nameof(ByteOrderLabel)); parent.OnSignalEdited(); }
        }
    }

    public string ByteOrderLabel => Model.ByteOrder == DbcByteOrder.Intel ? "Intel" : "Motorola";

    public double Scale
    {
        get => Model.Scale;
        set { if (Model.Scale != value) { Model.Scale = value; OnPropertyChanged(); parent.OnSignalEdited(); } }
    }

    public double Offset
    {
        get => Model.Offset;
        set { if (Model.Offset != value) { Model.Offset = value; OnPropertyChanged(); parent.OnSignalEdited(); } }
    }

    public string Unit
    {
        get => Model.Unit;
        set { if (Model.Unit != value) { Model.Unit = value; OnPropertyChanged(); parent.OnSignalEdited(); } }
    }

    public double Constant
    {
        get => Model.Constant;
        set { if (Model.Constant != value) { Model.Constant = value; OnPropertyChanged(); parent.OnSignalEdited(); } }
    }

    // True when the constant box should be enabled (source = Constant).
    public bool IsConstant => Model.ValueSource == BroadcastValueSource.Constant;

    // The signal's value source, bound to the picker. "(none)" -> 0, "Constant" -> the Constant box,
    // any engine signal -> the live model.
    public BroadcastSignalOption SelectedSource
    {
        get => SourceOptionsList.FirstOrDefault(o => o.Matches(Model.ValueSource, Model.Signal)) ?? SourceOptionsList[0];
        set
        {
            if (value is null || value.Matches(Model.ValueSource, Model.Signal)) return;
            Model.ValueSource = value.Source;
            Model.Signal = value.Source == BroadcastValueSource.Signal ? value.Signal : null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsConstant));
            parent.OnSignalEdited();
        }
    }

    public IReadOnlyList<BroadcastSignalOption> SourceOptions => SourceOptionsList;

    public string LiveValue
    {
        get => liveValue;
        private set => SetField(ref liveValue, value);
    }

    public void RefreshLive(EngineModel engine, double timeMs)
        => LiveValue = Model.SampleValue(engine, timeMs).ToString("F2");

    // "(none)" and "Constant" up front, then every catalogue signal by friendly name. Shared across rows.
    private static readonly IReadOnlyList<BroadcastSignalOption> SourceOptionsList = BuildOptions();

    private static IReadOnlyList<BroadcastSignalOption> BuildOptions()
    {
        var list = new List<BroadcastSignalOption>
        {
            new(BroadcastValueSource.None,     null, "(none)"),
            new(BroadcastValueSource.Constant, null, "Constant"),
        };
        foreach (var d in SignalCatalogue.All) list.Add(new BroadcastSignalOption(BroadcastValueSource.Signal, d.Id, d.Name));
        return list;
    }
}

// One entry in a broadcast signal's source picker. Source is the kind; Signal is the engine signal id
// (non-null only when Source == Signal). Display is the friendly name shown in the dropdown.
public sealed record BroadcastSignalOption(BroadcastValueSource Source, SignalId? Signal, string Display)
{
    public bool Matches(BroadcastValueSource source, SignalId? signal)
        => Source == source && (Source != BroadcastValueSource.Signal || Signal == signal);
}
