using System.Collections.ObjectModel;
using Common.Glitch;

namespace GmEcuSimulator.ViewModels;

// UI wrapper around Common.Glitch.GlitchConfig. The model lives on EcuNode
// (which the persistence layer round-trips) and these wrappers provide the
// INotifyPropertyChanged plumbing the WPF DataGrid / CheckBox bindings need
// for two-way edit. When the user toggles a checkbox or types a probability,
// the new value lands on the Model object directly — no copy step.
//
// Glitch dispatch logic in Core/Services is NOT yet implemented; these
// settings are visible and persistable, but currently have no runtime effect.

public sealed class GlitchConfigViewModel : NotifyPropertyChangedBase
{
    public GlitchConfig Model { get; }
    public ObservableCollection<GlitchServiceSettingViewModel> Services { get; } = new();

    public GlitchConfigViewModel(GlitchConfig model)
    {
        Model = model;
        foreach (var svc in model.Services)
            Services.Add(new GlitchServiceSettingViewModel(svc));
    }

    public bool Enabled
    {
        get => Model.Enabled;
        set { if (Model.Enabled != value) { Model.Enabled = value; OnPropertyChanged(); } }
    }

    // NRC pool toggles. The Model.NrcPool list is the source of truth; these
    // properties add/remove individual NRC bytes and notify so the checkboxes
    // stay in sync if the underlying list is reset (e.g. by a config load).
    public bool IncludeNrc11 { get => HasNrc(0x11); set => SetNrc(0x11, value); }
    public bool IncludeNrc12 { get => HasNrc(0x12); set => SetNrc(0x12, value); }
    public bool IncludeNrc22 { get => HasNrc(0x22); set => SetNrc(0x22, value); }
    public bool IncludeNrc31 { get => HasNrc(0x31); set => SetNrc(0x31, value); }
    public bool IncludeNrc33 { get => HasNrc(0x33); set => SetNrc(0x33, value); }
    public bool IncludeNrc78 { get => HasNrc(0x78); set => SetNrc(0x78, value); }

    private bool HasNrc(byte nrc) => Model.NrcPool.Contains(nrc);

    private void SetNrc(byte nrc, bool include, [System.Runtime.CompilerServices.CallerMemberName] string? prop = null)
    {
        bool has = Model.NrcPool.Contains(nrc);
        if (has == include) return;
        if (include) Model.NrcPool.Add(nrc);
        else         Model.NrcPool.Remove(nrc);
        OnPropertyChanged(prop);
    }
}

public sealed class GlitchServiceSettingViewModel : NotifyPropertyChangedBase
{
    public GlitchServiceSetting Model { get; }

    public GlitchServiceSettingViewModel(GlitchServiceSetting model) { Model = model; }

    /// <summary>Display string like "$22 ReadDataByPid".</summary>
    public string ServiceLabel => $"${Model.ServiceId:X2} {GlitchConfig.ServiceName(Model.ServiceId)}";

    public double Probability
    {
        get => Model.Probability;
        set
        {
            // Clamp to [0,1] so a careless paste of "1.5" or "-0.1" doesn't break the math later.
            double v = value < 0 ? 0 : value > 1 ? 1 : value;
            if (Model.Probability == v) return;
            Model.Probability = v;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ProbabilityPercent));
        }
    }

    /// <summary>
    /// Human-friendly probability as a percentage string (e.g. "12.5%").
    /// Editing the textbox parses "12.5" or "12.5%" back into Probability.
    /// </summary>
    public string ProbabilityPercent
    {
        get => $"{Model.Probability * 100:F2}%";
        set
        {
            var s = (value ?? "").Trim().TrimEnd('%').Trim();
            if (double.TryParse(s, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out var pct))
            {
                Probability = pct / 100.0;
            }
        }
    }

    public GlitchAction Action
    {
        get => Model.Action;
        set { if (Model.Action != value) { Model.Action = value; OnPropertyChanged(); } }
    }
}
