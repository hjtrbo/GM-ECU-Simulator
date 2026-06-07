using Core.Bus;
using GmEcuSimulator.ViewModels;
using GMThemeManager;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

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

    // Per-stream gates for the FILE log live on BusLogger.IncludeJ2534 /
    // IncludeCan / IncludeSim (set via SetInclude*FileLog forwarders).
    // True by default so a fresh install captures everything; the Log menu's
    // checkboxes can selectively suppress one stream. SIM diagnostics
    // currently share the J2534 gate since the existing menu only exposes
    // J2534 / bus; add a third gate when the user asks for it.

    // UI-only filter: when on, $3E TesterPresent requests and $7E positive
    // responses are skipped at AppendBusFrame's textbox path so the bus log
    // textbox isn't crowded with keepalives during long sessions. The file
    // log path runs first and is intentionally untouched - captures stay
    // complete and exactly reflect what hit the bus.
    private static volatile bool suppressTesterPresentInWindow;

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
    private const int MaxLogLines = 2000;

    // Unified file-logging sink. Wraps a FileLogSink and owns the
    // "[HH:mm:ss.fff],[TAG],<content>" line format. Writes go to a dedicated
    // background thread; UI is never touched. Independent of the "Log
    // traffic" textbox gate - the user can have UI logging off (for
    // performance during a download) and file logging on, capturing every
    // frame to disk for later analysis. AppendJ2534Log / AppendSimLog /
    // AppendBusFrame route through it via WriteJ2534 / WriteSim / WriteCan.
    private static readonly BusLogger busLogger = new();
    public static BusLogger BusLog => busLogger;

    // True between OnHostSessionStarted and OnHostSessionEnded. The "Log to
    // file" menu toggle is purely a persisted preference; whether the sink
    // is actually running depends on this flag, so toggling on with no host
    // present just arms the preference (status reads "Armed").
    private static volatile bool hostSessionActive;
    public static bool IsHostSessionActive => hostSessionActive;

    // Serializes the {hostSessionActive, busLogger.IsRunning} transition so the
    // VM setter (UI thread) and the IPC-thread host-session callbacks can't
    // race into a double-Start or a Start-on-the-tail-of-a-Stop.
    private static readonly object fileLogLifecycleLock = new();

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
        InputBindings.Add(new KeyBinding { Key = Key.P, Modifiers = ModifierKeys.Control | ModifierKeys.Shift,
            Command = new RelayCommand(() => vm?.OpenSetupWindowCommand.Execute(null)) });

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

    // J2534 control-plane log sink (PassThru* IPC narration, pipe-server
    // lifecycle, periodic register/unregister). Routed to the RIGHT pane
    // (IpcLogBox). File path is tagged [J2534] with embedded multi-line
    // input split into per-line entries by BusLogger.WriteJ2534.
    public static void AppendJ2534Log(string line)
    {
        busLogger.WriteJ2534(line);

        if (!logTraffic) return;
        Append(instance?.IpcLogBox, line);
    }

    // Sim-internal log sink (service-handler decisions, security-module
    // state, scheduler stalls, app lifecycle). Same UI pane as the J2534
    // sink so the user sees one merged stream; file path is tagged [SIM]
    // so disk captures distinguish sim internals from host-driven J2534
    // chatter.
    public static void AppendSimLog(string line)
    {
        busLogger.WriteSim(line);

        if (!logTraffic) return;
        Append(instance?.IpcLogBox, line);
    }

    // Frame-level traffic sink → LEFT pane (LogBox). Same master gate as
    // the J2534 / SIM sinks.
    //
    // Two formats arrive from VirtualBus:
    //   pretty - human-readable space-delimited line for the textbox
    //            e.g. "[chan 1] Rx 7E2 02 10 02  ; StartDiagnosticSession"
    //   csv    - comma-separated for the file; BusLogger prefixes
    //            [timestamp],[CAN], before the disk write
    //            e.g. "[06:12:34.567],[CAN],[chan 1],Rx,7E2 02 10 02,..."
    public static void AppendBusFrame(string pretty, string csv, bool isTesterPresent)
    {
        // File-log path is unconditional - the suppress toggle is a UI-only
        // filter so disk captures stay complete and reviewable.
        busLogger.WriteCan(csv);

        if (!logTraffic) return;
        // UI suppression: when the user has "Hide $3E" on, drop $3E requests
        // and $7E positive responses from the textboxes only. File capture
        // above already ran, so the disk record still has every frame.
        if (isTesterPresent && suppressTesterPresentInWindow) return;
        Append(instance?.LogBox, pretty);
    }

    // Single source of truth for the "Log traffic" toggle, shared between the
    // Bus log tab's checkbox and the Download tab's checkbox via two-way
    // binding through MainViewModel.IsLoggingEnabled.
    public static void SetLogTrafficEnabled(bool enabled) => logTraffic = enabled;

    // Forwarded from MainViewModel when the Log menu's per-stream gates flip.
    // The J2534 toggle currently also gates SIM internals because the menu
    // doesn't yet expose a separate "Include SIM diagnostics" checkbox.
    public static void SetIncludeJ2534FileLog(bool enabled)
    {
        busLogger.IncludeJ2534 = enabled;
        busLogger.IncludeSim = enabled;
    }
    public static void SetIncludeBusFileLog(bool enabled) => busLogger.IncludeCan = enabled;
    public static void SetSuppressTesterPresentInWindow(bool enabled)
        => suppressTesterPresentInWindow = enabled;

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
    // EditorRow.MinHeight=240 in XAML so the user can't drag the splitter up
    // and squash the editor card below "Selected ECU header + 1 form row +
    // PID grid header + 1 DataGrid row + padding". Maximize bypasses that
    // floor by zeroing MinHeight here; the Unchecked path restores both.
    // Keep this in sync with the XAML EditorRow.MinHeight value.
    private const double EditorMinHeightNormal = 240;

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
    }

    private void OnOpenLogFolderClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = BusLogger.DefaultDirectory();
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

    public void Bind(VirtualBus bus, Core.Replay.BinReplayCoordinator replay, Shim.Ipc.NamedPipeServer pipeServer,
                     Shim.Ipc.RawCanTcpServer rawCanServer)
    {
        vm = new MainViewModel(bus, replay, pipeServer, rawCanServer);
        DataContext = vm;
        bus.NodesChanged += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            vm?.Rebuild();
            // A new ECU set may carry different broadcasts; refresh the live emitter if a session is up.
            bus.BroadcastScheduler.RebuildIfRunning();
        });

        // DBC broadcast emission is meaningful only while a host session is open (frames only land on
        // an open channel). Start the broadcast scheduler on connect, stop it on disconnect.
        bus.HostConnected    += () => bus.BroadcastScheduler.RebuildAndStart();
        bus.HostDisconnected += () => bus.BroadcastScheduler.StopAll();

        // Per-session file-log lifecycle. The "Log to file" menu toggle is the
        // user's persisted PREFERENCE; the actual sink lifecycle is tied to
        // host sessions so each capture lands as one tidy bus_*.csv with a
        // trailer. HostDisconnected covers both clean PassThruClose and the
        // pipe-drop path in NamedPipeServer (USB unplug / host crash / host
        // process killed) - the time-based IdleBusSupervisor that used to
        // fire IdleReset was stubbed 2026-05-15, but the subscription is
        // kept in case a future force-idle path calls DoReset. Begin-of-
        // session (PassThruOpen) re-Starts the sink with a fresh timestamped
        // path if the preference is still on.
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
                ecu.RefreshObd2Live(now);
                ecu.RefreshBroadcastsLive(now);
                ecu.RefreshSecurity(nowLong);
            }
            vm.RefreshBinReplayLive();
            vm.RefreshFileLogStatus();
            vm.RefreshConnectionStatus();
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

    public void AutoPrime(string archivePath, string? donorBinPath = null)
        => vm?.TryAutoPrime(archivePath, donorBinPath, AppendSimLog);

    // Fires off the IPC worker thread (PassThruClose) or the NamedPipeServer
    // accept-loop thread (pipe-drop finally block on dirty disconnect).
    // BusLogger.Stop is thread-safe; the only UI-touching call is the
    // status-bar refresh which we marshal explicitly.
    private void OnHostSessionEnded()
    {
        lock (fileLogLifecycleLock)
        {
            hostSessionActive = false;
            if (!busLogger.IsRunning) return;
            busLogger.Stop();
        }
        Dispatcher.BeginInvoke(() => vm?.RefreshFileLogStatus());
    }

    // Fires off the IPC worker thread on PassThruOpen, synchronously before
    // the dispatcher returns STATUS_NOERROR to the host - so the file is open
    // and the writer thread is live before the host can issue another J2534
    // call. Only Starts when the user has the menu toggle armed; an unchecked
    // toggle means "don't capture", regardless of host activity.
    // True when any ECU on the bus is running the Ford UDS persona - the
    // signal that the user wants a faithful wire log written automatically.
    private static bool CapturePersonaActive()
    {
        foreach (var node in App.Bus.Nodes)
            if (node.Persona.Id == "ford-uds") return true;
        return false;
    }

    private void OnHostSessionStarted()
    {
        lock (fileLogLifecycleLock)
        {
            hostSessionActive = true;
            // The ford-uds persona exists to record a host's traffic, so the
            // wire capture must never depend on the user having armed the "Log to
            // file" toggle - force the complete bus_*.csv on whenever it's active.
            // Every other persona still honours the toggle.
            bool captureActive = CapturePersonaActive();
            if (vm?.IsFileLoggingEnabled != true && !captureActive) return;
            if (busLogger.IsRunning) return;
            try
            {
                busLogger.Start(Core.Bus.BusLogger.DefaultPath(), BusConfigBanner.For(App.Bus));
                if (captureActive && vm?.IsFileLoggingEnabled != true)
                    AppendSimLog($"[file-log] ford-uds active - auto-started wire capture: {busLogger.CurrentPath}");
            }
            catch (Exception ex)
            {
                // Disk full / path locked / etc. Swallow so we don't crash
                // the IPC thread; surface to the diagnostic log so the user
                // sees why the capture didn't start.
                AppendSimLog($"[file-log] auto-start on host connect failed: {ex.Message}");
            }
        }
        Dispatcher.BeginInvoke(() => vm?.RefreshFileLogStatus());
    }

    // Invoked by MainViewModel when the user flips the "Log to file" menu
    // toggle. The toggle is the persisted preference; the sink itself is
    // only running between host-session start and end. Turning the toggle
    // on with no host present just arms the preference; turning it on mid-
    // session opens a fresh capture now; turning it off mid-session closes
    // the current capture immediately.
    internal static void OnFileLoggingPreferenceChanged(bool enabled)
    {
        lock (fileLogLifecycleLock)
        {
            if (!enabled)
            {
                if (busLogger.IsRunning) busLogger.Stop();
                return;
            }
            if (!hostSessionActive) return;
            if (busLogger.IsRunning) return;
            try
            {
                busLogger.Start(Core.Bus.BusLogger.DefaultPath(), BusConfigBanner.For(App.Bus));
            }
            catch (Exception ex)
            {
                AppendSimLog($"[file-log] start on toggle failed: {ex.Message}");
            }
        }
    }

    private void OnExitClicked(object sender, RoutedEventArgs e) => Close();

    // ---------------- Live-tile dashboard: picker + drag-reorder ----------------

    // Drag payload format. The dragged tile itself is held in draggedTile; the
    // DataObject just tags the drag as ours so foreign drops are ignored.
    private const string LiveTileFormat = "GmEcuSim.LiveTile";

    private Point liveTileDragStart;
    private PidTileViewModel? draggedTile;
    private InsertionAdorner? insertionAdorner;

    // + button -> open the PID picker popup.
    private void AddTileButton_Click(object sender, RoutedEventArgs e)
    {
        if (vm != null) vm.PickerOpen = true;
    }

    // Double-click a candidate -> pin it (same as the Add button).
    private void PickerList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (vm == null) return;
        if (((ListBox)sender).SelectedItem is PidPickerEntry entry && vm.AddTileCommand.CanExecute(entry))
            vm.AddTileCommand.Execute(entry);
    }

    // Record the press point and which tile (if any) sits under it. The drag
    // doesn't begin until the pointer moves past the system drag threshold.
    private void LiveTileItems_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        liveTileDragStart = e.GetPosition(null);
        draggedTile = FindAncestor<ContentPresenter>(e.OriginalSource as DependencyObject)?.DataContext as PidTileViewModel;
    }

    private void LiveTileItems_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (draggedTile == null || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - liveTileDragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(pos.Y - liveTileDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        try
        {
            // Blocks until the drop completes; DragOver/Drop fire during it.
            DragDrop.DoDragDrop(LiveTileItems, new DataObject(LiveTileFormat, draggedTile), DragDropEffects.Move);
        }
        finally
        {
            RemoveInsertionAdorner();
            draggedTile = null;
        }
    }

    private void LiveTileItems_DragOver(object sender, DragEventArgs e)
    {
        if (draggedTile == null || !e.Data.GetDataPresent(LiveTileFormat))
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        e.Effects = DragDropEffects.Move;
        int index = ComputeInsertionIndex(e.GetPosition(LiveTileItems));
        ShowInsertionAdorner(MarkerForIndex(index));
        e.Handled = true;
    }

    private void LiveTileItems_DragLeave(object sender, DragEventArgs e)
    {
        // DragLeave also fires when crossing between child tiles - only clear
        // the indicator when the pointer has actually left the control.
        var p = e.GetPosition(LiveTileItems);
        if (p.X < 0 || p.Y < 0 || p.X > LiveTileItems.ActualWidth || p.Y > LiveTileItems.ActualHeight)
            RemoveInsertionAdorner();
    }

    private void LiveTileItems_Drop(object sender, DragEventArgs e)
    {
        RemoveInsertionAdorner();
        if (vm == null || draggedTile == null || !e.Data.GetDataPresent(LiveTileFormat)) return;

        int from = vm.LiveTiles.IndexOf(draggedTile);
        if (from < 0) return;
        int to = ComputeInsertionIndex(e.GetPosition(LiveTileItems));
        // ComputeInsertionIndex counts in pre-removal coordinates; once the
        // dragged tile is pulled out, every slot after it shifts down by one.
        if (to > from) to--;
        vm.MoveTile(from, to);
        e.Handled = true;
    }

    // Insertion index in reading order (left-to-right, top-to-bottom): the
    // number of tiles that sit before the cursor. A tile is "before" when the
    // cursor is on a lower row, or on the same row and right of the tile's
    // horizontal midpoint.
    private int ComputeInsertionIndex(Point pos)
    {
        int index = 0;
        for (int i = 0; i < LiveTileItems.Items.Count; i++)
        {
            if (LiveTileItems.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement fe
                || fe.ActualWidth == 0)
                continue;
            var tl = fe.TranslatePoint(new Point(0, 0), LiveTileItems);
            var r = new Rect(tl, new Size(fe.ActualWidth, fe.ActualHeight));
            bool before = pos.Y > r.Bottom || (pos.Y >= r.Top && pos.X > r.Left + r.Width / 2);
            if (before) index = i + 1;
        }
        return index;
    }

    // The snapped vertical-bar rectangle for an insertion index: the left edge
    // of the tile at that index, or the right edge of the last tile when the
    // index is past the end. The bar is centred in the inter-tile gap.
    private Rect MarkerForIndex(int index)
    {
        int count = LiveTileItems.Items.Count;
        if (count == 0) return Rect.Empty;
        if (index < count
            && LiveTileItems.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement fe
            && fe.ActualWidth > 0)
        {
            var tl = fe.TranslatePoint(new Point(0, 0), LiveTileItems);
            return new Rect(tl.X - 4, tl.Y, 0, fe.ActualHeight);
        }
        if (LiveTileItems.ItemContainerGenerator.ContainerFromIndex(count - 1) is FrameworkElement last
            && last.ActualWidth > 0)
        {
            var tl = last.TranslatePoint(new Point(0, 0), LiveTileItems);
            return new Rect(tl.X + last.ActualWidth + 4, tl.Y, 0, last.ActualHeight);
        }
        return Rect.Empty;
    }

    private void ShowInsertionAdorner(Rect marker)
    {
        if (marker.IsEmpty) { RemoveInsertionAdorner(); return; }
        var layer = AdornerLayer.GetAdornerLayer(LiveTileItems);
        if (layer == null) return;
        if (insertionAdorner == null)
        {
            var brush = TryFindResource("Accent.PrimaryBrush") as Brush ?? Brushes.DodgerBlue;
            insertionAdorner = new InsertionAdorner(LiveTileItems, brush);
            layer.Add(insertionAdorner);
        }
        insertionAdorner.Marker = marker;
        insertionAdorner.InvalidateVisual();
    }

    private void RemoveInsertionAdorner()
    {
        if (insertionAdorner == null) return;
        AdornerLayer.GetAdornerLayer(LiveTileItems)?.Remove(insertionAdorner);
        insertionAdorner = null;
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T) d = VisualTreeHelper.GetParent(d);
        return d as T;
    }

    // A non-hit-testable adorner that paints a single vertical accent bar at
    // the snap position between two tiles during a drag-reorder.
    private sealed class InsertionAdorner : Adorner
    {
        private readonly Brush brush;
        public Rect Marker { get; set; }

        public InsertionAdorner(UIElement adornedElement, Brush brush) : base(adornedElement)
        {
            this.brush = brush;
            IsHitTestVisible = false;
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            if (Marker.IsEmpty) return;
            var pen = new Pen(brush, 2.5)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
            };
            drawingContext.DrawLine(pen,
                new Point(Marker.X, Marker.Top),
                new Point(Marker.X, Marker.Top + Marker.Height));
        }
    }
}
