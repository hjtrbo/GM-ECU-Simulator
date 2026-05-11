using System.Windows.Input;

namespace GmEcuSimulator.ViewModels;

// Lightweight ICommand. CanExecute changes are signalled via the WPF
// CommandManager's RequerySuggested event.
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> execute;
    private readonly Predicate<object?>? canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute == null ? null : (Predicate<object?>)(_ => canExecute())) { }

    public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
    {
        this.execute = execute;
        this.canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => execute(parameter);

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}
