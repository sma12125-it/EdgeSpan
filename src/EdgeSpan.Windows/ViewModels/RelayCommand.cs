using System.Windows.Input;

namespace EdgeSpan.Windows.ViewModels;

public sealed class RelayCommand : ICommand
{
    private readonly Action _action;

    public RelayCommand(Action action) => _action = action;

    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => _action();
}

public sealed class RelayCommand<T> : ICommand
{
    private readonly Action<T?> _action;

    public RelayCommand(Action<T?> action) => _action = action;

    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => _action(parameter is T value ? value : default);
}
