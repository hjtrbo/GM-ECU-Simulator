using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using BinaryWorker;
using Common.Replay;
using Core.Bus;
using Core.Replay;
using GmEcuSimulator.Replay;
using Microsoft.Win32;

namespace GmEcuSimulator.ViewModels;

// Bin Replay tab. Owns the user-facing surface for loading / unloading a
// .bin and presents the coordinator's runtime state. The actual swap of
// the bus's ECU set is delegated to MainViewModel via the OnLoad / OnUnload
// callbacks so the prior-config snapshot / restore lives in one place.
//
// "Load…" picks a .bin produced by the sibling DataLogger; the bin's channel
// headers carry every field we need (Name, Unit, Address, NodeType, Size,
// DataType, Scalar, Offset). "Load synthetic demo" wires up an in-memory
// 4-channel source for offline testing.
public sealed class BinReplayViewModel : NotifyPropertyChangedBase
{
    // BinaryWorkerFactory enforces process-wide singleton in its ctor -
    // we construct it once at VM construction. Used only by LoadFile.
    private readonly BinaryWorkerFactory binWorker = new();
    private readonly BinReplayCoordinator coord;
    private readonly VirtualBus bus;
    private readonly Action<IBinSource, string?> onLoad;
    private readonly Action onUnload;

    public RelayCommand LoadFileCommand { get; }
    public RelayCommand LoadDemoCommand { get; }
    public RelayCommand UnloadCommand { get; }

    public ObservableCollection<BinReplayChannelViewModel> Channels { get; } = new();

    public BinReplayViewModel(
        BinReplayCoordinator coord,
        VirtualBus bus,
        Action<IBinSource, string?> onLoad,
        Action onUnload)
    {
        this.coord = coord;
        this.bus = bus;
        this.onLoad = onLoad;
        this.onUnload = onUnload;

        LoadFileCommand = new RelayCommand(LoadFile);
        LoadDemoCommand = new RelayCommand(LoadDemo);
        UnloadCommand = new RelayCommand(Unload, () => coord.State != BinReplayState.NoBin);

        coord.StateChanged += _ => OnAnyStateChanged();
    }

    public string FilePathLabel => string.IsNullOrEmpty(coord.FilePath) ? "(no bin loaded)" : coord.FilePath!;
    public string StateLabel => coord.State.ToString();

    public string ChannelSummary
    {
        get
        {
            var headers = coord.ChannelHeaders;
            if (headers == null) return "(no channels)";
            int mapped = 0, skipped = 0;
            foreach (var h in headers)
            {
                if (h.NodeType == 0) skipped++; else mapped++;
            }
            return skipped == 0
                ? $"{mapped} channels"
                : $"{mapped} channels mapped, {skipped} skipped (NodeType=None)";
        }
    }

    public string ElapsedSummary
    {
        get
        {
            long elapsed = coord.ElapsedAt(bus.NowMs);
            long total = coord.DurationMs;
            return $"{FormatMs(elapsed)} / {FormatMs(total)}";
        }
    }

    public BinReplayLoopMode LoopMode
    {
        get => coord.LoopMode;
        set { if (coord.LoopMode == value) return; coord.LoopMode = value; OnPropertyChanged(); }
    }

    public bool AutoLoadOnStart
    {
        get => coord.PersistedAutoLoadOnStart;
        set { if (coord.PersistedAutoLoadOnStart == value) return; coord.PersistedAutoLoadOnStart = value; OnPropertyChanged(); }
    }

    private void LoadFile()
    {
        var picker = new OpenFileDialog
        {
            Filter = "Bin (*.bin)|*.bin|All files|*.*",
            Title = "Pick a .bin to replay",
        };
        if (picker.ShowDialog() != true) return;

        try
        {
            var reader = binWorker.CreateLogReader(picker.FileName);
            var src = new LogReaderBinSource(reader);
            onLoad(src, picker.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Bin load failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadDemo()
    {
        var src = new SyntheticBinSource();
        onLoad(src, "(synthetic 4-channel demo)");
    }

    private void Unload()
    {
        onUnload();
    }

    /// <summary>
    /// Called by MainViewModel after a Load completes successfully so the
    /// channel grid can repopulate. Also called after Unload (with empty)
    /// to clear it.
    /// </summary>
    public void RebuildChannelGrid()
    {
        Channels.Clear();
        var headers = coord.ChannelHeaders;
        if (headers == null) { OnAnyStateChanged(); return; }
        for (int i = 0; i < headers.Count; i++)
        {
            Channels.Add(new BinReplayChannelViewModel(headers[i], i, coord));
        }
        OnAnyStateChanged();
    }

    /// <summary>
    /// Called by MainWindow's 100 ms refresh timer (alongside the per-PID
    /// live refresh). Updates the channel grid's Live column and the
    /// Elapsed/Total label so playback progress is visible.
    /// </summary>
    public void RefreshLive()
    {
        double now = bus.NowMs;
        foreach (var ch in Channels) ch.RefreshLive(now);
        OnPropertyChanged(nameof(ElapsedSummary));
    }

    private void OnAnyStateChanged()
    {
        OnPropertyChanged(nameof(FilePathLabel));
        OnPropertyChanged(nameof(StateLabel));
        OnPropertyChanged(nameof(ChannelSummary));
        OnPropertyChanged(nameof(ElapsedSummary));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    private static string FormatMs(long ms)
    {
        var ts = TimeSpan.FromMilliseconds(ms);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D3}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D3}";
    }
}
