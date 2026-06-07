using Common.Protocol;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;

namespace GmEcuSimulator.ViewModels;

// One collapsible per-mode section in the ECU editor ($1A / $22 / $2D). Each section is an independent,
// filterable+sortable view over the parent ECU's single shared Pids collection, showing only the rows whose Mode
// matches this section. The Mode column is gone - a row's mode is implied by the section it sits in, and the
// section's Add button stamps this mode onto the new row. Mirrors the read-only "$01 (OBD-II)" section's collapsible
// shape, extended to the editable modes.
public sealed class PidModeSection : NotifyPropertyChangedBase
{
    private readonly EcuViewModel parent;

    public PidMode Mode { get; }
    public string Title { get; }

    // $1A identity rows have fixed encoding (raw bytes), so the editor hides the analog-shaping columns (Type / Signal
    // / Scalar / Offset / Unit / Live) and shows Size read-only for this section. Bound by the shared section template
    // through the column BindingProxy. IsNotMode1A is the inverse for "show only when NOT $1A".
    public bool IsMode1A => Mode == PidMode.Mode1A;
    public bool IsNotMode1A => !IsMode1A;

    // $22 rows drive their response from a live signal / waveform, so the editor hides the static-payload Value column
    // for this section. IsNotMode22 is bound by the shared section template through the column BindingProxy.
    public bool IsMode22 => Mode == PidMode.Mode22;
    public bool IsNotMode22 => !IsMode22;

    // Independent view over the shared Pids collection (NOT the default view - each section needs its own filter and
    // sort state). A ListCollectionView subscribes to the ObservableCollection's CollectionChanged, so Add/Remove on
    // Pids re-flows every section automatically.
    public ICollectionView View { get; }

    // One Excel-style header menu per filterable column. No Mode column - the section IS the mode. Live is excluded
    // (its value changes continuously, so a snapshot filter would go stale).
    public PidColumnFilter IdentifierColumnFilter { get; }
    public PidColumnFilter NameColumnFilter { get; }
    public PidColumnFilter SizeColumnFilter { get; }
    public PidColumnFilter TypeColumnFilter { get; }
    public PidColumnFilter SignalColumnFilter { get; }
    public PidColumnFilter ScalarColumnFilter { get; }
    public PidColumnFilter OffsetColumnFilter { get; }
    public PidColumnFilter UnitColumnFilter { get; }

    private readonly List<PidColumnFilter> columnFilters = new();
    private readonly List<PidColumnFilter> activeSorts = new();

    public RelayCommand AddCommand { get; }
    public RelayCommand RemoveCommand { get; }

    public PidModeSection(EcuViewModel parent, PidMode mode, string title, ObservableCollection<PidViewModel> pids)
    {
        this.parent = parent;
        Mode = mode;
        Title = title;

        View = new ListCollectionView(pids);

        // Same projections the old single grid used, minus Mode. Selector text feeds the column's substring filter;
        // SortPath is the property the column orders by.
        IdentifierColumnFilter = Make("Identifier", "Address", p => $"{p.IdentifierLabel} {p.AddressHex}");
        NameColumnFilter       = Make("Name", "Name", p => p.Name);
        SizeColumnFilter       = Make("Size (B)", "Model.ResponseLength", p => p.LengthBytesText);
        TypeColumnFilter       = Make("Type", "DataType", p => p.DataType.ToString());
        SignalColumnFilter     = Make("Signal", "SignalDisplay", p => p.SignalDisplay);
        ScalarColumnFilter     = Make("Scalar", "Scalar", p => p.Scalar.ToString());
        OffsetColumnFilter     = Make("Offset", "Offset", p => p.Offset.ToString());
        UnitColumnFilter       = Make("Unit", "Unit", p => p.Unit);

        View.Filter = Matches;

        AddCommand    = new RelayCommand(() => parent.AddPid(Mode));
        RemoveCommand = new RelayCommand(RemoveSelected, CanRemoveSelected);
    }

    // Delete every row the user has highlighted. The grid is SelectionMode=Extended, so Ctrl/Shift-click builds a
    // multi-row selection; the Remove button passes the grid's SelectedItems (a live IList) as the command parameter.
    // We snapshot it to a list first because parent.RemovePid mutates the shared Pids collection, which re-flows this
    // section's view and would shift the IList out from under the loop. Falls back to SelectedPid when no parameter is
    // supplied (e.g. a keyboard binding) so single-row removal still works.
    private void RemoveSelected(object? parameter)
    {
        var targets = SelectionToList(parameter);
        foreach (var pid in targets)
            parent.RemovePid(pid);
    }

    private bool CanRemoveSelected(object? parameter)
        => parameter is System.Collections.IList list ? list.Count > 0 : SelectedPid != null;

    private List<PidViewModel> SelectionToList(object? parameter)
    {
        if (parameter is System.Collections.IList list)
            return list.OfType<PidViewModel>().ToList();
        return SelectedPid != null ? new List<PidViewModel> { SelectedPid } : new List<PidViewModel>();
    }

    // Per-section grid selection. Two-way bound to the section's DataGrid.SelectedItem. When a row is picked here, the
    // parent promotes it to the shared SelectedPid (which the waveform inspector reads) and clears the other sections'
    // selections so only one row is ever highlighted across the editor.
    private PidViewModel? selectedPid;
    public PidViewModel? SelectedPid
    {
        get => selectedPid;
        set
        {
            if (!SetField(ref selectedPid, value)) return;
            if (value != null) parent.NotifySectionSelected(this, value);
        }
    }

    // Clears this section's selection without notifying the parent (called by the parent when another section takes
    // over, so we don't recurse). Setting the DataGrid.SelectedItem binding to null deselects its row.
    internal void ClearSelection()
    {
        if (selectedPid == null) return;
        selectedPid = null;
        OnPropertyChanged(nameof(SelectedPid));
    }

    // Re-evaluate row membership + ordering. Called by the parent after a structural change (Add/Remove/ReplacePids).
    public void Refresh() => View.Refresh();

    private bool Matches(object item)
        => item is PidViewModel pid && pid.Mode == Mode && columnFilters.All(c => c.Matches(pid));

    private PidColumnFilter Make(string title, string sortPath, Func<PidViewModel, string?> selector)
    {
        var f = new PidColumnFilter(title, sortPath, selector, View.Refresh, ApplySort, ClearSort);
        columnFilters.Add(f);
        return f;
    }

    // Sorting a column promotes it to the primary key; columns already sorted drop to lower-priority tiebreakers.
    private void ApplySort(PidColumnFilter filter, ListSortDirection direction)
    {
        filter.SortDirection = direction;
        activeSorts.Remove(filter);
        activeSorts.Insert(0, filter);
        RebuildSort();
    }

    private void ClearSort(PidColumnFilter filter)
    {
        if (activeSorts.Remove(filter)) RebuildSort();
    }

    private void RebuildSort()
    {
        View.SortDescriptions.Clear();
        foreach (var f in activeSorts)
            View.SortDescriptions.Add(new SortDescription(f.SortPath, f.SortDirection ?? ListSortDirection.Ascending));

        bool multi = activeSorts.Count > 1;
        for (int i = 0; i < activeSorts.Count; i++)
            activeSorts[i].SortPriority = multi ? i + 1 : 0;

        foreach (var f in columnFilters)
        {
            if (activeSorts.Contains(f)) continue;
            f.SortDirection = null;
            f.SortPriority = 0;
        }
    }
}
