using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using EbookToolSuite.App.ViewModels;
using EbookToolSuite.Core.Errors;
using EbookToolSuite.Core.Exceptions;
using Serilog;
using Microsoft.Win32;
using EbookToolSuite.Core;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Reader.WebView;

namespace EbookToolSuite.App.Views;

/// <summary>
/// 书籍编辑器窗口（阶段 3 的 UI 入口：元数据 + 封面）。
/// 独立窗口、非模态、可多开（设计文档 9.1），窗口本身只管控件与弹窗，
/// 业务全在 <see cref="BookEditorViewModel"/> 里。
/// </summary>
/// <remarks>
/// 【为什么非模态】编辑中途要回头看网格里别的书是常态；
/// 也因为非模态，冒烟测试能开着窗口直接驱动视图模型（模态会卡住测试线程）。
/// 【关闭流程】X 按钮与两个按钮都汇到 CloseFlowAsync：
/// 脏了就按设计文档 9.4.4 问一句，然后一定清工作区再关。
/// </remarks>
public partial class BookEditorWindow : Window
{
    private readonly BookEditorViewModel _vm;
    private readonly IChapterSourceService _source;
    private readonly IVisualEditAutoFixService _autofix;
    private readonly IWebViewEnvironmentProvider _environments;
    private readonly IReaderAssets _assets;
    private readonly IWorkspaceSnapshotService? _snapshots;
    private readonly IResourceScanService? _scans;

    /// <summary>Serilog 的 ILogger（App 只引用了 Logging.Abstractions）。</summary>
    private readonly ILogger _log = Log.ForContext<BookEditorWindow>();

    private bool _loaded;
    private bool _closeConfirmed;

    /// <summary>冒烟测试直接驱动视图模型用（与书库页暴露 ViewModel 的做法一致）。</summary>
    internal BookEditorViewModel ViewModel => _vm;

    public BookEditorWindow(
        BookEditorViewModel vm,
        IChapterSourceService source,
        IVisualEditAutoFixService autofix,
        IWebViewEnvironmentProvider environments,
        IReaderAssets assets,
        IWorkspaceSnapshotService? snapshots = null,
        IResourceScanService? scans = null)
    {
        _vm = vm;
        _source = source;
        _autofix = autofix;
        _environments = environments;
        _assets = assets;
        _snapshots = snapshots;
        _scans = scans;

        InitializeComponent();
        DataContext = vm;

        Loaded += OnLoadedAsync;
        Closing += OnWindowClosing;
    }

    /// <summary>被编辑的书。EditCommand 用它做去重（同一本书不重复开窗）。</summary>
    public Guid BookId => _vm.Book.Id;

    /// <summary>保存成功后触发，参数是书的 Id。书库页靠它刷新卡片与封面。</summary>
    public event Action<Guid>? Saved;

    private async void OnLoadedAsync(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;

        try
        {
            await _vm.LoadAsync();
        }
        catch (AppException ex)
        {
            // DRM、EPUB 结构坏了之类：这是业务异常，给用户一句人话，然后关窗
            _log.Warning(ex, "打开编辑工作区失败 {BookId} {Code}", BookId, ex.Code);
            AppDialog.Show(this, ex.UserMessage, "无法编辑这本书", MessageBoxButton.OK, MessageBoxImage.Warning);
            CloseWithoutConfirm();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "打开编辑工作区失败（未预期）{BookId}", BookId);
            AppDialog.Show(this, "打开编辑器失败，详情见日志。", "编辑书籍",
                MessageBoxButton.OK, MessageBoxImage.Error);
            CloseWithoutConfirm();
        }
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_vm.IsBusy)
        {
            return;
        }

        try
        {
            var summary = await _vm.SaveAsync();

            if (_vm.HasPendingConfirmation)
            {
                // EpubCheck 拦下来了（或超时）：面板已经显示，等用户点「仍然保存」/「取消」。
                // 这里绝不能当成功，更不能通知书库刷新文件还没换。
                _log.Information("EpubCheck 门禁拦下保存，等用户决定 {BookId} {Summary}", BookId, summary);
                return;
            }

            _log.Information("编辑保存成功 {BookId} {Summary}", BookId, summary);
            Saved?.Invoke(BookId);
        }
        catch (AppException ex)
        {
            _log.Warning(ex, "编辑保存失败 {BookId} {Code}", BookId, ex.Code);
            AppDialog.Show(this, ex.UserMessage, "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "编辑保存失败（未预期）{BookId}", BookId);
            AppDialog.Show(this, "保存失败，详情见日志。", "保存失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>用户在「校验没过」面板上点了「仍然保存（不推荐）」。</summary>
    private async void OnConfirmSaveClick(object sender, RoutedEventArgs e)
    {
        if (_vm.IsBusy)
        {
            return;
        }

        try
        {
            var summary = await _vm.ConfirmSaveAnywayAsync();
            _log.Information("用户选择仍然保存 {BookId} {Summary}", BookId, summary);
            Saved?.Invoke(BookId);
        }
        catch (AppException ex)
        {
            _log.Warning(ex, "强制保存失败 {BookId} {Code}", BookId, ex.Code);
            AppDialog.Show(this, ex.UserMessage, "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "强制保存失败（未预期）{BookId}", BookId);
            AppDialog.Show(this, "保存失败，详情见日志。", "保存失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>用户在面板上点了「一键修复」：补 manifest 后自动重试保存。</summary>
    private async void OnAutoFixClick(object sender, RoutedEventArgs e)
    {
        if (_vm.IsBusy)
        {
            return;
        }

        try
        {
            var summary = await _vm.AutoFixAndRetryAsync();
            _log.Information("一键修复后重试保存 {BookId} {Summary}", BookId, summary);

            if (!_vm.HasPendingConfirmation)
            {
                Saved?.Invoke(BookId);
            }
        }
        catch (AppException ex)
        {
            _log.Warning(ex, "一键修复失败 {BookId} {Code}", BookId, ex.Code);
            AppDialog.Show(this, ex.UserMessage, "修复失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "一键修复失败（未预期）{BookId}", BookId);
            AppDialog.Show(this, "修复失败，详情见日志。", "修复失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>用户在面板上点了「取消」：丢掉暂存产物，原文件不动，工作区里的改动还在。</summary>
    private void OnCancelSaveClick(object sender, RoutedEventArgs e) => _vm.CancelPendingSave();

    private async void OnReplaceCoverClick(object sender, RoutedEventArgs e)
    {
        if (_vm.IsBusy)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "选择封面图片",
            Filter = "图片 (*.jpg;*.jpeg;*.png;*.webp)|*.jpg;*.jpeg;*.png;*.webp|所有文件 (*.*)|*.*",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(dialog.FileName);
            await _vm.ReplaceCoverAsync(bytes);
        }
        catch (AppException ex)
        {
            AppDialog.Show(this, ex.UserMessage, "封面替换失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "封面替换失败 {BookId}", BookId);
            AppDialog.Show(this, "封面替换失败，详情见日志。", "封面替换失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnDiscardClick(object sender, RoutedEventArgs e)
    {
        var answer = AppDialog.Show(
            this,
            "放弃工作区里的所有修改并关闭？原文件不会被改动。",
            "放弃修改",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        await FinishAndCloseAsync();
    }

    // ---------- 章节树（T5-1）：全部只改工作区，点【保存】才写进书里 ----------

    private async void OnChapterRenameClick(object sender, RoutedEventArgs e)
        => await RunChapterAsync("重命名章节", _vm.Chapters.RenameAsync());

    private async void OnChapterUpClick(object sender, RoutedEventArgs e)
        => await RunChapterAsync("上移章节", _vm.Chapters.MoveUpAsync());

    private async void OnChapterDownClick(object sender, RoutedEventArgs e)
        => await RunChapterAsync("下移章节", _vm.Chapters.MoveDownAsync());

    private async void OnChapterDeleteClick(object sender, RoutedEventArgs e)
        => await RunChapterAsync("删除章节", _vm.Chapters.DeleteAsync());

    private async void OnChapterAddClick(object sender, RoutedEventArgs e)
        => await RunChapterAsync("新增章节", _vm.Chapters.AddAsync());

    private async void OnChapterPromoteClick(object sender, RoutedEventArgs e)
        => await RunChapterAsync("升级章节", _vm.Chapters.PromoteAsync());

    private async void OnChapterDemoteClick(object sender, RoutedEventArgs e)
        => await RunChapterAsync("降级章节", _vm.Chapters.DemoteAsync());

    /// <summary>打开可视编辑（T5-2W）：同样只改工作区，落盘仍由本窗口的【保存】负责。</summary>
    private void OnChapterVisualClick(object sender, RoutedEventArgs e)
    {
        var workspace = _vm.Chapters.Workspace;

        if (workspace is null || _vm.Chapters.Items.Count == 0)
        {
            return;
        }

        var index = Math.Max(_vm.Chapters.SelectedIndex, 0);
        var chapters = _vm.Chapters.Items
            .Select(item => new SourceChapter(item.Index, item.Href, item.Title))
            .ToList();

        new ChapterVisualEditorWindow(
            _source, _autofix, _environments, _assets, workspace, chapters, index, _snapshots)
        {
            Owner = this,
        }.Show();
    }

    /// <summary>打开源码视图（T5-2）：源码窗口只改工作区，落盘仍由本窗口的【保存】负责。</summary>
    private void OnChapterSourceClick(object sender, RoutedEventArgs e)
    {
        var workspace = _vm.Chapters.Workspace;

        if (workspace is null || _vm.Chapters.Items.Count == 0)
        {
            return;
        }

        var index = _vm.Chapters.SelectedIndex;

        if (index < 0)
        {
            index = 0;
        }

        var chapters = _vm.Chapters.Items
            .Select(item => new SourceChapter(item.Index, item.Href, item.Title))
            .ToList();

        new ChapterSourceWindow(_source, workspace, chapters, index)
        {
            Owner = this,
        }.Show();
    }

    private async void OnChapterSplitClick(object sender, RoutedEventArgs e)
        => await RunChapterAsync("拆分章节", _vm.Chapters.SplitAsync());

    private async void OnChapterMergeClick(object sender, RoutedEventArgs e)
        => await RunChapterAsync("合并章节", _vm.Chapters.MergeAsync());

    private async void OnChapterRevertClick(object sender, RoutedEventArgs e)
        => await RunChapterAsync("回到会话开始", _vm.Chapters.RevertAsync());

    private async void OnChapterImportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要导入的章节文件",
            Filter = "章节文件 (*.xhtml;*.html;*.txt)|*.xhtml;*.html;*.txt|全部文件 (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await RunChapterAsync("导入章节", _vm.Chapters.ImportAsync(dialog.FileName));
    }

    /// <summary>
    /// 章节操作的失败在视图模型里已经被翻译成人话（状态条会显示），这里只兜住真正的意外。
    /// </summary>
    private async Task RunChapterAsync(string what, Task task)
    {
        try
        {
            await task;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "{What} 失败", what);
            AppDialog.Show(
                this,
                what + "失败：" + ex.SafeMessage(),
                "章节结构",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (await ConfirmCloseAsync())
        {
            await FinishAndCloseAsync();
        }
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_closeConfirmed)
        {
            return;
        }

        // Closing 是同步的，而「要不要保存」得弹窗 + await，所以先取消，异步走完再真关。
        e.Cancel = true;
        _ = CloseFlowAsync();
    }

    private async Task CloseFlowAsync()
    {
        if (!await ConfirmCloseAsync())
        {
            return;
        }

        await FinishAndCloseAsync();
    }

    /// <summary>脏了就先问一句（设计文档 9.4.4）。返回 true = 可以继续关。</summary>
    private async Task<bool> ConfirmCloseAsync()
    {
        if (!_vm.IsDirty)
        {
            return true;
        }

        var answer = AppDialog.Show(
            this,
            "这本书有未保存的修改，要先保存吗？" + Environment.NewLine
                + "（选「否」= 丢掉这次修改，原文件不受影响）",
            "有未保存的修改",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);

        if (answer == MessageBoxResult.Cancel)
        {
            return false;
        }

        if (answer != MessageBoxResult.Yes)
        {
            return true;
        }

        try
        {
            await _vm.SaveAsync();

            if (_vm.HasPendingConfirmation)
            {
                // 校验没过：面板已经出现，关窗先停在原地让用户决定，别把选择吞掉。
                return false;
            }

            Saved?.Invoke(BookId);
            return true;
        }
        catch (AppException ex)
        {
            AppDialog.Show(this, ex.UserMessage, "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "关窗前保存失败 {BookId}", BookId);
            AppDialog.Show(this, "保存失败，详情见日志。", "保存失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private async Task FinishAndCloseAsync()
    {
        await _vm.DiscardAsync();
        CloseWithoutConfirm();
    }

    private void CloseWithoutConfirm()
    {
        _closeConfirmed = true;

        // 【为什么要 BeginInvoke 而不是直接 Close()】
        // 关窗流程经常整个是同步跑完的（DiscardAsync 要删的目录小的时候会同步完成），
        // 于是这一行仍然在第一个 Close() 的 Closing 事件调用栈里；WPF 的 Window.Close()
        // 看到自己「正在关」就直接 return，窗口永远关不掉（实测：工作区删了、窗口还开着）。
        // 推到下一个消息循环，让它不在 Closing 的调用栈里。
        Dispatcher.BeginInvoke(new Action(Close), DispatcherPriority.Normal);
    }
    /// <summary>T5-5：打开资源检查窗口（悬空引用 / 孤儿资源）。</summary>
    private void OnResourceScanClick(object sender, RoutedEventArgs e)
    {
        if (_scans is null || _vm.Workspace is null)
        {
            AppDialog.Show(this, "资源检查不可用（缺少服务或工作区还没打开）。", "资源检查");
            return;
        }

        new ResourceScanWindow(_scans, _vm.Workspace, _source, _snapshots) { Owner = this }.Show();
    }
}
