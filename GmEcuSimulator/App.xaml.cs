using System.IO;
using System.Windows;
using Core.Bus;
using Core.Ipc;
using Core.Persistence;
using Core.Replay;
using Microsoft.Extensions.DependencyInjection;

namespace GmEcuSimulator;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    private NamedPipeServer? pipeServer;
    private MainWindow? mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
        // flood the textbox when nobody's watching.
        bus.LogFrame = s => GmEcuSimulator.MainWindow.AppendBusFrame(s);

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
        pipeServer.Start();

        mainWindow = new MainWindow();
        mainWindow.Bind(bus, replay);
        mainWindow.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        // Auto-save the current bus to the LocalAppData config so the next
        // launch picks up the user's edits. Use the stored mainWindow reference
        // rather than Current.MainWindow — WPF nulls the latter before OnExit fires.
        mainWindow?.AutoSave();

        if (pipeServer != null) await pipeServer.DisposeAsync();
        if (Services is IDisposable d) d.Dispose();
        base.OnExit(e);
    }
}
