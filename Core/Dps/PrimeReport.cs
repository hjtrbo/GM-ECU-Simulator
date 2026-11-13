using System.Text.Json;

namespace Core.Dps;

// Flash-layout entry for one calibration file in the archive. StartAddress is
// null when the $B0 instruction uses the global-memory-address mode ($F1-driven)
// and the value is not statically resolvable at prime time.
public sealed record CalBlockEntry(
    string FileName,
    long FileSizeBytes,
    uint? StartAddress);

// Human-readable summary of what the primer pulled out of the archive,
// what it elected to satisfy, and what it flagged for review. Surfaces in
// the status bar (one-line) and the Prime Report dialog (full view).
public sealed record PrimeReport(
    string ArchivePath,
    string? DonorBinPath,                 // Boot-block source spliced for the walker; null = archive-only prime
    string UtilityFileName,
    int CalFileCount,
    string? Vin,
    VinSource VinSource,
    string? Family,                       // "E38" / "T43" / "E67" / "Unknown"
    string? OsPartNumber,                 // 8 ASCII digits from the archive OS module header
    string? OsAlphaCode,                  // 2-char alpha code from the same header
    string SecurityModuleId,
    // Optional per-module config blob - shape is module-specific (each
    // ISecurityAccessModule deserialises its own keys from it). The
    // ArchivePrimer leaves this null by default; the Prime Wizard's
    // commit page overrides it when the user picks a module that needs
    // configuration (e.g. fixedSeed for gm-bypass-5byte).
    JsonElement? SecurityModuleConfig,
    int IdentifierDidCount,
    int PidsKnownFromBin,                 // 535-ish for E38 = E38PidExtractor records found
    int PidsSatisfiedFromBin,             // tier 1: we have value bytes for these
    int PidsSatisfiedFromArchive,         // tier 2
    int PidsReturningNrc,                 // tier 3
    IReadOnlyList<CalBlockEntry> CalBlocks, // flash layout: one entry per archive cal file
    IReadOnlyList<string> Flags)          // flag-for-review strings, see ArchivePrimer
{
    public UtilitySecuritySelection? SecuritySelection { get; init; }

    public string OneLineSummary()
    {
        var name = System.IO.Path.GetFileName(ArchivePath);
        return $"Primed: {name} ({IdentifierDidCount} DIDs, {PidsSatisfiedFromBin}+{PidsSatisfiedFromArchive} PIDs satisfied, {PidsReturningNrc} NRC-only)";
    }
}

public enum VinSource
{
    None,
    BinDescriptor,
    ArchiveFilename,
}
