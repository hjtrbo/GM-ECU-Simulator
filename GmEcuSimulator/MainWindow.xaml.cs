using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Core.Bus;
using GmEcuSimulator.Theming;
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

        // Swap the maximize / restore glyph when WindowState flips.
        StateChanged += (_, _) => UpdateMaximizeIcon();

        // Populate the View > Theme submenu from ThemeManager and keep its
        // check-marks in sync when the active palette changes.
        ThemeManager.RefreshAvailable();
        RebuildThemeMenu();
        ThemeManager.PaletteChanged += _ => Dispatcher.BeginInvoke(RebuildThemeMenu);
    }

    // Rebuilds View > Theme. Each palette is an IsCheckable item; selecting
    // one calls ThemeManager.Apply, which mutates the live brushes - we don't
    // need to do anything else to refresh the UI. Palettes are grouped by
    // Category (Dark / Mid / Light / User) with a small caption header.
    private void RebuildThemeMenu()
    {
        ThemeMenu.Items.Clear();

        var groups = ThemeManager.AvailablePalettes
            .GroupBy(p => p.Category)
            .OrderBy(g =>
            {
                var i = Array.IndexOf(ThemeManager.CategoryOrder, g.Key);
                return i >= 0 ? i : ThemeManager.CategoryOrder.Length;
            });

        var firstGroup = true;
        foreach (var group in groups)
        {
            if (!firstGroup) ThemeMenu.Items.Add(new Separator());
            firstGroup = false;

            ThemeMenu.Items.Add(MakeCategoryHeader(group.Key));

            foreach (var p in group)
            {
                var mi = new MenuItem
                {
                    Header = p.IsUser ? $"{p.DisplayName}  (user)" : p.DisplayName,
                    IsCheckable = true,
                    IsChecked = string.Equals(p.Name, ThemeManager.ActivePalette, StringComparison.OrdinalIgnoreCase),
                    StaysOpenOnClick = false,
                };
                var paletteName = p.Name;
                mi.Click += (_, _) => ThemeManager.Apply(paletteName);
                ThemeMenu.Items.Add(mi);
            }
        }

        ThemeMenu.Items.Add(new Separator());

        var openFolder = new MenuItem { Header = "Open palettes folder…" };
        openFolder.Click += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = ThemeManager.UserPaletteDirectory,
                    UseShellExecute = true,
                });
            }
            catch { /* explorer launch failure is non-fatal */ }
        };
        ThemeMenu.Items.Add(openFolder);

        var reload = new MenuItem { Header = "Reload palettes from disk" };
        reload.Click += (_, _) =>
        {
            ThemeManager.RefreshAvailable();
            RebuildThemeMenu();
        };
        ThemeMenu.Items.Add(reload);
    }

    // Non-interactive caption row inside View > Theme. IsHitTestVisible=false
    // means mouse never reaches it so the MenuItem hover trigger never fires.
    private static MenuItem MakeCategoryHeader(string text)
    {
        var caption = new TextBlock
        {
            Text = text.ToUpperInvariant(),
            FontWeight = FontWeights.SemiBold,
            FontSize = 10,
            Opacity = 0.55,
        };
        return new MenuItem
        {
            Header = caption,
            IsHitTestVisible = false,
            Focusable = false,
            StaysOpenOnClick = true,
        };
    }

    // ---- Custom-chrome window controls ----
    //
    // The XAML titlebar replaces the OS caption buttons with three templated
    // Buttons that route through these handlers. Keeping them in code-behind
    // (rather than using SystemCommands) means the buttons stay testable via
    // x:Name in the designer and we don't depend on the WindowChrome command
    // bindings that some Win10/Win11 versions render inconsistently.

    private void OnMinimizeClicked(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void OnMaximizeClicked(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseWindowClicked(object sender, RoutedEventArgs e) => Close();

    private void UpdateMaximizeIcon()
    {
        if (MaxIcon == null) return;
        var key = WindowState == WindowState.Maximized ? "Icon.Restore" : "Icon.Maximize";
        if (TryFindResource(key) is Geometry geom) MaxIcon.Data = geom;
    }

    // General-purpose log sink. Routed to the RIGHT pane (IpcLogBox) - used
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
    // AppendLog - the checkbox controls both panes together.
    public static void AppendBusFrame(string line)
    {
        if (!logTraffic) return;
        Append(instance?.LogBox, line);
    }

    // High-prominence status update routed to the bottom status bar. Used for
    // events the user should see without having to look at any log pane -
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

    // Maximize the bus log: collapse the editor row AND the surrounding
    // chrome (outer 14-px margin, GridSplitter) so the tabbed workspace
    // goes edge-to-edge against the menu bar and side walls. Without
    // hiding those, a ~28-px gap remained at the top even with
    // EditorRow.Height=0.
    // EditorRow.MinHeight=270 in XAML so the user can't drag the splitter
    // up and squash the editor cards. Maximize bypasses that floor by
    // zeroing MinHeight here; the Unchecked path restores both.
    private const double EditorMinHeightNormal = 290;

    private void OnMaximizeBusLogChecked(object sender, RoutedEventArgs e)
    {
        EditorRow.MinHeight    = 0;
        EditorRow.Height       = new System.Windows.GridLength(0);
        LogRow.Height          = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star);
        EditorSplitter.Visibility = Visibility.Collapsed;
        MainContentGrid.Margin = new Thickness(0);
        SetTabHeaderVisible(Visibility.Collapsed);
    }

    private void OnMaximizeBusLogUnchecked(object sender, RoutedEventArgs e)
    {
        EditorRow.MinHeight    = EditorMinHeightNormal;
        EditorRow.Height       = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star);
        LogRow.Height          = new System.Windows.GridLength(320);
        EditorSplitter.Visibility = Visibility.Visible;
        MainContentGrid.Margin = new Thickness(14);
        SetTabHeaderVisible(Visibility.Visible);
    }

    // The TabControl's tab strip is the named Border (PART_HeaderHost) at
    // Grid.Row=0 inside the templated TabControl. Walking the template by
    // name keeps the maximize behaviour independent of any reshuffling
    // of the Border's children.
    private void SetTabHeaderVisible(Visibility v)
    {
        if (BusLogTabControl.Template?.FindName("PART_HeaderHost", BusLogTabControl) is FrameworkElement hdr)
            hdr.Visibility = v;
    }

    private void OnClearLogClicked(object sender, RoutedEventArgs e)
    {
        LogBox.Clear();
        IpcLogBox.Clear();
    }

    // Double-click on the splitter between CAN frames and J2534 calls panes
    // restores the 50/50 split. After a drag, the column widths land at e.g.
    // "1.4*" / "0.6*" - resetting both to plain "1*" puts the divider back
    // in the middle.
    private void OnBusLogSplitterDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var oneStar = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star);
        LogBoxColumn.Width    = oneStar;
        IpcLogBoxColumn.Width = oneStar;
        e.Handled = true;
    }

    public void Bind(VirtualBus bus, Core.Replay.BinReplayCoordinator replay, Core.Ipc.NamedPipeServer pipeServer)
    {
        vm = new MainViewModel(bus, replay, pipeServer);
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
            long nowLong = (long)now;
            foreach (var ecu in vm.Ecus)
            {
                foreach (var pid in ecu.Pids)
                    pid.RefreshLive(now);
                ecu.RefreshSecurity(nowLong);
            }
            vm.RefreshBinReplayLive();
        };
        refreshTimer.Start();
    }

    public void AutoSave() => vm?.AutoSave();

    private void OnExitClicked(object sender, RoutedEventArgs e) => Close();
}
