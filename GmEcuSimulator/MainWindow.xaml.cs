using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Core.Bus;
using GMThemeManager;
using GmEcuSimulator.ViewModels;

namespace GmEcuSimulator;

public partial class MainWindow : Window
{
    private static MainWindow? instance;
    private MainViewModel? vm;
    private DispatcherTimer? refreshTimer;
    private DispatcherTimer? logDrainTimer;

    // Updated from the checkbox's Checked/Unchecked handlers; read every frame
    // by AppendBusFrame off arbitrary threads (IPC worker, DPID timer).
    // Marked volatile so the read sees the last write without a lock.
    private static volatile bool logTraffic;

    // Per-channel gates for the FILE log (independent of the UI textbox).
    // True by default so a fresh install captures everything; the Log menu's
    // "Include J2534 calls" / "Include bus traffic" checkboxes can selectively
    // suppress one stream if the user only cares about the other. Volatile
    // because they're read on the IPC + scheduler threads.
    private static volatile bool includeJ2534FileLog = true;
    private static volatile bool includeBusFileLog = true;

    // Worker threads (IPC pipe, DPID scheduler, etc.) enqueue formatted log
    // lines here; the UI-thread logDrainTimer drains the queue at ~30 Hz,
    // batches everything per-TextBox into one AppendText call, and ScrollToEnd
    // once at the end. Avoids the per-frame Dispatcher.BeginInvoke storm that
    // pinned the UI thread during $36 multi-frame downloads (~1000 frames/s,
    // each previously queued two AppendText invocations).
    //
    // Hard cap on the in-memory queue prevents unbounded growth when the UI
    // can't keep up - oldest lines drop first. The on-screen TextBox is also
    // capped to the most-recent MaxLogLines lines so a multi-minute trace
    // stays scannable (and WPF's text rendering stays cheap). For persistent
    // capture, the user toggles "Log to file" - that path retains everything.
    private static readonly ConcurrentQueue<(TextBox Box, string Line)> pendingAppends = new();
    private const int MaxPendingAppends = 20_000;
    private const int MaxLogLines = 1000;

    // File-logging sink. Writes go to a dedicated background thread; UI is
    // never touched. Independent of the "Log traffic" textbox gate - the
    // user can have UI logging off (for performance during a download) and
    // file logging on, capturing every frame to disk for later analysis.
    private static readonly FileLogSink fileLog = new();
    public static FileLogSink FileLog => fileLog;

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
    //
    // Mirrored into DownloadLogBox on the Download tab so a user watching
    // the programming flow doesn't have to switch tabs to see protocol traffic.
    public static void AppendLog(string line)
    {
        // File sink first - independent of the textbox gate. Two gates apply:
        // master "Log to file" (fileLog.IsRunning) AND the per-stream
        // "Include J2534 calls" toggle from the Log menu. CSV column layout:
        //   [timestamp],[J2534],<message>
        // J2534 control-plane messages are free-form text - the message column
        // is left unquoted because none of the call-site formatters emit commas.
        if (fileLog.IsRunning && includeJ2534FileLog)
            fileLog.Write($"[{DateTime.Now:HH:mm:ss.fff}],[J2534],{line}");

        if (!logTraffic) return;
        Append(instance?.IpcLogBox, line);
        Append(instance?.DownloadLogBox, "[J2534] " + line);
    }

    // Frame-level traffic sink → LEFT pane (LogBox). Same master gate as
    // AppendLog - the checkbox controls both panes together. Also mirrored to
    // the Download tab's log box.
    //
    // Two formats arrive from VirtualBus:
    //   pretty - human-readable space-delimited line for the textbox
    //            e.g. "[chan 1] Rx 7E2 02 10 02  ; StartDiagnosticSession"
    //   csv    - comma-separated for the file; we prefix timestamp + stream tag
    //            e.g. "[06:12:34.567],[CAN],[chan 1],Rx,7E2 02 10 02,..."
    public static void AppendBusFrame(string pretty, string csv)
    {
        if (fileLog.IsRunning && includeBusFileLog)
            fileLog.Write($"[{DateTime.Now:HH:mm:ss.fff}],[CAN],{csv}");

        if (!logTraffic) return;
        Append(instance?.LogBox, pretty);
        Append(instance?.DownloadLogBox, "[CAN]   " + pretty);
    }

    // Single source of truth for the "Log traffic" toggle, shared between the
    // Bus log tab's checkbox and the Download tab's checkbox via two-way
    // binding through MainViewModel.IsLoggingEnabled.
    public static void SetLogTrafficEnabled(bool enabled) => logTraffic = enabled;

    // Forwarded from MainViewModel when the Log menu's two per-stream gates flip.
    public static void SetIncludeJ2534FileLog(bool enabled) => includeJ2534FileLog = enabled;
    public static void SetIncludeBusFileLog(bool enabled)   => includeBusFileLog = enabled;

    // Same shape for the "Maximize" toggle - shared between Bus log and
    // Download tab toolbars via MainViewModel.IsMaximized. Forwards to the
    // instance handlers that own the EditorRow / LogRow / splitter mutations.
    public static void SetBusLogMaximized(bool maximized)
    {
        var w = instance;
        if (w == null) return;
        if (!w.Dispatcher.CheckAccess())
        {
            w.Dispatcher.BeginInvoke(() => SetBusLogMaximized(maximized));
            return;
        }
        if (maximized) w.OnMaximizeBusLogChecked(null!, null!);
        else           w.OnMaximizeBusLogUnchecked(null!, null!);
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
        // Format once on the caller's thread (cheap) and enqueue; the UI
        // drain timer batches everything per-box per tick.
        var formatted = $"[{DateTime.Now:HH:mm:ss.fff}] {line}{Environment.NewLine}";
        pendingAppends.Enqueue((box, formatted));
        // Bound the queue so a wedged UI thread can't OOM the process.
        while (pendingAppends.Count > MaxPendingAppends)
            pendingAppends.TryDequeue(out _);
    }

    // Drains pendingAppends on the UI thread. Grouped by TextBox so each
    // box receives a single AppendText+ScrollToEnd per tick regardless of
    // how many lines were queued.
    private static void DrainPendingAppends()
    {
        if (pendingAppends.IsEmpty) return;
        Dictionary<TextBox, StringBuilder>? byBox = null;
        while (pendingAppends.TryDequeue(out var item))
        {
            byBox ??= new Dictionary<TextBox, StringBuilder>();
            if (!byBox.TryGetValue(item.Box, out var sb))
            {
                sb = new StringBuilder();
                byBox[item.Box] = sb;
            }
            sb.Append(item.Line);
        }
        if (byBox == null) return;
        foreach (var (box, sb) in byBox)
        {
            box.AppendText(sb.ToString());
            // Keep only the most-recent MaxLogLines lines. Line-counted from
            // the end so we never leave a partial line at the top of the box.
            TrimToLastLines(box, MaxLogLines);
            box.ScrollToEnd();
        }
    }

    /// <summary>
    /// Trims box.Text so only the last <paramref name="maxLines"/> hard-newline-
    /// delimited lines remain. No-op when content is shorter than the cap.
    /// </summary>
    private static void TrimToLastLines(TextBox box, int maxLines)
    {
        var text = box.Text;
        if (text.Length == 0) return;
        int count = 0;
        int idx = text.Length;
        while (idx > 0)
        {
            idx = text.LastIndexOf('\n', idx - 1);
            if (idx < 0) return;        // fewer than maxLines lines; nothing to trim
            count++;
            if (count > maxLines)
            {
                // idx is the newline immediately before the kept window;
                // everything after it (idx+1..end) is the last maxLines lines.
                box.Text = text.Substring(idx + 1);
                return;
            }
        }
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
    }

    private void OnMaximizeBusLogUnchecked(object sender, RoutedEventArgs e)
    {
        EditorRow.MinHeight    = EditorMinHeightNormal;
        EditorRow.Height       = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star);
        LogRow.Height          = new System.Windows.GridLength(320);
        EditorSplitter.Visibility = Visibility.Visible;
        MainContentGrid.Margin = new Thickness(14);
    }

    private void OnClearLogClicked(object sender, RoutedEventArgs e)
    {
        LogBox.Clear();
        IpcLogBox.Clear();
        DownloadLogBox?.Clear();
    }

    private void OnDownloadClearLogClicked(object sender, RoutedEventArgs e)
    {
        DownloadLogBox?.Clear();
        // Clear the bus log boxes too since we mirror into them.
        LogBox.Clear();
        IpcLogBox.Clear();
    }

    private void OnOpenLogFolderClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = FileLogSink.DefaultDirectory();
            System.IO.Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,
            });
        }
        catch { /* user can navigate manually if shell launch fails */ }
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

    public void Bind(VirtualBus bus, Core.Replay.BinReplayCoordinator replay, Shim.Ipc.NamedPipeServer pipeServer)
    {
        vm = new MainViewModel(bus, replay, pipeServer);
        DataContext = vm;
        bus.NodesChanged += (_, _) => Dispatcher.BeginInvoke(() => vm?.Rebuild());

        // Per-session file-log lifecycle. The "Log to file" menu toggle is the
        // user's persisted PREFERENCE; the actual sink lifecycle is tied to
        // host sessions so each capture lands as one tidy bus_*.csv with a
        // trailer. End-of-session covers both clean PassThruClose and the
        // IdleBusSupervisor's host-vanish path (USB unplug / host crash).
        // Begin-of-session (PassThruOpen) re-Starts the sink with a fresh
        // timestamped path if the preference is still on.
        bus.HostDisconnected += OnHostSessionEnded;
        bus.IdleReset        += OnHostSessionEnded;
        bus.HostConnected    += OnHostSessionStarted;

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
            vm.DownloadWorkspace.Refresh();
            vm.RefreshFileLogStatus();
        };
        refreshTimer.Start();

        // Background-priority drain so UI input + render always win over log
        // flushes. ~33 ms (30 Hz) keeps the textbox feeling live without
        // generating per-frame dispatcher work.
        logDrainTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(33),
        };
        logDrainTimer.Tick += (_, _) => DrainPendingAppends();
        logDrainTimer.Start();
    }

    public void AutoSave() => vm?.AutoSave();

    // Fires off the IPC worker thread (PassThruClose) or the IdleBusSupervisor
    // threadpool tick (host vanish). FileLogSink.Stop is thread-safe; the only
    // UI-touching call is the status-bar refresh which we marshal explicitly.
    private void OnHostSessionEnded()
    {
        if (!fileLog.IsRunning) return;
        fileLog.Stop();
        Dispatcher.BeginInvoke(() => vm?.RefreshFileLogStatus());
    }

    // Fires off the IPC worker thread on PassThruOpen. Only auto-Starts when
    // the user has the menu toggle on; an unchecked toggle means "don't
    // capture", regardless of host activity. The IsRunning check guards
    // against the case where the menu was just toggled on while a host was
    // already connected - the setter already started the file.
    private void OnHostSessionStarted()
    {
        if (vm?.IsFileLoggingEnabled != true) return;
        if (fileLog.IsRunning) return;
        try
        {
            fileLog.Start(Core.Bus.FileLogSink.DefaultPath());
        }
        catch (Exception ex)
        {
            // Disk full / path locked / etc. Swallow so we don't crash the
            // IPC thread; surface to the diagnostic log so the user sees why
            // the capture didn't start.
            AppendLog($"[file-log] auto-start on host connect failed: {ex.Message}");
        }
        Dispatcher.BeginInvoke(() => vm?.RefreshFileLogStatus());
    }

    private void OnExitClicked(object sender, RoutedEventArgs e) => Close();
}
