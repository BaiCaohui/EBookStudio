using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Input;
using EbookToolSuite.App.Commands;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Errors;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using Microsoft.Extensions.Logging;

namespace EbookToolSuite.App.ViewModels;

/// <summary>
/// 书库页的 ViewModel。规格见执行计划 T1-3。
/// </summary>
public sealed partial class LibraryViewModel : INotifyPropertyChanged
{
    /// <summary>同时最多解码 4 张封面。50 张一起上会让刚打开的界面顿一下。</summary>
    private readonly SemaphoreSlim _coverGate = new(4);

    private readonly IBookRepository _repo;
    private readonly IAppPaths _paths;
    private readonly IConfigStore _config;
    private readonly ILogger<LibraryViewModel> _log;
    private readonly IBookDeletionService _deletion;
    private readonly IDuplicateDetectionService _duplicates;
    private readonly ILibraryScanService _scan;
    private readonly ILibraryWatcher? _watcher;
    private readonly IEpubCacheService? _epubCache;

    private bool _isLoading;
    private bool _isEmpty = true;
    private string? _errorMessage;
    private int _totalCount;
    private BookCardViewModel? _detailBook;
    private BookCardViewModel? _selectedBook;

    private bool _isSelectionMode;
    private bool _isDeleting;

    public LibraryViewModel(
        IBookRepository repo,
        IAppPaths paths,
        IConfigStore config,
        ILogger<LibraryViewModel> log,
        IBookDeletionService deletion,
        IDuplicateDetectionService duplicates,
        ILibraryScanService scan,
        ILibraryWatcher? watcher = null,
        IContentSearchService? contentSearch = null,
        ILoggerFactory? loggerFactory = null,
        IEpubCacheService? epubCache = null)
    {
        _repo = repo;
        _paths = paths;
        _config = config;
        _log = log;
        _deletion = deletion;
        _duplicates = duplicates;
        _scan = scan;
        _watcher = watcher;
        _epubCache = epubCache;

        // T6-4：全库正文搜索面板。DI 里没有 IContentSearchService 时（纯单测）退化成空面板。
        FullText = new FullTextSearchViewModel(
            contentSearch, loggerFactory?.CreateLogger<FullTextSearchViewModel>());

        // T2-6：目录变化只当提示  防抖之后重扫一遍，刷新卡片上的文件状态。
        if (_watcher is not null)
        {
            _watcher.Changed += OnLibraryFolderChanged;
        }

        // T2-3 补丁八：书库视图初值。默认网格，用户可在工具栏或设置页改。
        _isListView = string.Equals(config.GetString("ui.libraryView"), "list", StringComparison.OrdinalIgnoreCase);

        SelectCommand = new RelayCommand(o => Select(o as BookCardViewModel));
        OpenDetailCommand = new RelayCommand(o => OpenDetail(o as BookCardViewModel));
        CloseDetailCommand = new RelayCommand(_ => CloseDetail());
        ToggleSelectionModeCommand = new RelayCommand(_ => ToggleSelectionMode());
        SelectAllCommand = new RelayCommand(_ => CheckAll());
        ClearCheckedCommand = new RelayCommand(_ => ClearChecked());

        /* T7-3B 用户需求：重开书库要恢复上次的搜索词 / 筛选 / 排序。
           这里只改内存字段、不触发查询；首屏由 LoadAsync 按恢复后的条件查一次，
           所以"第一次载入书库"不会反过来把已保存的状态覆盖掉。 */
        RestoreQueryState();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>T6-4：全库正文搜索面板（书库页顶栏的「全文」按钮打开它）。</summary>
    public FullTextSearchViewModel FullText { get; }

    public ObservableCollection<BookCardViewModel> Books { get; } = new();

    // ---------- 书库视图切换（T2-3 补丁八）----------

    private bool _isListView;

    /// <summary>
    /// 书库视图：false = 网格（默认），true = 列表。
    /// 初值来自 Config.ui.libraryView，切换后立刻落盘。
    /// </summary>
    public bool IsListView => _isListView;

    /// <summary>工具栏按钮上的文案：写的是「点了会切到哪个视图」。</summary>
    public string ViewToggleText => _isListView ? "网格视图" : "列表视图";

    /// <summary>
    /// 切换视图并落盘。落盘失败只记警告，本次切换仍然生效
    /// （设置存不下来不该让界面停止工作）。
    /// </summary>
    public async Task SetListViewAsync(bool listView, CancellationToken ct = default)
    {
        if (_isListView == listView)
        {
            return;
        }

        _isListView = listView;

        OnPropertyChanged(nameof(IsListView));
        OnPropertyChanged(nameof(ViewToggleText));

        try
        {
            await _config.SetAsync("ui.libraryView", JsonValue.Create(listView ? "list" : "grid"), ct)
                .ConfigureAwait(true);
            await _config.SaveAsync(ct).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "书库视图设置没能存下来，本次切换仍然生效");
        }
    }

    /// <summary>切换视图（工具栏按钮调它）。</summary>
    public Task ToggleViewAsync(CancellationToken ct = default) => SetListViewAsync(!_isListView, ct);

    /// <summary>
    /// 从配置恢复书库的查询状态（搜索词、格式 / 状态筛选、只看有封面、排序）。
    /// 无值或无法解析时回退默认；搜索词恢复后顶栏搜索框因为绑定 SearchText 自然显示。
    /// </summary>
    private void RestoreQueryState()
    {
        var savedSearch = _config.GetString(SearchConfigKey, null);
        if (!string.IsNullOrWhiteSpace(savedSearch))
        {
            _searchText = savedSearch;
        }

        var savedFormat = _config.GetString(FormatConfigKey, null);
        if (Enum.TryParse<BookFormatKind>(savedFormat, ignoreCase: true, out var format)
            && format != BookFormatKind.Unknown)
        {
            _formatOption = FormatOption.AllOptions.FirstOrDefault(o => o.Kind == format) ?? FormatOption.All;
        }

        var savedStatus = _config.GetString(StatusConfigKey, null);
        if (Enum.TryParse<ReadingStatus>(savedStatus, ignoreCase: true, out var status))
        {
            _statusOption = StatusOption.AllOptions.FirstOrDefault(o => o.Status == status) ?? StatusOption.All;
        }

        var savedSortField = _config.GetString(SortFieldConfigKey, null);
        if (Enum.TryParse<BookSort>(savedSortField, ignoreCase: true, out var sort))
        {
            var descending = _config.GetBool(SortDescendingConfigKey, false);

            _sortOption = SortOption.AllOptions.FirstOrDefault(o => o.Sort == sort && o.Descending == descending)
                ?? SortOption.AllOptions.FirstOrDefault(o => o.Sort == sort)
                ?? SortOption.Default;
        }

        _onlyWithCover = _config.GetBool(OnlyWithCoverConfigKey, false);

        // chips 与"重置筛选"可用性要按恢复后的条件显示
        RefreshChips();

        // 恢复出来的状态就当作"已写过"：用户不改条件时不会平白多写一次盘
        _lastPersistedQueryState = CurrentQueryState();
    }

    /// <summary>单击卡片：选中它（工具栏「转换」按钮靠这个决定能不能点）。</summary>
    public ICommand SelectCommand { get; }

    public ICommand OpenDetailCommand { get; }

    public ICommand CloseDetailCommand { get; }
    /// <summary>进入/退出批量选择模式（工具栏那个「批量选择」按钮）。</summary>
    public ICommand ToggleSelectionModeCommand { get; }

    /// <summary>全选当前网格里显示的书。</summary>
    public ICommand SelectAllCommand { get; }

    /// <summary>清空勾选。不退出批量模式，方便重新选。</summary>
    public ICommand ClearCheckedCommand { get; }

    /// <summary>
    /// 删除前的二次确认。默认弹一个「默认按钮是取消」的系统对话框。
    /// 做成可替换的委托，是为了让冒烟脚本能在无人值守下把「点确定」和「点取消」两条路径都走一遍；
    /// 否则删除这条路径永远进不了自动化，只能靠人手点。
    /// </summary>
    public Func<string, string, bool> ConfirmDelete { get; set; } = DefaultConfirm;

    /// <summary>删除结果提示。只在「有残留文件」或「有删除失败」时弹。同样可被冒烟替换。</summary>
    public Action<string, string> Notify { get; set; } = DefaultNotify;

    private static bool DefaultConfirm(string message, string caption)
        => AppDialog.Show(
            message, caption, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
           == MessageBoxResult.Yes;

    private static void DefaultNotify(string message, string caption)
        => AppDialog.Show(message, caption, MessageBoxButton.OK, MessageBoxImage.Information);

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (Set(ref _isLoading, value))
            {
                OnPropertyChanged(nameof(ShowEmptyState));
            }
        }
    }

    public bool IsEmpty
    {
        get => _isEmpty;
        private set
        {
            if (Set(ref _isEmpty, value))
            {
                OnPropertyChanged(nameof(ShowEmptyState));
                OnPropertyChanged(nameof(ShowGrid));
            }
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (Set(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
                OnPropertyChanged(nameof(ShowEmptyState));
                OnPropertyChanged(nameof(ShowGrid));
            }
        }
    }

    public bool HasError => _errorMessage is not null;

    public int TotalCount
    {
        get => _totalCount;
        private set
        {
            if (Set(ref _totalCount, value))
            {
                OnPropertyChanged(nameof(CountText));
            }
        }
    }

    /// <summary>顶栏那行"共 N 本"。
    public string CountText => Books.Count == 0
        ? string.Empty
        : _hasFilters
            ? "命中 " + Books.Count + " / 共 " + _totalCount + " 本"
            : Books.Count < _totalCount
                ? "显示 " + Books.Count + " / 共 " + _totalCount + " 本"
                : "共 " + _totalCount + " 本";

    public BookCardViewModel? DetailBook
    {
        get => _detailBook;
        private set
        {
            if (Set(ref _detailBook, value))
            {
                OnPropertyChanged(nameof(IsDetailOpen));
            }
        }
    }

    public bool IsDetailOpen => _detailBook is not null;

    public bool ShowEmptyState => _isEmpty && !_isLoading && !HasError;

    public bool ShowGrid => !_isEmpty && !HasError;
    /// <summary>重新读一遍书库。每次导航到书库页都会调一次。</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        IsLoading = true;
        ErrorMessage = null;

        // 重新加载后列表里的卡片对象是新的，旧的选中引用必须丢掉，否则会指向已经不显示的卡片。
        SelectedBook = null;
        DetailBook = null;
        IsSelectionMode = false;

        try
        {
            TotalCount = await _repo.CountQueryAsync(new BookQuery()).ConfigureAwait(true);

            // T2-6：第一次进书库时后台扫一遍文件状态（不 await，别挡首屏）。
            // 之后的刷新与目录变化提示各自负责重扫。
            if (_scan.LastReport is null)
            {
                _ = RunStartupScanAsync();
            }

            await ApplyQueryAsync(ct).ConfigureAwait(true);

            _log.LogInformation("书库已加载 {Shown} / {Total} 本", Books.Count, TotalCount);
        }
        catch (OperationCanceledException)
        {
            // 切页导致的取消，不算错误
        }
        catch (Exception ex)
        {
            ErrorMessage = "书库读不出来：" + ex.SafeMessage();
            _log.LogError(ex, "加载书库失败");
        }
        finally
        {
            IsLoading = false;
        }


    }

    /// <summary>当前选中的书。未选中时为 null。</summary>
    public BookCardViewModel? SelectedBook
    {
        get => _selectedBook;
        private set
        {
            if (ReferenceEquals(_selectedBook, value))
            {
                return;
            }

            if (_selectedBook is not null)
            {
                _selectedBook.IsSelected = false;
            }

            _selectedBook = value;
            if (_selectedBook is not null)
            {
                _selectedBook.IsSelected = true;
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelection));
        }
    }

    public bool HasSelection => _selectedBook is not null;

    /// <summary>
    /// 单击卡片。普通模式 = 选中它（工具栏「转换」按钮靠这个决定能不能点）；
    /// 批量模式 = 切换这一本的勾选态。
    /// </summary>
    public void Select(BookCardViewModel? card)
    {
        if (card is null)
        {
            return;
        }

        if (IsSelectionMode)
        {
            ToggleChecked(card);
            return;
        }

        SelectedBook = card;
    }

    public void OpenDetail(BookCardViewModel? card)
    {
        if (card is null)
        {
            return;
        }

        // 批量模式下双击不弹详情：单击已经切换过一次勾选，
        // 再弹个浮层只会让人看不清自己在选什么。
        if (IsSelectionMode)
        {
            return;
        }

        Select(card);
        DetailBook = card;
    }

    /// <summary>
    /// 关掉详情浮层。返回时顺手刷新这本书的状态与进度 
    /// 用户可能刚从详情页里的「阅读」去读了一会儿（T2-3 用户要求：返回后状态要实时刷新）。
    /// </summary>
    public void CloseDetail()
    {
        var card = _detailBook;

        DetailBook = null;

        if (card is not null)
        {
            _ = RefreshBookAsync(card);
        }
    }

    // ---------- 批量选择与删除（T1-3 补丁需求：单本删除 + 批量删除） ----------

    /// <summary>批量选择模式。开着的时候单击卡片 = 勾选 / 取消勾选。</summary>
    public bool IsSelectionMode
    {
        get => _isSelectionMode;
        private set
        {
            if (Set(ref _isSelectionMode, value))
            {
                OnPropertyChanged(nameof(SelectionModeText));
                RefreshSelectionState();
            }
        }
    }

    /// <summary>工具栏那个按钮的文案（同一个按钮切换两种文案，省一个横向位置）。</summary>
    public string SelectionModeText => _isSelectionMode ? "退出批量选择" : "批量选择";

    /// <summary>正在删除。删除期间按钮要禁掉，避免连点。</summary>
    public bool IsDeleting
    {
        get => _isDeleting;
        private set => Set(ref _isDeleting, value);
    }

    public int CheckedCount => Books.Count(b => b.IsChecked);

    public bool HasChecked => CheckedCount > 0;

    public string CheckedText => "已选 " + CheckedCount + " 本";

    /// <summary>删除按钮的文案。带上本数，让人在点之前再看一眼自己要删几本。</summary>
    public string DeleteSelectedText => "删除选中的 " + CheckedCount + " 本";

    public void ToggleSelectionMode()
    {
        if (IsSelectionMode)
        {
            ClearChecked();
        }
        else
        {
            // 进批量模式先清掉「单选」：两个概念不能同时亮着，
            // 否则用户分不清工具栏的「转换」和「删除选中」各作用于谁。
            SelectedBook = null;
        }

        IsSelectionMode = !IsSelectionMode;
    }

    public void CheckAll()
    {
        foreach (var card in Books)
        {
            card.IsChecked = true;
        }

        RefreshSelectionState();
    }

    public void ClearChecked()
    {
        foreach (var card in Books)
        {
            card.IsChecked = false;
        }

        RefreshSelectionState();
    }

    public void ToggleChecked(BookCardViewModel card)
    {
        card.IsChecked = !card.IsChecked;
        RefreshSelectionState();
    }

    /// <summary>勾选态变了就通知界面刷新「已选 N 本」与按钮可用性。</summary>
    private void RefreshSelectionState()
    {
        OnPropertyChanged(nameof(CheckedCount));
        OnPropertyChanged(nameof(HasChecked));
        OnPropertyChanged(nameof(CheckedText));
        OnPropertyChanged(nameof(DeleteSelectedText));
    }

    /// <summary>
    /// 删除若干本书。**先问后删**，确认文案由 BuildConfirmMessage 生成。
    /// 删完重新加载网格：卡片消失，删空了自动回到空状态。
    /// </summary>
    public async Task DeleteAsync(IReadOnlyList<BookCardViewModel> cards, CancellationToken ct = default)
    {
        if (cards.Count == 0 || IsDeleting)
        {
            return;
        }

        if (!ConfirmDelete(BuildConfirmMessage(cards), "删除书籍"))
        {
            _log.LogInformation("用户取消了删除 {Count} 本", cards.Count);
            return;
        }

        IsDeleting = true;

        try
        {
            var ids = cards.Select(c => c.Id).ToList();
            var report = await _deletion.DeleteAsync(ids, ct).ConfigureAwait(true);

            _log.LogInformation("删除 {Count} 本完成：{Summary}", ids.Count, report.Summary);

            ClearChecked();
            IsSelectionMode = false;

            // 重新读库：删掉的卡片要消失，删空了要回到空状态
            await LoadAsync(ct).ConfigureAwait(true);

            // 顺利删完不弹窗：网格里书没了就是最好的反馈。
            // 只有「有文件没删干净」或「有本压根没删掉」才值得打断用户。
            if (report.WarningCount > 0 || report.FailedCount > 0)
            {
                Notify(report.Summary + Environment.NewLine + Environment.NewLine + DescribeProblems(report), "删除结果");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "删除失败");
            Notify("删除时出错：" + ex.SafeMessage(), "删除失败");
        }
        finally
        {
            IsDeleting = false;
        }
    }

    /// <summary>
    /// 二次确认的正文。单本列出书名；多本列前 10 本再报总数，别让框长到屏幕外面去。
    /// 公开成 static 是为了让冒烟脚本能断言「文案里确实带了书名 / 本数」。
    /// </summary>
    public static string BuildConfirmMessage(IReadOnlyList<BookCardViewModel> cards)
    {
        var lines = new List<string>();

        if (cards.Count == 1)
        {
            lines.Add("确定要删除《" + cards[0].Title + "》吗？");
        }
        else
        {
            lines.Add("确定要删除选中的 " + cards.Count + " 本书吗？");
            lines.Add(string.Empty);

            foreach (var card in cards.Take(10))
            {
                lines.Add("- " + card.Title);
            }

            if (cards.Count > 10)
            {
                lines.Add("... 还有 " + (cards.Count - 10) + " 本");
            }
        }

        lines.Add(string.Empty);
        lines.Add("这会同时删掉正式区里的文件、封面图和阅读缓存。");
        lines.Add("删除后无法撤销。");

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>把「没删干净 / 没删掉」的逐本条理化，拼给用户看。</summary>
    private static string DescribeProblems(BookDeleteReport report)
        => string.Join(
            Environment.NewLine,
            report.Items
                .Where(i => !i.Removed || i.Warning is not null)
                .Select(i => "- " + i.Title + "：" + (i.Warning ?? "未删除")));

    /// <summary>缩略图路径是导入时定下的约定：Cache/Covers/{bookId:D}_t.jpg。</summary>
    // ---------- 文件状态（T2-6 文件监控与缺失检测） ----------

    /// <summary>最近一次扫描的一句话结论。全正常时是空串。</summary>
    public string FileScanSummary { get; private set; } = string.Empty;

    public bool HasFileProblems => FileScanSummary.Length > 0;

    /// <summary>
    /// 启动后第一次进书库时后台扫一遍（T2-6）。**不挡首屏**：
    /// 扫完只把状态灌到卡片上，不重载网格（重载会清掉滚动位置与筛选）。
    /// </summary>
    public async Task RunStartupScanAsync()
    {
        try
        {
            ApplyFileStates(await _scan.ScanAsync().ConfigureAwait(true));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "启动时扫描文件状态失败");
        }
    }

    /// <summary>手动「刷新」：先扫文件状态，再重新查询。T2-6 的判据 1~3 走的就是这条。</summary>
    public async Task RefreshWithScanAsync(CancellationToken ct = default)
    {
        try
        {
            ApplyFileStates(await _scan.ScanAsync(ct).ConfigureAwait(true));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "刷新时扫描文件状态失败");
        }

        await LoadAsync(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// 接受sample的文件改动（T2-6）：把库里记的体积 / 修改时间 / 快速指纹更新成磁盘现状。
    /// 只处理"被外部修改"的格式；接受完刷新这张卡片。
    /// </summary>
    public async Task<bool> AcceptModificationAsync(
        BookCardViewModel card, CancellationToken ct = default)
    {
        var changed = false;

        foreach (var format in card.Book.Formats.ToList())
        {
            if (await _scan.AcceptModificationAsync(card.Book.Id, format.Format, ct).ConfigureAwait(true))
            {
                changed = true;
            }
        }

        if (changed)
        {
            await RefreshBookAsync(card, ct).ConfigureAwait(true);

            // 接受改动时服务端会顺手重扫一遍（见 LibraryScanService），摘要也要跟着走
            RefreshScanSummary();
        }

        return changed;
    }

    /// <summary>
    /// 用户 2026-10-21：详情页里把某个格式设为「当前阅读格式」——之后点阅读打开的就是它。
    /// </summary>
    /// <remarks>
    /// 只写 book_reading_format 那一行，**不动阅读进度**：
    /// 换格式继续读时由阅读器按"进度来源格式"换算落点（PDF/漫画按百分比，文字按指纹精确找位）。
    /// 读不了的格式（FB2 / 未知）即使被点到也拒绝，界面本来也不会给按钮。
    /// </remarks>
    /// <returns>是否真的切换成功（落库失败返回 false，界面保持原样）。</returns>
    public async Task<bool> SetReadingFormatAsync(
        BookCardViewModel card, BookFormatKind format, CancellationToken ct = default)
    {
        if (!ReaderFormatSupport.CanRead(format))
        {
            _log.LogInformation("切换阅读格式：{Format} 读不了，忽略 {BookId}", format, card.Book.Id);
            return false;
        }

        try
        {
            await _repo.SetReadingFormatAsync(card.Book.Id, format, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "切换阅读格式失败 {BookId} {Format}", card.Book.Id, format);
            return false;
        }

        card.SetReadingFormat(format, ConvertedEpubSizeOf(card.Book, format));
        _log.LogInformation("当前阅读格式已切换：{BookId} -> {Format}", card.Book.Id, format);
        return true;
    }

    /// <summary>正在移除格式。按钮连点时挡住第二次，避免同一行删两遍。</summary>
    private bool _isRemovingFormat;

    /// <summary>
    /// 用户 2026-10-20：把某个「文件已删除」的格式记录从书库移掉（详情行尾的「移除该格式」）。
    /// </summary>
    /// <remarks>
    /// 只删数据库里的 book_formats 那一行：文件早就不在了，没东西可删。
    /// 删完做两件事：
    /// 1. 重扫一遍文件状态（与 AcceptModificationAsync 同一条刷新路径）。缺格式的记录没了，
    ///    这本书的状态自然回到 Normal，卡片角标与详情里的缺失提示一起消失；
    /// 2. 重读这一本并刷到卡片上，格式徽标 / 大小统计就切到剩下的格式（"扫描格式"的等价效果）。
    /// **阅读进度与阅读状态按书 Id 存，这里刻意一行都不碰**，用户重开还是原来的进度与状态。
    /// </remarks>
    /// <returns>是否真的移掉了一个格式。</returns>
    public async Task<bool> RemoveFormatAsync(
        BookCardViewModel card, BookFormatKind format, CancellationToken ct = default)
    {
        var target = card.Book.Formats.FirstOrDefault(f => f.Format == format);

        if (target is null)
        {
            _log.LogInformation("移除格式：这本书已经没有 {Format} 记录了 {BookId}", format, card.Book.Id);
            return false;
        }

        if (_isRemovingFormat)
        {
            return false;
        }

        _isRemovingFormat = true;

        try
        {
            var deleted = await _repo.DeleteFormatAsync(target.Id, ct).ConfigureAwait(true);

            if (!deleted)
            {
                _log.LogInformation("移除格式：记录已经不在了 {FormatId} {BookId}", target.Id, card.Book.Id);
                return false;
            }

            _log.LogInformation(
                "已从书库移除格式记录：{BookId} {Format}，文件路径 {Path}",
                card.Book.Id, format, target.FilePath);

            /* 重扫：这是"卡片角标 / FileState 回到 Normal"的来源。
               与「接受改动」一致，服务端扫完 LastReport 就是新的；摘要也要刷新。 */
            try
            {
                await _scan.ScanAsync(ct).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 扫描失败不该把"已经删掉记录"这件事报成失败；角标下次刷新会自己跟上
                _log.LogWarning(ex, "移除格式后重扫文件状态失败 {BookId}", card.Book.Id);
            }

            await RefreshBookAsync(card, ct).ConfigureAwait(true);
            RefreshScanSummary();

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "移除格式失败 {BookId} {Format}", card.Book.Id, format);
            ErrorMessage = "移除格式失败：" + ex.SafeMessage();
            return false;
        }
        finally
        {
            _isRemovingFormat = false;
        }
    }

    /// <summary>把扫描结果灌到现有卡片上：不重载网格，滚动位置、选中与筛选都保住。</summary>
    public void ApplyFileStates(LibraryScanReport report)
    {
        var states = report.BuildBookStates();

        foreach (var card in Books)
        {
            card.SetFileState(states.TryGetValue(card.Book.Id, out var state)
                ? state
                : BookFileState.Normal);
        }

        RefreshScanSummary();
    }

    /// <summary>把扫描摘要同步到界面。扫描在别处跑完（比如接受改动时顺手重扫）也要调它。</summary>
    private void RefreshScanSummary()
    {
        var summary = _scan.LastReport?.Summary ?? string.Empty;

        if (summary == FileScanSummary)
        {
            return;
        }

        FileScanSummary = summary;
        OnPropertyChanged(nameof(FileScanSummary));
        OnPropertyChanged(nameof(HasFileProblems));
    }

    /// <summary>
    /// 目录变化提示（T2-6）。<see cref="ILibraryWatcher"/> 在**后台线程**触发，
    /// 所以要先切回 UI 线程再动卡片集合（ObservableCollection 绑着界面）。
    /// </summary>
    private void OnLibraryFolderChanged()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;

        if (dispatcher is null || dispatcher.CheckAccess())
        {
            _ = RunStartupScanAsync();
            return;
        }

        dispatcher.InvokeAsync(() => _ = RunStartupScanAsync());
    }

    /// <summary>
    /// sample的封面被换过之后，让网格里的那张卡片重新解码（执行计划 T3-3 判据 4）。
    /// </summary>
    public async Task RefreshBookCoverAsync(Guid bookId, CancellationToken ct = default)
    {
        var card = Books.FirstOrDefault(c => c.Id == bookId);
        if (card is not null)
        {
            await card.ReloadCoverAsync(ct).ConfigureAwait(true);
        }
    }

    private string ThumbnailPathOf(Book book)
        => Path.Combine(_paths.CoversCacheDir, book.Id.ToString("D") + "_t.jpg");

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}