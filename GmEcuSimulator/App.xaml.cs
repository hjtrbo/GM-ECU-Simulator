using System.IO;
using System.Windows;
using Core.Bus;
using Core.Persistence;
using Core.Replay;
using GMThemeManager;
using Microsoft.Extensions.DependencyInjection;
using Shim;
using Shim.Ipc;

namespace GmEcuSimulator;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    private NamedPipeServer? pipeServer;
    private MainWindow? mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
            new NamedPipeServer(sp.GetRequiredService<VirtualBus>(), s => GmEcuSimulator.MainWindow.AppendLog(s)));
        Services = services.BuildServiceProvider();

        var bus = Services.GetRequiredService<VirtualBus>();
        var replay = Services.GetRequiredService<BinReplayCoordinator>();

        // Frame-level Tx/Rx sink for the Bus log tab. AppendBusFrame is gated
        // by the "Log frame traffic" checkbox so DPID Fast-band streams don't
        // flood the textbox when nobody's watching. Two formats are delivered:
        // pretty (space-delimited) for the textbox, csv for the file.
        bus.LogFrame = (pretty, csv) => GmEcuSimulator.MainWindow.AppendBusFrame(pretty, csv);

        // Always-on diagnostic sink — control-plane events (periodic
        // register/unregister, etc.). Low volume; never gated.
        bus.LogDiagnostic = s => GmEcuSimulator.MainWindow.AppendLog(s);

        // High-prominence status sink — currently only rejected non-CAN
        // connect attempts. Routed to the status bar at the bottom of the
        // main window so a third-party tester user can see why their host
        // got ERR_INVALID_PROTOCOL_ID back from PassThruConnect.
        bus.OnStatusMessage = s => GmEcuSimulator.MainWindow.SetStatus(s);

        // Auto-load: if the user has a saved config in LocalAppData, hydrate the
        // bus from it; otherwise fall back to the built-in default ECUs.
        try
        {
            var path = ConfigStore.DefaultPath;
            if (File.Exists(path))
            {
                var cfg = ConfigStore.Load(path);
                ConfigStore.ApplyTo(cfg, bus);
                if (cfg.BinReplay != null)
                {
                    replay.LoopMode = cfg.BinReplay.LoopMode;
                    replay.PersistedAutoLoadOnStart = cfg.BinReplay.AutoLoadOnStart;
                    // Auto-loading the actual bin file is gated on the
                    // BinaryWorker reference — log an info message until then
                    // so the user knows their AutoLoadOnStart preference
                    // survived but the load itself is queued.
                    if (cfg.BinReplay.AutoLoadOnStart && !string.IsNullOrEmpty(cfg.BinReplay.FilePath))
                        GmEcuSimulator.MainWindow.AppendLog(
                            $"[bin-replay] AutoLoadOnStart={cfg.BinReplay.FilePath} - skipped (file loader not wired)");
                }
            }
            else DefaultEcuConfig.ApplyIfEmpty(bus);
        }
        catch (Exception ex)
        {
            GmEcuSimulator.MainWindow.AppendLog($"Auto-load failed: {ex.Message}; reverting to defaults");
            DefaultEcuConfig.ApplyIfEmpty(bus);
        }

        bus.Scheduler.Start();
        bus.Ticker.Start();
        bus.IdleSupervisor.Start();

        pipeServer = Services.GetRequiredService<NamedPipeServer>();

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
            bus.LogDiagnostic?.Invoke($"J2534 registration probe failed: {ex.Message}; assuming unregistered");
        }
        if (registered)
            pipeServer.Start();
        else
            bus.LogDiagnostic?.Invoke("J2534 not registered - IPC pipe not started; register from Tools to enable host connections");

        mainWindow = new MainWindow();
        mainWindow.Bind(bus, replay, pipeServer);
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
        GmEcuSimulator.MainWindow.FileLog.Stop();

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
