using Core.Dps;
using Microsoft.Win32;
using System.IO;

namespace GmEcuSimulator.ViewModels.PrimeWizard;

// Page 1: pick the DPS archive .zip and show what's in it.
public sealed class Page1ArchiveViewModel : NotifyPropertyChangedBase
{
    private readonly PrimeWizardContext context;
    private readonly Action notifyChanged;
    private string? archivePath;
    private string? utilityFileName;
    private int calCount;
    private string? osPartNumber;
    private string? errorMessage;

    public Page1ArchiveViewModel(PrimeWizardContext context, Action notifyChanged)
    {
        this.context = context;
        this.notifyChanged = notifyChanged;
        PickArchiveCommand = new RelayCommand(PickArchive);
    }

    public RelayCommand PickArchiveCommand { get; }

    public string? ArchivePath
    {
        get => archivePath;
        private set { if (SetField(ref archivePath, value)) { OnPropertyChanged(nameof(ArchiveBaseName)); OnPropertyChanged(nameof(IsArchiveLoaded)); OnPropertyChanged(nameof(IsNextEnabled)); notifyChanged(); } }
    }

    public string ArchiveBaseName => archivePath is null ? "(none selected)" : Path.GetFileName(archivePath);
    public bool IsArchiveLoaded => archivePath is not null && errorMessage is null;

    public string? UtilityFileName
    {
        get => utilityFileName;
        private set => SetField(ref utilityFileName, value);
    }

    public int CalCount
    {
        get => calCount;
        private set => SetField(ref calCount, value);
    }

    public string? OsPartNumber
    {
        get => osPartNumber;
        private set => SetField(ref osPartNumber, value);
    }

    public string? ErrorMessage
    {
        get => errorMessage;
        private set
        {
            if (SetField(ref errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
                OnPropertyChanged(nameof(IsArchiveLoaded));
                OnPropertyChanged(nameof(IsNextEnabled));
                notifyChanged();
            }
        }
    }

    public bool HasError => errorMessage is not null;

    public bool IsNextEnabled => IsArchiveLoaded;

    private void PickArchive()
    {
        // Seed InitialDirectory from the last archive dir we wrote into
        // settings.json. Falls through to Windows' MRU for a fresh install
        // (PrimeWizardArchiveDir is null) or if the prior dir has since
        // been deleted.
        var settings = AppSettings.Load();
        var initialDir = (settings.PrimeWizardArchiveDir is { } d && Directory.Exists(d))
            ? d : null;

        var dlg = new OpenFileDialog
        {
            Title = "Select DPS archive",
            Filter = "DPS archive (*.zip)|*.zip|All files|*.*",
            CheckFileExists = true,
            InitialDirectory = initialDir ?? string.Empty,
        };
        if (dlg.ShowDialog() != true) return;
        Load(dlg.FileName);

        // Persist the dir we landed in so the next session opens there too.
        var chosenDir = Path.GetDirectoryName(dlg.FileName);
        if (!string.IsNullOrEmpty(chosenDir))
        {
            settings.PrimeWizardArchiveDir = chosenDir;
            settings.Save();
        }
    }

    public void RestoreFromContext()
    {
        if (context.ArchivePath is null) return;
        Load(context.ArchivePath);
    }

    private void Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                ErrorMessage = $"Archive not found: {path}";
                ArchivePath = null;
                context.Archive = null;
                context.ArchivePath = null;
                context.OverrideSecurityModuleId = null;
                context.OverrideFixedSeedHex = null;
                return;
            }

            // Detect "user picked a genuinely different archive" so we only
            // invalidate the donor + Phase 3 manifest in that case. On the
            // re-edit RestoreFromContext path, path == context.ArchivePath
            // and the prior donor selection should survive untouched.
            bool isSwappingArchive = !string.IsNullOrEmpty(context.ArchivePath)
                                     && !string.Equals(context.ArchivePath, path,
                                            StringComparison.OrdinalIgnoreCase);

            var descriptor = ArchivePrimer.ParseArchive(path);
            ArchivePath = path;
            UtilityFileName = descriptor.UtilityFileName;
            CalCount = descriptor.CalibrationBlockCount;
            OsPartNumber = descriptor.OsPartNumber;
            ErrorMessage = null;

            context.ArchivePath = path;
            context.Archive = descriptor;

            if (isSwappingArchive)
            {
                context.LoadedBinPath = null;
                context.Dataset = null;
                context.EditedManifest = null;
                context.UserEdits.Clear();
                // An explicit model/seed choice belongs to the previous archive.
                context.OverrideSecurityModuleId = null;
                context.OverrideFixedSeedHex = null;
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to parse archive: {ex.Message}";
            ArchivePath = null;
            UtilityFileName = null;
            CalCount = 0;
            OsPartNumber = null;
            context.Archive = null;
            context.OverrideSecurityModuleId = null;
            context.OverrideFixedSeedHex = null;
        }
    }
}
