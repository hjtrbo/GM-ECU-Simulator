using Common.Dbc;
using Core.Dbc;
using Core.Ecu;
using System.Collections.ObjectModel;

namespace GmEcuSimulator.ViewModels;

// Backs the scoped DBC-import dialog. A DBC describes the whole bus, so the user first picks the
// transmitting module, then ticks which of its messages to import; messages carrying an
// auto-mappable live signal are pre-ticked. Import replaces the ECU's broadcast set with the picked
// messages (the DBC owns the message/signal set - no merge).
public sealed class DbcImportViewModel : NotifyPropertyChangedBase
{
    private readonly DbcDatabase db;

    public string FileName { get; }
    public IReadOnlyList<TransmitterOption> Transmitters { get; }
    public ObservableCollection<DbcMessageRow> Messages { get; } = new();

    public RelayCommand SelectAllCommand { get; }
    public RelayCommand SelectNoneCommand { get; }

    public DbcImportViewModel(DbcDatabase db, string fileName)
    {
        this.db = db;
        FileName = fileName;
        Transmitters = DbcImporter.TransmittersByMessageCount(db)
            .Select(t => new TransmitterOption(t.Transmitter, t.Count))
            .ToList();
        SelectAllCommand = new RelayCommand(() => SetAll(true));
        SelectNoneCommand = new RelayCommand(() => SetAll(false));
        selectedTransmitter = Transmitters.FirstOrDefault();
        RebuildMessages();
    }

    private TransmitterOption? selectedTransmitter;
    public TransmitterOption? SelectedTransmitter
    {
        get => selectedTransmitter;
        set { if (SetField(ref selectedTransmitter, value)) RebuildMessages(); }
    }

    public int SelectedCount => Messages.Count(m => m.Selected);

    private void RebuildMessages()
    {
        Messages.Clear();
        if (SelectedTransmitter is null) { OnPropertyChanged(nameof(SelectedCount)); return; }
        foreach (var m in db.Messages.Where(m => m.Transmitter == SelectedTransmitter.Name).OrderBy(m => m.Id))
        {
            var row = new DbcMessageRow(m, DbcImporter.HasMappableSignal(m), MappedHint(m));
            row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(DbcMessageRow.Selected)) OnPropertyChanged(nameof(SelectedCount)); };
            Messages.Add(row);
        }
        OnPropertyChanged(nameof(SelectedCount));
    }

    // "live: RPM, ECT" hint - the live signals the importer would auto-map for this message.
    private static string MappedHint(DbcMessage m)
    {
        var mapped = m.Signals.Select(DbcImporter.AutoMap).Where(s => s is not null).Select(s => s!.Value.ToString()).ToList();
        return mapped.Count == 0 ? "" : "live: " + string.Join(", ", mapped);
    }

    private void SetAll(bool value)
    {
        foreach (var m in Messages) m.Selected = value;
        OnPropertyChanged(nameof(SelectedCount));
    }

    // The picked messages converted to runtime broadcast messages (auto-mapped).
    public List<BroadcastMessage> BuildSelected()
    {
        if (SelectedTransmitter is null) return new();
        var ids = Messages.Where(m => m.Selected).Select(m => m.Id).ToHashSet();
        return DbcImporter.ToBroadcasts(db, SelectedTransmitter.Name, ids);
    }
}

public sealed record TransmitterOption(string Name, int Count)
{
    public string Display => $"{Name}  ({Count} msgs)";
}

public sealed class DbcMessageRow : NotifyPropertyChangedBase
{
    public uint Id { get; }
    public string Display { get; }
    public string Hint { get; }

    public DbcMessageRow(DbcMessage m, bool preTick, string hint)
    {
        Id = m.Id;
        Display = $"0x{m.Id:X3}  {m.Name}";
        Hint = hint;
        selected = preTick;
    }

    private bool selected;
    public bool Selected
    {
        get => selected;
        set => SetField(ref selected, value);
    }
}
