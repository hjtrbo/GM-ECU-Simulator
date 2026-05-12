using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Core.Bus;
using GmEcuSimulator.ViewModels;

namespace GmEcuSimulator;

public partial class MainWindow : Window
{
    private static MainWindow? instance;
    private MainViewModel? vm;
    private DispatcherTimer? refreshTimer;

    // Updated from the checkbox's Checked/Unchecked handlers; read every frame
    // by AppendBusFrame off arbitrary threads (IPC worker, DPID timer).
    // Marked volatile so the read sees the last write without a lock.
    private static volatile bool logTraffic;

    public MainWindow()
    {
        InitializeComponent();
        instance = this;
        // Ctrl+N / Ctrl+O / Ctrl+S / Ctrl+Shift+S keyboard shortcuts.
        InputBindings.Add(new KeyBinding { Key = Key.N, Modifiers = ModifierKeys.Control,
            Command = new RelayCommand(() => vm?.NewCommand.Execute(null)) });
        InputBindings.Add(new KeyBinding { Key = Key.O, Modifiers = ModifierKeys.Control,
            Command = new RelayCommand(() => vm?.OpenCommand.Execute(null)) });
        InputBindings.Add(new KeyBinding { Key = Key.S, Modifiers = ModifierKeys.Control,
            Command = new RelayCommand(() => vm?.SaveCommand.Execute(null)) });
        InputBindings.Add(new KeyBinding { Key = Key.S, Modifiers = ModifierKeys.Control | ModifierKeys.Shift,
            Command = new RelayCommand(() => vm?.SaveAsCommand.Execute(null)) });
    }

    // General-purpose log sink. Routed to the RIGHT pane (IpcLogBox) — used
    // for control-plane events: pipe connect/disconnect, J2534 calls received
    // by the shim, [periodic] register/unregister diagnostics, auto-load
    // failures, etc. Gated by the master "Log traffic" checkbox so neither
    // pane fills up while the user isn't watching.
    public static void AppendLog(string line)
    {
        if (!logTraffic) return;
        Append(instance?.IpcLogBox, line);
    }

    // Frame-level traffic sink → LEFT pane (LogBox). Same master gate as
    // AppendLog — the checkbox controls both panes together.
    public static void AppendBusFrame(string line)
    {
        if (!logTraffic) return;
        Append(instance?.LogBox, line);
    }

    // High-prominence status update routed to the bottom status bar. Used for
    // events the user should see without having to look at any log pane —
    // currently just rejected non-CAN connect attempts. Always shown, not
    // gated by Log traffic.
    public static void SetStatus(string line)
    {
        var w = instance;
        if (w == null) return;
        if (!w.Dispatcher.CheckAccess())
        {
            w.Dispatcher.BeginInvoke(() => SetStatus(line));
            return;
        }
        if (w.vm != null) w.vm.StatusText = line;
    }

    private static void Append(System.Windows.Controls.TextBox? box, string line)
    {
        if (box == null) return;
        var w = instance;
        if (w == null) return;
        if (!w.Dispatcher.CheckAccess())
        {
            w.Dispatcher.BeginInvoke(() => Append(box, line));
            return;
        }
        box.AppendText($"[{DateTime.Now:HH:mm:ss.fff}] {line}{Environment.NewLine}");
        box.ScrollToEnd();
    }

    private void OnLogTrafficChecked(object sender, RoutedEventArgs e)   => logTraffic = true;
    private void OnLogTrafficUnchecked(object sender, RoutedEventArgs e) => logTraffic = false;

    // Maximize the bus log: collapse the editor row, expand the log row.
    private void OnMaximizeBusLogChecked(object sender, RoutedEventArgs e)
    {
        EditorRow.Height = new System.Windows.GridLength(0);
        LogRow.Height    = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star);
    }

    private void OnMaximizeBusLogUnchecked(object sender, RoutedEventArgs e)
    {
        EditorRow.Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star);
        LogRow.Height    = new System.Windows.GridLength(220);
    }

    private void OnClearLogClicked(object sender, RoutedEventArgs e)
    {
        LogBox.Clear();
        IpcLogBox.Clear();
    }

    public void Bind(VirtualBus bus, Core.Replay.BinReplayCoordinator replay)
    {
        vm = new MainViewModel(bus, replay);
        DataContext = vm;
        bus.NodesChanged += (_, _) => Dispatcher.BeginInvoke(() => vm?.Rebuild());

        refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100),    // 10 Hz live values
        };
        refreshTimer.Tick += (_, _) =>
        {
            if (vm == null) return;
            double now = bus.NowMs;
            foreach (var ecu in vm.Ecus)
                foreach (var pid in ecu.Pids)
                    pid.RefreshLive(now);
            vm.RefreshBinReplayLive();
        };
        refreshTimer.Start();
    }

    public void AutoSave() => vm?.AutoSave();

    private void OnExitClicked(object sender, RoutedEventArgs e) => Close();
}
