using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Collections.Specialized;
using System.IO;
using System.Text.Json;
using System.Windows;
using Common.Protocol;
using Common.Waveforms;
using Core.Ecu;
using Core.Identification;
using Core.Scheduler;
using Core.Security;
using Core.Services;
using Microsoft.Win32;

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
        // Re-evaluate Mode2D alias collisions whenever rows are added,
        // removed, or replaced. Per-row mode/address edits flow through
        // PidViewModel -> RaisePidsChanged which also calls this; the two
        // entry points keep every collision warning in sync.
        Pids.CollectionChanged += (_, __) => RefreshAliasCollisions();
        RefreshAliasCollisions();

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

        LoadInfoFromBinCommand = new RelayCommand(LoadInfoFromBin);
        AutoPopulateDidsCommand = new RelayCommand(AutoPopulateMissingDids);
        EditPrimeCommand = new RelayCommand(EditPrime, () => primeContext != null && bus != null);
    }

    // -------- Prime wizard re-entry --------

    private PrimeWizard.PrimeWizardContext? primeContext;
    private Core.Bus.VirtualBus? bus;

    /// <summary>
    /// True when this ECU was produced by a successful Prime wizard run and
    /// the wizard context is available for re-entry. Drives the Edit prime
    /// button's visibility on the main window's per-ECU template.
    /// </summary>
    public bool IsPrimed => primeContext != null;

    /// <summary>
    /// Called by MainViewModel.Rebuild for every ECU. The bus reference is
    /// needed when the user re-opens the prime wizard via EditPrimeCommand.
    /// </summary>
    public void BindBus(Core.Bus.VirtualBus bus) => this.bus = bus;

    /// <summary>
    /// Called by MainViewModel after the wizard registers a new primed ECU.
    /// Stores the wizard's final context so the user can re-open the wizard
    /// later via <see cref="EditPrimeCommand"/>; the context lives for the
    /// session and is discarded when the ECU is removed.
    /// </summary>
    public void AttachPrimeContext(PrimeWizard.PrimeWizardContext context)
    {
        primeContext = context;
        OnPropertyChanged(nameof(IsPrimed));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    public RelayCommand EditPrimeCommand { get; }

    private void EditPrime()
    {
        if (primeContext is null || bus is null) return;
        var wizard = new Views.PrimeWizard.PrimeWizardWindow(bus, Model, primeContext)
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };
        wizard.ShowDialog();
        if (wizard.CommittedNode is not null && wizard.CommittedDataset is not null)
        {
            // Wizard already swapped the bus node; refresh the stashed
            // context with the (possibly mutated) one for the next re-open.
            primeContext = wizard.Context;
        }
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

    // ---------------- $1A ECU identity DIDs ----------------
    //
    // No grid surface in the inspector - the Bin menu's Load Info From Bin /
    // Auto-populate DIDs items are the user-facing way to populate DIDs.
    // Both writers push straight to EcuNode.SetIdentifier, the same storage
    // the $1A handler reads from at runtime. DIDs live in memory only
    // (v12 dropped IdentifierDto from the JSON schema); re-seed every
    // session via the Bin menu or File -> Prime from DPS archive.

    /// <summary>
    /// "Load Info From Bin" command. Pops a file picker, parses the selected
    /// .bin via <see cref="Mode1ADidBinExtractor"/>, and pushes the extracted
    /// identity fields into <see cref="EcuNode.Identifiers"/>. ORPHAN: the Bin
    /// menu that used to bind this was retired - kept alive (along with
    /// <see cref="AutoPopulateDidsCommand"/>) because we'll wire it back up
    /// when the donor-bin flow returns. Don't remove without checking.
    /// </summary>
    public RelayCommand LoadInfoFromBinCommand { get; }

    private void LoadInfoFromBin()
    {
        var settings = AppSettings.Load();
        var picker = new OpenFileDialog
        {
            Title = "Pick a GM ECU flash image",
            Filter = "ECU bin (*.bin)|*.bin|All files|*.*",
            InitialDirectory = AppSettings.ResolveInitialDir(settings.LastBinDir),
        };
        if (picker.ShowDialog() != true) return;

        // Persist the dir before we do any parsing - the user picked a real
        // file in that folder, so even if the bin turns out to be unreadable
        // / unrecognised we still want the next session to land there.
        var chosenDir = Path.GetDirectoryName(picker.FileName);
        if (!string.IsNullOrEmpty(chosenDir))
        {
            settings.LastBinDir = chosenDir;
            settings.Save();
        }

        // Ask the user which load mode to use BEFORE touching the file - if
        // they cancel, no work is done. Yes = replace-all (destructive but
        // explicit), No = merge (keeps user-edited / auto-populated values),
        // Cancel = bail.
        var modeChoice = MessageBox.Show(
            "Replace all existing $1A DIDs on this ECU with what the bin contains?\n\n" +
            "[Yes] Replace all - clear every DID on this ECU first, then write only " +
            "what the bin surfaces. DIDs the bin can't extract end up unconfigured.\n\n" +
            "[No] Add only if blank - keep existing DIDs; only fill ones currently empty. " +
            "User edits and prior auto-populate values are preserved.\n\n" +
            "[Cancel] Don't load.",
            "Load Info From Bin",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        if (modeChoice == MessageBoxResult.Cancel) return;
        var mode = modeChoice == MessageBoxResult.Yes
            ? BinIdentificationApplier.LoadMode.ReplaceAll
            : BinIdentificationApplier.LoadMode.Merge;

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(picker.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not read file:\n{ex.Message}", "Load Info From Bin",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var result = Mode1ADidBinExtractor.Parse(bytes);
        if (result == null)
        {
            MessageBox.Show(
                "Could not identify this file as a GM ECU flash image. No service " +
                "dispatcher was located - file may be truncated, encrypted, or a " +
                "different ECU family than T43/E38/E67.",
                "Load Info From Bin", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Delegate to the testable Core helper. It clears + writes the model
        // per the chosen mode and returns the lists for the summary dialog.
        var outcome = BinIdentificationApplier.Apply(Model, result, mode);
        var applied = outcome.Applied.ToList();
        var skipped = outcome.Skipped.ToList();
        if (!string.IsNullOrEmpty(result.CalibrationPartNumber))
            applied.Add($"Cal P/N ({result.CalibrationPartNumber} - not stored, no fixed DID)");

        // Compose a summary message - shows which fields were populated and
        // surfaces any parser warnings (e.g. "no trampoline pattern detected").
        string modeLabel = mode == BinIdentificationApplier.LoadMode.ReplaceAll
            ? "Replace all (existing DIDs cleared first)"
            : "Add only if blank (existing DIDs preserved)";
        var lines = new List<string>
        {
            $"Mode: {modeLabel}",
            $"Family: {result.Family}",
            $"Service dispatcher: 0x{result.ServiceDispatcherOffset:X6}",
            $"$1A handler: 0x{result.Service1AHandlerOffset:X6}",
            $"DID dispatcher: 0x{result.DidDispatcherOffset:X6}",
            $"Supported SIDs: {string.Join(", ", result.SupportedSids.Select(s => $"${s:X2}"))}",
            $"Supported DIDs: {string.Join(", ", result.Dids.Select(s => $"${s.Did:X2}"))}",
            "",
            applied.Count > 0
                ? $"Populated: {string.Join(", ", applied)}"
                : "No fields populated (no extractable values found).",
        };
        if (skipped.Count > 0)
        {
            lines.Add("");
            lines.Add($"Kept existing (precedence: user/auto-populate wins): {string.Join(", ", skipped)}");
        }
        if (result.Warnings.Count > 0)
        {
            lines.Add("");
            lines.Add("Warnings:");
            foreach (var w in result.Warnings) lines.Add("  - " + w);
        }

        MessageBox.Show(string.Join(Environment.NewLine, lines),
            "Load Info From Bin", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>
    /// True iff the model has a non-empty byte array stored for this DID.
    /// Used by the precedence guard in <see cref="AutoPopulateMissingDids"/>
    /// so the auto-populate pass doesn't clobber a higher-precedence value
    /// (user hand-edit and bin-load both take precedence over defaults).
    /// </summary>
    private bool HasIdentifier(byte did)
    {
        var bytes = Model.GetIdentifier(did);
        return bytes != null && bytes.Length > 0;
    }

    /// <summary>
    /// "Auto-populate missing $1A DIDs" command. Prompts the user for one of
    /// three modes, then fills well-known DIDs (<see cref="Gmw3110DidNames.KnownDids"/>)
    /// with placeholder values from <see cref="DefaultDidValues"/>. DIDs that
    /// already have a non-empty value are NEVER overwritten - they stay even
    /// in the aggressive mode. The two modes differ in how they treat
    /// sticky-user blanks (source=User with no bytes - the user explicitly
    /// cleared a row).
    /// </summary>
    public RelayCommand AutoPopulateDidsCommand { get; }

    private void AutoPopulateMissingDids()
    {
        // Three-way prompt: Yes = aggressive (fill all blanks including ones
        // the user deliberately cleared), No = conservative (default - keep
        // sticky-user blanks), Cancel = bail with no model change.
        var choice = MessageBox.Show(
            "How should Auto-populate handle DIDs the user previously cleared?\n\n" +
            "[Yes] Overwrite user-blank fields - fill every well-known DID that is " +
            "currently empty, including ones the user explicitly cleared (the sticky-User " +
            "rule is ignored for this run; rows already containing a value still aren't " +
            "touched).\n\n" +
            "[No] Populate blanks - only fill DIDs that are blank AND not tagged " +
            "source=user. Rows the user deliberately cleared stay empty.\n\n" +
            "[Cancel] Don't change anything.",
            "Auto-populate DIDs",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        if (choice == MessageBoxResult.Cancel) return;
        bool overwriteUserBlanks = choice == MessageBoxResult.Yes;

        int populated = 0;
        int respectedUserBlanks = 0;
        foreach (var did in Common.Protocol.Gmw3110DidNames.KnownDids)
        {
            // Existing values are never overwritten by Auto-populate - the
            // user owns rows with content regardless of mode.
            if (HasIdentifier(did)) continue;

            // Sticky-User blanks: skip in conservative mode; fill in aggressive.
            // Auto / Bin / Blank sources always get the default.
            if (!overwriteUserBlanks &&
                Model.GetIdentifierSource(did) == Common.Protocol.DidSource.User)
            {
                respectedUserBlanks++;
                continue;
            }

            var bytes = Common.Protocol.DefaultDidValues.Get(did);
            if (bytes == null || bytes.Length == 0) continue;
            Model.SetIdentifier(did, bytes, Common.Protocol.DidSource.Auto);
            populated++;
        }

        if (populated == 0)
        {
            var msg = respectedUserBlanks > 0
                ? $"Nothing to do - every well-known DID already has a value or " +
                  $"is a user-blanked row. {respectedUserBlanks} row(s) tagged " +
                  $"source=user were preserved; re-run and pick 'Overwrite user-blank " +
                  $"fields' to fill those too."
                : "Nothing to do - every well-known DID already has a value. " +
                  "Edit the JSON config to clear an entry and re-run to re-fill " +
                  "it with the placeholder default.";
            MessageBox.Show(msg, "Auto-populate DIDs",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    /// <summary>FC.BS byte sent on First Frame reception. Hex display, e.g. "0x01".</summary>
    public string FlowControlBlockSizeHex
    {
        get => $"0x{Model.FlowControlBlockSize:X2}";
        set
        {
            if (TryParseHexByte(value, out var v) && Model.FlowControlBlockSize != v)
            {
                Model.FlowControlBlockSize = v;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// 8-bit diagnostic address returned by $1A $B0. Hex display, e.g. "0x11".
    /// Typically the low byte of PhysicalRequestCanId.
    /// </summary>
    public string DiagnosticAddressHex
    {
        get => $"0x{Model.DiagnosticAddress:X2}";
        set
        {
            if (TryParseHexByte(value, out var v) && Model.DiagnosticAddress != v)
            {
                Model.DiagnosticAddress = v;
                OnPropertyChanged();
            }
        }
    }

    private static bool TryParseHexByte(string s, out byte v)
    {
        var trimmed = (s ?? "").Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[2..];
        return byte.TryParse(trimmed, System.Globalization.NumberStyles.HexNumber,
                             System.Globalization.CultureInfo.InvariantCulture, out v);
    }

    public PidViewModel? SelectedPid
    {
        get => selectedPid;
        set => SetField(ref selectedPid, value);
    }

    public void AddPid()
    {
        // Default the new PID to Word (2 bytes). Pick the next address that
        // doesn't overlap an existing PID's [Address, Address+ResponseLength)
        // range - simulator addresses are byte-granular (see Service2D's
        // 32-bit memory addressing), so a Word at 0x0001 occupies bytes
        // 0x0001..0x0002 and the next Word has to start at 0x0003. Walks
        // from 0x0001 and skips forward over each occupied span until a gap
        // of at least newSize bytes is found.
        const PidSize NewSize = PidSize.Word;
        int newSize = (int)NewSize;
        var occupied = Pids
            .Select(p => (Start: p.Model.Address, End: p.Model.Address + (uint)p.Model.ResponseLength))
            .OrderBy(s => s.Start)
            .ToList();
        uint addr = 0x0001;
        foreach (var (start, end) in occupied)
        {
            if (addr + newSize <= start) break;     // gap fits before this PID
            if (end > addr) addr = end;             // skip past the occupied span
        }

        // Pick a unique name: walk "New PID N" upwards until none collides
        // with an existing PID name. Bare "New PID" stays the first label so
        // single-PID configs don't get a number suffix gratuitously.
        var existingNames = Pids.Select(p => p.Model.Name).ToHashSet(StringComparer.Ordinal);
        string name = "New PID";
        if (existingNames.Contains(name))
        {
            int n = 2;
            while (existingNames.Contains($"New PID {n}")) n++;
            name = $"New PID {n}";
        }

        var pid = new Pid
        {
            Address = addr,
            Name = name,
            Size = NewSize,
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

    /// <summary>
    /// Atomically replace this ECU's PID list with <paramref name="loaded"/>.
    /// Used by the SetupWindow's Load PIDs button: clears the model + VM
    /// collection, then appends each new PID through the same pipeline
    /// AddPid uses so the model's address lookup and the observable list
    /// stay in sync.
    /// </summary>
    public void ReplacePids(IEnumerable<Pid> loaded)
    {
        foreach (var existing in Pids.Select(p => p.Model).ToList())
            Model.RemovePid(existing);
        Pids.Clear();
        SelectedPid = null;
        foreach (var pid in loaded)
        {
            Model.AddPid(pid);
            Pids.Add(new PidViewModel(pid, this));
        }
        RaisePidsChanged();
    }

    public void RaisePidsChanged()
    {
        RefreshAliasCollisions();
        Model.RaisePidsChanged();
    }

    // Walks Mode2D rows and flags any pair that derives to the same wire
    // alias (0xF000 | (addr & 0x0FFF)). Cleared on every recompute so
    // resolving a collision wipes both rows' warnings on the next tick.
    // Cheap O(N) - the typical PID list is < 50 rows; no need to memoise.
    private void RefreshAliasCollisions()
    {
        var aliasToRows = new Dictionary<ushort, List<PidViewModel>>();
        foreach (var vm in Pids)
        {
            if (vm.Model.Mode != PidMode.Mode2D) { vm.HasAliasCollision = false; vm.AliasCollisionTooltip = null; continue; }
            ushort alias = (ushort)(0xF000 | (vm.Model.Address & 0x0FFF));
            if (!aliasToRows.TryGetValue(alias, out var list))
                aliasToRows[alias] = list = new List<PidViewModel>();
            list.Add(vm);
        }
        foreach (var (alias, rows) in aliasToRows)
        {
            bool collision = rows.Count > 1;
            string? tip = collision
                ? $"$2D alias 0x{alias:X4} is shared by {rows.Count} rows ("
                  + string.Join(", ", rows.Select(r => $"0x{r.Model.Address:X8}"))
                  + "). The first matching row wins on the $22 wire; the others are unreachable."
                : null;
            foreach (var r in rows)
            {
                r.HasAliasCollision = collision;
                r.AliasCollisionTooltip = tip;
            }
        }
    }

    private static bool TryParseHexU16(string s, out ushort v)
    {
        var trimmed = (s ?? "").Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[2..];
        return ushort.TryParse(trimmed, System.Globalization.NumberStyles.HexNumber,
                               System.Globalization.CultureInfo.InvariantCulture, out v);
    }

    // ---------------- Security ($27) ----------------

    private const string NoneSecurityModuleLabel = "(none)";

    /// <summary>All registered module IDs, prefixed with a synthetic "(none)" entry.</summary>
    public ObservableCollection<string> AvailableSecurityModuleIds { get; }

    /// <summary>Editable key→string map for the module's SecurityModuleConfig JsonElement.</summary>
    public ObservableCollection<KeyValueEntry> SecurityModuleConfigEntries { get; }

    // ---- Live security state (refreshed from MainWindow refresh timer) ----

    private string securityStatusText = "Locked";
    public string SecurityStatusText
    {
        get => securityStatusText;
        private set => SetField(ref securityStatusText, value);
    }

    // Short, pill-friendly variant of SecurityStatusText for the titlebar
    // pill. The Security tab still uses SecurityStatusText so the level /
    // remaining-time detail stays visible there.
    private string securityPillText = "ECU Locked";
    public string SecurityPillText
    {
        get => securityPillText;
        private set => SetField(ref securityPillText, value);
    }

    private string securityFailedAttemptsText = "0 / 3";
    public string SecurityFailedAttemptsText
    {
        get => securityFailedAttemptsText;
        private set => SetField(ref securityFailedAttemptsText, value);
    }

    private string securityPendingSeedText = "(none)";
    public string SecurityPendingSeedText
    {
        get => securityPendingSeedText;
        private set => SetField(ref securityPendingSeedText, value);
    }

    private string securityProgSessionText = "(no module)";
    public string SecurityProgSessionText
    {
        get => securityProgSessionText;
        private set => SetField(ref securityProgSessionText, value);
    }

    /// <summary>
    /// Simulated power-cycle for this ECU. Combines the spec-defined $20
    /// ReturnToNormalMode exit (clears programming/download/$28/$A5 state and
    /// emits the unsolicited $60 if a host is still attached) with a full
    /// security re-lock. This is the canonical behaviour for any "Reset ECU
    /// state" button across the workspace tabs - keep all such buttons routed
    /// through here so they stay in sync.
    ///
    /// Note: $20 alone is spec-correct to NOT touch security (GMW3110 §8.5.6.2),
    /// so EcuExitLogic deliberately omits it; the power-cycle delta is added here.
    /// </summary>
    public void ResetEcuState(DpidScheduler scheduler)
    {
        var channel = Model.State.LastEnhancedChannel;
        EcuExitLogic.Run(Model, scheduler, channel);
        ResetSecurityState();
    }

    /// <summary>
    /// Re-locks the ECU and clears every transient $27 field on its NodeState
    /// (unlocked level, pending seed, failed-attempt counter, lockout deadline,
    /// module-private bookkeeping). Equivalent to a power-cycle for this one
    /// ECU's security subsystem. Bound to the "Reset state" button in the
    /// Security tab.
    /// </summary>
    public void ResetSecurityState()
    {
        var s = Model.State;
        lock (s.Sync)
        {
            s.SecurityUnlockedLevel = 0;
            s.SecurityPendingSeedLevel = 0;
            s.SecurityLastIssuedSeed = null;
            s.SecurityFailedAttempts = 0;
            s.SecurityLockoutUntilMs = 0;
            s.SecurityModuleState = null;
        }
        // The 10Hz refresh tick would catch this within 100ms; push an
        // immediate update so the click feels instant.
        RefreshSecurity(0);
    }

    /// <summary>Called from the main refresh timer to update the live security display.</summary>
    public void RefreshSecurity(long nowMs)
    {
        var s = Model.State;
        if (s.IsInLockout(nowMs))
        {
            double remainingSec = (s.SecurityLockoutUntilMs - nowMs) / 1000.0;
            SecurityStatusText = $"Locked out - {remainingSec:F1} s remaining";
            SecurityPillText   = "ECU Locked out";
        }
        else if (s.SecurityUnlockedLevel > 0)
        {
            SecurityStatusText = $"Unlocked (level {s.SecurityUnlockedLevel})";
            SecurityPillText   = "ECU Unlocked";
        }
        else
        {
            SecurityStatusText = "Locked";
            SecurityPillText   = "ECU Locked";
        }

        SecurityFailedAttemptsText = $"{s.SecurityFailedAttempts} / 3";

        var seed = s.SecurityLastIssuedSeed;
        if (s.SecurityPendingSeedLevel == 0 || seed is null)
        {
            SecurityPendingSeedText = "(none)";
        }
        else
        {
            SecurityPendingSeedText =
                $"level {s.SecurityPendingSeedLevel}, seed = {string.Join(" ", seed.Select(b => b.ToString("X2")))}";
        }

        // Module's programming-session policy + the live shortcut flag that
        // gates the bypass path in Gmw3110_2010_Generic. The shortcut flag is
        // set by $10 $02 or by the full $28 + $A5 $01/$02 + $A5 $03 chain; it
        // only changes the wire behaviour when the module's policy is
        // BypassAll.
        var module = Model.SecurityModule;
        if (module is null)
        {
            SecurityProgSessionText = "(no module)";
        }
        else
        {
            bool inProgSession = s.SecurityProgrammingShortcutActive;
            SecurityProgSessionText = module.Behaviour switch
            {
                SecurityModuleBehaviour.BypassAll => inProgSession
                    ? "Bypass (in prog session)"
                    : "Bypass (not in prog session)",
                SecurityModuleBehaviour.Strict => inProgSession
                    ? "Enforce seed/key (in prog session)"
                    : "Enforce seed/key (not in prog session)",
                _ => module.Behaviour.ToString(),
            };
        }
    }

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
        // Spec-aligned: a real ECU's security state is bound to its security
        // module's lifetime. Replacing the module means a fresh start - any
        // unlocked level, pending seed, failed-attempt counter, or module-
        // private bookkeeping from the prior module is now stale and would
        // silently mask the new module's behaviour (e.g. a prior BypassAll
        // unlock makes the new algorithm's $27 short-circuit through the
        // "already unlocked -> seed=00 00" branch without ever running).
        ResetSecurityState();
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
        // Every entry value persists as a JSON string - modules parse strings
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
