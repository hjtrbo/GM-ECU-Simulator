namespace GmEcuSimulator.ViewModels;

// One entry in the per-ECU persona-selector ComboBox (Advanced tab of the ECU
// settings window). Pairs a persistence-side persona id (the same string
// PersonaRegistry.Resolve / ConfigSchema.PidDto.PersonaId use) with a short
// human label for the dropdown. Record gives value equality on (Id, Label) so
// the ComboBox highlights the current pick; the getter on
// EcuViewModel.SelectedPersonaOption returns the matching instance from the
// AvailablePersonas list so reference equality holds too.
//
// Only the two shipping dispatch tables are offered here:
//   "gmw3110"      -> Gmw3110Persona   ("GM Gen 4")
//   "ford-uds" -> FordUdsPersona ("Ford")
// uds-kernel is intentionally absent - it is a runtime-only persona swapped in
// by Service36Handler when a kernel boot-loads, not something a user picks.
public sealed record PersonaOption(string Id, string Label);
