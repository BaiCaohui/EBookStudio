using System.Windows;
using EbookToolSuite.App.Settings;
using EbookToolSuite.App.Views;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Reader.WebView;
using Microsoft.Extensions.Logging;
using Serilog;

namespace EbookToolSuite.App.Commands;

/// <summary>
/// 打开阅读窗口。书库详情浮层的「阅读」按钮与将来的菜单项共用这一份，
/// 避免两处行为不一致（与 ImportCommand / ConvertCommand 同一套路）。
/// </summary>
/// <remarks>
/// 【为什么不限制只开一个窗口】WebView2 环境是 DI 单例，多个阅读窗口共享同一个
/// userDataFolder 是官方支持的用法（同一个环境配多个控件）。
/// 想同时对照看两本书是正常需求，没必要人为禁止。
/// </remarks>
public sealed class ReadCommand
{
    private readonly IReaderService _reader;
    private readonly IWebViewEnvironmentProvider _environments;
    private readonly IReadingProgressService _progress;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>阅读模式的持久化入口（T4-1：分页模式要记住）。</summary>
    private readonly IConfigStore _config;
    private readonly IEpubWorkspaceFactory _workspaces;
    private readonly IChapterSourceService _source;
    private readonly IVisualEditAutoFixService _autofix;
    private readonly IReaderAssets _assets;
    private readonly IBookRepository _repository;
    private readonly IChapterFindReplaceService _findReplace;
    private readonly IWorkspaceSnapshotService? _snapshots;

    /// <summary>T6-1 判据 4：转换失败时用系统默认程序打开源文件。</summary>
    private readonly IShellLauncher? _shell;

    /// <summary>
    /// T6-2B：PDF 自绘引擎要用 pdfium 读页数、渲染每一页、取文本层与目录。
    ///
    /// 【这里曾经漏传】T6-2 给 ReaderWindow 加了 pdfPages 参数，但生产路径（本命令）
    /// 一直没传，于是那里 _pdfPages 恒为 null：页数恒 0、百分比恒"--"（只有冒烟脚本传了）。
    /// T6-2B 整个渲染链路都建立在 pdfium 之上，这个漏传必须先补掉。
    /// </summary>
    private readonly IPdfDocumentService? _pdfPages;

    /// <summary>T6-3：漫画（CBZ/CBR）按页读图。与 pdfPages 一样必须从生产路径传进来。</summary>
    private readonly IComicBookService? _comics;

    /// <summary>T6-5：书签与笔记。阅读窗口用它读写书签/笔记并导出 Markdown。</summary>
    private readonly IAnnotationService? _annotations;

    /// <summary>阅读器目录抽屉的「编辑目录」要打开的编辑器命令（可空，冒烟可省）。</summary>
    private readonly EditCommand? _edit;

    /// <summary>T7-3B：阅读会话采集。可空（单测 / 冒烟构造命令时可以不传）。</summary>
    private readonly IReadingSessionService? _stats;

    /// <summary>
    /// 书库扫描服务（用户 2026-10-20 要求）：TXT 全文编辑器保存后同步书库基线用，
    /// 免得书库把内部编辑报成「文件被外部修改」。可空（冒烟构造命令时可以不传）。
    /// </summary>
    private readonly ILibraryScanService? _libraryScan;

    public ReadCommand(
        IReaderService reader,
        IWebViewEnvironmentProvider environments,
        IReadingProgressService progress,
        ILoggerFactory loggerFactory,
        IConfigStore config,
        IEpubWorkspaceFactory workspaces,
        IChapterSourceService source,
        IVisualEditAutoFixService autofix,
        IReaderAssets assets,
        IBookRepository repository,
        IChapterFindReplaceService findReplace,
        IWorkspaceSnapshotService? snapshots = null,
        IShellLauncher? shell = null,
        IPdfDocumentService? pdfPages = null,
        IComicBookService? comics = null,
        IAnnotationService? annotations = null,
        EditCommand? edit = null,
        IReadingSessionService? stats = null,
        ILibraryScanService? libraryScan = null)
    {
        _reader = reader;
        _environments = environments;
        _progress = progress;
        _loggerFactory = loggerFactory;
        _config = config;
        _workspaces = workspaces;
        _source = source;
        _autofix = autofix;
        _assets = assets;
        _repository = repository;
        _findReplace = findReplace;
        _snapshots = snapshots;
        _shell = shell;
        _pdfPages = pdfPages;
        _comics = comics;
        _annotations = annotations;
        _edit = edit;
        _stats = stats;
        _libraryScan = libraryScan;
    }

    /// <summary>
    /// 阅读窗口已关闭、进度已经落盘。参数是被关闭的那本书。
    /// 书库页订阅它来刷新进度角标（关掉阅读窗口后网格要自己跟上，不用切页）。
    /// </summary>
    public event Action<Guid>? Closed;

    /// <summary>
    /// 打开阅读窗口。
    /// </summary>
    /// <param name="owner">父窗口。</param>
    /// <param name="book">要读的书（这里只用 Id，全文搜索命中时可用只带 Id 的壳对象）。</param>
    /// <param name="startIndex">EPUB/TXT 要直接打开的章号（0 基）。</param>
    /// <param name="startPage">PDF/漫画要直接落到的那一页（0 基；null = 按保存的进度）。</param>
    /// <param name="findText">打开后要自动高亮的词（T6-4 从全文搜索跳过来时带）。</param>
    public void Run(
        Window owner,
        Book book,
        int startIndex = 0,
        int? startPage = null,
        string? findText = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(book);

        try
        {
            var bookId = book.Id;

            var window = new ReaderWindow(
                _reader,
                _environments,
                _progress,
                _loggerFactory.CreateLogger<ReaderWindow>(),
                bookId,
                startIndex: startIndex,
                config: _config,
                workspaces: _workspaces,
                source: _source,
                autofix: _autofix,
                assets: _assets,
                repository: _repository,
                findReplace: _findReplace,
                snapshots: _snapshots,
                shell: _shell,
                pdfPages: _pdfPages,
                comics: _comics,
                startPage: startPage,
                initialFind: findText,
                annotations: _annotations,
                edit: _edit,
                stats: _stats,
                libraryScan: _libraryScan);

            // 关窗后要把这本书的进度补写落盘，再通知订阅者（书库页靠它刷新进度角标）。
            window.Closed += (_, _) => OnReaderWindowClosed(bookId);

            /* 【不能设 Owner】WPF 里被拥有的窗口会跟着拥有者一起最小化：
               用户在任务栏把主界面最小化，正在读的窗口也被一起收走（用户报的 bug）。
               阅读窗口必须是独立顶层窗口，所以这里不设 Owner；为了不让它
               "开在主窗口后面像没反应"，位置按主窗口居中算，Show 之后再显式激活。 */
            PositionOverOwner(window, owner);
            window.Show();
            window.Activate();

            /* T7-4：记住最后打开的书（设置页「常规 → 启动时打开上次的书」要用）。
               写配置失败只记 WRN，绝不能影响正在读的人。 */
            _ = RememberLastBookAsync(bookId);

            /* 原来"主窗口关闭 → 阅读窗口一起关闭"是靠 Owner 关系自动完成的。
               去掉 Owner 后要显式补回来，否则关掉主窗口进程会只剩阅读窗口，
               跟以前行为不一致，也少了一次关窗落盘与书库刷新。
               窗口自己关掉时把监听摘掉，避免主窗口的事件里越攒越多已关闭的阅读窗口。 */
            void CloseWithOwner(object? sender, EventArgs e) => window.Close();

            owner.Closed += CloseWithOwner;
            window.Closed += (_, _) => owner.Closed -= CloseWithOwner;
        }
        catch (Exception ex)
        {
            // 能走到这里的是程序缺陷（业务异常由阅读窗口自己弹人话提示）
            Log.Error(ex, "打开阅读窗口失败");
            AppDialog.Show(
                owner,
                "打开阅读器失败，详情见日志。",
                "阅读",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    /// <summary>T7-4：把"最后打开的书"记进 Config.json（失败不影响阅读）。</summary>
    private async Task RememberLastBookAsync(Guid bookId)
    {
        try
        {
            await _config.SetAsync(AppSettingsKeys.LastBookId, System.Text.Json.Nodes.JsonValue.Create(bookId.ToString()))
                .ConfigureAwait(true);
            await _config.SaveAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "记录最后打开的书失败（不影响阅读）");
        }
    }

    /// <summary>关窗后：先把进度强制落盘，再通知订阅者。</summary>    /// <remarks>
    /// 【为什么必须先 FlushAsync】ReaderWindow.OnClosed 只是 ScheduleSave（1.5 秒防抖），
    /// 关窗那一刻库里多半还是旧值。书库页收到通知会立刻重读进度，
    /// 不先落盘的话网格上显示的是 1.5 秒之前的位置，看起来像"根本没刷新"。
    /// 【为什么直接挂 Window.Closed】ReaderWindow 在构造函数里就挂了 OnClosed，
    /// 而事件处理器按注册顺序调用，所以走到这里时那次 ScheduleSave 一定已经执行过。
    /// </remarks>
    private async void OnReaderWindowClosed(Guid bookId)
    {
        try
        {
            await _progress.FlushAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // 进度没补上不该影响别的窗口，更不该影响界面刷新
            Log.Warning(ex, "关窗后补写阅读进度失败 {BookId}", bookId);
        }

        try
        {
            Closed?.Invoke(bookId);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "阅读窗口关闭后的通知处理失败 {BookId}", bookId);
        }
    }

    /// <summary>
    /// 把阅读窗口摆到主窗口正中（代替原来的 WindowStartupLocation=CenterOwner）。
    /// </summary>
    private static void PositionOverOwner(Window window, Window owner)
    {
        // 最小化时 Left/Top/ActualWidth 不可靠，用还原边界；最大化时用真实边界
        // （用 RestoreBounds 会让窗口居中到"还原后的大小"，跟以前居中于主窗口不一致）。
        var bounds = owner.WindowState == WindowState.Minimized
            ? owner.RestoreBounds
            : new Rect(owner.Left, owner.Top, owner.ActualWidth, owner.ActualHeight);

        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            // 主窗口还没有真实尺寸（冒烟/极端时序）：退回到屏幕居中
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = bounds.Left + Math.Max(0, (bounds.Width - window.Width) / 2);
        window.Top = bounds.Top + Math.Max(0, (bounds.Height - window.Height) / 2);
    }
}
