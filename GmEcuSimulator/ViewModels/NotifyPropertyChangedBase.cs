using System.Collections;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace GmEcuSimulator.ViewModels;

// Base class for view models. Provides INotifyPropertyChanged plus
// INotifyDataErrorInfo so per-property validation can show a red border
// in the view via WPF's Validation.ErrorTemplate. Errors are stored as
// "either null (valid) or one string message (invalid)" per property -
// no list of messages, since the UI surfaces a single tooltip.
public abstract class NotifyPropertyChangedBase : INotifyPropertyChanged, INotifyDataErrorInfo
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

    private readonly Dictionary<string, string> errors = new();

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    // ------------------------- validation surface -------------------------

    /// <summary>
    /// Set or clear the error message for <paramref name="propertyName"/>.
    /// Pass null/empty to mark the property valid. Raises ErrorsChanged so
    /// any bound view re-evaluates Validation.HasError.
    /// </summary>
    protected void SetError(string propertyName, string? error)
    {
        bool had = errors.ContainsKey(propertyName);
        if (string.IsNullOrEmpty(error))
        {
            if (!had) return;
            errors.Remove(propertyName);
        }
        else
        {
            if (had && errors[propertyName] == error) return;
            errors[propertyName] = error;
        }
        ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(propertyName));
    }

    /// <summary>
    /// Convenience helper: run <paramref name="validate"/> on the supplied
    /// value and call <see cref="SetError"/> with its result. Used by
    /// property setters that store the value first and then validate so
    /// the user can hold "in-progress" invalid input without it being
    /// silently rejected.
    /// </summary>
    protected void Validate(string propertyName, string? value, Func<string?, string?> validate)
        => SetError(propertyName, validate(value));

    // ------------------------- INotifyDataErrorInfo -------------------------

    public bool HasErrors => errors.Count > 0;

    public IEnumerable GetErrors(string? propertyName)
    {
        if (string.IsNullOrEmpty(propertyName)) return errors.Values;
        return errors.TryGetValue(propertyName, out var e) ? new[] { e } : Array.Empty<string>();
    }
}
