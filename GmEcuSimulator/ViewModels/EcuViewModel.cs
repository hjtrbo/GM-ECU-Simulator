using Common.Protocol;
using Common.Signals;
using Common.Signals.Engines;
using Common.Waveforms;
using Core.Ecu;
using Core.Identification;
using Core.Scheduler;
using Core.Security;
using Core.Services;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;

namespace GmEcuSimulator.ViewModels;

// Editable view of one EcuNode plus its child PidViewModels. CAN ID
// edits push straight to the model; on the next IPC dispatch they take
// effect (the bus's FindByRequestId does the lookup fresh per frame).
public sealed class EcuViewModel : NotifyPropertyChangedBase
{
    public EcuNode Model { get; }
    public ObservableCollection<PidViewModel> Pids { get; } = new();

    // The ECU's $01 (OBD-II) PIDs: read-only catalogue rows with a per-PID Supported toggle, shown in the editor's
    // "$01 (OBD-II)" section. Together with the editable Pids grid ($1A/$22/$2D) this is the whole-ECU view.
    public ObservableCollection<J1979RowViewModel> Obd2Pids { get; } = new();

    // The editable modes, each rendered as its own collapsible section in the editor (mirrors the read-only "$01"
    // section). Every section is an independent filtered/sorted view over the single shared Pids collection above, so
    // Add/Remove/alias-collision logic keeps operating on Pids directly. Order = display order top-to-bottom.
    public IReadOnlyList<PidModeSection> Sections { get; }

    // The DBC-driven CAN broadcast messages this ECU emits, shown in the editor's "CAN Broadcast"
    // section (above $01). Each wraps a Core BroadcastMessage; edits flow through OnBroadcastEdited so
    // the live scheduler rebuilds. Import / Save / Load (*.dbc / *.dbc.json) are MainViewModel
    // commands (they own the file dialogs); Add / Remove message are local commands below.
    public ObservableCollection<BroadcastMessageViewModel> Broadcasts { get; } = new();
    private BroadcastMessageViewModel? selectedBroadcast;

    public GlitchConfigViewModel Glitch { get; }
    private PidViewModel? selectedPid;

    public EcuViewModel(EcuNode model)
    {
        Model = model;
        Glitch = new GlitchConfigViewModel(model.Glitch);
        // AllPids unions the four per-mode stores in deterministic order
        // (Mode22 -> Mode2D -> Mode1A -> Mode1), so the editor grid renders
        // every row regardless of which underlying dictionary owns it. The
        // Mode setter on PidViewModel calls EcuNode.RelocatePidMode when
        // the user flips a row, which moves the underlying Pid between
        // stores without churning this ObservableCollection.
        foreach (var pid in model.AllPids) Pids.Add(new PidViewModel(pid, this));
        foreach (var def in J1979Catalogue.All) Obd2Pids.Add(new J1979RowViewModel(def, model));
        foreach (var msg in model.Broadcasts) Broadcasts.Add(new BroadcastMessageViewModel(msg, this));

        AddBroadcastCommand = new RelayCommand(AddBroadcast);
        RemoveBroadcastCommand = new RelayCommand(
            () => { if (SelectedBroadcast != null) RemoveBroadcast(SelectedBroadcast); },
            () => SelectedBroadcast != null);

        // One collapsible section per editable mode. Each builds its own filtered/sorted view over Pids and its own
        // column-filter set; the order here is the editor's top-to-bottom order.
        Sections = new[]
        {
            new PidModeSection(this, PidMode.Mode1A, "$1A (Identity / ReadDataByIdentifier)", Pids),
            new PidModeSection(this, PidMode.Mode22, "$22 (ReadDataByIdentifier)", Pids),
            new PidModeSection(this, PidMode.Mode2D, "$2D (DefinePIDByAddress)", Pids),
        };

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

    // -------- CAN broadcast section --------

    public RelayCommand AddBroadcastCommand { get; private set; } = null!;
    public RelayCommand RemoveBroadcastCommand { get; private set; } = null!;

    public BroadcastMessageViewModel? SelectedBroadcast
    {
        get => selectedBroadcast;
        set => SetField(ref selectedBroadcast, value);   // RemoveBroadcastCommand re-queries via CommandManager
    }

    private void AddBroadcast()
    {
        // Pick a CAN id not already used by another broadcast on this ECU.
        var taken = new HashSet<uint>(Broadcasts.Select(b => b.Model.CanId));
        uint canId = 0x100;
        while (taken.Contains(canId)) canId++;
        var msg = new BroadcastMessage { CanId = canId, Name = "New broadcast", Dlc = 8, PeriodMs = 100, Enabled = true };
        Model.AddBroadcast(msg);
        var vm = new BroadcastMessageViewModel(msg, this);
        Broadcasts.Add(vm);
        SelectedBroadcast = vm;
        OnBroadcastEdited();
    }

    public void RemoveBroadcast(BroadcastMessageViewModel vm)
    {
        Model.RemoveBroadcast(vm.Model);
        Broadcasts.Remove(vm);
        if (ReferenceEquals(selectedBroadcast, vm)) SelectedBroadcast = null;
        OnBroadcastEdited();
    }

    // Re-sync the VM collection from the model after a bulk change (DBC import / .dbc.json load /
    // replace-all). The caller has already mutated Model.Broadcasts.
    public void ReloadBroadcasts()
    {
        Broadcasts.Clear();
        foreach (var msg in Model.Broadcasts) Broadcasts.Add(new BroadcastMessageViewModel(msg, this));
        SelectedBroadcast = null;
        OnBroadcastEdited();
    }

    // Any broadcast edit: tell the model (so node-level subscribers know) and rebuild the live
    // scheduler if a host session is currently emitting. No-op on the scheduler when idle.
    public void OnBroadcastEdited()
    {
        Model.RaiseBroadcastsChanged();
        bus?.BroadcastScheduler.RebuildIfRunning();
    }

    // 10 Hz live-value refresh for the broadcast signal readouts (driven by MainWindow's timer).
    public void RefreshBroadcastsLive(double timeMs)
    {
        foreach (var b in Broadcasts) b.RefreshLive(Model.EngineModel, timeMs);
    }

    // The operating points the user can drive this ECU through; bound to the Scenario ComboBox in the inspector.
    public ScenarioId[] Scenarios { get; } = Enum.GetValues<ScenarioId>();

    // The live operating point. Backed by the engine model (not a VM field) so it always reflects the model. Setting
    // it ramps the signals toward the new scenario from the current bus clock, which a connected tool sees move on
    // Mode $01 and on any signal-backed $22 PIDs.
    public ScenarioId SelectedScenario
    {
        get => Model.EngineModel.ActiveScenario;
        set
        {
            if (Model.EngineModel.ActiveScenario == value) return;
            Model.EngineModel.SetScenario(value, bus?.NowMs ?? 0);
            OnPropertyChanged();
        }
    }

    // The engine characters the user can pick for this ECU; bound to the Engine Model ComboBox in the inspector.
    public EngineModelOption[] EngineModels { get; } =
        EngineCharacterRegistry.Catalogue.Select(c => new EngineModelOption(c.Id, c.DisplayName)).ToArray();

    // The selected engine character's id. Backed by the live model (not a VM field) so it always reflects what is
    // running. Setting it swaps the character behind the engine model's volatile reference - so a connected tool
    // immediately sees the new induction behaviour (e.g. MAP and fuel pressure rising above base under boost) on the
    // next $01 / $22 read, without re-creating the ECU or dropping the session.
    public string SelectedEngineModelId
    {
        get => Model.EngineModel.Character.Id;
        set
        {
            if (Model.EngineModel.Character.Id == value) return;
            Model.EngineModel.Character = EngineCharacterRegistry.Create(value);
            OnPropertyChanged();
        }
    }

    // The AccelDecelSweep rev-pull timing is fixed at SweepProfile.Default for every ECU - it is not editable per
    // ECU and not persisted. The former Sweep* editor properties (and their collapsed Setup-pane inputs) were retired.

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

        // Surface the just-loaded identity as editable $1A rows. Apply() writes the runtime identifier
        // dictionary, but the editor's $1A section (and the $1A handler's preferred lookup) work off Mode1A
        // Pid rows - so without this the rows never appear and the synthetic seeded $90 placeholder would
        // even shadow the bin's real VIN on the wire.
        MaterializeIdentifiersToMode1ARows(mode);

        // Compose a summary message - shows which fields were populated and
        // surfaces any parser warnings (e.g. "no trampoline pattern detected").
        string modeLabel = mode == BinIdentificationApplier.LoadMode.ReplaceAll
            ? "Replace all ($1A rows become exactly the bin's set)"
            : "Merge (bin values added; rows the bin doesn't provide kept)";
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

    // Mirrors the runtime identifier dictionary (just written by BinIdentificationApplier.Apply) into editable
    // Mode1A Pid rows so the bin's identity shows in the editor's $1A section, persists in config, and is the value
    // the $1A handler returns (the handler prefers Mode1A rows over the dict, so the row MUST carry the bin value or
    // a stale seeded placeholder would shadow it). The bin is the source the user explicitly chose, so it overwrites
    // any existing row for the same DID; ReplaceAll additionally drops $1A rows the bin didn't surface.
    private void MaterializeIdentifiersToMode1ARows(BinIdentificationApplier.LoadMode mode)
    {
        if (mode == BinIdentificationApplier.LoadMode.ReplaceAll)
            foreach (var vm in Pids.Where(p => p.Model.Mode == PidMode.Mode1A).ToList())
                RemovePid(vm);

        foreach (var did in Model.Identifiers.Keys.OrderBy(d => d))
        {
            var data = Model.GetIdentifier(did);
            if (data is null || data.Length == 0) continue;

            // Overwrite any existing row for this DID (e.g. the seeded $90 placeholder) with the bin value.
            var existing = Pids.FirstOrDefault(p => p.Model.Mode == PidMode.Mode1A
                                                 && (byte)(p.Model.Address & 0xFF) == did);
            if (existing != null) RemovePid(existing);

            var pid = new Pid
            {
                Mode        = PidMode.Mode1A,
                Address     = did,
                Name        = Gmw3110DidNames.NameOf(did) ?? $"DID {did:X2}",
                StaticBytes = data,
                LengthBytes = data.Length,
                Size        = PidSize.DWord,
                DataType    = PidDataType.Unsigned,
            };
            Model.AddPid(pid);
            Pids.Add(new PidViewModel(pid, this));
        }

        foreach (var s in Sections) s.Refresh();
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

    // The single "active row" across all sections. The waveform inspector and the per-ECU Remove route through this;
    // each section's grid drives it via NotifySectionSelected. Setting it from code (e.g. after Add) does NOT clear
    // the sections - NotifySectionSelected owns the cross-section deselect.
    public PidViewModel? SelectedPid
    {
        get => selectedPid;
        set => SetField(ref selectedPid, value);
    }

    // Called by a section when the user selects one of its rows. Promotes the row to the shared SelectedPid and clears
    // every other section's selection so only one row is highlighted across the whole editor.
    internal void NotifySectionSelected(PidModeSection source, PidViewModel pid)
    {
        SelectedPid = pid;
        foreach (var s in Sections)
            if (!ReferenceEquals(s, source)) s.ClearSelection();
    }

    // Add a new PID into a specific mode's section. The Mode column is gone, so the section's Add button stamps its
    // mode here; the new row appears in that section (and only that section) because each section's view filters by
    // Mode. Catalogue-driven modes ($1A / $22) start on a placeholder address the user re-points via the Identifier
    // dropdown; $2D rows start on the next free byte range so a hand-rolled address doesn't overlap an existing one.
    public void AddPid(PidMode mode)
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
            Mode = mode,
            Scalar = 1.0,
            Offset = 0.0,
            Unit = "",
            // A fresh row has no live source - it reads 0 until the user picks a signal or "Waveform" in the Signal
            // column. The waveform config is still seeded with a sensible visible shape so picking "Waveform" produces
            // something immediately, but it stays dormant under ValueSource.None.
            ValueSource = PidValueSource.None,
            WaveformConfig = new WaveformConfig { Shape = WaveformShape.Sin, Amplitude = 50, Offset = 50, FrequencyHz = 1.0 },
        };
        // AddPid routes by pid.Mode into the right per-mode store.
        Model.AddPid(pid);
        var vm = new PidViewModel(pid, this);
        Pids.Add(vm);                       // ListCollectionView in each section re-flows; only the matching one shows it
        RefreshAliasCollisions();
        NotifyIdentifierSetChanged();
        var section = Sections.FirstOrDefault(s => s.Mode == mode);
        if (section != null) section.SelectedPid = vm;   // selects in-section + promotes to SelectedPid
        else SelectedPid = vm;
    }

    // Back-compat no-arg add (MainViewModel's top toolbar): default to a $22 row.
    public void AddPid() => AddPid(PidMode.Mode22);

    // The per-mode store keys currently occupied by rows in <paramref name="mode"/>, optionally excluding one row.
    // Two rows that share a key collide in EcuNode's store (last write wins, the rest go silent on the wire), so the
    // editor consults this to keep each identifier unique - the $22 catalogue picker hides taken identifiers and the
    // $1A/$2D address box rejects a typed duplicate.
    internal HashSet<uint> IdentifiersInUse(PidMode mode, Pid? exclude = null)
    {
        var set = new HashSet<uint>();
        foreach (var vm in Pids)
            if (vm.Model.Mode == mode && !ReferenceEquals(vm.Model, exclude))
                set.Add(vm.Model.StoreKey);
        return set;
    }

    // True when a row other than <paramref name="self"/> in <paramref name="mode"/> already serves the identifier
    // <paramref name="address"/> maps to.
    internal bool IsIdentifierTaken(Pid self, PidMode mode, uint address)
        => IdentifiersInUse(mode, exclude: self).Contains(Pid.StoreKeyFor(mode, address));

    // Re-announce each row's IdentifierCatalogue so the $22 picker drops (or restores) identifiers as rows claim or
    // release them. Called after a structural identifier change (add / remove / address edit / bulk replace), not on
    // every field edit, so the per-row filtering stays cheap.
    private void NotifyIdentifierSetChanged()
    {
        foreach (var vm in Pids) vm.RefreshIdentifierCatalogue();
    }

    // Remove a specific row (the section Remove buttons call this with the section's selection).
    public void RemovePid(PidViewModel vm)
    {
        // RemovePid routes to the correct per-mode store based on Pid.Mode.
        Model.RemovePid(vm.Model);
        Pids.Remove(vm);
        foreach (var s in Sections) s.ClearSelection();
        if (ReferenceEquals(selectedPid, vm)) SelectedPid = null;
        RefreshAliasCollisions();
        NotifyIdentifierSetChanged();
    }

    public void RemoveSelectedPid()
    {
        if (selectedPid != null) RemovePid(selectedPid);
    }

    /// <summary>
    /// Called by PidViewModel.Mode when the user flips a row's mode. The
    /// underlying Pid object stays the same; only its EcuNode-side storage
    /// location changes - <see cref="EcuNode.RelocatePidMode"/> handles the
    /// move atomically. The Pids ObservableCollection isn't touched, so the
    /// same VM entry keeps rendering the same model.
    /// </summary>
    public void OnPidModeChanged(Pid pid, PidMode oldMode, PidMode newMode)
    {
        if (oldMode == newMode) return;
        // PidViewModel has already set pid.Mode = newMode by the time we get
        // here, so pass oldMode explicitly to find the source store.
        Model.RelocatePidMode(pid, oldMode);
    }

    // Called by PidViewModel.Address when the user edits a row's address. The per-mode stores are keyed by Address, so
    // the model has to re-key the entry. Otherwise GetPid / GetPidByWireId miss after the edit and the ECU NRCs the
    // request - e.g. a $2D or $22 read returns RequestOutOfRange. PidViewModel has already written the new address onto
    // the model, so pass the prior address to find the existing entry.
    internal void OnPidAddressChanged(Pid pid, uint oldAddress)
    {
        Model.RekeyPidAddress(pid, oldAddress);
        NotifyIdentifierSetChanged();
    }

    /// <summary>
    /// Atomically replace this ECU's PID list with <paramref name="loaded"/>.
    /// Used by the SetupWindow's Load PIDs button: clears the model + VM
    /// collection, then appends each new PID through the same pipeline
    /// AddPid uses so the model's per-mode stores and the observable list
    /// stay in sync.
    /// </summary>
    public void ReplacePids(IEnumerable<Pid> loaded)
    {
        // RemovePid routes by Pid.Mode into the right store.
        foreach (var existing in Pids.Select(p => p.Model).ToList())
            Model.RemovePid(existing);
        Pids.Clear();
        SelectedPid = null;
        foreach (var pid in loaded)
        {
            // AddPid routes by pid.Mode into the right store.
            Model.AddPid(pid);
            Pids.Add(new PidViewModel(pid, this));
        }
        foreach (var s in Sections) s.Refresh();   // re-apply each section's filter + sort to the swapped-in rows
        NotifyIdentifierSetChanged();
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

    // Called from the main refresh timer to refresh the $01 section's live wire-byte readout.
    public void RefreshObd2Live(double timeMs)
    {
        foreach (var row in Obd2Pids) row.RefreshLive(timeMs);
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
