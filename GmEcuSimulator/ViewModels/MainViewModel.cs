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

namespace GmEcuSimulator.ViewModels;

public sealed class MainViewModel : NotifyPropertyChangedBase
{
    private readonly VirtualBus bus;
    private readonly BinReplayCoordinator replay;
    public ObservableCollection<EcuViewModel> Ecus { get; } = new();
    private EcuViewModel? selectedEcu;
    private string? currentFilePath;
    private string statusText = "Ready";
    private bool j2534Busy;

    // In-memory snapshot of the ECU set captured the moment a bin is loaded.
    // Restored on Unload so the user's pre-bin configuration comes back
    // intact. ecu_config.json on disk is never overwritten by load/unload.
    private SimulatorConfig? priorSnapshot;

    public BinReplayViewModel BinReplay { get; }

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
    public RelayCommand RegisterJ2534Command { get; }
    public RelayCommand UnregisterJ2534Command { get; }
    public RelayCommand ShowRegisteredDevicesCommand { get; }

    public MainViewModel(VirtualBus bus, BinReplayCoordinator replay)
    {
        this.bus = bus;
        this.replay = replay;
        BinReplay = new BinReplayViewModel(replay, bus, OnBinReplayLoad, OnBinReplayUnload);
        Rebuild();

        NewCommand                   = new RelayCommand(New);
        OpenCommand                  = new RelayCommand(Open);
        SaveCommand                  = new RelayCommand(Save);
        SaveAsCommand                = new RelayCommand(SaveAs);
        ImportCommand                = new RelayCommand(Import);
        ExportCommand                = new RelayCommand(Export);
        AddEcuCommand                = new RelayCommand(AddEcu);
        RemoveEcuCommand             = new RelayCommand(RemoveEcu, () => SelectedEcu != null);
        AddPidCommand                = new RelayCommand(AddPid,    () => SelectedEcu != null);
        RemovePidCommand             = new RelayCommand(RemovePid, () => SelectedEcu?.SelectedPid != null);
        RegisterJ2534Command         = new RelayCommand(RegisterJ2534,         () => !j2534Busy);
        UnregisterJ2534Command       = new RelayCommand(UnregisterJ2534,       () => !j2534Busy);
        ShowRegisteredDevicesCommand = new RelayCommand(ShowRegisteredDevices, () => !j2534Busy);

        RefreshJ2534Status();
    }

    public EcuViewModel? SelectedEcu
    {
        get => selectedEcu;
        set => SetField(ref selectedEcu, value);
    }

    public string? CurrentFilePath
    {
        get => currentFilePath;
        set { SetField(ref currentFilePath, value); OnPropertyChanged(nameof(WindowTitle)); }
    }

    public string WindowTitle => string.IsNullOrEmpty(CurrentFilePath)
        ? "GM ECU Simulator"
        : $"GM ECU Simulator — {Path.GetFileName(CurrentFilePath)}";

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

    // Import is identical to Open but does not change CurrentFilePath —
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

    private void AddPid() => SelectedEcu?.AddPid();
    private void RemovePid() => SelectedEcu?.RemoveSelectedPid();

    // ---------------- J2534 registry buttons ----------------
    //
    // Writing HKLM\SOFTWARE\PassThruSupport.04.04 needs admin. The simulator
    // runs unelevated, so we shell out to the existing PowerShell scripts
    // with Verb=runas — UAC prompts the user, the script does the registry
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

    // async void is only acceptable on event/command handlers — this is one.
    private async void RegisterJ2534()
    {
        StatusText = "Awaiting UAC approval…";
        var (ok, canceled) = await RunPwshAsync(ScriptPath("Register.ps1"));
        StatusText = ok        ? "Registered. Restart your J2534 host to pick up the new device."
                   : canceled  ? "Registration cancelled (UAC declined)."
                   :             "Registration failed — see the PowerShell window for details.";
        RefreshJ2534Status();
    }

    private async void UnregisterJ2534()
    {
        StatusText = "Awaiting UAC approval…";
        var (ok, canceled) = await RunPwshAsync(ScriptPath("Unregister.ps1"));
        StatusText = ok        ? "Unregistered."
                   : canceled  ? "Unregister cancelled (UAC declined)."
                   :             "Unregister failed — see the PowerShell window for details.";
        RefreshJ2534Status();
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
            const string key32 = @"SOFTWARE\WOW6432Node\PassThruSupport.04.04\GmEcuSim";
            const string key64 = @"SOFTWARE\PassThruSupport.04.04\GmEcuSim";
            using var k32 = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key32);
            using var k64 = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key64);
            var has32 = k32?.GetValue("FunctionLibrary") is string p32 && File.Exists(p32);
            var has64 = k64?.GetValue("FunctionLibrary") is string p64 && File.Exists(p64);

            J2534Status = (has32, has64) switch
            {
                (true, true) => "✓ Registered (32-bit + 64-bit)",
                (true, false) => "✓ Registered (32-bit only)",
                (false, true) => "✓ Registered (64-bit only)",
                (false, false) => "Not registered",
            };
        }
        catch (Exception ex)
        {
            J2534Status = $"Status check failed: {ex.Message}";
        }
    }

    // Elevated PowerShell launch (UAC via Verb=runas). The wait happens on a
    // background thread so the WPF UI keeps drawing while UAC and the script
    // run; the resulting Task continues back on the UI sync context.
    // Returns (ok, canceled): canceled=true means the user dismissed UAC.
    private async Task<(bool ok, bool canceled)> RunPwshAsync(string scriptPath)
    {
        SetJ2534Busy(true);
        try
        {
            return await Task.Run(() =>
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                        UseShellExecute = true,         // required for Verb=runas
                        Verb = "runas",
                    };
                    using var p = Process.Start(psi);
                    if (p == null) return (false, false);
                    p.WaitForExit();
                    return (p.ExitCode == 0, false);
                }
                catch (Win32Exception wex) when (wex.NativeErrorCode == 1223)
                {
                    // ERROR_CANCELLED — user dismissed UAC.
                    return (false, true);
                }
                catch (Exception ex)
                {
                    Application.Current?.Dispatcher.Invoke(() =>
                        MessageBox.Show(ex.Message, "PowerShell launch failed", MessageBoxButton.OK, MessageBoxImage.Error));
                    return (false, false);
                }
            }).ConfigureAwait(true);
        }
        finally
        {
            SetJ2534Busy(false);
        }
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
            // Auto-save failure on shutdown shouldn't crash the app — the user's
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
