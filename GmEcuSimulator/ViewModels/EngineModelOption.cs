namespace GmEcuSimulator.ViewModels;

// One entry in the engine-model ComboBox: the registry id the config stores paired with the human label shown in the
// dropdown. Record gives value equality on (Id, DisplayName) so the combo highlights the current pick; the bound
// SelectedValue is the Id (see EcuViewModel.SelectedEngineModelId).
public sealed record EngineModelOption(string Id, string DisplayName);
