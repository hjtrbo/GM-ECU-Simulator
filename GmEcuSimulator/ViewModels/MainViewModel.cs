using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using Common;
using Common.Persistence;
using Core.Bus;
using Core.Dps;
using Core.Ecu;
using Core.Persistence;
using Core.Replay;
using GmEcuSimulator.Views;
using Microsoft.Win32;
using Shim;
using Shim.Ipc;

namespace GmEcuSimulator.ViewModels;

public sealed class MainViewModel : NotifyPropertyChangedBase
{
    private readonly VirtualBus bus;
    private readonly BinReplayCoordinator replay;
    private readonly NamedPipeServer pipeServer;
    public ObservableCollection<EcuViewModel> Ecus { get; } = new();
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
    public RelayCommand ImportCommand { get; }
    public RelayCommand ExportCommand { get; }
    public RelayCommand AddEcuCommand { get; }
    public RelayCommand RemoveEcuCommand { get; }
    public RelayCommand AddPidCommand { get; }
    public RelayCommand RemovePidCommand { get; }
    public RelayCommand AddSetupPidCommand { get; }
    public RelayCommand RemoveSetupPidCommand { get; }
    public RelayCommand OpenSetupWindowCommand { get; }
    public RelayCommand ConfigurePidsCommand { get; }
    public RelayCommand SavePidsCommand { get; }
    public RelayCommand LoadPidsCommand { get; }
    public RelayCommand ResetStateCommand { get; }
    public RelayCommand RegisterJ2534Command { get; }
    public RelayCommand UnregisterJ2534Command { get; }
    public RelayCommand ShowRegisteredDevicesCommand { get; }
    public RelayCommand ResetIpcPipeCommand { get; }
    public RelayCommand PrimeFromArchiveCommand { get; }
    public RelayCommand ClearPrimeArchiveCommand { get; }

    /// <summary>
    /// All five top-level modes in declaration order. Bound to the mode
    /// selector ComboBox at the top of the main window.
    /// </summary>
    public IReadOnlyList<AppMode> AvailableModes { get; } = new[]
    {
        AppMode.EcuSimulator,
        AppMode.DpsWrite,
        AppMode.DpsRead,
        AppMode.FlashToolWrite,
        AppMode.FlashToolRead,
    };

    public MainViewModel(VirtualBus bus, BinReplayCoordinator replay, NamedPipeServer pipeServer)
    {
        this.bus = bus;
        this.replay = replay;
        this.pipeServer = pipeServer;

        BinReplay = new BinReplayViewModel(replay, bus, OnBinReplayLoad, OnBinReplayUnload);
        Capture = new CaptureViewModel(bus.Capture);

        // Hydrate UI preferences. The setters fan out to the static gates in
        // MainWindow + bus.AnnotateFrames so behaviour matches the persisted
        // choices before any frame flows.
        appSettings = AppSettings.Load();
        currentMode                  = appSettings.Mode;
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

        // Rebuild after currentMode is set so ECUs inherit the right
        // visibility flags from the get-go.
        Rebuild();

        NewCommand                   = new RelayCommand(New);
        OpenCommand                  = new RelayCommand(Open);
        SaveCommand                  = new RelayCommand(Save);
        SaveAsCommand                = new RelayCommand(SaveAs);
        ImportCommand                = new RelayCommand(Import);
        ExportCommand                = new RelayCommand(Export);
        AddEcuCommand                = new RelayCommand(AddEcu, CanAddEcu);
        RemoveEcuCommand             = new RelayCommand(RemoveEcu, () => SelectedEcu != null);
        AddPidCommand                = new RelayCommand(AddPid,    () => SelectedEcu != null);
        RemovePidCommand             = new RelayCommand(RemovePid, () => SelectedEcu?.SelectedPid != null);
        AddSetupPidCommand           = new RelayCommand(AddSetupPid,    () => SetupSelectedEcu != null);
        RemoveSetupPidCommand        = new RelayCommand(RemoveSetupPid, () => SetupSelectedEcu?.SelectedPid != null);
        OpenSetupWindowCommand       = new RelayCommand(OpenSetupWindow);
        ConfigurePidsCommand         = new RelayCommand(ConfigurePids, CanConfigurePids);
        SavePidsCommand              = new RelayCommand(SaveSetupPids, () => SetupSelectedEcu != null && SetupSelectedEcu.Pids.Count > 0);
        LoadPidsCommand              = new RelayCommand(LoadSetupPids, () => SetupSelectedEcu != null);
        ResetStateCommand            = new RelayCommand(ResetState, () => Ecus.Count > 0);
        RegisterJ2534Command         = new RelayCommand(RegisterJ2534,         () => !j2534Busy);
        UnregisterJ2534Command       = new RelayCommand(UnregisterJ2534,       () => !j2534Busy);
        ShowRegisteredDevicesCommand = new RelayCommand(ShowRegisteredDevices, () => !j2534Busy);
        ResetIpcPipeCommand           = new RelayCommand(ResetIpcPipe,          () => !j2534Busy);
        PrimeFromArchiveCommand       = new RelayCommand(PrimeFromArchive);
        ClearPrimeArchiveCommand      = new RelayCommand(ClearPrimeArchive, () => !string.IsNullOrEmpty(primeArchivePath));

        RefreshJ2534Status();
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

    /// <summary>
    /// Top-level operational mode. Bound two-way to the mode selector
    /// ComboBox above the menu. Setter delegates to <see cref="ChangeMode"/>
    /// so the dialog + ECU clear + per-mode config swap all run together.
    /// Cancelling the dialog restores the prior selection via this setter.
    /// </summary>
    public AppMode CurrentMode
    {
        get => currentMode;
        set
        {
            if (currentMode == value) return;
            // Suppress reentrancy while we may snap the value back on cancel.
            if (modeSwitchInProgress) return;
            if (!ChangeMode(value))
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
    /// Drives the mode transition: optionally save current state, clear the
    /// bus, persist the new mode, then load the new mode's config (if any).
    /// Returns false when the user cancels - the caller restores the
    /// previous selection in the bound control.
    /// </summary>
    private bool ChangeMode(AppMode newMode)
    {
        var oldMode = currentMode;
        bool hasEcus = Ecus.Count > 0;

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
                    $"Switching from {oldMode.DisplayName()} to {newMode.DisplayName()} will clear " +
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
                                Filter = "JSON config (*.json)|*.json",
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
                    $"Switching from {oldMode.DisplayName()} will clear the current ECU. " +
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
        appSettings.Mode = newMode;
        try { appSettings.Save(); } catch { /* persistence best-effort */ }

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
                    // Priming is DPS-only. If a persisted non-DPS config carries
                    // a stale primeArchivePath (e.g. from a pre-gating session
                    // where a user primed into ECU Simulator mode), drop it on
                    // load so the next save scrubs the field from disk.
                    bool canPrime = newMode is AppMode.DpsWrite or AppMode.DpsRead;
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
        StatusText = $"Mode: {newMode.DisplayName()}";
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        return true;
    }

    private SimulatorConfig SnapshotForSave()
    {
        var cfg = priorSnapshot ?? ConfigStore.Snapshot(bus, replay: replay);
        cfg.PrimeArchivePath = PrimeArchivePath;
        cfg.DonorBinPath = donorBinPath;
        return cfg;
    }

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

    public string WindowTitle => string.IsNullOrEmpty(CurrentFilePath)
        ? "GM ECU Simulator"
        : $"GM ECU Simulator - {Path.GetFileName(CurrentFilePath)}";

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
        OnPropertyChanged(nameof(ShowsSecurityModuleField));
        OnPropertyChanged(nameof(ShowsSecurityModuleReadOnly));
        OnPropertyChanged(nameof(IsEcuSimulatorMode));
        OnPropertyChanged(nameof(ShowsPidLiveGrid));
        OnPropertyChanged(nameof(ShowsProgrammingFields));
        OnPropertyChanged(nameof(ShowsSecurityPill));
        OnPropertyChanged(nameof(FormRowMaxHeight));
        OnPropertyChanged(nameof(PidRowMinHeight));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Bin Replay tab is ECU-Simulator-only.</summary>
    public bool ShowsBinReplayTab => currentMode == AppMode.EcuSimulator;
    /// <summary>Glitch tab is ECU-Simulator-only.</summary>
    public bool ShowsGlitchTab    => currentMode == AppMode.EcuSimulator;
    /// <summary>Captures tab shows in DPS and Flash Tool modes.</summary>
    public bool ShowsCaptureTab
        => currentMode is AppMode.DpsWrite or AppMode.DpsRead
                       or AppMode.FlashToolWrite or AppMode.FlashToolRead;
    /// <summary>
    /// Prime menu (Prime from DPS archive / Clear primed archive) is DPS-only.
    /// Priming creates a persona at $7E0 from a DPS programming archive, which
    /// only makes sense in a single-ECU DPS session - never in the multi-ECU
    /// editor (ECU Simulator) or in Flash Tool readback modes. Hiding the menu
    /// in those modes prevents a stray prime from corrupting the persisted
    /// config file with a path that would resurrect a "removed" ECU on next
    /// launch.
    /// </summary>
    public bool ShowsPrimeMenu
        => currentMode is AppMode.DpsWrite or AppMode.DpsRead;

    /// <summary>
    /// Per-ECU security-module dropdown in the inspector. Shown only in the
    /// Flash Tool modes. Hidden in DPS modes because the security module is
    /// owned by the Prime Wizard there (chosen from the archive's algo
    /// metadata, edited on Page 3), and the inspector dropdown would let the
    /// user clobber that choice mid-session. Hidden in ECU Simulator mode
    /// because the user-facing simulator persona doesn't depend on $27 key
    /// math; the field clutters the inspector without driving any behaviour
    /// users exercise from that mode.
    /// </summary>
    public bool ShowsSecurityModuleField
        => currentMode is AppMode.FlashToolWrite or AppMode.FlashToolRead;

    /// <summary>
    /// Read-only display of the per-ECU security module in DPS Write / DPS Read
    /// modes. The dropdown is hidden there (Prime Wizard owns the choice), but
    /// the user still wants to see which algorithm got picked - so a read-only
    /// TextBox sits in the inspector with the same label position.
    /// </summary>
    public bool ShowsSecurityModuleReadOnly
        => currentMode is AppMode.DpsWrite or AppMode.DpsRead;

    /// <summary>
    /// Visibility gate for ECU-Simulator-mode-only inspector actions
    /// (e.g. the per-ECU "Configure PIDs..." launcher in the Selected ECU
    /// pane). In DPS / Flash Tool modes the Prime Wizard / archive owns the
    /// PID list so the standalone editor isn't surfaced from the inspector.
    /// </summary>
    public bool IsEcuSimulatorMode => currentMode == AppMode.EcuSimulator;

    /// <summary>
    /// Visibility gate for the live PID DataGrid in the Selected ECU pane.
    /// Visible only in ECU Simulator mode. Hidden in:
    /// - Flash Tool Write: flashing is a one-way data push, no PID responses.
    /// - DPS Write / DPS Read: the primed PID set is an internal implementation
    ///   detail of the prime pipeline (the solver synthesises bytecode-pinned
    ///   $22 responses); surfacing it in the inspector implies user-tunable
    ///   PIDs that don't exist in those modes.
    /// - Flash Tool Read: same reasoning - PID list is not a user-facing
    ///   configuration surface in flash-readback flows.
    /// </summary>
    public bool ShowsPidLiveGrid => currentMode == AppMode.EcuSimulator;

    /// <summary>
    /// Visibility gate for programming-session-only inspector fields
    /// (FC.BS, Diag addr). Hidden in ECU Simulator mode where the user is
    /// driving PID/waveform behaviour rather than tuning the ISO-TP / SPS
    /// addressing the host uses during a programming flow.
    /// </summary>
    public bool ShowsProgrammingFields => currentMode != AppMode.EcuSimulator;

    /// <summary>
    /// Visibility gate for the titlebar Security pill. Shown when an ECU is
    /// selected (the normal case in ECU Simulator + Flash Tool modes) AND in
    /// DPS Write / DPS Read modes regardless of selection - those modes are
    /// about programming a single primed ECU, so the pill stays present even
    /// before the user primes one. The TextBlock falls back to "No ECU primed"
    /// when SelectedEcu is null.
    /// </summary>
    public bool ShowsSecurityPill
        => SelectedEcu != null
           || currentMode is AppMode.DpsWrite or AppMode.DpsRead;

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
        AppMode.EcuSimulator                => 120.0,
        AppMode.DpsWrite or AppMode.DpsRead => 220.0,
        _                                   => double.PositiveInfinity,
    };

    /// <summary>
    /// Floor for the PID row. 125 px = the PID Border header + DataGrid
    /// column-header row + 1 data row + padding. Set to 0 when the PID grid
    /// is hidden (Flash Tool Write) so the row collapses cleanly with its
    /// Collapsed child.
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
            Filter = "JSON config (*.json)|*.json|All files|*.*",
            InitialDirectory = AppSettings.ResolveInitialDir(settings.LastConfigDir),
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var cfg = ConfigStore.Load(dlg.FileName);
            ConfigStore.ApplyTo(cfg, bus);
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
            Filter = "JSON config (*.json)|*.json",
            FileName = currentMode.ConfigFileName(),
            InitialDirectory = AppSettings.ResolveInitialDir(settings.LastConfigDir),
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var cfg = ConfigStore.Snapshot(bus);
            cfg.PrimeArchivePath = PrimeArchivePath;
            cfg.DonorBinPath = donorBinPath;
            ConfigStore.Save(cfg, dlg.FileName);
            CurrentFilePath = dlg.FileName;
            StatusText = $"Saved to {dlg.FileName}";
            PersistLastConfigDir(settings, dlg.FileName);
        }
        catch (Exception ex) { Error("Save failed", ex); }
    }

    // Import is identical to Open but does not change CurrentFilePath -
    // intent is "merge a profile in" without committing the working file.
    // For now we replace the bus state; future work could MERGE instead.
    private void Import()
    {
        var settings = AppSettings.Load();
        var dlg = new OpenFileDialog
        {
            Filter = "JSON config (*.json)|*.json|All files|*.*",
            InitialDirectory = AppSettings.ResolveInitialDir(settings.LastConfigDir),
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var cfg = ConfigStore.Load(dlg.FileName);
            ConfigStore.ApplyTo(cfg, bus);
            Rebuild();
            StatusText = $"Imported {Ecus.Count} ECU(s) from {dlg.FileName}";
            PersistLastConfigDir(settings, dlg.FileName);
        }
        catch (Exception ex) { Error("Import failed", ex); }
    }

    private void Export()
    {
        var settings = AppSettings.Load();
        var dlg = new SaveFileDialog
        {
            Filter = "JSON config (*.json)|*.json",
            FileName = "ecu_config_export.json",
            InitialDirectory = AppSettings.ResolveInitialDir(settings.LastConfigDir),
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            ConfigStore.Save(ConfigStore.Snapshot(bus), dlg.FileName);
            StatusText = $"Exported to {dlg.FileName}";
            PersistLastConfigDir(settings, dlg.FileName);
        }
        catch (Exception ex) { Error("Export failed", ex); }
    }

    // Shared helper for the four config dialogs (Open / SaveAs / Import /
    // Export). Round-trips the parent of the chosen file into settings.json
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

    private void AddEcu()
    {
        // Single-ECU modes refuse the second add at the CanExecute layer;
        // this guard catches a programmatic invocation that bypassed it.
        if (!CanAddEcu()) return;

        // DPS modes don't add blank ECUs - the only path that yields a useful
        // persona is the Prime wizard, so route Add straight into it.
        if (currentMode is AppMode.DpsWrite or AppMode.DpsRead)
        {
            PrimeFromArchive();
            return;
        }

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
            UudtResponseCanId = (ushort)(req - 0x1F8),       // 0x7E0 → 0x5E8
        };
        bus.AddNode(node);
        var vm = new EcuViewModel(node);
        vm.BindBus(bus);
        Ecus.Add(vm);
        SelectedEcu = vm;
        StatusText = $"Added {node.Name}";
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    private void RemoveEcu()
    {
        if (SelectedEcu == null) return;
        var name = SelectedEcu.Name;
        bus.RemoveNode(SelectedEcu.Model);
        Ecus.Remove(SelectedEcu);
        SelectedEcu = Ecus.FirstOrDefault();
        StatusText = $"Removed {name}";
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    private void AddPid() => SelectedEcu?.AddPid();
    private void RemovePid() => SelectedEcu?.RemoveSelectedPid();
    private void AddSetupPid() => SetupSelectedEcu?.AddPid();
    private void RemoveSetupPid() => SetupSelectedEcu?.RemoveSelectedPid();

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

    // Opens (or re-focuses) the modeless setup window. Modeless so the user
    // can keep the main window's Selected ECU inspector visible while editing
    // PIDs / waveforms next to it. A single-instance check brings the existing
    // window forward instead of stacking copies.
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
        setupWindow.Closed += (_, _) => setupWindow = null;
        setupWindow.Show();
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
        RefreshJ2534Status();
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
        RefreshJ2534Status();
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
            J2534Status = (s.Has32, s.Has64) switch
            {
                (true, true) => "Registered (32-bit + 64-bit)",
                (true, false) => "Registered (32-bit only)",
                (false, true) => "Registered (64-bit only)",
                (false, false) => "Not registered",
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
        // launch comes up clean. Flash-Tool and ECU Simulator persist normally.
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
