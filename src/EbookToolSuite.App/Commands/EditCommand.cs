using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using EbookToolSuite.App.ViewModels;
using EbookToolSuite.App.Views;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core;
using EbookToolSuite.Core.Errors;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Engines.Comics;
using EbookToolSuite.Reader.WebView;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Engines.Epub;
using Serilog;

namespace EbookToolSuite.App.Commands;

/// <summary>
/// 打开书籍编辑器窗口（阶段 3 的元数据 / 封面编辑 UI 入口）。
/// 三个入口共用这一份：卡片右键菜单「编辑书籍」、详情浮层的「编辑」、工具栏的「编辑」，
/// 避免三处行为不一致（与 ImportCommand / ConvertCommand / ReadCommand 同一套路）。
/// </summary>
/// <remarks>
/// 【为什么同一本书只开一个窗口】两个工作区同时写同一个 EPUB，后保存的会把先保存的覆盖掉，
/// 而且谁也看不懂自己屏幕上那份是哪个版本。已经开着就直接前置。
/// 【为什么限制 2 个窗口】设计文档 9.1 的窗口策略（编辑器非单例，最多 2 个）：
/// 每个窗口都持有一份解包好的全书工作区，开太多内存吃不消。
/// </remarks>
public sealed class EditCommand
{
    /// <summary>同时打开的编辑器窗口上限（设计文档 9.1）。</summary>
    public const int MaxWindows = 2;

    private readonly IEpubWorkspaceFactory _workspaces;
    private readonly CoverService _covers;
    private readonly IEpubSaveService _save;
    private readonly ILibraryCoverService _coverCache;
    private readonly ILibraryScanService _scan;
    private readonly IChapterSourceService _source;
    private readonly IVisualEditAutoFixService _autofix;
    private readonly IWebViewEnvironmentProvider _environments;
    private readonly IReaderAssets _assets;
    private readonly IBookRepository _repo;
    private readonly IOperationLogRepository _operations;
    private readonly IChapterTreeService _chapters;
    private readonly IWorkspaceSnapshotService? _snapshots;
    private readonly IResourceScanService? _scans;
    private readonly ICbzEditService _cbzEdits;
    private readonly IConfigStore _config;
    private readonly List<BookEditorWindow> _windows = new();

    /// <summary>TXT 全文编辑器窗口（与 EPUB 编辑器分开计数：两者不是同一类窗口）。</summary>
    private readonly List<TxtEditorWindow> _txtWindows = new();

    public EditCommand(
        IEpubWorkspaceFactory workspaces,
        CoverService covers,
        IEpubSaveService save,
        ILibraryCoverService coverCache,
        ILibraryScanService scan,
        IChapterSourceService source,
        IVisualEditAutoFixService autofix,
        IWebViewEnvironmentProvider environments,
        IReaderAssets assets,
        IBookRepository repo,
        IOperationLogRepository operations,
        IChapterTreeService chapters,
        ICbzEditService cbzEdits,
        IConfigStore config,
        IWorkspaceSnapshotService? snapshots = null,
        IResourceScanService? scans = null)
    {
        _workspaces = workspaces;
        _covers = covers;
        _save = save;
        _coverCache = coverCache;
        _scan = scan;
        _source = source;
        _autofix = autofix;
        _environments = environments;
        _assets = assets;
        _repo = repo;
        _operations = operations;
        _chapters = chapters;
        _cbzEdits = cbzEdits;
        _config = config;
        _snapshots = snapshots;
        _scans = scans;
    }

    /// <summary>sample保存成功（参数是书 Id）。书库页订阅它刷新卡片与封面。</summary>
    public event Action<Guid>? Saved;

    /// <summary>当前打开的编辑器窗口数（冒烟测试与将来的菜单状态用）。</summary>
    public int OpenWindowCount => _windows.Count;

    public void Run(Window owner, Book book)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(book);

        // 阶段 3 只编辑 EPUB：别的格式（MOBI/AZW3）没有 OPF 工作区这一说
        var format = book.Formats.FirstOrDefault(f => f.Format == BookFormatKind.Epub && !f.Missing);

        if (format is null)
        {
            /* 用户 2026-10-21：书库里的 CBZ 也能进编辑模式。
               走的是「打包图片文件夹为 CBZ」那个窗口：先解压现有 CBZ 到临时工作区，
               保存时重新打包并覆盖原文件（先备份）。 */
            var cbz = book.Formats.FirstOrDefault(f => f.Format == BookFormatKind.Cbz && !f.Missing);

            if (cbz is not null)
            {
                if (string.IsNullOrWhiteSpace(cbz.FilePath) || !File.Exists(cbz.FilePath))
                {
                    AppDialog.Show(
                        owner,
                        "CBZ 文件不在磁盘上了，先「接受改动」或重新导入再编辑。",
                        "编辑书籍",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                // 解压可能读几百 MB：异步跑，别卡住 UI 线程
                _ = RunCbzEditorAsync(owner, book, cbz);
                return;
            }

            /* 用户 2026-10-20：TXT 也要能进编辑模式。TXT 没有 OPF 工作区，
               编辑方式是"整本 txt 当纯文本改"（像记事本），所以走另一个窗口。 */
            var txt = book.Formats.FirstOrDefault(f => f.Format == BookFormatKind.Txt && !f.Missing);

            if (txt is not null)
            {
                OpenTextEditor(owner, book, txt);
                return;
            }

            AppDialog.Show(
                owner,
                "这本书没有可编辑的文件（目前能编辑 EPUB、CBZ 与 TXT）。",
                "编辑书籍",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(format.FilePath) || !File.Exists(format.FilePath))
        {
            AppDialog.Show(
                owner,
                "EPUB 文件不在磁盘上了，先「接受改动」或重新导入再编辑。",
                "编辑书籍",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        // 同一本书已经开着：前置它，别开第二个工作区
        var existing = _windows.FirstOrDefault(w => w.BookId == book.Id);
        if (existing is not null)
        {
            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }

            existing.Activate();
            return;
        }

        if (_windows.Count >= MaxWindows)
        {
            AppDialog.Show(
                owner,
                "编辑器最多同时打开 " + MaxWindows + " 个窗口，请先关掉一个。",
                "编辑书籍",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            var vm = new BookEditorViewModel(
                book,
                format,
                _workspaces,
                _covers,
                _save,
                _coverCache,
                _scan,
                _repo,
                _operations,
                _chapters,
                _snapshots);

            var window = new BookEditorWindow(vm, _source, _autofix, _environments, _assets, _snapshots, _scans)
            {
                Owner = owner,
            };

            window.Saved += OnWindowSaved;
            window.Closed += (_, _) => _windows.Remove(window);

            _windows.Add(window);

            // 非模态：编辑时还要能回头看网格（设计文档 9.1 的独立窗口策略）
            window.Show();
        }
        catch (Exception ex)
        {
            // 能走到这里的是程序缺陷（业务异常由窗口自己弹人话提示）
            Log.Error(ex, "打开书籍编辑器失败 {BookId}", book.Id);
            AppDialog.Show(owner, "打开编辑器失败，详情见日志。", "编辑书籍",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 打开 TXT 全文编辑器（用户 2026-10-20 要求：TXT 也能进编辑模式，整本内容像记事本一样直接改）。
    /// 同一本书只开一个窗口；保存后发 <see cref="Saved"/>，书库页据此刷新卡片。
    /// 阅读器里的「编辑本章」走的是同一个窗口，只是把光标落到当前章标题行。
    /// </summary>
    private void OpenTextEditor(Window owner, Book book, BookFormat format)
    {
        if (string.IsNullOrWhiteSpace(format.FilePath) || !File.Exists(format.FilePath))
        {
            AppDialog.Show(
                owner,
                "TXT 文件不在磁盘上了，先「接受改动」或重新导入再编辑。",
                "编辑书籍",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var existing = _txtWindows.FirstOrDefault(w => w.BookId == book.Id && w.IsLoaded);

        if (existing is not null)
        {
            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }

            existing.Activate();
            return;
        }

        if (_txtWindows.Count >= MaxWindows)
        {
            AppDialog.Show(
                owner,
                "编辑器最多同时打开 " + MaxWindows + " 个窗口，请先关掉一个。",
                "编辑书籍",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            var window = new TxtEditorWindow(
                book.Id,
                book.Title,
                format.FilePath,
                TxtBookEncoding.Get(_config, book.Id),
                config: _config,
                libraryScan: _scan)
            {
                Owner = owner,
            };

            window.Saved += (_, _) => OnWindowSaved(book.Id);
            window.Closed += (_, _) => _txtWindows.Remove(window);

            _txtWindows.Add(window);
            window.Show();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "打开 TXT 全文编辑器失败 {BookId}", book.Id);
            AppDialog.Show(owner, "打开编辑器失败，详情见日志。", "编辑书籍",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 编辑书库里已有的 CBZ：解压 → 复用「编辑 CBZ 信息」窗口 → 重新打包覆盖原文件。
    /// </summary>
    /// <remarks>
    /// 【为什么是模态 ShowDialog】编辑窗必须等用户改完（或取消）才能确定工作区怎么收尾；
    /// 非模态会让临时工作区的生命周期跟着窗口散落到各处。
    /// 【保存链路】新文件先写到工作区 → 备份原件到 Data/Backup/Files → 原子替换
    /// → 回写 books 元数据 → 更新 book_formats 基线（否则书库扫描把这报告成「外部修改」）
    /// → 重渲封面（第一页可能因调序而变）→ 通知书库刷新。
    /// </remarks>
    private async Task RunCbzEditorAsync(Window owner, Book book, BookFormat format)
    {
        CbzEditWorkspace? workspace = null;

        try
        {
            Mouse.OverrideCursor = Cursors.Wait;

            try
            {
                workspace = await _cbzEdits.OpenAsync(format.FilePath).ConfigureAwait(true);
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }

            var window = new CbzPackEditWindow(
                workspace.Folder,
                workspace.Images,
                workspace.Metadata,
                format.FilePath,
                overwriteOriginal: true)
            {
                Owner = owner,
            };

            if (window.ShowDialog() != true || window.Result is null)
            {
                return;
            }

            var request = window.Result;

            // 标题是书库必填项：用户清空了就用原书名顶上，别把库里书名写成空。
            var metadata = request.Metadata with
            {
                Title = string.IsNullOrWhiteSpace(request.Metadata.Title)
                    ? book.Title
                    : request.Metadata.Title.Trim(),
            };

            var saved = await _cbzEdits
                .SaveAsync(workspace, request.Entries, metadata)
                .ConfigureAwait(true);

            await _repo.UpdateMetadataAsync(BuildMetadataUpdate(book, metadata)).ConfigureAwait(true);

            await _scan
                .RecordInternalModificationAsync(book.Id, BookFormatKind.Cbz)
                .ConfigureAwait(true);

            await RefreshComicCoverAsync(book.Id, saved.OutputPath).ConfigureAwait(true);

            OnWindowSaved(book.Id);

            AppDialog.Show(
                owner,
                "已保存并重新打包。\n\n" + saved.OutputPath,
                "编辑完成",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (AppException ex)
        {
            Log.Warning(ex, "CBZ 编辑失败 {Code} {BookId}", ex.Code, book.Id);
            AppDialog.Show(owner, ex.UserMessage, "编辑 CBZ", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "CBZ 编辑时发生未预期的异常 {BookId}", book.Id);
            AppDialog.Show(owner, "编辑 CBZ 失败：" + ex.SafeMessage(), "编辑 CBZ",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Mouse.OverrideCursor = null;

            if (workspace is not null)
            {
                _cbzEdits.Cleanup(workspace);
            }
        }
    }

    /// <summary>把 ComicInfo 元数据翻成 books 表要回写的那几列（Rating 原样保留）。</summary>
    private static BookMetadataUpdate BuildMetadataUpdate(Book book, ComicMetadata metadata)
    {
        return new BookMetadataUpdate(
            book.Id,
            metadata.Title ?? book.Title,

            /* 作者要按导入时的同一口径取：导入认 Writer ?? Penciller（很多漫画只有画师署名）。
               只写 Writer 的话，「原来靠 Penciller 进来的作者」会被这次保存清空。
               两者都空时退回库里原值：用户没动过作者，不该因为编辑就丢。 */
            Authors: FirstNonEmpty(metadata.Writer, metadata.Penciller, book.Authors),

            Publisher: metadata.Publisher,
            PublishedDate: BuildPublishedDate(metadata),
            Isbn: metadata.Isbn,
            Language: metadata.LanguageISO,
            Description: metadata.Summary,
            Series: metadata.Series,
            SeriesIndex: ParseSeriesIndex(metadata.Number),
            Rating: book.Rating);
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>ComicInfo 的 Year/Month/Day 三个可选整数 → 书库的 DateOnly（只有年就补 1 月 1 日）。</summary>
    private static string? BuildPublishedDate(ComicMetadata metadata)
    {
        if (metadata.Year is not int year || year is < 1 or > 9999)
        {
            return null;
        }

        var month = metadata.Month is int m and >= 1 and <= 12 ? m : 1;
        var day = metadata.Day is int d && d >= 1 && d <= DateTime.DaysInMonth(year, month) ? d : 1;

        return new DateOnly(year, month, day).ToString("yyyy-MM-dd");
    }

    /// <summary>ComicInfo 的 Number 是字符串（"1"、"1.5"），解析不了就放弃序号，不拿 0 冒充。</summary>
    private static double? ParseSeriesIndex(string? number)
    {
        if (string.IsNullOrWhiteSpace(number))
        {
            return null;
        }

        return double.TryParse(number.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            && double.IsFinite(value)
            ? value
            : null;
    }

    /// <summary>
    /// 重打包后封面可能变了（第一页被换掉/调序）：按新第一页重写书库封面缓存。
    /// 渲不出来只是旧封面继续用，绝不让它拖垮「保存成功」这件事本身。
    /// </summary>
    private async Task RefreshComicCoverAsync(Guid bookId, string cbzPath)
    {
        try
        {
            using var archive = ComicArchive.Open(cbzPath);

            if (archive.PageCount == 0)
            {
                return;
            }

            await _coverCache
                .RefreshAsync(bookId, archive.ReadPage(0))
                .ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "重打包后刷新 CBZ 封面失败 {BookId}", bookId);
        }
    }

    private void OnWindowSaved(Guid bookId)
    {
        try
        {
            Saved?.Invoke(bookId);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "编辑器保存后的通知处理失败 {BookId}", bookId);
        }
    }
}
