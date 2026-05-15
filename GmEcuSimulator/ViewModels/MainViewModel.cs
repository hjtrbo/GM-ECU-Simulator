using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using Common.Persistence;
using Core.Bus;
using Core.Ecu;
using Core.Persistence;
using Core.Replay;
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

    // Per-user UI preferences loaded from %LOCALAPPDATA%\GmEcuSimulator\settings.json.
    // Each Log-menu checkbox setter writes back through SaveAppSettings() so the
    // user's choices survive across restarts.
    private readonly AppSettings appSettings;

    // In-memory snapshot of the ECU set captured the moment a bin is loaded.
    // Restored on Unload so the user's pre-bin configuration comes back
    // intact. ecu_config.json on disk is never overwritten by load/unload.
    private SimulatorConfig? priorSnapshot;

    public BinReplayViewModel BinReplay { get; }
    public DownloadWorkspaceViewModel DownloadWorkspace { get; }
    public CaptureBootloaderViewModel CaptureBootloader { get; }

    public RelayCommand NewCommand { get; }
    public RelayCommand OpenCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand SaveAsCommand { get; }
    public RelayCommand ImportCommand { get; }
    public RelayCommand ExportCommand { get; }
    public RelayCommand AddEcuCommand { get; }
    public RelayCommand RemoveEcuCommand { get; }
    public RelayCommand LoadEcuFromBinCommand { get; }
    public RelayCommand AddPidCommand { get; }
    public RelayCommand RemovePidCommand { get; }
    public RelayCommand AddSetupPidCommand { get; }
    public RelayCommand RemoveSetupPidCommand { get; }
    public RelayCommand OpenSetupWindowCommand { get; }
    public RelayCommand ResetSecurityCommand { get; }
    public RelayCommand RegisterJ2534Command { get; }
    public RelayCommand UnregisterJ2534Command { get; }
    public RelayCommand ShowRegisteredDevicesCommand { get; }

    public MainViewModel(VirtualBus bus, BinReplayCoordinator replay, NamedPipeServer pipeServer)
    {
        this.bus = bus;
        this.replay = replay;
        this.pipeServer = pipeServer;
        BinReplay = new BinReplayViewModel(replay, bus, OnBinReplayLoad, OnBinReplayUnload);
        DownloadWorkspace = new DownloadWorkspaceViewModel(bus.Scheduler);
        CaptureBootloader = new CaptureBootloaderViewModel(bus.Capture);
        Rebuild();

        // Hydrate UI preferences. The setters fan out to the static gates in
        // MainWindow + bus.AnnotateFrames so behaviour matches the persisted
        // choices before any frame flows.
        appSettings = AppSettings.Load();
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

        NewCommand                   = new RelayCommand(New);
        OpenCommand                  = new RelayCommand(Open);
        SaveCommand                  = new RelayCommand(Save);
        SaveAsCommand                = new RelayCommand(SaveAs);
        ImportCommand                = new RelayCommand(Import);
        ExportCommand                = new RelayCommand(Export);
        AddEcuCommand                = new RelayCommand(AddEcu);
        RemoveEcuCommand             = new RelayCommand(RemoveEcu, () => SelectedEcu != null);
        LoadEcuFromBinCommand        = new RelayCommand(LoadEcuFromBin, () => SelectedEcu != null);
        AddPidCommand                = new RelayCommand(AddPid,    () => SelectedEcu != null);
        RemovePidCommand             = new RelayCommand(RemovePid, () => SelectedEcu?.SelectedPid != null);
        AddSetupPidCommand           = new RelayCommand(AddSetupPid,    () => SetupSelectedEcu != null);
        RemoveSetupPidCommand        = new RelayCommand(RemoveSetupPid, () => SetupSelectedEcu?.SelectedPid != null);
        OpenSetupWindowCommand       = new RelayCommand(OpenSetupWindow);
        ResetSecurityCommand         = new RelayCommand(ResetSecurity, () => SelectedEcu != null);
        RegisterJ2534Command         = new RelayCommand(RegisterJ2534,         () => !j2534Busy);
        UnregisterJ2534Command       = new RelayCommand(UnregisterJ2534,       () => !j2534Busy);
        ShowRegisteredDevicesCommand = new RelayCommand(ShowRegisteredDevices, () => !j2534Busy);

        RefreshJ2534Status();
    }

    public EcuViewModel? SelectedEcu
    {
        get => selectedEcu;
        set
        {
            if (SetField(ref selectedEcu, value))
                DownloadWorkspace.Ecu = value;
        }
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
    // gate inside MainWindow that AppendLog / AppendBusFrame consult on every
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
            var sink = MainWindow.FileLog;
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
        foreach (var node in bus.Nodes) Ecus.Add(new EcuViewModel(node));
        SelectedEcu = Ecus.FirstOrDefault();
        SetupSelectedEcu = Ecus.FirstOrDefault();
    }

    private void New()
    {
        bus.ReplaceNodes(Array.Empty<EcuNode>());
        Rebuild();
        CurrentFilePath = null;
        StatusText = "New empty configuration";
    }

    private void Open()
    {
        var dlg = new OpenFileDialog { Filter = "JSON config (*.json)|*.json|All files|*.*" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var cfg = ConfigStore.Load(dlg.FileName);
            ConfigStore.ApplyTo(cfg, bus);
            Rebuild();
            CurrentFilePath = dlg.FileName;
            StatusText = $"Loaded {Ecus.Count} ECU(s) from {dlg.FileName}";
        }
        catch (Exception ex) { Error("Open failed", ex); }
    }

    private void Save()
    {
        if (string.IsNullOrEmpty(CurrentFilePath)) { SaveAs(); return; }
        try
        {
            ConfigStore.Save(ConfigStore.Snapshot(bus), CurrentFilePath);
            StatusText = $"Saved to {CurrentFilePath}";
        }
        catch (Exception ex) { Error("Save failed", ex); }
    }

    private void SaveAs()
    {
        var dlg = new SaveFileDialog
        {
            Filter = "JSON config (*.json)|*.json",
            FileName = "ecu_config.json",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            ConfigStore.Save(ConfigStore.Snapshot(bus), dlg.FileName);
            CurrentFilePath = dlg.FileName;
            StatusText = $"Saved to {dlg.FileName}";
        }
        catch (Exception ex) { Error("Save failed", ex); }
    }

    // Import is identical to Open but does not change CurrentFilePath -
    // intent is "merge a profile in" without committing the working file.
    // For now we replace the bus state; future work could MERGE instead.
    private void Import()
    {
        var dlg = new OpenFileDialog { Filter = "JSON config (*.json)|*.json|All files|*.*" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var cfg = ConfigStore.Load(dlg.FileName);
            ConfigStore.ApplyTo(cfg, bus);
            Rebuild();
            StatusText = $"Imported {Ecus.Count} ECU(s) from {dlg.FileName}";
        }
        catch (Exception ex) { Error("Import failed", ex); }
    }

    private void Export()
    {
        var dlg = new SaveFileDialog
        {
            Filter = "JSON config (*.json)|*.json",
            FileName = "ecu_config_export.json",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            ConfigStore.Save(ConfigStore.Snapshot(bus), dlg.FileName);
            StatusText = $"Exported to {dlg.FileName}";
        }
        catch (Exception ex) { Error("Export failed", ex); }
    }

    private void AddEcu()
    {
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
        Ecus.Add(vm);
        SelectedEcu = vm;
        StatusText = $"Added {node.Name}";
    }

    private void RemoveEcu()
    {
        if (SelectedEcu == null) return;
        var name = SelectedEcu.Name;
        bus.RemoveNode(SelectedEcu.Model);
        Ecus.Remove(SelectedEcu);
        SelectedEcu = Ecus.FirstOrDefault();
        StatusText = $"Removed {name}";
    }

    // Scaffold for "ECU > Load from BIN...". Surfaces an OpenFileDialog
    // filtered to .bin and hands the path off to the (future) extractor.
    // The extractor itself is a TODO: it needs to locate and decode the
    // identity DIDs already exposed on EcuViewModel:
    //   $90 VIN                         (17 ASCII bytes)
    //   $92 Supplier HW number          (ASCII)
    //   $98 Supplier HW version         (ASCII)
    //   $C1 End-model part number       (ASCII)
    //   $C2 Base-model part number      (ASCII)
    //   $CC ECU diagnostic address      (one hex byte)
    // Each one round-trips through EcuNode.SetIdentifier(did, bytes), so
    // once the bin parser knows where to look the wiring at this end is
    // a per-DID SetIdentifier call followed by OnPropertyChanged() raises
    // for the bound fields. Until that lands, the picker just reports the
    // file size in the status bar so the menu wiring is exercisable.
    private void LoadEcuFromBin()
    {
        if (SelectedEcu == null) return;
        var dlg = new OpenFileDialog
        {
            Title = $"Load BIN into {SelectedEcu.Name}",
            Filter = "ECU flash dump (*.bin)|*.bin|All files|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var info = new FileInfo(dlg.FileName);

            // TODO: replace with the real DID extractor. The shape it needs:
            //   var dids = BinDidExtractor.Extract(dlg.FileName);
            //   foreach (var (id, bytes) in dids)
            //       SelectedEcu.Model.SetIdentifier(id, bytes);
            //   then raise PropertyChanged for Vin/SupplierHardwareNumber/etc.
            // so the inspector textboxes refresh.

            StatusText =
                $"BIN picker scaffold: would extract DIDs from {info.Name} ({info.Length:N0} bytes) into {SelectedEcu.Name}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Load BIN", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AddPid() => SelectedEcu?.AddPid();
    private void RemovePid() => SelectedEcu?.RemoveSelectedPid();
    private void AddSetupPid() => SetupSelectedEcu?.AddPid();
    private void RemoveSetupPid() => SetupSelectedEcu?.RemoveSelectedPid();
    private void ResetSecurity() => SelectedEcu?.ResetSecurityState();

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
            catch (Exception ex) { bus.LogDiagnostic?.Invoke($"Pipe server Start after Register failed: {ex.Message}"); }
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
            catch (Exception ex) { bus.LogDiagnostic?.Invoke($"Pipe server Stop after Unregister failed: {ex.Message}"); }
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
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GmEcuSimulator", "logs");
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
        try
        {
            var path = ConfigStore.DefaultPath;
            // While a bin is loaded the bus shows the bin-derived ECU set;
            // saving that would clobber the user's real config. Save the
            // prior-config snapshot instead so ecu_config.json is unchanged
            // by a bin session.
            var cfgToSave = priorSnapshot ?? ConfigStore.Snapshot(bus, replay: replay);
            // Always carry through the current BinReplay settings.
            cfgToSave.BinReplay = ConfigStore.Snapshot(bus, replay: replay).BinReplay;
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

    private static void Error(string title, Exception ex)
        => MessageBox.Show(ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
}
