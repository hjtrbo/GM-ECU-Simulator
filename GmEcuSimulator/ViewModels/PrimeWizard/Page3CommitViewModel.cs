using Core.Dps;
using Core.Security;
using System.IO;

namespace GmEcuSimulator.ViewModels.PrimeWizard;

// Page 3 of the prime wizard: read-only commit summary. Refreshed on
// entry from the current wizard context so any Back-page changes are
// reflected. Apply (the wizard's primary footer button on this page)
// runs from PrimeWizardViewModel.Apply.
public sealed class Page3CommitViewModel : NotifyPropertyChangedBase
{
    // Default fixed seed used when the user picks a 5-byte bypass module
    // without supplying their own seed. Trailing 0x06 is the E92 family byte
    // DPS's algo-92 cipher keys off; the first four bytes are just
    // memorable filler.
    private const string DefaultBypass5ByteSeed = "11 22 33 44 06";

    private readonly PrimeWizardContext context;
    private string archiveLine = "(none)";
    private string loadedBinLine = "(none)";
    private string selectedSecurityModule = UtilitySecuritySelection.UnresolvedModuleId;
    private string fixedSeedHex = "";
    private bool suppressContextWrites;     // OnEnter rehydration must not double-fire writes
    private int totalRows;
    private int bytecodeRows;
    private int binRows;
    private int defaultRows;
    private int userRows;
    private int emptyRows;
    private int emptyComparedRows;
    private IReadOnlyList<string> flags = Array.Empty<string>();

    public Page3CommitViewModel(PrimeWizardContext context)
    {
        this.context = context;
        AvailableSecurityModules = SecurityModuleRegistry.KnownIds.OrderBy(s => s).ToList();
    }

    public string ArchiveLine    { get => archiveLine;    private set => SetField(ref archiveLine, value); }
    public string LoadedBinLine  { get => loadedBinLine;  private set => SetField(ref loadedBinLine, value); }

    /// <summary>Dropdown contents - every module the registry knows about.</summary>
    public IReadOnlyList<string> AvailableSecurityModules { get; }

    /// <summary>
    /// Two-way bound to the security-module ComboBox. Setting it both updates
    /// the field and pushes the override into the wizard context so the
    /// Apply step picks it up. Side-effect: clears FixedSeedHex when switching
    /// to a non-bypass module (other modules don't use it; keeping stale text
    /// around would be confusing); preloads a default when switching to a
    /// 5-byte bypass module so the user can hit Apply without hunting.
    /// </summary>
    public string SelectedSecurityModule
    {
        get => selectedSecurityModule;
        set
        {
            if (!SetField(ref selectedSecurityModule, value)) return;
            if (!suppressContextWrites) context.OverrideSecurityModuleId = value;
            OnPropertyChanged(nameof(IsFixedSeedVisible));
            if (IsBypassModule(value))
            {
                if (string.IsNullOrWhiteSpace(fixedSeedHex) && Is5ByteBypassModule(value))
                    FixedSeedHex = DefaultBypass5ByteSeed;
            }
            else if (!string.IsNullOrEmpty(fixedSeedHex))
            {
                // Reset stale text so users don't see a value that won't apply.
                FixedSeedHex = "";
            }
        }
    }

    /// <summary>
    /// Two-way bound to the fixed-seed TextBox. Free-form input - the module's
    /// LoadConfig is the source of truth for validation, so an invalid value
    /// here simply means the module falls back to random-seed mode (its
    /// existing behaviour when fixedSeed is absent).
    /// </summary>
    public string FixedSeedHex
    {
        get => fixedSeedHex;
        set
        {
            if (!SetField(ref fixedSeedHex, value ?? "")) return;
            // Keep the displayed text readable ("11 22 33 44 06") but hand the
            // module a contiguous hex string. RandomSeedCipher's LoadConfig
            // tolerates whitespace too, but stripping here keeps the persisted
            // context value canonical.
            if (!suppressContextWrites)
                context.OverrideFixedSeedHex = StripWhitespace(fixedSeedHex);
        }
    }

    private static string StripWhitespace(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var buf = new char[s.Length];
        int n = 0;
        foreach (var c in s) if (!char.IsWhiteSpace(c)) buf[n++] = c;
        return new string(buf, 0, n);
    }

    public bool IsFixedSeedVisible => IsBypassModule(SelectedSecurityModule);

    // Probes the registry to ask the module itself whether it's a bypass
    // module - keeps the gate honest if new bypass IDs get added later.
    private static bool IsBypassModule(string? moduleId)
    {
        if (string.IsNullOrEmpty(moduleId)) return false;
        var module = SecurityModuleRegistry.Create(moduleId);
        return module?.Behaviour == SecurityModuleBehaviour.BypassAll;
    }

    // Convention: bypass module IDs end in "-{width}byte". Used only to decide
    // whether to preload the 5-byte default seed; everything else asks the
    // module via IsBypassModule.
    private static bool Is5ByteBypassModule(string? moduleId)
        => IsBypassModule(moduleId)
           && moduleId!.EndsWith("-5byte", StringComparison.OrdinalIgnoreCase);

    public int TotalRows         { get => totalRows;         private set => SetField(ref totalRows, value); }
    public int BytecodeRows      { get => bytecodeRows;      private set => SetField(ref bytecodeRows, value); }
    public int BinRows           { get => binRows;           private set => SetField(ref binRows, value); }
    public int DefaultRows       { get => defaultRows;       private set => SetField(ref defaultRows, value); }
    public int UserRows          { get => userRows;          private set => SetField(ref userRows, value); }
    public int EmptyRows         { get => emptyRows;         private set { SetField(ref emptyRows, value); OnPropertyChanged(nameof(HasEmptyCompared)); } }
    public int EmptyComparedRows { get => emptyComparedRows; private set { SetField(ref emptyComparedRows, value); OnPropertyChanged(nameof(HasEmptyCompared)); } }
    public bool HasEmptyCompared => emptyComparedRows > 0;
    public IReadOnlyList<string> Flags { get => flags; private set => SetField(ref flags, value); }

    public void OnEnter()
    {
        ArchiveLine = context.Archive is null
            ? "(none)"
            : $"{Path.GetFileName(context.Archive.ArchivePath)}  ({context.Archive.CalibrationBlockCount} cal block(s), OS PN {context.Archive.OsPartNumber ?? "(none)"})";

        LoadedBinLine = string.IsNullOrEmpty(context.LoadedBinPath)
            ? "(none - bytecode + defaults only)"
            : Path.GetFileName(context.LoadedBinPath);

        // Rehydrate the module dropdown + fixed-seed text. The override on
        // the context wins (so Back+Next preserves the user's choice); fall
        // back to whatever the dataset's primer picked. Suppress the setters'
        // write-back into the context during rehydration so an OnEnter
        // doesn't smash the override with the dataset default.
        suppressContextWrites = true;
        try
        {
            SelectedSecurityModule = context.OverrideSecurityModuleId
                                     ?? context.Dataset?.Report.SecurityModuleId
                                     ?? UtilitySecuritySelection.UnresolvedModuleId;
            FixedSeedHex = context.OverrideFixedSeedHex ?? "";
        }
        finally
        {
            suppressContextWrites = false;
        }

        // First entry with a 5-byte bypass module and no user override:
        // preload the default so Apply has something to send. Goes through the
        // setter (not the field) so the value propagates into the context.
        if (Is5ByteBypassModule(SelectedSecurityModule)
            && string.IsNullOrWhiteSpace(fixedSeedHex)
            && context.OverrideFixedSeedHex is null)
        {
            FixedSeedHex = DefaultBypass5ByteSeed;
        }

        var manifest = context.EditedManifest ?? context.Dataset?.Phase3;
        if (manifest is null)
        {
            TotalRows = BytecodeRows = BinRows = DefaultRows = UserRows = EmptyRows = EmptyComparedRows = 0;
            Flags = Array.Empty<string>();
            return;
        }

        TotalRows = manifest.Rows.Count;
        BytecodeRows = manifest.Rows.Count(r => r.Source == Phase3RowSource.Bytecode);
        BinRows = manifest.Rows.Count(r => r.Source == Phase3RowSource.Bin);
        DefaultRows = manifest.Rows.Count(r => r.Source == Phase3RowSource.Default);
        UserRows = manifest.Rows.Count(r => r.Source == Phase3RowSource.User);
        EmptyRows = manifest.Rows.Count(r => r.Source == Phase3RowSource.Empty);
        EmptyComparedRows = manifest.Rows.Count(r => r.Source == Phase3RowSource.Empty && r.HasCompareDownstream);

        Flags = context.Dataset?.Report.Flags ?? Array.Empty<string>();
    }
}
