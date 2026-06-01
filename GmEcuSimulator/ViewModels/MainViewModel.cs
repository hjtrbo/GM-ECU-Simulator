using Common;
using Common.Persistence;
using Common.Protocol;
using Core.Bus;
using Core.Dps;
using Core.Ecu;
using Core.Identification;
using Core.Persistence;
using Core.Replay;
using GmEcuSimulator.Views;
using Microsoft.Win32;
using Shim;
using Shim.Ipc;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;

namespace GmEcuSimulator.ViewModels;

public sealed class MainViewModel : NotifyPropertyChangedBase
{
    private readonly VirtualBus bus;
    private readonly BinReplayCoordinator replay;
    private readonly NamedPipeServer pipeServer;
    private readonly RawCanTcpServer rawCanServer;
    public ObservableCollection<EcuViewModel> Ecus { get; } = new();

    // The ordered set of PIDs pinned to the main window's live-tile dashboard.
    // Cross-ECU (each tile names its owning ECU), so it lives here rather than
    // on any one EcuViewModel. Persisted per-config as SimulatorConfig.LiveTiles
    // and re-resolved against the freshly-built ECUs on every load / rebuild.
    public ObservableCollection<PidTileViewModel> LiveTiles { get; } = new();

    // Tile descriptors parked by a config-load path (startup / File>Open /
    // Import / mode-switch) for the next Rebuild() to resolve against the new
    // ECU set. Null means "no fresh config to apply" - Rebuild then reconciles
    // the existing tiles in place instead (rebinding to rebuilt PID instances,
    // dropping orphans).
    private List<LiveTileDto>? pendingTileDescriptors;

    private bool pickerOpen;
    private EcuViewModel? pickerSelectedEcu;
    private string pickerFilterText = "";
    private PickerModeOption pickerSelectedMode = AllPickerModes[0];

    private EcuViewModel? selectedEcu;
    private EcuViewModel? setupSelectedEcu;
    private string? currentFilePath;

    // Holds the currently open setup window so a second "Open setup window..."
    // click brings the existing one back to the front instead of stacking
    // duplicates. Nulled when the user closes the window so the next click
    // creates a fresh one.
    private Window? setupWindow;
    private string statusText = "Ready";
    private bool j2534Busy;

    // Set when the user has primed the simulator from a DPS archive; persisted
    // through ConfigSchema.PrimeArchivePath so the auto-load path picks it up
    // on the next launch. Null = not primed.
    private string? primeArchivePath;
    private string? donorBinPath;
    private PrimedDataset? primedDataset;

    // Per-user UI preferences loaded from %LOCALAPPDATA%\GmEcuSimulator\config\settings.json.
    // Each Log-menu checkbox setter writes back through SaveAppSettings() so the
    // user's choices survive across restarts.
    private readonly AppSettings appSettings;

    // In-memory snapshot of the ECU set captured the moment a bin is loaded.
    // Restored on Unload so the user's pre-bin configuration comes back
    // intact. ecu_config.json on disk is never overwritten by load/unload.
    private SimulatorConfig? priorSnapshot;

    public BinReplayViewModel BinReplay { get; }
    public CaptureViewModel Capture { get; }

    public RelayCommand NewCommand { get; }
    public RelayCommand OpenCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand SaveAsCommand { get; }
    public RelayCommand AddEcuCommand { get; }
    public RelayCommand AddBlankEcuCommand { get; }
    public RelayCommand RemoveEcuCommand { get; }
    public RelayCommand AddPidCommand { get; }
    public RelayCommand RemovePidCommand { get; }
    public RelayCommand AddSetupPidCommand { get; }
    public RelayCommand RemoveSetupPidCommand { get; }
    public RelayCommand OpenSetupWindowCommand { get; }
    public RelayCommand ConfigurePidsCommand { get; }
    public RelayCommand SavePidsCommand { get; }
    public RelayCommand LoadPidsCommand { get; }
    public RelayCommand SaveEcuCommand { get; }
    public RelayCommand LoadEcuCommand { get; }
    public RelayCommand SaveSetupEcuCommand { get; }
    public RelayCommand RemoveSetupEcuCommand { get; }
    public RelayCommand ResetStateCommand { get; }
    public RelayCommand RegisterJ2534Command { get; }
    public RelayCommand UnregisterJ2534Command { get; }
    public RelayCommand ShowRegisteredDevicesCommand { get; }
    public RelayCommand ResetIpcPipeCommand { get; }
    public RelayCommand PrimeFromArchiveCommand { get; }
    public RelayCommand ClearPrimeArchiveCommand { get; }
    public RelayCommand AddTileCommand { get; }

    /// <summary>
    /// Mode + connection-type combinations offered by the selector ComboBox.
    /// ECU Simulator is offered on both transports (J2534 / raw-CAN TCP); DPS
    /// programming is J2534-only. Bound to the mode selector at the top of the
    /// main window.
    /// </summary>
    public IReadOnlyList<ModeOption> AvailableModes { get; } = new[]
    {
        new ModeOption(AppMode.EcuSimulator, ConnectionType.J2534),
        new ModeOption(AppMode.EcuSimulator, ConnectionType.RawCanTcp),
        new ModeOption(AppMode.DpsSimulator, ConnectionType.J2534),
    };

    public MainViewModel(VirtualBus bus, BinReplayCoordinator replay, NamedPipeServer pipeServer,
                         RawCanTcpServer rawCanServer)
    {
        this.bus = bus;
        this.replay = replay;
        this.pipeServer = pipeServer;
        this.rawCanServer = rawCanServer;

        BinReplay = new BinReplayViewModel(replay, bus, OnBinReplayLoad, OnBinReplayUnload);
        Capture = new CaptureViewModel(bus.Capture);

        // Hydrate UI preferences. The setters fan out to the static gates in
        // MainWindow + bus.AnnotateFrames so behaviour matches the persisted
        // choices before any frame flows.
        appSettings = AppSettings.Load();
        currentMode                  = appSettings.Mode;
        currentConnection            = appSettings.ConnectionType;
        logIncludeJ2534Calls         = appSettings.LogIncludeJ2534Calls;
        logIncludeBusTraffic         = appSettings.LogIncludeBusTraffic;
        logAppendDescriptionTag      = appSettings.LogAppendDescriptionTag;
        logCollapseBulkTransfers     = appSettings.LogCollapseBulkTransfers;
        allowPeriodicTesterPresent   = appSettings.AllowPeriodicTesterPresent;
        suppressTesterPresentInWindow = appSettings.LogSuppressTesterPresentInWindow;
        MainWindow.SetIncludeJ2534FileLog(logIncludeJ2534Calls);
        MainWindow.SetIncludeBusFileLog(logIncludeBusTraffic);
        MainWindow.SetSuppressTesterPresentInWindow(suppressTesterPresentInWindow);
        bus.AnnotateFrames               = logAppendDescriptionTag;
        bus.CollapseBulkTransfers        = logCollapseBulkTransfers;
        bus.AllowPeriodicTesterPresent   = allowPeriodicTesterPresent;
        // IsFileLoggingEnabled goes through the public setter so the sink
        // actually starts if the user had the toggle on at last shutdown.
        IsFileLoggingEnabled = appSettings.LogToFile;

        // Seed the live-tile dashboard from the per-mode config file that App
        // already applied to the bus on startup - same file, so the descriptors
        // resolve against the ECUs the upcoming Rebuild() builds. Guarded: DPS
        // modes don't persist, the file may be absent, or it may be a pre-v17
        // config with no LiveTiles - any of which just means an empty dashboard.
        try
        {
            if (currentMode.PersistsConfig())
            {
                var tilePath = ConfigStore.PathForMode(currentMode);
                if (File.Exists(tilePath))
                    pendingTileDescriptors = ConfigStore.Load(tilePath).LiveTiles;
            }
        }
        catch { /* corrupt / unreadable config -> empty dashboard */ }

        // Rebuild after currentMode is set so ECUs inherit the right
        // visibility flags from the get-go.
        Rebuild();

        NewCommand                   = new RelayCommand(New);
        OpenCommand                  = new RelayCommand(Open);
        SaveCommand                  = new RelayCommand(Save);
        SaveAsCommand                = new RelayCommand(SaveAs);
        AddEcuCommand                = new RelayCommand(AddEcu, CanAddEcu);
        AddBlankEcuCommand           = new RelayCommand(AddBlankEcu, CanAddEcu);
        RemoveEcuCommand             = new RelayCommand(RemoveEcu, () => SelectedEcu != null);
        AddPidCommand                = new RelayCommand(AddPid,    () => SelectedEcu != null);
        RemovePidCommand             = new RelayCommand(RemovePid, () => SelectedEcu?.SelectedPid != null);
        AddSetupPidCommand           = new RelayCommand(AddSetupPid,    () => SetupSelectedEcu != null);
        RemoveSetupPidCommand        = new RelayCommand(RemoveSetupPid, () => SetupSelectedEcu?.SelectedPid != null);
        OpenSetupWindowCommand       = new RelayCommand(OpenSetupWindow);
        ConfigurePidsCommand         = new RelayCommand(ConfigurePids, CanConfigurePids);
        SavePidsCommand              = new RelayCommand(SaveSetupPids, () => SetupSelectedEcu != null && SetupSelectedEcu.Pids.Count > 0);
        LoadPidsCommand              = new RelayCommand(LoadSetupPids, () => SetupSelectedEcu != null);
        SaveEcuCommand               = new RelayCommand(SaveEcu, () => SelectedEcu != null);
        LoadEcuCommand               = new RelayCommand(LoadEcu, CanAddEcu);
        SaveSetupEcuCommand          = new RelayCommand(SaveSetupEcu,   () => SetupSelectedEcu != null);
        RemoveSetupEcuCommand        = new RelayCommand(RemoveSetupEcu, () => SetupSelectedEcu != null);
        ResetStateCommand            = new RelayCommand(ResetState, () => Ecus.Count > 0);
        RegisterJ2534Command         = new RelayCommand(RegisterJ2534,         () => !j2534Busy);
        UnregisterJ2534Command       = new RelayCommand(UnregisterJ2534,       () => !j2534Busy);
        ShowRegisteredDevicesCommand = new RelayCommand(ShowRegisteredDevices, () => !j2534Busy);
        ResetIpcPipeCommand           = new RelayCommand(ResetIpcPipe,          () => !j2534Busy);
        PrimeFromArchiveCommand       = new RelayCommand(PrimeFromArchive);
        ClearPrimeArchiveCommand      = new RelayCommand(ClearPrimeArchive, () => !string.IsNullOrEmpty(primeArchivePath));
        AddTileCommand                = new RelayCommand(AddTile, p => p is PidPickerEntry);

        RefreshConnectionStatus();
    }

    public EcuViewModel? SelectedEcu
    {
        get => selectedEcu;
        set
        {
            if (SetField(ref selectedEcu, value))
                OnPropertyChanged(nameof(ShowsSecurityPill));
        }
    }

    // ---------------- Global mode ----------------

    private AppMode currentMode;
    private ConnectionType currentConnection;

    /// <summary>
    /// Active top-level mode (what is simulated). Connection type is the other
    /// half of the selection - see <see cref="CurrentConnection"/> and
    /// <see cref="SelectedModeOption"/>.
    /// </summary>
    public AppMode CurrentMode => currentMode;

    /// <summary>Active transport for the current mode.</summary>
    public ConnectionType CurrentConnection => currentConnection;

    /// <summary>
    /// The (mode, connection) pick bound two-way to the selector ComboBox.
    /// Setter delegates to <see cref="ChangeSelection"/> so the dialog + ECU
    /// clear + per-mode config reload + transport flip all run together.
    /// Cancelling the dialog restores the prior selection via this setter.
    /// </summary>
    public ModeOption SelectedModeOption
    {
        get => AvailableModes.FirstOrDefault(o => o.Mode == currentMode && o.Connection == currentConnection)
               ?? AvailableModes[0];
        set
        {
            if (value is null) return;
            if (value.Mode == currentMode && value.Connection == currentConnection) return;
            // Suppress reentrancy while we may snap the value back on cancel.
            if (modeSwitchInProgress) return;
            if (!ChangeSelection(value.Mode, value.Connection))
            {
                modeSwitchInProgress = true;
                try { OnPropertyChanged(); } // notify so the ComboBox refreshes
                finally { modeSwitchInProgress = false; }
                return;
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(WindowTitle));
        }
    }

    private bool modeSwitchInProgress;

    private bool CanAddEcu()
        => currentMode.AllowsMultipleEcus() || Ecus.Count == 0;

    /// <summary>
    /// Drives a mode and/or connection-type transition: optionally save current
    /// state, clear the bus, persist the new selection, reload the mode's config
    /// (so every ECU + bus module re-initialises from a clean slate - no session
    /// remnants carry over), then flip the active transport. Returns false when
    /// the user cancels - the caller restores the previous selection.
    /// </summary>
    private bool ChangeSelection(AppMode newMode, ConnectionType newConnection)
    {
        var oldMode = currentMode;
        var oldConnection = currentConnection;
        var fromLabel = new ModeOption(oldMode, oldConnection).Label;
        var toLabel = new ModeOption(newMode, newConnection).Label;
        bool hasEcus = Ecus.Count > 0;

        // Connection-type-only flip within the same mode: the ECU configuration
        // is identical on either transport, so there is nothing to save and no
        // need to reload from disk (which would discard unsaved edits and pop a
        // save dialog for no reason). Just reset transient per-ECU session state
        // - the same teardown the Reset State command performs - so no J2534
        // session remnants (unlocked security, DPID streams, programming state)
        // carry over to the gauge link, then swap the transport. The save dialog
        // below is reserved for true mode changes, where the config file differs.
        if (newMode == oldMode)
        {
            foreach (var ecu in Ecus) ecu.ResetEcuState(bus.Scheduler);
            currentConnection = newConnection;
            appSettings.ConnectionType = newConnection;
            try { appSettings.Save(); } catch { /* persistence best-effort */ }
            ApplyActiveTransport(oldConnection, newConnection);
            StatusText = $"Connection: {toLabel}";
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            return true;
        }

        if (hasEcus)
        {
            // Themed prompts use closures to capture the user's decision.
            // ThemedMessageBox is modal (ShowDialog) so it blocks until the
            // user clicks something; we then inspect the local 'proceed'
            // flag to decide whether to continue with the mode switch.
            var owner = Application.Current?.MainWindow;
            bool proceed = false;

            if (oldMode.PersistsConfig())
            {
                ThemedMessageBox.Show(
                    owner,
                    "Change mode",
                    $"Switching from {fromLabel} to {toLabel} will clear " +
                    "the current ECU set.\n\nSave the current configuration first?",
                    MessageBoxImage.Question,
                    new ThemedDialogButton(
                        "Cancel",
                        isCancel: true),
                    new ThemedDialogButton(
                        "Discard",
                        onClick: () => proceed = true),
                    new ThemedDialogButton(
                        "Save As...",
                        onClick: () =>
                        {
                            var settings = AppSettings.Load();
                            var dlg = new SaveFileDialog
                            {
                                Filter = "Mode config (*.mode.json)|*.mode.json|JSON (*.json)|*.json|All files|*.*",
                                DefaultExt = ".mode.json",
                                FileName = oldMode.ConfigFileName(),
                                InitialDirectory = AppSettings.ResolveInitialDir(settings.LastConfigDir),
                            };
                            if (dlg.ShowDialog() != true) return;
                            try
                            {
                                ConfigStore.Save(SnapshotForSave(), dlg.FileName);
                                proceed = true;
                                PersistLastConfigDir(settings, dlg.FileName);
                            }
                            catch (Exception ex) { Error("Save failed", ex); }
                        }),
                    new ThemedDialogButton(
                        "Save & Switch",
                        onClick: () =>
                        {
                            try
                            {
                                ConfigStore.Save(SnapshotForSave(), ConfigStore.PathForMode(oldMode));
                                proceed = true;
                            }
                            catch (Exception ex) { Error("Save failed", ex); }
                        },
                        isDefault: true,
                        primary: true));
            }
            else
            {
                ThemedMessageBox.Show(
                    owner,
                    "Change mode",
                    $"Switching from {fromLabel} will clear the current ECU. " +
                    $"{oldMode.DisplayName()} state is not persisted - continue?",
                    MessageBoxImage.Warning,
                    new ThemedDialogButton(
                        "Cancel",
                        isCancel: true),
                    new ThemedDialogButton(
                        "Continue",
                        onClick: () => proceed = true,
                        isDefault: true,
                        primary: true));
            }

            if (!proceed) return false;
        }
        else if (oldMode.PersistsConfig())
        {
            // No ECUs but we're leaving a persistable mode - silently save so
            // the empty-bus state survives the round trip. Without this the
            // on-disk file still carries whatever was there before the user
            // deleted everything, and coming back to the mode would resurrect
            // the deleted ECUs from disk.
            try { ConfigStore.Save(SnapshotForSave(), ConfigStore.PathForMode(oldMode)); }
            catch (Exception ex) { Error("Save failed", ex); return false; }
        }

        // Drop the current ECU set and any prime / bin-replay snapshot tied
        // to the old mode. Each transition starts the new mode from a clean
        // bus so the user sees nothing from the previous workflow.
        bus.ReplaceNodes(Array.Empty<EcuNode>());
        priorSnapshot = null;
        PrimeArchivePath = null;
        donorBinPath = null;
        PrimedDataset = null;

        currentMode = newMode;
        currentConnection = newConnection;
        appSettings.Mode = newMode;
        appSettings.ConnectionType = newConnection;
        try { appSettings.Save(); } catch { /* persistence best-effort */ }

        // Switching modes resets the dashboard to the new mode's saved tiles
        // (empty unless a config below carries some) rather than carrying the
        // old mode's tiles forward.
        pendingTileDescriptors = new List<LiveTileDto>();

        // Load the new mode's config if it persists and a file is present.
        try
        {
            if (newMode.PersistsConfig())
            {
                var path = ConfigStore.PathForMode(newMode);
                if (File.Exists(path))
                {
                    var cfg = ConfigStore.Load(path);
                    ConfigStore.ApplyTo(cfg, bus);
                    // Backfill curated identity + live $22 seeds onto the loaded ECUs (same reason as the App startup
                    // auto-load): the load path never seeds, so a pre-seeder config would hide the 12 signal-backed
                    // $22 PIDs. Precedence-safe and EcuSimulator-only.
                    if (newMode == AppMode.EcuSimulator) DefaultEcuConfig.SeedDefaults(bus);
                    pendingTileDescriptors = cfg.LiveTiles;
                    // Priming is DPS-only. If a persisted non-DPS config carries
                    // a stale primeArchivePath (e.g. from a pre-gating session
                    // where a user primed into ECU Simulator mode), drop it on
                    // load so the next save scrubs the field from disk.
                    bool canPrime = newMode is AppMode.DpsSimulator;
                    PrimeArchivePath = canPrime ? cfg.PrimeArchivePath : null;
                    donorBinPath     = canPrime ? cfg.DonorBinPath     : null;
                }
                else if (newMode == AppMode.EcuSimulator)
                {
                    DefaultEcuConfig.ApplyIfEmpty(bus);
                }
            }
        }
        catch (Exception ex) { Error("Load failed", ex); }

        Rebuild();

        // Flip the active transport around the freshly re-initialised bus. A
        // mode-only change that keeps the same connection type is a no-op here.
        ApplyActiveTransport(oldConnection, newConnection);

        StatusText = $"Mode: {toLabel}";
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        return true;
    }

    /// <summary>
    /// Stops the outgoing transport and starts the incoming one so exactly one
    /// is live. No-op when the connection type is unchanged. Both servers'
    /// Start / StopAsync are idempotent and target disjoint resources (named
    /// pipe vs TCP port), so a brief overlap during the async stop is harmless.
    /// async void is acceptable here: it is invoked as a fire-and-forget side
    /// effect of a UI selection change.
    /// </summary>
    private async void ApplyActiveTransport(ConnectionType oldConnection, ConnectionType newConnection)
    {
        if (oldConnection == newConnection) return;

        try
        {
            if (oldConnection == ConnectionType.RawCanTcp) await rawCanServer.StopAsync();
            else                                           await pipeServer.StopAsync();
        }
        catch (Exception ex) { bus.LogSim?.Invoke($"Stopping {oldConnection} transport failed: {ex.Message}"); }

        try
        {
            if (newConnection == ConnectionType.RawCanTcp)
            {
                // No registry gating - the gauge sim is hand-configured with the port.
                rawCanServer.Start();
            }
            else
            {
                // Mirror App startup: only listen on the pipe if the shim is
                // registered, so an unregistered host can't reach us.
                bool registered = false;
                try { registered = J2534Registration.Check().IsRegistered; } catch { }
                if (registered) pipeServer.Start();
                else bus.LogSim?.Invoke("J2534 not registered - IPC pipe not started; register from the J2534 menu to enable host connections.");
            }
        }
        catch (Exception ex) { bus.LogSim?.Invoke($"Starting {newConnection} transport failed: {ex.Message}"); }

        RefreshConnectionStatus();
    }

    private SimulatorConfig SnapshotForSave()
    {
        var cfg = priorSnapshot ?? ConfigStore.Snapshot(bus, replay: replay);
        cfg.PrimeArchivePath = PrimeArchivePath;
        cfg.DonorBinPath = donorBinPath;
        cfg.LiveTiles = SnapshotTileDescriptors();
        return cfg;
    }

    // ---------------- Live-tile dashboard ----------------

    // Two-way bound to the picker Popup's IsOpen. Opening it seeds the source
    // ECU (the inspector's selection, or the first ECU) and clears the filter
    // so the candidate list starts fresh.
    public bool PickerOpen
    {
        get => pickerOpen;
        set
        {
            if (!SetField(ref pickerOpen, value)) return;
            if (value)
            {
                pickerFilterText = "";
                pickerSelectedEcu = SelectedEcu ?? Ecus.FirstOrDefault();
                pickerSelectedMode = AllPickerModes[0];   // default filter: Mode $01
                OnPropertyChanged(nameof(PickerFilterText));
                OnPropertyChanged(nameof(PickerSelectedEcu));
                OnPropertyChanged(nameof(PickerSelectedMode));
                OnPropertyChanged(nameof(ShowsPickerEcuDropdown));
                OnPropertyChanged(nameof(PickerCandidates));
            }
        }
    }

    // The ECU the picker draws its candidate PID list from. The dropdown that
    // sets this only shows when more than one ECU is configured.
    public EcuViewModel? PickerSelectedEcu
    {
        get => pickerSelectedEcu;
        set
        {
            if (SetField(ref pickerSelectedEcu, value))
                OnPropertyChanged(nameof(PickerCandidates));
        }
    }

    // Case-insensitive filter applied to the candidate list, matched against
    // both the PID id (AddressHex) and the friendly name.
    public string PickerFilterText
    {
        get => pickerFilterText;
        set
        {
            if (SetField(ref pickerFilterText, value))
                OnPropertyChanged(nameof(PickerCandidates));
        }
    }

    public bool ShowsPickerEcuDropdown => Ecus.Count > 1;

    // The modes the picker can filter by. $01 (OBD-II) is the built-in J1979
    // projection; the rest are editable PID-row modes. Order = dropdown order.
    private static readonly PickerModeOption[] AllPickerModes =
    {
        new(PickerModeFilter.Obd2,   "Mode $01 (OBD-II)"),
        new(PickerModeFilter.Mode1A, "Mode $1A (Identity)"),
        new(PickerModeFilter.Mode22, "Mode $22"),
        new(PickerModeFilter.Mode2D, "Mode $2D"),
    };

    public IReadOnlyList<PickerModeOption> PickerModes => AllPickerModes;

    // The mode the picker list is filtered to. Defaults to (and resets on each
    // open to) Mode $01.
    public PickerModeOption PickerSelectedMode
    {
        get => pickerSelectedMode;
        set
        {
            if (SetField(ref pickerSelectedMode, value))
                OnPropertyChanged(nameof(PickerCandidates));
        }
    }

    // The "<pid id> - <friendly name>" candidates for the picker list, scoped
    // to the selected source ECU and mode, then filtered by the text box. $01
    // (OBD-II) PIDs aren't Pid rows - they're the built-in J1979 projection -
    // so that mode draws from Obd2Pids and only lists ones the ECU advertises
    // (Supported); the other modes draw the matching rows from Pids.
    public IEnumerable<PidPickerEntry> PickerCandidates
    {
        get
        {
            var ecu = pickerSelectedEcu;
            if (ecu is null) yield break;
            var filter = (pickerFilterText ?? "").Trim();

            if (pickerSelectedMode.Filter == PickerModeFilter.Obd2)
            {
                foreach (var row in ecu.Obd2Pids)
                {
                    if (!row.Supported) continue;
                    if (Matches(filter, row.PidHex, row.Name))
                        yield return new PidPickerEntry(ecu, null, row, $"{row.PidHex} - {row.Name}");
                }
                yield break;
            }

            var mode = pickerSelectedMode.Filter switch
            {
                PickerModeFilter.Mode1A => PidMode.Mode1A,
                PickerModeFilter.Mode2D => PidMode.Mode2D,
                _                       => PidMode.Mode22,
            };
            foreach (var pid in ecu.Pids)
            {
                if (pid.Model.Mode != mode) continue;
                if (Matches(filter, pid.AddressHex, pid.Name))
                    yield return new PidPickerEntry(ecu, pid, null, $"{pid.AddressHex} - {pid.Name}");
            }
        }
    }

    private static bool Matches(string filter, string id, string name)
        => filter.Length == 0
        || id.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
        || name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

    // Picker selection -> append a tile for that PID and close the popup.
    private void AddTile(object? parameter)
    {
        if (parameter is not PidPickerEntry entry) return;
        var tile = entry.Pid is not null
            ? new PidTileViewModel(entry.Ecu, entry.Pid, RemoveTile)
            : new PidTileViewModel(entry.Ecu, entry.Obd2!, RemoveTile);
        LiveTiles.Add(tile);
        PickerOpen = false;
        AutoSave();
    }

    // Right-click -> Delete on a tile.
    public void RemoveTile(PidTileViewModel tile)
    {
        if (LiveTiles.Remove(tile)) { tile.Unhook(); AutoSave(); }
    }

    // Drag-reorder commit from the code-behind. Bounds-checked so a stale drop
    // index can't throw.
    public void MoveTile(int from, int to)
    {
        if (from < 0 || from >= LiveTiles.Count) return;
        to = Math.Clamp(to, 0, LiveTiles.Count - 1);
        if (from == to) return;
        LiveTiles.Move(from, to);
        AutoSave();
    }

    // Find the live tile target for a persisted descriptor. Pid tiles resolve
    // ECU by name then PID by mode + address; $01 tiles resolve ECU by name
    // then the J1979 row by $01 PID id. Null if the target is gone (an orphan).
    private PidTileViewModel? ResolveTile(LiveTileDto d)
    {
        var ecu = Ecus.FirstOrDefault(e => e.Name == d.Ecu);
        if (ecu is null) return null;
        if (d.Source == LiveTileSource.Obd2)
        {
            var row = ecu.Obd2Pids.FirstOrDefault(r => r.Pid == (byte)d.Address);
            return row is not null ? new PidTileViewModel(ecu, row, RemoveTile) : null;
        }
        var pid = ecu.Pids.FirstOrDefault(p => p.Model.Mode == d.Mode && p.Model.Address == d.Address);
        return pid is not null ? new PidTileViewModel(ecu, pid, RemoveTile) : null;
    }

    // Rebuild LiveTiles from a descriptor list, dropping any that don't resolve.
    // Order is preserved. Existing tiles are unhooked first so they don't dangle
    // off the source VMs.
    private void LoadTilesFromDescriptors(IEnumerable<LiveTileDto> descriptors)
    {
        foreach (var t in LiveTiles) t.Unhook();
        LiveTiles.Clear();
        foreach (var d in descriptors)
        {
            var tile = ResolveTile(d);
            if (tile is not null) LiveTiles.Add(tile);
        }
    }

    // Re-resolve the current tiles against the live ECU set. Re-binds tiles
    // whose source VM instance was replaced (Load PIDs) and drops orphans.
    private void ReconcileTiles() => LoadTilesFromDescriptors(SnapshotTileDescriptors());

    // Derive persistable descriptors from the live tile refs - reads the
    // current mode/address so an in-place edit in the editor is captured.
    private List<LiveTileDto> SnapshotTileDescriptors()
        => LiveTiles.Select(t => t.Obd2 is { } row
            ? new LiveTileDto { Source = LiveTileSource.Obd2, Ecu = t.Ecu.Name, Address = row.Pid }
            : new LiveTileDto
            {
                Source = LiveTileSource.Pid,
                Ecu = t.Ecu.Name,
                Mode = t.Pid!.Model.Mode,
                Address = t.Pid!.Model.Address,
            }).ToList();

    // Independent ECU selection for the setup window's PID/waveform panes.
    // The main window's Selected ECU inspector continues to track SelectedEcu;
    // the setup window's ECU list drives SetupSelectedEcu instead so picking
    // a different ECU there doesn't disturb the inspector the user is reading.
    public EcuViewModel? SetupSelectedEcu
    {
        get => setupSelectedEcu;
        set => SetField(ref setupSelectedEcu, value);
    }

    // Two-way bound by both Log-traffic checkboxes (Bus log tab + Download tab)
    // so toggling either pane stays in sync. Setter forwards to the static
    // gate inside MainWindow that AppendJ2534Log / AppendSimLog / AppendBusFrame consult on every
    // append.
    private bool isLoggingEnabled;
    public bool IsLoggingEnabled
    {
        get => isLoggingEnabled;
        set
        {
            if (SetField(ref isLoggingEnabled, value))
                MainWindow.SetLogTrafficEnabled(value);
        }
    }

    // Independent file-logging toggle. This is the persisted PREFERENCE -
    // the sink itself only runs between PassThruOpen and PassThruClose (see
    // MainWindow.OnHostSessionStarted / OnHostSessionEnded). Toggling on
    // with no host present arms the preference (status reads "Armed");
    // toggling on mid-session opens a fresh bus_yyyyMMdd_HHmmss.csv now;
    // toggling off mid-session closes the current capture.
    //
    // Coupled to the three sub-option toggles so the file log can't be armed
    // with nothing selected to log: turning it ON with all three off
    // auto-selects "Include bus traffic"; turning the last sub-option OFF
    // auto-turns this off.
    private bool isFileLoggingEnabled;
    public bool IsFileLoggingEnabled
    {
        get => isFileLoggingEnabled;
        set
        {
            // Turning ON with nothing selected to log - auto-enable bus traffic
            // so a future capture isn't empty. Has to happen before the sink
            // starts so the gate is in place by the time the first frame lands.
            if (value && !logIncludeJ2534Calls && !logIncludeBusTraffic && !logAppendDescriptionTag)
                LogIncludeBusTraffic = true;

            if (!SetField(ref isFileLoggingEnabled, value)) return;
            MainWindow.OnFileLoggingPreferenceChanged(value);
            OnPropertyChanged(nameof(FileLogStatus));
            PersistAppSettings();
        }
    }

    // "Include J2534 calls" - per-stream gate on the file sink only.
    // The textbox / Download tab mirror is unaffected.
    private bool logIncludeJ2534Calls = true;
    public bool LogIncludeJ2534Calls
    {
        get => logIncludeJ2534Calls;
        set
        {
            if (!SetField(ref logIncludeJ2534Calls, value)) return;
            MainWindow.SetIncludeJ2534FileLog(value);
            AutoDisableFileLogIfAllSubOptionsOff();
            PersistAppSettings();
        }
    }

    // "Include bus traffic" - per-stream gate on the file sink only.
    private bool logIncludeBusTraffic = true;
    public bool LogIncludeBusTraffic
    {
        get => logIncludeBusTraffic;
        set
        {
            if (!SetField(ref logIncludeBusTraffic, value)) return;
            MainWindow.SetIncludeBusFileLog(value);
            AutoDisableFileLogIfAllSubOptionsOff();
            PersistAppSettings();
        }
    }

    // "Append description tag" - flips VirtualBus.AnnotateFrames so each
    // bus log line gets a "  ; ServiceName ..." suffix produced by
    // Gmw3110Annotator. Affects BOTH file and textbox output because the
    // annotation is computed at the bus layer (one place; one truth).
    private bool logAppendDescriptionTag;
    public bool LogAppendDescriptionTag
    {
        get => logAppendDescriptionTag;
        set
        {
            if (!SetField(ref logAppendDescriptionTag, value)) return;
            bus.AnnotateFrames = value;
            AutoDisableFileLogIfAllSubOptionsOff();
            PersistAppSettings();
        }
    }

    // "Collapse bulk transfers" - flips VirtualBus.CollapseBulkTransfers so
    // long ISO-TP transfers get condensed in the bus log (first 3 + last 3
    // CFs visible, middle replaced with a single marker line). Affects both
    // file and textbox output. Does NOT participate in the auto-disable
    // rule for "Log to file" - this is a formatting modifier, not a stream
    // gate, and the file is still meaningful with this off and the other
    // options on.
    private bool logCollapseBulkTransfers;
    public bool LogCollapseBulkTransfers
    {
        get => logCollapseBulkTransfers;
        set
        {
            if (!SetField(ref logCollapseBulkTransfers, value)) return;
            bus.CollapseBulkTransfers = value;
            PersistAppSettings();
        }
    }

    // "Hide $3E" toolbar checkbox (next to Maximize on the bus log tab).
    // UI-only filter that drops TesterPresent traffic from the textboxes.
    // The file-log capture is intentionally untouched - this is a live-view
    // readability tweak, not a stream gate.
    private bool suppressTesterPresentInWindow;
    public bool SuppressTesterPresentInWindow
    {
        get => suppressTesterPresentInWindow;
        set
        {
            if (!SetField(ref suppressTesterPresentInWindow, value)) return;
            MainWindow.SetSuppressTesterPresentInWindow(value);
            PersistAppSettings();
        }
    }

    // ECU > "Drive HW $3E keepalives" menu item. When on, RequestDispatcher
    // creates a Timer for every PassThruStartPeriodicMsg registration so the
    // simulator ticks $3E on the host's behalf. Off accepts the registration
    // but skips the tick - delegating hosts have to maintain P3C themselves.
    // Stored in AppSettings (not SimulatorConfig) because it's a simulator-
    // wide helper preference, not part of any one ECU profile.
    private bool allowPeriodicTesterPresent;
    public bool AllowPeriodicTesterPresent
    {
        get => allowPeriodicTesterPresent;
        set
        {
            if (!SetField(ref allowPeriodicTesterPresent, value)) return;
            bus.AllowPeriodicTesterPresent = value;
            PersistAppSettings();
        }
    }

    // Called from each sub-option setter after the change lands. If the user
    // has just turned off the LAST of the three sub-options while file
    // logging is on, kill the master toggle - an empty log file is just
    // confusing.
    private void AutoDisableFileLogIfAllSubOptionsOff()
    {
        if (!isFileLoggingEnabled) return;
        if (logIncludeJ2534Calls || logIncludeBusTraffic || logAppendDescriptionTag) return;
        IsFileLoggingEnabled = false;
    }

    private void PersistAppSettings()
    {
        if (appSettings == null) return;
        appSettings.LogToFile                  = isFileLoggingEnabled;
        appSettings.LogIncludeJ2534Calls       = logIncludeJ2534Calls;
        appSettings.LogIncludeBusTraffic       = logIncludeBusTraffic;
        appSettings.LogAppendDescriptionTag    = logAppendDescriptionTag;
        appSettings.LogCollapseBulkTransfers   = logCollapseBulkTransfers;
        appSettings.AllowPeriodicTesterPresent = allowPeriodicTesterPresent;
        appSettings.LogSuppressTesterPresentInWindow = suppressTesterPresentInWindow;
        appSettings.Save();
    }

    /// <summary>Human-readable status of the file-logging sink. Refreshed by the 10 Hz UI timer.</summary>
    public string FileLogStatus
    {
        get
        {
            var sink = MainWindow.BusLog;
            if (sink.IsRunning)
            {
                double kb = sink.BytesWritten / 1024.0;
                return $"{System.IO.Path.GetFileName(sink.CurrentPath)} - {sink.LinesWritten:N0} lines, {kb:N1} KB";
            }
            if (isFileLoggingEnabled) return "Armed - waiting for host";
            return "Not logging";
        }
    }

    /// <summary>Called from MainWindow's 10 Hz refresh tick to update the live status text.</summary>
    public void RefreshFileLogStatus() => OnPropertyChanged(nameof(FileLogStatus));

    // Two-way bound by both Maximize checkboxes (Bus log tab + Download tab)
    // so the editor pane / tab-header collapse stays consistent regardless of
    // which tab the user toggled it from.
    private bool isMaximized;
    public bool IsMaximized
    {
        get => isMaximized;
        set
        {
            if (SetField(ref isMaximized, value))
                MainWindow.SetBusLogMaximized(value);
        }
    }

    public string? CurrentFilePath
    {
        get => currentFilePath;
        set { SetField(ref currentFilePath, value); OnPropertyChanged(nameof(WindowTitle)); }
    }

    public string WindowTitle => "GM ECU Simulator";

    public string StatusText
    {
        get => statusText;
        set => SetField(ref statusText, value);
    }

    public void Rebuild()
    {
        Ecus.Clear();
        foreach (var node in bus.Nodes)
        {
            var vm = new EcuViewModel(node);
            vm.BindBus(bus);
            Ecus.Add(vm);
        }
        SelectedEcu = Ecus.FirstOrDefault();
        SetupSelectedEcu = Ecus.FirstOrDefault();
        OnPropertyChanged(nameof(ShowsBinReplayTab));
        OnPropertyChanged(nameof(ShowsGlitchTab));
        OnPropertyChanged(nameof(ShowsCaptureTab));
        OnPropertyChanged(nameof(ShowsPrimeMenu));
        OnPropertyChanged(nameof(ShowsSecurityModuleReadOnly));
        OnPropertyChanged(nameof(IsEcuSimulatorMode));
        OnPropertyChanged(nameof(ShowsPidLiveGrid));
        OnPropertyChanged(nameof(ShowsProgrammingFields));
        OnPropertyChanged(nameof(ShowsSecurityPill));
        OnPropertyChanged(nameof(FormRowMaxHeight));
        OnPropertyChanged(nameof(PidRowMinHeight));

        // Reset the picker's source-ECU selection (the old instance was just
        // discarded) and re-evaluate the dashboard. A fresh config load hands
        // us pendingTileDescriptors to resolve; otherwise we reconcile the
        // existing tiles against the rebuilt ECU instances - rebinding tiles
        // whose PidViewModel was replaced (Load PIDs) and dropping orphans.
        pickerSelectedEcu = null;
        OnPropertyChanged(nameof(PickerSelectedEcu));
        OnPropertyChanged(nameof(ShowsPickerEcuDropdown));
        OnPropertyChanged(nameof(PickerCandidates));
        if (pendingTileDescriptors is { } pending)
        {
            pendingTileDescriptors = null;
            LoadTilesFromDescriptors(pending);
        }
        else
        {
            ReconcileTiles();
        }

        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>
    /// Convenience predicate: modes whose inspector behaves like the ECU
    /// editor (PID grid visible, Configure PIDs button, no programming-only
    /// fields). Currently ECU Simulator only - drives synthesised PID
    /// responses from waveforms; supports Mode22 / Mode2D / Mode1A / Mode1
    /// PIDs side-by-side via the per-PID PidMode dropdown.
    /// </summary>
    private bool IsPidEditorMode => currentMode is AppMode.EcuSimulator;

    /// <summary>Bin Replay tab shows in PID-editor modes.</summary>
    public bool ShowsBinReplayTab => IsPidEditorMode;
    /// <summary>Glitch tab shows in PID-editor modes.</summary>
    public bool ShowsGlitchTab    => IsPidEditorMode;
    /// <summary>Captures tab shows in DPS Simulator mode.</summary>
    public bool ShowsCaptureTab
        => currentMode is AppMode.DpsSimulator;
    /// <summary>
    /// Prime menu (Prime from DPS archive / Clear primed archive) is DPS-only.
    /// Priming creates a persona at $7E0 from a DPS programming archive, which
    /// only makes sense in a single-ECU DPS session - never in the multi-ECU
    /// editor (ECU Simulator). Hiding the menu in those modes prevents a stray
    /// prime from corrupting the persisted config file with a path that would
    /// resurrect a "removed" ECU on next launch.
    /// </summary>
    public bool ShowsPrimeMenu
        => currentMode is AppMode.DpsSimulator;

    /// <summary>
    /// Read-only display of the per-ECU security module in DPS Simulator
    /// mode. The dropdown is hidden there (Prime Wizard owns the choice), but
    /// the user still wants to see which algorithm got picked - so a read-only
    /// TextBox sits in the inspector with the same label position.
    /// </summary>
    public bool ShowsSecurityModuleReadOnly
        => currentMode is AppMode.DpsSimulator;

    /// <summary>
    /// Visibility gate for PID-editor-mode inspector actions (e.g. the per-
    /// ECU "Configure PIDs..." launcher). True in modes where the user
    /// defines/edits PIDs directly (ECU Simulator). In DPS Simulator mode
    /// the Prime Wizard / archive owns the PID list so the standalone editor
    /// isn't surfaced from the inspector. Property name retained for XAML-
    /// binding stability - it now reads "is this a PID-editor mode" rather
    /// than literally "is ECU Simulator".
    /// </summary>
    public bool IsEcuSimulatorMode => IsPidEditorMode;

    /// <summary>
    /// Visibility gate for the live PID DataGrid in the Selected ECU pane.
    /// Visible in ECU Simulator. Hidden in DPS Simulator:
    /// the primed PID set is an internal implementation detail of the prime
    /// pipeline (the solver synthesises bytecode-pinned $22 responses);
    /// surfacing it in the inspector implies user-tunable PIDs that don't
    /// exist in those modes.
    /// </summary>
    public bool ShowsPidLiveGrid => IsPidEditorMode;

    /// <summary>
    /// Visibility gate for programming-session-only inspector fields
    /// (FC.BS, Diag addr). Hidden in PID-editor modes where the user is
    /// driving PID/waveform behaviour rather than tuning the ISO-TP / SPS
    /// addressing the host uses during a programming flow.
    /// </summary>
    public bool ShowsProgrammingFields => !IsPidEditorMode;

    /// <summary>
    /// Visibility gate for the titlebar Security pill. Shown when an ECU is
    /// selected (the normal case in ECU Simulator) AND in DPS Simulator
    /// mode regardless of selection - that mode is about programming a
    /// single primed ECU, so the pill stays present even before the user
    /// primes one. The TextBlock falls back to "No ECU primed" when
    /// SelectedEcu is null.
    /// </summary>
    public bool ShowsSecurityPill
        => SelectedEcu != null
           || currentMode is AppMode.DpsSimulator;

    /// <summary>
    /// Cap on the Selected ECU inspector's form row when it is sized as a "*"
    /// row (i.e. when the PID grid is visible and competes for vertical
    /// space). Set to match the form WrapPanel's natural content height so
    /// there is no empty band when there's plenty of room, while still
    /// allowing the row to compress below natural height when the pane is
    /// tight - the form ScrollViewer's auto vertical scrollbar then takes
    /// over and only one row of fields is visible at the minimum pane size.
    /// PositiveInfinity in modes where the row is Auto-sized (PID hidden)
    /// so the cap doesn't matter.
    /// </summary>
    public double FormRowMaxHeight => currentMode switch
    {
        AppMode.EcuSimulator => 120.0,
        AppMode.DpsSimulator => 220.0,
        _                    => double.PositiveInfinity,
    };

    /// <summary>
    /// Floor for the PID row. 125 px = the PID Border header + DataGrid
    /// column-header row + 1 data row + padding. Set to 0 when the PID grid
    /// is hidden so the row collapses cleanly with its Collapsed child.
    /// </summary>
    public double PidRowMinHeight => ShowsPidLiveGrid ? 125.0 : 0.0;

    private void New()
    {
        bus.ReplaceNodes(Array.Empty<EcuNode>());
        Rebuild();
        CurrentFilePath = null;
        StatusText = "New empty configuration";
    }

    private void Open()
    {
        var settings = AppSettings.Load();
        var dlg = new OpenFileDialog
        {
            Filter = "Mode config (*.mode.json)|*.mode.json|JSON (*.json)|*.json|All files|*.*",
            InitialDirectory = AppSettings.ResolveInitialDir(settings.LastConfigDir),
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var cfg = ConfigStore.Load(dlg.FileName);
            ConfigStore.ApplyTo(cfg, bus);
            pendingTileDescriptors = cfg.LiveTiles;
            Rebuild();
            CurrentFilePath = dlg.FileName;
            // Restore PrimeArchivePath. Skip applying it inline - the user's
            // explicit Open should replace the bus, not stack a prime on top.
            // Auto-apply belongs to App.OnStartup.
            PrimeArchivePath = cfg.PrimeArchivePath;
            donorBinPath = cfg.DonorBinPath;
            PrimedDataset = null;
            StatusText = $"Loaded {Ecus.Count} ECU(s) from {dlg.FileName}";
            PersistLastConfigDir(settings, dlg.FileName);
        }
        catch (Exception ex) { Error("Open failed", ex); }
    }

    private void Save()
    {
        if (string.IsNullOrEmpty(CurrentFilePath)) { SaveAs(); return; }
        try
        {
            var cfg = ConfigStore.Snapshot(bus);
            cfg.PrimeArchivePath = PrimeArchivePath;
            cfg.DonorBinPath = donorBinPath;
            cfg.LiveTiles = SnapshotTileDescriptors();
            ConfigStore.Save(cfg, CurrentFilePath);
            StatusText = $"Saved to {CurrentFilePath}";
        }
        catch (Exception ex) { Error("Save failed", ex); }
    }

    private void SaveAs()
    {
        var settings = AppSettings.Load();
        var dlg = new SaveFileDialog
        {
            Filter = "Mode config (*.mode.json)|*.mode.json|JSON (*.json)|*.json|All files|*.*",
            DefaultExt = ".mode.json",
            FileName = currentMode.ConfigFileName(),
            InitialDirectory = AppSettings.ResolveInitialDir(settings.LastConfigDir),
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var cfg = ConfigStore.Snapshot(bus);
            cfg.PrimeArchivePath = PrimeArchivePath;
            cfg.DonorBinPath = donorBinPath;
            cfg.LiveTiles = SnapshotTileDescriptors();
            ConfigStore.Save(cfg, dlg.FileName);
            CurrentFilePath = dlg.FileName;
            StatusText = $"Saved to {dlg.FileName}";
            PersistLastConfigDir(settings, dlg.FileName);
        }
        catch (Exception ex) { Error("Save failed", ex); }
    }

    // Shared helper for the config dialogs (Open / SaveAs). Round-trips the
    // parent of the chosen file into settings.json
    // so the next session opens where the user was last working. Silent on
    // any I/O failure - the dialog already succeeded, persistence is a
    // nice-to-have.
    private static void PersistLastConfigDir(AppSettings settings, string chosenFile)
    {
        var dir = Path.GetDirectoryName(chosenFile);
        if (string.IsNullOrEmpty(dir)) return;
        settings.LastConfigDir = dir;
        settings.Save();
    }

    // Primary "Add ECU" action. Adds a blank, fully-functional default ECU - in the signal-centric model that's a
    // complete simulator ($01 J1979 catalogue + live signals + seeded identity). Importing real identity from a flash
    // readback is a separate, explicit step in the editor's Advanced card ("Load identity from bin..."), not forced at
    // creation time. DPS modes still short-circuit into the Prime wizard (the only path that yields a programming
    // persona). Binds to the sidebar button via AddEcuCommand.
    private void AddEcu()
    {
        // Single-ECU modes refuse the second add at the CanExecute layer; this guard catches a programmatic call.
        if (!CanAddEcu()) return;

        if (currentMode is AppMode.DpsSimulator)
        {
            PrimeFromArchive();
            return;
        }

        AddBlankEcu();
    }

    // "Add blank ECU" - a fresh default ECU with no bin. In the signal-centric model this is already a complete
    // simulator ($01 J1979 catalogue + live signals + seeded identity), so it's a first-class way to add an ECU, not
    // just a dev shortcut. Reached two ways: the Tools -> Developer menu (AddBlankEcuCommand), and as the fallback
    // when the sidebar Add's bin picker is cancelled (AddEcuFromBin above).
    private void AddBlankEcu()
    {
        if (!CanAddEcu()) return;

        // Pick the next OBD-II 11-bit pair: request $7E0+, USDT response
        // $7E8+ (= req + $08), UUDT response $5E8+. This is the convention
        // real GM vehicles use; GMW3110's $241/$641 examples are pedagogical.
        ushort req = 0x7E0;
        while (bus.FindByRequestId(req) != null && req < 0x7E8) req++;
        var node = new EcuNode
        {
            Name = $"ECU{Ecus.Count + 1}",
            PhysicalRequestCanId = req,
            UsdtResponseCanId = (ushort)(req + 0x008),
            UudtResponseCanId = (ushort)(req - 0x1F8),       // 0x7E0 -> 0x5E8
        };
        // Seed the baseline identity (VIN, ...) and the curated live $22 set before the view-model is built so both
        // the $1A and $22 editor sections are populated out of the box.
        EcuIdentitySeeder.Seed(node);
        EcuMode22Seeder.Seed(node);
        bus.AddNode(node);
        var vm = new EcuViewModel(node);
        vm.BindBus(bus);
        Ecus.Add(vm);
        SelectedEcu = vm;
        // Also focus the new ECU in the editor's independent selection so its PIDs and Name field target it
        // immediately (the editor's "+" lands here; without this the editor keeps showing the previously-selected ECU).
        SetupSelectedEcu = vm;
        StatusText = $"Added {node.Name} (blank)";
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    private void RemoveEcu() => RemoveEcuCore(SelectedEcu);
    private void RemoveSetupEcu() => RemoveEcuCore(SetupSelectedEcu);

    // Shared remove-with-confirmation. Used by the main window's "-" button
    // (operates on SelectedEcu) and by the PID Setup window's toolbar
    // "Remove" button (operates on SetupSelectedEcu). Either selection that
    // is non-null is removed from the shared bus + Ecus list, and the OPPOSITE
    // selection is fixed up if it pointed at the same VM.
    private void RemoveEcuCore(EcuViewModel? ecu)
    {
        if (ecu is null) return;
        var name = ecu.Name;

        // Confirm before removal - the ECU and its PIDs / waveforms are gone
        // immediately and any pending edits in inspector textboxes are lost.
        // ThemedMessageBox matches the rest of the app's chrome; using the
        // OS MessageBox here would jar visually.
        bool proceed = false;
        ThemedMessageBox.Show(
            Application.Current?.MainWindow,
            "Remove ECU",
            $"Remove ECU \"{name}\" from the bus? This discards its PIDs, " +
            "waveforms, and any unsaved inspector edits.",
            MessageBoxImage.Warning,
            new ThemedDialogButton("Cancel", isCancel: true),
            new ThemedDialogButton("Remove",
                onClick: () => proceed = true,
                isDefault: true));

        if (!proceed) return;

        bus.RemoveNode(ecu.Model);
        Ecus.Remove(ecu);
        // Keep both selections valid: whichever pointed at the removed VM
        // falls back to the first remaining (or null on empty).
        if (ReferenceEquals(SelectedEcu, ecu))      SelectedEcu      = Ecus.FirstOrDefault();
        if (ReferenceEquals(SetupSelectedEcu, ecu)) SetupSelectedEcu = Ecus.FirstOrDefault();
        StatusText = $"Removed {name}";
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    private void AddPid() => SelectedEcu?.AddPid();
    private void RemovePid() => SelectedEcu?.RemoveSelectedPid();
    private void AddSetupPid() => SetupSelectedEcu?.AddPid();
    private void RemoveSetupPid() => SetupSelectedEcu?.RemoveSelectedPid();

    // Save / Load a single ECU as a *.ecu.json file. The DTO is the same shape
    // ConfigSchema uses inside SimulatorConfig.Ecus (full EcuDto - CAN IDs,
    // glitch, security module + config, PIDs, etc.) so an ECU saved here is
    // a structural subset of a whole-config save. LastConfigDir is the right
    // anchor for both since the user thinks of ECU files as configs.
    private void SaveEcu()      => SaveEcuCore(SelectedEcu);
    private void SaveSetupEcu() => SaveEcuCore(SetupSelectedEcu);

    private void SaveEcuCore(EcuViewModel? ecu)
    {
        if (ecu is null) return;
        var settings = AppSettings.Load();
        var dlg = new SaveFileDialog
        {
            Filter = "ECU (*.ecu.json)|*.ecu.json|JSON (*.json)|*.json|All files|*.*",
            DefaultExt = ".ecu.json",
            FileName = $"{ecu.Name}.ecu.json",
            InitialDirectory = AppSettings.ResolveInitialDir(settings.LastConfigDir),
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var dto = Core.Persistence.ConfigStore.EcuDtoFrom(ecu.Model);
            var json = System.Text.Json.JsonSerializer.Serialize(dto,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(dlg.FileName, json);
            StatusText = $"Saved ECU '{ecu.Name}' to {Path.GetFileName(dlg.FileName)}";
            PersistLastConfigDir(settings, dlg.FileName);
        }
        catch (Exception ex)
        {
            StatusText = $"Save ECU failed: {ex.Message}";
        }
    }

    private void LoadEcu()
    {
        // CanAddEcu enforces the per-mode ECU count limit (single-ECU modes
        // refuse a second add). Surface that same gate here so the dropdown
        // entry disables when adding wouldn't be legal anyway.
        if (!CanAddEcu()) return;
        var settings = AppSettings.Load();
        var dlg = new OpenFileDialog
        {
            Filter = "ECU (*.ecu.json)|*.ecu.json|JSON (*.json)|*.json|All files|*.*",
            InitialDirectory = AppSettings.ResolveInitialDir(settings.LastConfigDir),
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var json = File.ReadAllText(dlg.FileName);
            var dto = System.Text.Json.JsonSerializer.Deserialize<Common.Persistence.EcuDto>(json);
            if (dto is null) { StatusText = "Load ECU: empty file"; return; }

            // CAN-ID collision check: if the bus already has a node on this
            // ECU's physical request id, suffix the new ECU's name + bump its
            // CAN IDs to the next free OBD-II pair so the load succeeds
            // non-destructively. Same convention AddEcu uses for fresh ECUs.
            var node = Core.Persistence.ConfigStore.EcuNodeFrom(dto);
            if (bus.FindByRequestId(node.PhysicalRequestCanId) is not null)
            {
                ushort req = 0x7E0;
                while (bus.FindByRequestId(req) != null && req < 0x7E8) req++;
                node.PhysicalRequestCanId = req;
                node.UsdtResponseCanId = (ushort)(req + 0x008);
                node.UudtResponseCanId = (ushort)(req - 0x1F8);
                node.Name = $"{node.Name} (loaded)";
            }
            bus.AddNode(node);
            var vm = new EcuViewModel(node);
            vm.BindBus(bus);
            Ecus.Add(vm);
            SelectedEcu = vm;
            StatusText = $"Loaded ECU '{node.Name}' from {Path.GetFileName(dlg.FileName)}";
            PersistLastConfigDir(settings, dlg.FileName);
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
        catch (Exception ex)
        {
            StatusText = $"Load ECU failed: {ex.Message}";
        }
    }

    // Save / Load only the PID list of the SetupSelectedEcu - a partial-config
    // export so a user can move PID + waveform definitions between ECUs / configs
    // without round-tripping the whole simulator config. JSON shape is a flat
    // List<PidDto>, the same shape ConfigSchema uses inside an EcuDto.
    private void SaveSetupPids()
    {
        if (SetupSelectedEcu is not { } ecu) return;
        var settings = AppSettings.Load();
        var dlg = new SaveFileDialog
        {
            Filter = "PID list (*.pids.json)|*.pids.json|JSON (*.json)|*.json|All files|*.*",
            DefaultExt = ".pids.json",
            FileName = $"{ecu.Name}.pids.json",
            InitialDirectory = AppSettings.ResolveInitialDir(settings.LastPidListDir),
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var pids = ecu.Pids.Select(p => Core.Persistence.ConfigStore.PidDtoFrom(p.Model)).ToList();
            var json = System.Text.Json.JsonSerializer.Serialize(pids, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(dlg.FileName, json);
            StatusText = $"Saved {pids.Count} PID(s) to {Path.GetFileName(dlg.FileName)}";
            PersistLastPidListDir(settings, dlg.FileName);
        }
        catch (Exception ex)
        {
            StatusText = $"Save PIDs failed: {ex.Message}";
        }
    }

    private void LoadSetupPids()
    {
        if (SetupSelectedEcu is not { } ecu) return;
        var settings = AppSettings.Load();
        var dlg = new OpenFileDialog
        {
            Filter = "PID list (*.pids.json)|*.pids.json|JSON (*.json)|*.json|All files|*.*",
            InitialDirectory = AppSettings.ResolveInitialDir(settings.LastPidListDir),
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var json = File.ReadAllText(dlg.FileName);
            var dtos = System.Text.Json.JsonSerializer.Deserialize<List<Common.Persistence.PidDto>>(json) ?? new();
            // Replace the ECU's PID list atomically. Existing PIDs are cleared
            // from both the model and the VM observable, then the loaded set
            // is appended through the same VM/model pipeline AddPid uses so
            // bindings/lookups stay consistent.
            ecu.ReplacePids(dtos.Select(Core.Persistence.ConfigStore.PidFrom));
            StatusText = $"Loaded {dtos.Count} PID(s) from {Path.GetFileName(dlg.FileName)}";
            PersistLastPidListDir(settings, dlg.FileName);
        }
        catch (Exception ex)
        {
            StatusText = $"Load PIDs failed: {ex.Message}";
        }
    }

    // Twin of PersistLastConfigDir for the Save/Load PIDs pair.
    private static void PersistLastPidListDir(AppSettings settings, string chosenFile)
    {
        var dir = Path.GetDirectoryName(chosenFile);
        if (string.IsNullOrEmpty(dir)) return;
        settings.LastPidListDir = dir;
        settings.Save();
    }

    /// <summary>
    /// ECU > Reset State menu handler. Power-cycles every ECU on the bus
    /// (spec-correct $20 ReturnToNormalMode teardown + full $27 re-lock).
    /// Replaces the per-tab Reset buttons removed alongside the Download
    /// and Security tabs.
    /// </summary>
    private void ResetState()
    {
        foreach (var ecu in Ecus)
            ecu.ResetEcuState(bus.Scheduler);
        StatusText = Ecus.Count == 1
            ? $"Reset state on {Ecus[0].Name}"
            : $"Reset state on {Ecus.Count} ECU(s)";
    }

    // Opens the ECU Editor as a modal dialog. ShowDialog blocks until the
    // user closes the editor, so the main window's inspector can't be
    // interleaved with PID/waveform edits - the editor owns the user's
    // attention for its lifetime. setupWindow is still tracked so a second
    // command invocation while one is already open re-focuses rather than
    // stacking copies (the field guards against that even on the modal path).
    private void OpenSetupWindow()
    {
        if (setupWindow != null)
        {
            if (setupWindow.WindowState == WindowState.Minimized)
                setupWindow.WindowState = WindowState.Normal;
            setupWindow.Activate();
            return;
        }
        setupWindow = new Views.SetupWindow
        {
            DataContext = this,
            Owner = Application.Current?.MainWindow,
        };
        setupWindow.Closed += (_, _) =>
        {
            setupWindow = null;
            // The editor may have deleted PIDs / ECUs or rebuilt a PID list
            // (Load PIDs swaps in fresh PidViewModel instances). Re-resolve the
            // dashboard against the current ECU set: tiles rebind to the new
            // instances, and any whose (Ecu, Mode, Address) no longer exists
            // are dropped.
            ReconcileTiles();
        };
        setupWindow.ShowDialog();
    }

    // Launches the setup window pre-pointed at the ECU passed in (via the
    // button's CommandParameter binding, usually the row's EcuViewModel). The
    // setup window has an independent SetupSelectedEcu so we explicitly seed
    // it - otherwise it would keep whatever ECU was last picked there, which
    // wouldn't match the inspector the user just clicked from.
    private bool CanConfigurePids(object? parameter)
        => parameter is EcuViewModel;

    private void ConfigurePids(object? parameter)
    {
        if (parameter is not EcuViewModel ecu) return;
        SetupSelectedEcu = ecu;
        OpenSetupWindow();
    }

    // ---------------- J2534 registry buttons ----------------
    //
    // Writing HKLM\SOFTWARE\PassThruSupport.04.04 needs admin. The simulator
    // runs unelevated, so we shell out to the existing PowerShell scripts
    // with Verb=runas - UAC prompts the user, the script does the registry
    // work, then exits. Reading the registry (the diagnostic / status check)
    // doesn't need elevation, so List.ps1 runs without UAC.

    private string j2534Status = "(checking…)";
    public string J2534Status
    {
        get => j2534Status;
        private set => SetField(ref j2534Status, value);
    }

    private string connectionStatus = "(checking…)";
    /// <summary>
    /// Status-pill text for the ACTIVE transport. In J2534 mode this mirrors
    /// <see cref="J2534Status"/> (registry-derived); in raw-CAN TCP mode it
    /// reports the gauge link state. Refreshed by the 10 Hz UI timer and after
    /// any transport change, so the keyword-driven StatusToBrush dot stays live.
    /// </summary>
    public string ConnectionStatus
    {
        get => connectionStatus;
        private set => SetField(ref connectionStatus, value);
    }

    /// <summary>
    /// Recomputes <see cref="ConnectionStatus"/> from the active transport.
    /// Safe to call on the UI timer.
    /// </summary>
    public void RefreshConnectionStatus()
    {
        if (currentConnection == ConnectionType.RawCanTcp)
        {
            ConnectionStatus =
                rawCanServer.IsConnected ? "Gauge link: connected"
              : rawCanServer.IsRunning  ? $"Gauge link: listening :{rawCanServer.Port}"
              :                           "Gauge link: stopped";
        }
        else
        {
            RefreshJ2534Status();
            ConnectionStatus = J2534Status;
        }
    }

    private static string ScriptPath(string name)
    {
        // The EXE lives at GmEcuSimulator/bin/Debug/net9.0-windows/. The
        // installer scripts are at <repo-root>/Installer/. Walk up looking
        // for the marker file (GM ECU Simulator.sln) rather than hard-coding
        // ../ hops, so this still works after a publish that flattens the layout.
        var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        for (int i = 0; i < 10 && dir != null; i++)
        {
            var marker = Path.Combine(dir, "GM ECU Simulator.sln");
            if (File.Exists(marker)) return Path.Combine(dir, "Installer", name);
            dir = Path.GetDirectoryName(dir);
        }
        // Fallback: assume installer beside the EXE (deployed-flat scenario).
        var exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        return Path.Combine(exeDir, "Installer", name);
    }

    // async void is only acceptable on event/command handlers - this is one.
    private async void RegisterJ2534()
    {
        StatusText = "Awaiting UAC approval...";
        var (ok, canceled, output, logPath) = await RunPwshAsync(ScriptPath("Register.ps1"));
        if (ok)
        {
            // Open the IPC pipe so hosts that just discovered us through
            // HKLM can actually connect. Start is idempotent.
            try { pipeServer.Start(); }
            catch (Exception ex) { bus.LogSim?.Invoke($"Pipe server Start after Register failed: {ex.Message}"); }
            StatusText = "Registered. Restart your J2534 host to pick up the new device.";
        }
        else if (canceled)
            StatusText = "Registration cancelled (UAC declined).";
        else
        {
            StatusText = $"Registration failed - log at {logPath}";
            ShowScriptFailureDialog("J2534 Registration failed", "Register.ps1", logPath, output);
        }
        RefreshConnectionStatus();
    }

    private async void UnregisterJ2534()
    {
        StatusText = "Awaiting UAC approval...";
        var (ok, canceled, output, logPath) = await RunPwshAsync(ScriptPath("Unregister.ps1"));
        if (ok)
        {
            // Tear the pipe down. Any host currently connected sees a broken
            // pipe on its next IPC frame and disconnects; new PassThruOpen
            // calls fail because nothing is listening. Without this the
            // host stayed connected and kept streaming data even after the
            // registry write claimed we were gone.
            try { await pipeServer.StopAsync(); }
            catch (Exception ex) { bus.LogSim?.Invoke($"Pipe server Stop after Unregister failed: {ex.Message}"); }
            StatusText = "Unregistered.";
        }
        else if (canceled)
            StatusText = "Unregister cancelled (UAC declined).";
        else
        {
            StatusText = $"Unregister failed - log at {logPath}";
            ShowScriptFailureDialog("J2534 Unregister failed", "Unregister.ps1", logPath, output);
        }
        RefreshConnectionStatus();
    }

    // Surfaces the captured PowerShell output in the same dialog used for
    // "Show registered devices", with a failure-flavoured title and the log
    // path included so the user can copy / share it.
    private static void ShowScriptFailureDialog(string title, string scriptName, string logPath, string output)
    {
        var win = new GmEcuSimulator.Views.RegisteredDevicesWindow(
            title: title,
            subtitle: title,
            description: $"Captured stdout/stderr from {scriptName}. A copy is saved at {logPath} - reference it when reporting the failure.",
            output: string.IsNullOrWhiteSpace(output)
                ? "(no output was captured - the script may have failed before Start-Transcript started)"
                : output)
        {
            Owner = Application.Current?.MainWindow,
        };
        win.ShowDialog();
    }

    private async void ShowRegisteredDevices()
    {
        StatusText = "Reading registry…";
        var output = await CapturePwshAsync(ScriptPath("List.ps1"));
        StatusText = "Ready";
        var win = new GmEcuSimulator.Views.RegisteredDevicesWindow(output)
        {
            Owner = Application.Current?.MainWindow,
        };
        win.ShowDialog();
    }

    // Force-cycle the named-pipe server without touching the registry.
    // Useful when a host left the pipe in a weird state (crashed mid-call,
    // detached without PassThruClose, etc.) and the simulator needs to drop
    // the stale connection so a fresh PassThruOpen can land.
    private async void ResetIpcPipe()
    {
        SetJ2534Busy(true);
        try
        {
            StatusText = "Resetting IPC pipe…";
            try { await pipeServer.StopAsync(); }
            catch (Exception ex) { bus.LogSim?.Invoke($"Pipe server Stop during reset failed: {ex.Message}"); }
            try { pipeServer.Start(); StatusText = "IPC pipe reset. Reconnect from your J2534 host."; }
            catch (Exception ex)
            {
                StatusText = $"IPC pipe restart failed: {ex.Message}";
                bus.LogSim?.Invoke($"Pipe server Start during reset failed: {ex.Message}");
            }
        }
        finally
        {
            SetJ2534Busy(false);
        }
    }

    private void SetJ2534Busy(bool busy)
    {
        j2534Busy = busy;
        // RelayCommand subscribes to CommandManager.RequerySuggested; nudge it.
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>
    /// Reads the registry (no elevation needed) and updates J2534Status.
    /// </summary>
    public void RefreshJ2534Status()
    {
        try
        {
            var s = J2534Registration.Check();
            // Pill text drops the bitness annotation when both shims are
            // registered (the normal case). When only one is registered we
            // surface the asymmetric state as a "fault" - 32-bit-only hosts
            // can't see a 64-only shim and vice versa, so the registration
            // is effectively broken for the missing-bitness side.
            J2534Status = (s.Has32, s.Has64) switch
            {
                (true, true)   => "Shim Registered",
                (true, false)  => "32-bit Shim Fault",
                (false, true)  => "64-bit Shim Fault",
                (false, false) => "Shim Not Registered",
            };
        }
        catch (Exception ex)
        {
            J2534Status = $"Status check failed: {ex.Message}";
        }
    }

    // Elevated PowerShell launch (UAC via Verb=runas).
    //
    // Verb=runas forces UseShellExecute=true, which precludes piping
    // stdout/stderr back through Process. The elevated console window opens,
    // runs the script, and closes - if the script throws, the error text
    // dies with the window and the user never sees it.
    //
    // Workaround: build a small PowerShell wrapper that opens a transcript
    // BEFORE invoking the target script, catches any terminating error to
    // write it into the transcript, then closes it. The transcript path is
    // chosen on this side (the unelevated app) so we always know where to
    // read after the process exits. Pass the wrapper via -EncodedCommand to
    // avoid any quoting nightmares.
    //
    // Returns:
    //   ok       - script reported exit 0
    //   canceled - user dismissed the UAC prompt
    //   output   - everything the elevated session printed (or "" if nothing)
    //   logPath  - on-disk copy of `output`, kept around for share / repro
    private async Task<(bool ok, bool canceled, string output, string logPath)> RunPwshAsync(string scriptPath)
    {
        SetJ2534Busy(true);

        // Stable per-run log file. Lives under LocalAppData so an admin
        // elevation in the same user session writes back where we can read it.
        // Co-located with the bus_*.csv files since both are user-facing
        // captures from this user's session.
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GmEcuSimulator", "logs", "bus logs");
        Directory.CreateDirectory(logDir);
        var scriptBase = Path.GetFileNameWithoutExtension(scriptPath);
        var logPath = Path.Combine(
            logDir,
            $"{scriptBase}-{DateTime.Now:yyyyMMdd-HHmmss}.log");

        // PowerShell wrapper. Single-quoted string literals so embedded '$' is
        // not interpolated by C#. Doubled single quotes inside literals are
        // PowerShell's escape for an embedded single quote.
        var psWrapper =
            $"Start-Transcript -Path '{logPath.Replace("'", "''")}' -Force | Out-Null; " +
            "$ec = 0; " +
            "try { " +
            $"  & '{scriptPath.Replace("'", "''")}'; " +
            "  if ($LASTEXITCODE -ne $null -and $LASTEXITCODE -ne 0) { $ec = $LASTEXITCODE } " +
            "} catch { " +
            "  Write-Host ('ERROR: ' + $_); " +
            "  Write-Host ('ScriptStackTrace:'); " +
            "  Write-Host $_.ScriptStackTrace; " +
            "  $ec = 1 " +
            "} finally { " +
            "  try { Stop-Transcript | Out-Null } catch { } " +
            "} " +
            "exit $ec";

        // EncodedCommand expects UTF-16LE base64. Sidesteps every quoting
        // gotcha that lives between cmd.exe, the runas verb, and PowerShell.
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(psWrapper));

        try
        {
            return await Task.Run(() =>
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}",
                        UseShellExecute = true,
                        Verb = "runas",
                    };
                    using var p = Process.Start(psi);
                    if (p == null) return (false, false, "", logPath);
                    p.WaitForExit();
                    var output = ReadLogSafe(logPath);
                    return (p.ExitCode == 0, false, output, logPath);
                }
                catch (Win32Exception wex) when (wex.NativeErrorCode == 1223)
                {
                    // ERROR_CANCELLED - user dismissed UAC. Nothing was logged.
                    return (false, true, "", logPath);
                }
                catch (Exception ex)
                {
                    Application.Current?.Dispatcher.Invoke(() =>
                        MessageBox.Show(ex.Message, "PowerShell launch failed", MessageBoxButton.OK, MessageBoxImage.Error));
                    return (false, false, ex.ToString(), logPath);
                }
            }).ConfigureAwait(true);
        }
        finally
        {
            SetJ2534Busy(false);
        }
    }

    // Tolerant log read - Start-Transcript can be slightly delayed flushing,
    // and the file may be missing entirely if powershell.exe crashed before
    // running our wrapper. Return what's there, blank if nothing.
    private static string ReadLogSafe(string path)
    {
        try
        {
            if (!File.Exists(path)) return "";
            return File.ReadAllText(path);
        }
        catch (Exception ex) { return $"(could not read log: {ex.Message})"; }
    }

    // Runs a script unelevated and captures stdout/stderr for display.
    // Reads both streams concurrently so a chatty stderr can't deadlock the
    // child against a full pipe buffer while we're stuck on stdout.
    private async Task<string> CapturePwshAsync(string scriptPath)
    {
        SetJ2534Busy(true);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            try
            {
                using var p = Process.Start(psi);
                if (p == null) return "(failed to launch powershell.exe)";
                var stdoutTask = p.StandardOutput.ReadToEndAsync();
                var stderrTask = p.StandardError.ReadToEndAsync();
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(true);
                await p.WaitForExitAsync().ConfigureAwait(true);
                var output = stdoutTask.Result;
                var err = stderrTask.Result;
                return string.IsNullOrWhiteSpace(err) ? output : output + "\n--- stderr ---\n" + err;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }
        finally
        {
            SetJ2534Busy(false);
        }
    }

    public void AutoSave()
    {
        // DPS modes are intentionally volatile - skip the write so the next
        // launch comes up clean. ECU Simulator persists normally.
        if (!currentMode.PersistsConfig()) return;
        try
        {
            var path = ConfigStore.PathForMode(currentMode);
            // While a bin is loaded the bus shows the bin-derived ECU set;
            // saving that would clobber the user's real config. Save the
            // prior-config snapshot instead so the on-disk file is unchanged
            // by a bin session.
            var cfgToSave = priorSnapshot ?? ConfigStore.Snapshot(bus, replay: replay);
            // Always carry through the current BinReplay settings.
            cfgToSave.BinReplay = ConfigStore.Snapshot(bus, replay: replay).BinReplay;
            cfgToSave.PrimeArchivePath = PrimeArchivePath;
            cfgToSave.DonorBinPath = donorBinPath;
            cfgToSave.LiveTiles = SnapshotTileDescriptors();
            ConfigStore.Save(cfgToSave, path);
        }
        catch
        {
            // Auto-save failure on shutdown shouldn't crash the app - the user's
            // explicit File>Save still works. A clean retry will save on next exit.
        }
    }

    // ---------------- Bin replay load / unload ----------------

    // Snapshot the current ECU set (so Unload can restore it), wipe the bus,
    // and rebuild from the bin's channel headers. Called by BinReplayViewModel
    // on Load button click. Idempotent if a bin was already loaded - the
    // existing snapshot is preserved (you don't want load-then-load to lose
    // the original config).
    private void OnBinReplayLoad(IBinSource source, string? path)
    {
        try
        {
            if (priorSnapshot == null)
                priorSnapshot = ConfigStore.Snapshot(bus, description: "pre-bin-replay snapshot");

            replay.Load(source, path);

            var headers = replay.ChannelHeaders;
            if (headers == null) { StatusText = "Bin loaded but no channels found"; return; }
            var build = BinChannelToPid.BuildEcus(headers, replay);
            bus.ReplaceNodes(build.Nodes);
            Rebuild();
            BinReplay.RebuildChannelGrid();
            StatusText = build.SkippedChannels == 0
                ? $"Bin loaded: {build.Nodes.Count} ECU(s), {headers.Count} channels"
                : $"Bin loaded: {build.Nodes.Count} ECU(s), {headers.Count - build.SkippedChannels} channels ({build.SkippedChannels} skipped)";
        }
        catch (Exception ex)
        {
            Error("Bin load failed", ex);
            replay.Unload();
            BinReplay.RebuildChannelGrid();
        }
    }

    private void OnBinReplayUnload()
    {
        try
        {
            replay.Unload();
            BinReplay.RebuildChannelGrid();
            if (priorSnapshot != null)
            {
                ConfigStore.ApplyTo(priorSnapshot, bus);
                priorSnapshot = null;
                Rebuild();
                StatusText = "Bin unloaded; prior ECU configuration restored";
            }
            else
            {
                StatusText = "Bin unloaded";
            }
        }
        catch (Exception ex) { Error("Bin unload failed", ex); }
    }

    public void RefreshBinReplayLive() => BinReplay.RefreshLive();

    // -------- Prime From Archive --------

    public string? PrimeArchivePath
    {
        get => primeArchivePath;
        private set
        {
            if (SetField(ref primeArchivePath, value))
            {
                OnPropertyChanged(nameof(PrimeArchiveDisplay));
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public PrimedDataset? PrimedDataset
    {
        get => primedDataset;
        private set
        {
            if (SetField(ref primedDataset, value))
                OnPropertyChanged(nameof(PrimeArchiveDisplay));
        }
    }

    public string PrimeArchiveDisplay => PrimedDataset?.Report.OneLineSummary()
        ?? (string.IsNullOrEmpty(PrimeArchivePath) ? "" : $"Primed (pending): {Path.GetFileName(PrimeArchivePath)}");

    // Invoked by the File menu. Opens the three-page wizard which drives the
    // entire prime flow (archive pick, Phase 3 review, commit). The wizard
    // mutates the bus directly on Apply, so we just have to refresh UI state
    // and capture context for re-entry via Edit prime...
    private void PrimeFromArchive()
    {
        // Defense in depth: the menu item is hidden in non-DPS modes via
        // ShowsPrimeMenu, but a stray keyboard shortcut or test harness call
        // must also be refused.
        if (!ShowsPrimeMenu) return;

        // In single-ECU modes, a fresh prime replaces the current bus
        // contents rather than appending. Avoids the bus accumulating
        // multiple primed personas from successive archive picks.
        if (!currentMode.AllowsMultipleEcus() && bus.Nodes.Count() > 0)
            bus.ReplaceNodes(Array.Empty<EcuNode>());

        var wizard = new Views.PrimeWizard.PrimeWizardWindow(bus)
        {
            Owner = Application.Current.MainWindow,
        };
        wizard.ShowDialog();

        if (wizard.CommittedNode is null || wizard.CommittedDataset is null) return;

        var dataset = wizard.CommittedDataset;
        var node = wizard.CommittedNode;
        PrimeArchivePath = wizard.Context.ArchivePath;
        donorBinPath = null;                  // donor concept dropped
        PrimedDataset = dataset;
        Rebuild();

        // Find the freshly-bound EcuViewModel and attach the wizard context
        // so the per-ECU "Edit prime..." button can re-open the wizard.
        var ecuVm = Ecus.FirstOrDefault(e => ReferenceEquals(e.Model, node));
        if (ecuVm is not null) ecuVm.AttachPrimeContext(wizard.Context);

        var reportPath = PrimeReportWriter.Write(node, dataset);
        MainWindow.AppendSimLog($"[prime] full report: {reportPath}");
        StatusText = $"{dataset.Report.OneLineSummary()}  |  report: {reportPath}";
    }

    private void ClearPrimeArchive()
    {
        PrimeArchivePath = null;
        donorBinPath = null;
        PrimedDataset = null;
        StatusText = "Prime cleared";
    }

    // Re-applied by App.OnStartup if the persisted config carried an archive
    // path. Silent on success; surfaces failure to the log only so a missing
    // archive does not block app startup. donorPath is accepted for back-compat
    // with older persisted configs but is ignored.
    public void TryAutoPrime(string archivePath, string? donorPath, Action<string> log)
    {
        _ = donorPath;   // intentionally ignored; donor concept dropped
        if (!File.Exists(archivePath))
        {
            log($"[prime] archive not found at startup: {archivePath} - clearing persisted path");
            PrimeArchivePath = null;
            donorBinPath = null;
            return;
        }
        try
        {
            var (node, dataset) = ArchivePrimer.ApplyTo(bus, archivePath);
            PrimeArchivePath = archivePath;
            donorBinPath = null;
            PrimedDataset = dataset;
            Rebuild();
            var reportPath = PrimeReportWriter.Write(node, dataset);
            log($"[prime] {dataset.Report.OneLineSummary()}");
            log($"[prime] full report: {reportPath}");
            StatusText = dataset.Report.OneLineSummary();
        }
        catch (Exception ex)
        {
            log($"[prime] auto-load failed: {ex.Message}");
        }
    }

    private static void Error(string title, Exception ex)
        => MessageBox.Show(ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
}

// One row in the live-tile picker. Carries the source ECU and exactly one of
// an editable PID row (Pid) or an $01 OBD-II row (Obd2), plus the
// "<pid id> - <friendly name>" label shown in the list. AddTileCommand
// consumes the selected entry to append a dashboard tile.
public sealed record PidPickerEntry(EcuViewModel Ecu, PidViewModel? Pid, J1979RowViewModel? Obd2, string Display);

// The mode buckets the picker can filter by. Obd2 maps to the built-in $01
// J1979 projection; the others map 1:1 to PidMode.
public enum PickerModeFilter { Obd2, Mode1A, Mode22, Mode2D }

// One entry in the picker's mode dropdown: the filter value + its display label.
public sealed record PickerModeOption(PickerModeFilter Filter, string Display);
