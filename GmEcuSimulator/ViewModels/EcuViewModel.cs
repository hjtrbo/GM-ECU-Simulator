using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Collections.Specialized;
using System.Text.Json;
using Common.Protocol;
using Common.Waveforms;
using Core.Ecu;
using Core.Security;

namespace GmEcuSimulator.ViewModels;

// Editable view of one EcuNode plus its child PidViewModels. CAN ID
// edits push straight to the model; on the next IPC dispatch they take
// effect (the bus's FindByRequestId does the lookup fresh per frame).
public sealed class EcuViewModel : NotifyPropertyChangedBase
{
    public EcuNode Model { get; }
    public ObservableCollection<PidViewModel> Pids { get; } = new();
    public GlitchConfigViewModel Glitch { get; }
    private PidViewModel? selectedPid;

    public EcuViewModel(EcuNode model)
    {
        Model = model;
        Glitch = new GlitchConfigViewModel(model.Glitch);
        foreach (var pid in model.Pids) Pids.Add(new PidViewModel(pid, this));

        // Security module picker: synthetic "(none)" at index 0, then every
        // registered module ID. Matches the ComboBox's ItemsSource binding.
        AvailableSecurityModuleIds = new ObservableCollection<string> { NoneSecurityModuleLabel };
        foreach (var id in SecurityModuleRegistry.KnownIds) AvailableSecurityModuleIds.Add(id);
        selectedSecurityModuleId = model.SecurityModule?.Id ?? NoneSecurityModuleLabel;

        // Initial KV entries from any persisted config.
        SecurityModuleConfigEntries = new ObservableCollection<KeyValueEntry>();
        LoadEntriesFromJson(model.SecurityModuleConfig);
        SecurityModuleConfigEntries.CollectionChanged += OnSecurityEntriesChanged;
        foreach (var e in SecurityModuleConfigEntries) e.PropertyChanged += OnSecurityEntryPropertyChanged;
    }

    public string Name
    {
        get => Model.Name;
        set { if (Model.Name != value) { Model.Name = value; OnPropertyChanged(); } }
    }

    public ushort PhysicalRequestCanId
    {
        get => Model.PhysicalRequestCanId;
        set { if (Model.PhysicalRequestCanId != value) { Model.PhysicalRequestCanId = value; OnPropertyChanged(); OnPropertyChanged(nameof(PhysicalRequestCanIdHex)); } }
    }

    public string PhysicalRequestCanIdHex
    {
        get => $"0x{Model.PhysicalRequestCanId:X3}";
        set { if (TryParseHexU16(value, out var v)) PhysicalRequestCanId = v; }
    }

    public ushort UsdtResponseCanId
    {
        get => Model.UsdtResponseCanId;
        set { if (Model.UsdtResponseCanId != value) { Model.UsdtResponseCanId = value; OnPropertyChanged(); OnPropertyChanged(nameof(UsdtResponseCanIdHex)); } }
    }

    public string UsdtResponseCanIdHex
    {
        get => $"0x{Model.UsdtResponseCanId:X3}";
        set { if (TryParseHexU16(value, out var v)) UsdtResponseCanId = v; }
    }

    public ushort UudtResponseCanId
    {
        get => Model.UudtResponseCanId;
        set { if (Model.UudtResponseCanId != value) { Model.UudtResponseCanId = value; OnPropertyChanged(); OnPropertyChanged(nameof(UudtResponseCanIdHex)); } }
    }

    public string UudtResponseCanIdHex
    {
        get => $"0x{Model.UudtResponseCanId:X3}";
        set { if (TryParseHexU16(value, out var v)) UudtResponseCanId = v; }
    }

    public bool AllowPeriodicTesterPresent
    {
        get => Model.AllowPeriodicTesterPresent;
        set { if (Model.AllowPeriodicTesterPresent != value) { Model.AllowPeriodicTesterPresent = value; OnPropertyChanged(); } }
    }

    public PidViewModel? SelectedPid
    {
        get => selectedPid;
        set => SetField(ref selectedPid, value);
    }

    public void AddPid()
    {
        // Pick the next free address — start at 0x0001 and walk up. We stay
        // in the 16-bit space for the auto-pick so the new PID is reachable
        // by a wire-format $22 request without first going through $2D.
        uint addr = 0x0001;
        while (Model.GetPid(addr) != null) addr++;

        var pid = new Pid
        {
            Address = addr,
            Name = "New PID",
            Size = PidSize.Word,
            DataType = PidDataType.Unsigned,
            Scalar = 1.0,
            Offset = 0.0,
            Unit = "",
            WaveformConfig = new WaveformConfig { Shape = WaveformShape.Sin, Amplitude = 50, Offset = 50, FrequencyHz = 1.0 },
        };
        Model.AddPid(pid);
        var vm = new PidViewModel(pid, this);
        Pids.Add(vm);
        SelectedPid = vm;
    }

    public void RemoveSelectedPid()
    {
        if (selectedPid == null) return;
        Model.RemovePid(selectedPid.Model);
        Pids.Remove(selectedPid);
        SelectedPid = null;
    }

    public void RaisePidsChanged() => Model.RaisePidsChanged();

    private static bool TryParseHexU16(string s, out ushort v)
    {
        var trimmed = (s ?? "").Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[2..];
        return ushort.TryParse(trimmed, System.Globalization.NumberStyles.HexNumber,
                               System.Globalization.CultureInfo.InvariantCulture, out v);
    }

    // ---------------- Security Access ($27) ----------------

    private const string NoneSecurityModuleLabel = "(none)";

    /// <summary>All registered module IDs, prefixed with a synthetic "(none)" entry.</summary>
    public ObservableCollection<string> AvailableSecurityModuleIds { get; }

    /// <summary>Editable key→string map for the module's SecurityModuleConfig JsonElement.</summary>
    public ObservableCollection<KeyValueEntry> SecurityModuleConfigEntries { get; }

    private string selectedSecurityModuleId;
    public string SelectedSecurityModuleId
    {
        get => selectedSecurityModuleId;
        set
        {
            if (selectedSecurityModuleId == value) return;
            selectedSecurityModuleId = value;
            OnPropertyChanged();
            ApplyModuleSelection();
        }
    }

    private void ApplyModuleSelection()
    {
        if (selectedSecurityModuleId == NoneSecurityModuleLabel)
        {
            Model.SecurityModule = null;
        }
        else
        {
            Model.SecurityModule = SecurityModuleRegistry.Create(selectedSecurityModuleId);
            Model.SecurityModule?.LoadConfig(Model.SecurityModuleConfig);
        }
    }

    private void OnSecurityEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
            foreach (KeyValueEntry old in e.OldItems) old.PropertyChanged -= OnSecurityEntryPropertyChanged;
        if (e.NewItems != null)
            foreach (KeyValueEntry n in e.NewItems) n.PropertyChanged += OnSecurityEntryPropertyChanged;
        PushEntriesToModel();
    }

    private void OnSecurityEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
        => PushEntriesToModel();

    private void PushEntriesToModel()
    {
        Model.SecurityModuleConfig = BuildJsonFromEntries();
        Model.SecurityModule?.LoadConfig(Model.SecurityModuleConfig);
    }

    private void LoadEntriesFromJson(JsonElement? json)
    {
        SecurityModuleConfigEntries?.Clear();
        if (json is null || json.Value.ValueKind != JsonValueKind.Object) return;
        foreach (var prop in json.Value.EnumerateObject())
            SecurityModuleConfigEntries!.Add(new KeyValueEntry(prop.Name, ValueToDisplayString(prop.Value)));
    }

    private JsonElement? BuildJsonFromEntries()
    {
        // Every entry value persists as a JSON string — modules parse strings
        // themselves (hex bytes, integers, etc.). Round-tripping a number-typed
        // load lands as a stringified number; modules that care can parse it.
        var dict = new Dictionary<string, string>();
        foreach (var e in SecurityModuleConfigEntries)
        {
            if (string.IsNullOrWhiteSpace(e.Key)) continue;
            dict[e.Key] = e.Value ?? "";
        }
        if (dict.Count == 0) return null;
        return JsonSerializer.SerializeToElement(dict);
    }

    private static string ValueToDisplayString(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() ?? "",
        JsonValueKind.Number => e.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "",
        _ => e.GetRawText(),
    };
}

public sealed class KeyValueEntry : NotifyPropertyChangedBase
{
    private string key = "";
    private string value = "";

    // Parameterless ctor lets the DataGrid create new rows when CanUserAddRows=True.
    public KeyValueEntry() { }
    public KeyValueEntry(string key, string value) { this.key = key; this.value = value; }

    public string Key
    {
        get => key;
        set => SetField(ref key, value);
    }

    public string Value
    {
        get => value;
        set => SetField(ref this.value, value);
    }
}
