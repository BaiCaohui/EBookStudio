using System.Windows.Input;

namespace EbookToolSuite.App.Commands;

/// <summary>最小命令实现。只用于"执行、不管能不能执行"的场景。</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;

    public RelayCommand(Action<object?> execute) => _execute = execute;

    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => _execute(parameter);
}