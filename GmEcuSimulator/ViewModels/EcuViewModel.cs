using System.Collections.ObjectModel;
using Common.Protocol;
using Common.Waveforms;
using Core.Ecu;

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

}
