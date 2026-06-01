using Common;
using Core.Bus;
using Core.Dps;
using Core.Persistence;
using Core.Replay;
using GMThemeManager;
using Microsoft.Extensions.DependencyInjection;
using Shim;
using Shim.Ipc;
using System.IO;
using System.Windows;

namespace GmEcuSimulator;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>Convenience accessor for the singleton VirtualBus.</summary>
    public static VirtualBus Bus => Services.GetRequiredService<VirtualBus>();
    private NamedPipeServer? pipeServer;
    private RawCanTcpServer? rawCanServer;
    private MainWindow? mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // DPS prime reports get their own folder under the shared logs/
        // parent. Sibling of logs/bus logs and logs/shim logs and
        // logs/captures, all four organised by output source rather than
        // dumped loose at the GmEcuSimulator root.
        var primeLogDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GmEcuSimulator", "logs", "death by dps");
        Directory.CreateDirectory(primeLogDir);
        PrimeReportWriter.LogDir = primeLogDir;

        // Install the active palette into Application.Resources BEFORE any
        // window is constructed - that way DynamicResource lookups in the
        // first MainWindow already resolve through the swap dictionary.
        // Without this the initial window picks up Theme.xaml's hard-coded
        // default and Apply() has no installed slot to replace.
        ThemeManager.RefreshAvailable();
        ThemeManager.InstallInitialPalette();

        var services = new ServiceCollection();
        services.AddSingleton<VirtualBus>();
        services.AddSingleton<BinReplayCoordinator>(sp =>
        {
            var b = sp.GetRequiredService<VirtualBus>();
            var coord = new BinReplayCoordinator(b);
            b.Replay = coord;
            return coord;
        });
        services.AddSingleton<NamedPipeServer>(sp =>
            new NamedPipeServer(sp.GetRequiredService<VirtualBus>(), s => GmEcuSimulator.MainWindow.AppendJ2534Log(s)));
        services.AddSingleton<RawCanTcpServer>(sp =>
            new RawCanTcpServer(sp.GetRequiredService<VirtualBus>(), RawCanTcpServer.DefaultPort,
                s => GmEcuSimulator.MainWindow.AppendJ2534Log(s)));
        Services = services.BuildServiceProvider();

        var bus = Services.GetRequiredService<VirtualBus>();
        var replay = Services.GetRequiredService<BinReplayCoordinator>();

        // Default the bootloader-capture directory. CaptureSettings leaves
        // it null in its constructor so unit tests don't write to disk; WPF
        // unconditionally points it at %LOCALAPPDATA%\GmEcuSimulator\logs\captures
        // so every programming session leaves a usable .bin trail.
        // ConfigStore.ApplyTo overwrites this if the loaded config carries
        // a user-set directory override.
        bus.Capture.CaptureDirectory = CaptureSettings.DefaultDirectory();

        // Frame-level Tx/Rx sink for the Bus log tab. AppendBusFrame is gated
        // by the "Log frame traffic" checkbox so DPID Fast-band streams don't
        // flood the textbox when nobody's watching. Two formats are delivered:
        // pretty (space-delimited) for the textbox, csv for the file.
        bus.LogFrame = (pretty, csv, isTp) => GmEcuSimulator.MainWindow.AppendBusFrame(pretty, csv, isTp);

        // Always-on diagnostic sinks. LogJ2534 handles events emitted from
        // the Shim/ project (PassThru* IPC narration, pipe lifecycle,
        // periodic register/unregister); LogSim handles simulator-internal
        // events (service handlers, security modules, scheduler, app
        // lifecycle). Both are low volume; never gated by the textbox flag.
        bus.LogJ2534 = s => GmEcuSimulator.MainWindow.AppendJ2534Log(s);
        bus.LogSim   = s => GmEcuSimulator.MainWindow.AppendSimLog(s);

        // High-prominence status sink — currently only rejected non-CAN
        // connect attempts. Routed to the status bar at the bottom of the
        // main window so a third-party tester user can see why their host
        // got ERR_INVALID_PROTOCOL_ID back from PassThruConnect.
        bus.OnStatusMessage = s => GmEcuSimulator.MainWindow.SetStatus(s);

        // Pick up the persisted mode now (separate from MainViewModel's full
        // AppSettings load - we only need Mode here to choose the right config
        // file). Mode defaults to EcuSimulator for fresh installs.
        var bootSettings = AppSettings.Load();
        var mode = bootSettings.Mode;
        var connection = bootSettings.ConnectionType;

        // Auto-load: per-mode config file. DPS modes are volatile-by-design
        // (clean state every launch), so the load path is skipped entirely and
        // the bus starts empty. Persistable modes load if a file exists; for
        // ECU Simulator first-run we fall back to the built-in default ECUs so
        // the user has something visible immediately.
        try
        {
            if (mode.PersistsConfig())
            {
                var path = ConfigStore.PathForMode(mode);
                if (File.Exists(path))
                {
                    var cfg = ConfigStore.Load(path);
                    ConfigStore.ApplyTo(cfg, bus);
                    // Backfill the curated identity + live $22 seed set onto the loaded ECUs. The load path rebuilds
                    // ECUs verbatim from the file and (unlike Add ECU / first-run defaults) never seeds, so a config
                    // saved before the curated $22 set existed shows none of the 12 signal-backed $22 PIDs. SeedDefaults
                    // is precedence-safe (existing DIDs win, primed ECUs skipped), so it only fills the gaps.
                    if (mode == AppMode.EcuSimulator) DefaultEcuConfig.SeedDefaults(bus);
                    if (cfg.BinReplay != null)
                    {
                        replay.LoopMode = cfg.BinReplay.LoopMode;
                        replay.PersistedAutoLoadOnStart = cfg.BinReplay.AutoLoadOnStart;
                        if (cfg.BinReplay.AutoLoadOnStart && !string.IsNullOrEmpty(cfg.BinReplay.FilePath))
                            GmEcuSimulator.MainWindow.AppendSimLog(
                                $"[bin-replay] AutoLoadOnStart={cfg.BinReplay.FilePath} - skipped (file loader not wired)");
                    }
                }
                else if (mode == AppMode.EcuSimulator)
                {
                    DefaultEcuConfig.ApplyIfEmpty(bus);
                }
            }
        }
        catch (Exception ex)
        {
            GmEcuSimulator.MainWindow.AppendSimLog($"Auto-load failed: {ex.Message}; reverting to defaults");
            if (mode == AppMode.EcuSimulator) DefaultEcuConfig.ApplyIfEmpty(bus);
        }

        bus.Scheduler.Start();
        bus.Ticker.Start();
        bus.IdleSupervisor.Start();

        pipeServer = Services.GetRequiredService<NamedPipeServer>();
        rawCanServer = Services.GetRequiredService<RawCanTcpServer>();

        // Exactly one transport is live at a time, chosen by the persisted
        // ConnectionType (selected as a sub-variant of the mode dropdown).
        // MainViewModel flips them when the user changes the selection.
        if (connection == ConnectionType.RawCanTcp)
        {
            // No registry gating: the gauge sim is hand-configured with the
            // port, nothing needs to discover us through HKLM.
            try { rawCanServer.Start(); }
            catch (Exception ex) { bus.LogSim?.Invoke($"Raw-CAN TCP listener failed to start: {ex.Message}"); }
        }
        else
        {
            // Bind the IPC pipe to registration state. If the shim DLL isn't
            // registered, no host can discover us through HKLM, so there's no
            // point listening - and crucially, if a previous run left the
            // shim registered and a host has it loaded, starting the pipe here
            // would let that host connect even though the user has since
            // unregistered. RegisterJ2534 / UnregisterJ2534 in MainViewModel
            // drive Start / StopAsync from the buttons.
            bool registered;
            try { registered = J2534Registration.Check().IsRegistered; }
            catch (Exception ex)
            {
                registered = false;
                bus.LogSim?.Invoke($"J2534 registration probe failed: {ex.Message}; assuming unregistered");
            }
            if (registered)
                pipeServer.Start();
            else
                bus.LogSim?.Invoke("J2534 not registered - IPC pipe not started; register from Tools to enable host connections");
        }

        mainWindow = new MainWindow();
        mainWindow.Bind(bus, replay, pipeServer, rawCanServer);

        // No startup auto-prime: priming is a DPS-mode operation, and DPS modes
        // do not PersistsConfig - they always start clean and the user re-primes
        // manually. A previous build auto-applied any cfg.PrimeArchivePath found
        // in persistable-mode config files, which is precisely how a stray prime
        // in ECU Simulator mode used to resurrect a "removed" ECU on next launch.
        // The MainViewModel mode-load path (ChangeMode) now scrubs stale prime
        // paths from non-DPS configs on load, so a corrupt file self-heals on
        // the next save.

        mainWindow.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        // Auto-save the current bus to the LocalAppData config so the next
        // launch picks up the user's edits. Use the stored mainWindow reference
        // rather than Current.MainWindow - WPF nulls the latter before OnExit fires.
        mainWindow?.AutoSave();

        // Flush + close the file log if it's active. Stop() drains the writer's
        // pending queue, writes a trailer, and disposes the StreamWriter so the
        // last few hundred lines of a download don't get lost on shutdown.
        GmEcuSimulator.MainWindow.BusLog.Stop();

        // Async path is mandatory here: NamedPipeServer is IAsyncDisposable
        // (no IDisposable). ServiceProvider.Dispose() walks its singletons
        // and throws InvalidOperationException the moment it hits one that
        // is IAsyncDisposable but not IDisposable. DisposeAsync handles both
        // shapes correctly. The container disposes singletons in reverse
        // construction order, which fans out cleanly to NamedPipeServer's
        // StopAsync (idempotent) and BinReplayCoordinator.Dispose().
        if (Services is IAsyncDisposable dAsync)
            await dAsync.DisposeAsync();
        else if (Services is IDisposable d)
            d.Dispose();
        base.OnExit(e);
    }
}
