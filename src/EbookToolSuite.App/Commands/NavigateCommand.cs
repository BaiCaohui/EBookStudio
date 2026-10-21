using System.Windows.Input;
using EbookToolSuite.App.Navigation;

namespace EbookToolSuite.App.Commands;

/// <summary>左栏切换用的命令（供 Window.InputBindings 绑定）。</summary>
public sealed class NavigateCommand : ICommand
{
    private readonly NavigationService _nav;

    public NavigateCommand(NavigationService nav) => _nav = nav;

    /// <summary>命令是否可执行从不变化，所以这个事件永远不触发。</summary>
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter)
    {
        if (parameter is string route && route.Length > 0)
        {
            // 异常已经被 NavigationService 全部兜住（未知路由降级、页面异常进 PageFailed）
            _ = _nav.NavigateAsync(route);
        }
    }
}