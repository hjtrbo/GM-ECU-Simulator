using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Core.Bus;

namespace GmEcuSimulator.ViewModels;

// Backs the Capture Bootloader tab. One toggle (Enabled) flips
// CaptureSettings.BootloaderCaptureEnabled, which is the sole switch that
// makes Service36Handler relax its §8.13.4 NRC $31 bounds check and tells
// EcuExitLogic to dump the assembled $36 buffer to disk on session end.
//
// When Enabled is false the simulator is byte-for-byte spec-correct - the
// promise the user asked for when adding this feature.
//
// The captured-downloads list reflects whatever's already on disk in the
// capture directory, plus anything written this session via the bus's
// CaptureWritten event. Refresh dispatches to the UI thread because the
// event fires from whatever thread EcuExitLogic ran on.
public sealed class CaptureBootloaderViewModel : NotifyPropertyChangedBase
{
    private readonly CaptureSettings settings;

    public ObservableCollection<CapturedFile> Captures { get; } = new();

    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand RefreshCommand { get; }

    public CaptureBootloaderViewModel(CaptureSettings settings)
    {
        this.settings = settings;
        settings.CaptureWritten += OnCaptureWritten;

        OpenFolderCommand = new RelayCommand(OpenFolder);
        RefreshCommand    = new RelayCommand(Refresh);

        Refresh();
    }

    public bool Enabled
    {
        get => settings.BootloaderCaptureEnabled;
        set
        {
            if (settings.BootloaderCaptureEnabled == value) return;
            settings.BootloaderCaptureEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public string CaptureDirectory => settings.CaptureDirectory;

    public string StatusText => Enabled
        ? "Capture ON. Service36Handler bounds check relaxed; $36 buffers will be written to disk when the programming session ends."
        : "Capture OFF. Service36Handler behaves per GMW3110 §8.13.4 (NRC $31 on out-of-range addresses); nothing is written to disk.";

    private void OnCaptureWritten(string path)
    {
        // Marshal to the UI thread - the event fires from whatever thread
        // EcuExitLogic ran on (TesterPresentTicker P3C timeout / IPC).
        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            // Refresh wholesale - new file might also bump the timestamps of
            // others (e.g. a session rebuild) and the list is small.
            Refresh();
        }));
    }

    public void Refresh()
    {
        Captures.Clear();
        if (!Directory.Exists(settings.CaptureDirectory)) return;
        var infos = new DirectoryInfo(settings.CaptureDirectory)
            .GetFiles("*.bin")
            .OrderByDescending(f => f.LastWriteTimeUtc);
        foreach (var fi in infos)
            Captures.Add(new CapturedFile(fi.FullName, fi.Length, fi.LastWriteTime));
    }

    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(settings.CaptureDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = settings.CaptureDirectory,
                UseShellExecute = true,
            });
        }
        catch { /* user-visible failure goes to status bar by other means */ }
    }
}

public sealed record CapturedFile(string FullPath, long SizeBytes, DateTime Modified)
{
    public string FileName => Path.GetFileName(FullPath);
    public string SizeText => SizeBytes < 1024 ? $"{SizeBytes} B"
                            : SizeBytes < 1024 * 1024 ? $"{SizeBytes / 1024.0:N1} KB"
                            : $"{SizeBytes / 1024.0 / 1024.0:N2} MB";
}
