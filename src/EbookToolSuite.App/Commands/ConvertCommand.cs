using System.Windows;
using EbookToolSuite.App.Views;
using EbookToolSuite.Core.Errors;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Services.Conversion;
using Serilog;

namespace EbookToolSuite.App.Commands;

/// <summary>
/// 打开转换窗口。书库详情浮层的「转换」按钮与将来的队列页**共用这一份**，
/// 避免两处行为不一致（与 ImportCommand 同一套路）。
/// </summary>
public sealed class ConvertCommand
{
    private readonly IConversionService _conversion;
    private readonly IBookRepository _repository;
    private readonly IConversionQueue _queue;
    private readonly ConversionTemplateStore _templates;
    private readonly IToolLocator _tools;
    private readonly IConfigStore _config;

    public ConvertCommand(
        IConversionService conversion,
        IConversionQueue queue,
        IBookRepository repository,
        ConversionTemplateStore templates,
        IToolLocator tools,
        IConfigStore config)
    {
        _conversion = conversion;
        _repository = repository;
        _queue = queue;
        _templates = templates;
        _tools = tools;
        _config = config;
    }

    /// <summary>正在转换。用于按钮置灰与防重入。</summary>
    public bool IsRunning { get; private set; }

    /// <summary>成功把一次转换排进队列后触发（书库页靠它刷新界面；产物要等队列跑完）。</summary>
    public event EventHandler? Enqueued;

    /// <summary>
    /// 当前选中的书。书库页在选中变化时同步过来；
    /// 菜单项与工具栏按钮的可用性都看它，保证"同一个作用对象"。
    /// </summary>
    public Book? CurrentBook { get; private set; }

    /// <summary>CurrentBook 变化时触发（主窗口菜单项靠它刷新可用性）。</summary>
    public event EventHandler? CurrentBookChanged;

    public bool CanRun => CurrentBook is not null;

    /// <summary>书库页选中变化时调用。传 null 表示取消选中。</summary>
    public void SetCurrentBook(Book? book)
    {
        if (ReferenceEquals(CurrentBook, book))
        {
            return;
        }

        CurrentBook = book;
        CurrentBookChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>不带书的重载：作用于当前选中的书（菜单走这条）。</summary>
    public Task RunAsync(Window owner)
        => CurrentBook is null ? Task.CompletedTask : RunAsync(owner, CurrentBook);

    public async Task RunAsync(Window owner, Book book)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(book);

        if (IsRunning)
        {
            return;
        }

        // T7-4：设置页的「默认输出格式」需要读 Config.json，所以把 config 显式传给转换窗口。
        var window = new ConvertWindow(_conversion, _queue, _templates, _tools, book, _config) { Owner = owner };

        IsRunning = true;
        try
        {
            var succeeded = window.ShowDialog() == true;
            if (succeeded)
            {
                Enqueued?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            // 转换窗口自己会把业务异常显示成人话；能漏到这里的是程序缺陷。
            Log.Error(ex, "转换窗口发生未处理的异常");
            AppDialog.Show(owner, ex.SafeMessage(), "转换失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsRunning = false;
        }

        await Task.CompletedTask.ConfigureAwait(true);
    }
}
