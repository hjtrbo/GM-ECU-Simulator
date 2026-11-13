using Core.Dps;
using Core.Ecu;

namespace GmEcuSimulator.ViewModels.PrimeWizard;

// Mutable shared state for the prime wizard. Owned by PrimeWizardViewModel;
// every page VM reads/writes it through that owner.
//
// The donor concept is gone. Phase 3 baseline comes from the archive's
// $53 bytecode literals; users fill remaining Empty rows via the Load-
// from-bin and Auto-populate buttons on the Phase 3 page (or by manual
// cell edits, which become User-source rows).
//
// EditedManifest mirrors PrimedDataset.EditedPhase3 - it's the manifest
// the commit step hands to BuildEcuNode. UserEdits is a separate dict
// of byte-array overrides keyed by (instruction index, did/pid) so
// user-typed cells survive Back navigation across page 3.
//
// LoadedBinPath is the bin file the user last loaded via "Load from
// bin..." on page 2; preserved so a Back+Next round trip remembers it.
//
// ExistingNode is non-null when the wizard is opened in re-edit mode
// from an already-primed ECU. The commit step removes ExistingNode from
// the bus before registering the rebuilt one.
public sealed class PrimeWizardContext
{
    // Page 1
    public string? ArchivePath { get; set; }
    public ArchiveDescriptor? Archive { get; set; }

    // Page 2 (Phase 3 review)
    public PrimedDataset? Dataset { get; set; }
    public Phase3Manifest? EditedManifest { get; set; }
    public Dictionary<(int InstructionIndex, ushort DidOrPid), byte[]> UserEdits { get; }
        = new();
    public string? LoadedBinPath { get; set; }

    // Page 3 (commit) - security module override. Null means "use the
    // dataset's default" (gm-archive-unresolved from ArchivePrimer). The
    // fixed-seed hex is only meaningful when OverrideSecurityModuleId
    // resolves to a bypass module; for strict modules it's ignored at apply
    // time (see PrimeWizardViewModel.ApplySecurityOverrides).
    public string? OverrideSecurityModuleId { get; set; }
    public string? OverrideFixedSeedHex { get; set; }

    // Re-edit mode (post-commit re-open)
    public EcuNode? ExistingNode { get; set; }

    public bool IsReEdit => ExistingNode is not null;
}
