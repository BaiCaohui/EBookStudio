using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using System.Windows.Input;
using EbookToolSuite.App.Commands;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Errors;
using EbookToolSuite.Core.Models;
using Microsoft.Extensions.Logging;

namespace EbookToolSuite.App.ViewModels;

/// <summary>
/// LibraryViewModel 的 T2-3 部分：搜索、筛选、排序、虚拟化行、三种空状态。
/// 拆成 partial 是因为这一块自己就够长，而且和"网格 + 详情 + 删除"本来是两件事。
/// </summary>
public sealed partial class LibraryViewModel
{
    /// <summary>卡片宽 150 + 右侧间距 16。算每行几列时用。</summary>
    private const double CardStride = 166;

    private string _searchText = string.Empty;
    private FormatOption _formatOption = FormatOption.All;
    private StatusOption _statusOption = StatusOption.All;
    private SortOption _sortOption = SortOption.Default;
    private bool _onlyWithCover;
    private bool _hasFilters;
    private int _matchedCount;
    private int _columns = 6;
    private double _viewportWidth;
    private CancellationTokenSource? _debounceCts;

    /* T7-3B 用户需求：书库的检索 / 筛选 / 排序状态要能记住（重开书库恢复上次条件）。
       配置键与恢复、保存口径见 RestoreQueryState / PersistQueryStateAsync。 */
    private const string SearchConfigKey = "ui.library.search";
    private const string SortFieldConfigKey = "ui.library.sortField";
    private const string SortDescendingConfigKey = "ui.library.sortDescending";
    private const string FormatConfigKey = "ui.library.format";
    private const string StatusConfigKey = "ui.library.status";
    private const string OnlyWithCoverConfigKey = "ui.library.onlyWithCover";

    /// <summary>上一次已经写回配置的状态快照。相等就跳过写盘，避免每次输入都写文件。</summary>
    private LibraryQueryState? _lastPersistedQueryState;

    /// <summary>写配置串行化：连续改条件时可能有多个防抖任务同时到点。</summary>
    private readonly SemaphoreSlim _queryPersistGate = new(1, 1);

    /// <summary>
    /// 需要持久化的书库查询状态。用 record 的值相等判断"条件到底有没有变"；
    /// 枚举存名字而不是数字，Config.json 直接可读。
    /// </summary>
    private sealed record LibraryQueryState(
        string Search,
        string? Format,
        string? Status,
        bool OnlyWithCover,
        BookSort Sort,
        bool Descending);

    /// <summary>
    /// 搜索防抖窗口。做成可写属性是为了让冒烟把它调成 0 
    /// 否则每条搜索判据都要白等 200 ms。
    /// </summary>
    public TimeSpan SearchDebounce { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>网格的行（每行最多 _columns 张卡片）。ListBox 绑的是它，不是 Books。</summary>
    public ObservableCollection<BookRowViewModel> Rows { get; } = new();

    /// <summary>本次查询命中多少本（可能大于屏幕上这一页）。</summary>
    public int MatchedCount => _matchedCount;

    public IReadOnlyList<FormatOption> FormatOptions => FormatOption.AllOptions;

    public IReadOnlyList<StatusOption> StatusOptions => StatusOption.AllOptions;

    public IReadOnlyList<SortOption> SortOptions => SortOption.AllOptions;

    /// <summary>搜索框绑它。每敲一个字符就重排一次防抖计时器。</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value ?? string.Empty))
            {
                RefreshChips();
                ScheduleSearch();
            }
        }
    }

    public FormatOption SelectedFormat
    {
        get => _formatOption;
        set
        {
            if (Set(ref _formatOption, value ?? FormatOption.All))
            {
                RefreshChips();
                ScheduleSearch();
            }
        }
    }

    public StatusOption SelectedStatus
    {
        get => _statusOption;
        set
        {
            if (Set(ref _statusOption, value ?? StatusOption.All))
            {
                RefreshChips();
                ScheduleSearch();
            }
        }
    }

    public SortOption SelectedSort
    {
        get => _sortOption;
        set
        {
            if (Set(ref _sortOption, value ?? SortOption.Default))
            {
                ScheduleSearch();
            }
        }
    }

    public bool OnlyWithCover
    {
        get => _onlyWithCover;
        set
        {
            if (Set(ref _onlyWithCover, value))
            {
                RefreshChips();
                ScheduleSearch();
            }
        }
    }

    /// <summary>已选条件 chips。点某一枚就把那个条件去掉。</summary>
    public IReadOnlyList<FilterChip> Chips { get; private set; } = Array.Empty<FilterChip>();

    public bool HasChips => Chips.Count > 0;

    /// <summary>
    /// 只算"筛选"（不含搜索关键词）：决定「重置筛选」按钮是否可用。
    /// 只有关键词时不该点亮它  那个按钮不负责清关键词（清关键词是搜索框自己的 ）。
    /// </summary>
    public bool HasFieldFilters => _formatOption.Kind is not null
        || _statusOption.Status is not null
        || _onlyWithCover;

    // ---------- 查询 ----------

    /// <summary>
    /// 重新查一遍（按当前条件）。搜索、筛选、排序、导入完成、删除完成之后都走它。
    /// </summary>
    private async Task ApplyQueryAsync(CancellationToken ct = default)
    {
        /* T2-3：虚拟化之后不再需要"只加载前 N 本"这种分页了  列表里可以放 1000 本，
           真正实现出来的只有屏幕上那几行。这里的上限只用来防住配置被写成 0 或负数。 */
        var pageSize = (int)_config.GetDouble("library.gridPageSize", 100000);

        if (pageSize <= 0)
        {
            pageSize = 100000;
        }

        var query = BuildQuery() with { Limit = pageSize };

        var books = await _repo.QueryAsync(query, ct).ConfigureAwait(true);
        _matchedCount = await _repo.CountQueryAsync(query, ct).ConfigureAwait(true);

        var ids = books.Select(b => b.Id).ToList();

        /* 阅读进度一次性查回来（T1-6 判据 4）。查失败不该让书库打不开，所以单独 try 掉。 */
        Dictionary<Guid, double> progressMap = new();

        try
        {
            var progressRows = await _repo.GetProgressMapAsync(ids, ct).ConfigureAwait(true);

            foreach (var pair in progressRows)
            {
                progressMap[pair.Key] = pair.Value.Percent;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "读阅读进度失败，网格不显示继续阅读角标");
        }

        /* 用户 2026-10-21：每本书"当前阅读格式"也一次查回来（详情页的切换按钮与底部小字要用）。
           同样是单独 try：读失败就按默认优先级显示，不能让书库打不开。 */
        Dictionary<Guid, BookFormatKind> readingFormatMap = new();

        try
        {
            var readingFormats = await _repo.GetReadingFormatMapAsync(ids, ct).ConfigureAwait(true);

            foreach (var pair in readingFormats)
            {
                readingFormatMap[pair.Key] = pair.Value;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "读当前阅读格式失败，按默认优先级显示");
        }

        Books.Clear();

        foreach (var book in books)
        {
            var card = new BookCardViewModel(book, ThumbnailPathOf(book), _coverGate, _log);

            if (progressMap.TryGetValue(book.Id, out var percent))
            {
                card.SetProgress(percent);
            }

            BookFormatKind? preferredFormat = readingFormatMap.TryGetValue(book.Id, out var kind)
                ? kind
                : null;
            card.SetReadingFormat(preferredFormat, ConvertedEpubSizeOf(book, preferredFormat));

            // T2-6：文件状态来自扫描服务的内存快照，卡片自己不去碰磁盘。
            card.SetFileState(_scan.GetBookState(book.Id));

            Books.Add(card);
        }

        IsEmpty = Books.Count == 0;
        _hasFilters = query.HasFilters;

        RebuildRows();
        RefreshEmptyState();

        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(MatchedCount));

        // 封面不在这里解码：1000 本首屏解码 1000 张图会把首屏拖垮。
        // 谁被实现出来谁自己解码（见 LibraryPage 的卡片 Loaded 事件）。
        _log.LogInformation("书库查询：命中 {Matched} 本，本页 {Shown} 本", _matchedCount, Books.Count);
    }

    private BookQuery BuildQuery() => new()
    {
        SearchText = _searchText,
        Format = _formatOption.Kind,
        Status = _statusOption.Status,
        OnlyWithCover = _onlyWithCover,
        Sort = _sortOption.Sort,
        Descending = _sortOption.Descending,
    };

    /// <summary>
    /// MOBI / AZW3 的转换产物大小（用户 2026-10-21）。
    /// 返回 null 表示"还没打开过阅读器、还没转出来"——详情里因此不显示 EPUB 行，
    /// 底部小字改为"当前格式不支持阅读，点击阅读后自动转换"。
    /// 别的格式没有这一步，直接返回 null。
    /// </summary>
    private long? ConvertedEpubSizeOf(Book book, BookFormatKind? preferred)
    {
        if (_epubCache is null)
        {
            return null;
        }

        var effective = ReaderFormatSupport.Resolve(book.Formats, preferred);

        return effective is not null && ReaderFormatSupport.RequiresEpubConversion(effective.Format)
            ? _epubCache.GetConvertedArtifactSize(effective)
            : null;
    }

    /// <summary>把当前查询条件抓成快照（持久化，以及"有没有变"的比较都用它）。</summary>
    private LibraryQueryState CurrentQueryState() => new(
        _searchText,
        _formatOption.Kind?.ToString(),
        _statusOption.Status?.ToString(),
        _onlyWithCover,
        _sortOption.Sort,
        _sortOption.Descending);

    /// <summary>
    /// T7-3B：把当前查询条件写回配置。搜索词、筛选、排序的变化以及重置、清空都会
    /// 经 ScheduleSearch / FlushSearchAsync 走到这里；与上次已写快照相同就不写盘。
    /// 写失败只 WRN  —— 记不住条件不该让书库用不了。
    /// </summary>
    private async Task PersistQueryStateAsync(CancellationToken ct)
    {
        var state = CurrentQueryState();

        if (state == _lastPersistedQueryState)
        {
            return;
        }

        try
        {
            await _queryPersistGate.WaitAsync(ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 被取消说明又排了一次新的查询，那次会带着最新条件写
            return;
        }

        try
        {
            // 拿到锁后重新判断：排在前面的那次可能已经写过同样的状态
            if (state == _lastPersistedQueryState)
            {
                return;
            }

            await _config.SetAsync(SearchConfigKey, JsonValue.Create(state.Search), ct).ConfigureAwait(true);
            await _config.SetAsync(SortFieldConfigKey, JsonValue.Create(state.Sort.ToString()), ct).ConfigureAwait(true);
            await _config.SetAsync(SortDescendingConfigKey, JsonValue.Create(state.Descending), ct).ConfigureAwait(true);
            await _config.SetAsync(FormatConfigKey, JsonValue.Create(state.Format ?? string.Empty), ct).ConfigureAwait(true);
            await _config.SetAsync(StatusConfigKey, JsonValue.Create(state.Status ?? string.Empty), ct).ConfigureAwait(true);
            await _config.SetAsync(OnlyWithCoverConfigKey, JsonValue.Create(state.OnlyWithCover), ct).ConfigureAwait(true);

            // 六个键一次落盘：不要每改一个键就写一次文件
            await _config.SaveAsync(ct).ConfigureAwait(true);

            _lastPersistedQueryState = state;
        }
        catch (OperationCanceledException)
        {
            // 切页 / 关窗取消：内存里的配置已改，下一次查询会再写一遍
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "书库检索/排序状态没能存下来，本次使用不受影响");
        }
        finally
        {
            _queryPersistGate.Release();
        }
    }

    /// <summary>排一次防抖计时器。连续输入只有最后一次生效。</summary>
    private async void ScheduleSearch()
    {
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();

        var cts = new CancellationTokenSource();
        _debounceCts = cts;

        try
        {
            if (SearchDebounce > TimeSpan.Zero)
            {
                // ConfigureAwait(true)：之后要回到 UI 线程，ObservableCollection 只能在 UI 线程改
                await Task.Delay(SearchDebounce, cts.Token).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (cts.IsCancellationRequested)
        {
            return;
        }

        /* T7-3B：防抖窗口结束、真正查询之前，把当前条件的快照写回配置。
           写失败只 WRN（见 PersistQueryStateAsync），绝不挡住查询。 */
        await PersistQueryStateAsync(cts.Token).ConfigureAwait(true);

        await SearchSafeAsync(cts.Token).ConfigureAwait(true);
    }

    /// <summary>查一遍但把异常吃掉：防抖回调是 async void，抛出去会直接杀进程。</summary>
    private async Task SearchSafeAsync(CancellationToken ct)
    {
        try
        {
            await ApplyQueryAsync(ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "书库查询失败");
            ErrorMessage = "查询失败：" + ex.SafeMessage();
        }
    }

    /// <summary>取消防抖、立刻查（Enter 键与冒烟脚本用，保证可断言）。</summary>
    public async Task FlushSearchAsync(CancellationToken ct = default)
    {
        _debounceCts?.Cancel();

        /* T7-3B：走这条路径没有防抖窗口，先写状态再查，
           保证"设完条件就能被重开的书库读到"。 */
        await PersistQueryStateAsync(ct).ConfigureAwait(true);

        await ApplyQueryAsync(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// 立刻把当前检索 / 排序状态落盘（程序退出时调用）。
    /// 平时状态是在防抖窗口结束时写的；用户敲完关键词 200 ms 内直接关程序时，
    /// 最后这一次改动还没到写盘时机，靠退出前补一下。
    /// </summary>
    public Task FlushQueryStateAsync(CancellationToken ct = default)
        => PersistQueryStateAsync(ct);

    public void ClearSearch() => SearchText = string.Empty;

    /// <summary>只清筛选（格式 / 状态 / 只看有封面），关键词保留。</summary>
    public void ResetFilters()
    {
        _formatOption = FormatOption.All;
        _statusOption = StatusOption.All;
        _onlyWithCover = false;

        OnPropertyChanged(nameof(SelectedFormat));
        OnPropertyChanged(nameof(SelectedStatus));
        OnPropertyChanged(nameof(OnlyWithCover));

        RefreshChips();
        ScheduleSearch();
    }

    /// <summary>关键词和筛选一起清（空状态里的「清除筛选」按钮）。</summary>
    public void ClearAllFilters()
    {
        _searchText = string.Empty;
        _formatOption = FormatOption.All;
        _statusOption = StatusOption.All;
        _onlyWithCover = false;

        OnPropertyChanged(nameof(SearchText));
        OnPropertyChanged(nameof(SelectedFormat));
        OnPropertyChanged(nameof(SelectedStatus));
        OnPropertyChanged(nameof(OnlyWithCover));

        RefreshChips();
        ScheduleSearch();
    }

    /// <summary>
    /// 手动改一本书的阅读状态（卡片右键菜单的「标记为」）。
    /// 改完就地刷新那张卡片，**不整页重载**  重载会把选中、滚动位置、详情浮层全清掉，代价没必要。
    /// 但如果当前正按状态筛，这本书可能已经不该留在列表里，那就重查一遍。
    /// </summary>
    public async Task<bool> MarkStatusAsync(
        BookCardViewModel card,
        ReadingStatus status,
        CancellationToken ct = default)
    {
        if (card.Book.Status == status)
        {
            return false;
        }

        try
        {
            var changed = await _repo.SetReadingStatusAsync(card.Book.Id, status, ct).ConfigureAwait(true);

            if (!changed)
            {
                return false;
            }

            card.ApplyStatusChange(status);

            if (_statusOption.Status is not null)
            {
                ScheduleSearch();
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "改阅读状态失败 {BookId}", card.Book.Id);
            ErrorMessage = "改阅读状态失败：" + ex.SafeMessage();
            return false;
        }
    }

    /// <summary>
    /// 从库里重新读一本书的状态与进度，刷到卡片上。
    /// **详情浮层返回时调它**  用户在详情页里可能去读了一会儿（阅读窗口），
    /// 返回时卡片上的状态与进度必须是新的。只重读这一本，不整页重载
    /// （重载会清掉选中、滚动位置与筛选状态）。
    /// </summary>
    public async Task RefreshBookAsync(BookCardViewModel card, CancellationToken ct = default)
    {
        try
        {
            var fresh = await _repo.GetByIdAsync(card.Book.Id, ct).ConfigureAwait(true);

            if (fresh is null)
            {
                // 书已经不在了（可能在别的地方删了）：这张卡片留着没意义，整页重载一遍
                await LoadAsync(ct).ConfigureAwait(true);
                return;
            }

            var progress = await _repo.GetReadingProgressAsync(card.Book.Id, ct).ConfigureAwait(true);

            card.ApplyRefresh(fresh, progress?.Percent ?? 0);

            /* 用户 2026-10-21：读回来之后当前阅读格式也要重算——
               刚在阅读器里读过 MOBI/AZW3 时，转换产物这时才存在，EPUB 行与底部小字要跟着出现。 */
            var readingFormat = await _repo.GetReadingFormatAsync(card.Book.Id, ct).ConfigureAwait(true);
            card.SetReadingFormat(readingFormat, ConvertedEpubSizeOf(card.Book, readingFormat));

            // 顺手刷新文件状态（T2-6）：接受改动之后，卡片上的"已修改"角标要立刻消失，
            // 不能等下一次整页重载才更新。
            card.SetFileState(_scan.GetBookState(card.Book.Id));

            // 扫描可能刚在别处跑完（编辑器保存后同步基线会触发一次重扫）：
            // 状态栏那句"n 个文件被外部修改"也要跟着变，否则卡片清白了、顶上还挂着旧数字。
            RefreshScanSummary();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "刷新这本书的状态失败 {BookId}", card.Book.Id);
        }
    }

    /// <summary>
    /// 按书 id 刷新一张卡片。**阅读窗口关闭时调它**：用户刚在阅读窗口里读了一会儿，
    /// 关窗后网格上的进度与状态必须自己跟上，否则要切页或手动刷新才更新。
    /// 只重读这一本，不整页重载（重载会清掉选中、滚动位置与筛选状态）。
    /// 这本书被当前筛选条件挡住（不在网格里）时什么都不做，不主动把它捞出来。
    /// </summary>
    public async Task RefreshBookByIdAsync(Guid bookId, CancellationToken ct = default)
    {
        var card = Books.FirstOrDefault(b => b.Book.Id == bookId);

        if (card is null)
        {
            return;
        }

        await RefreshBookAsync(card, ct).ConfigureAwait(true);
    }

    // ---------- chips 与空状态 ----------

    private void RefreshChips()
    {
        var chips = new List<FilterChip>();

        if (!string.IsNullOrWhiteSpace(_searchText))
        {
            chips.Add(new FilterChip(
                "搜索：" + _searchText.Trim(),
                new RelayCommand(_ => ClearSearch())));
        }

        if (_formatOption.Kind is not null)
        {
            chips.Add(new FilterChip(
                "格式：" + _formatOption.Label,
                new RelayCommand(_ => SelectedFormat = FormatOption.All)));
        }

        if (_statusOption.Status is not null)
        {
            chips.Add(new FilterChip(
                "状态：" + _statusOption.Label,
                new RelayCommand(_ => SelectedStatus = StatusOption.All)));
        }

        if (_onlyWithCover)
        {
            chips.Add(new FilterChip(
                "只看有封面",
                new RelayCommand(_ => OnlyWithCover = false)));
        }

        Chips = chips;
        OnPropertyChanged(nameof(Chips));
        OnPropertyChanged(nameof(HasChips));
        OnPropertyChanged(nameof(HasFieldFilters));
    }

    private bool HasKeyword => !string.IsNullOrWhiteSpace(_searchText);

    public string EmptyTitle => HasKeyword
        ? "没有找到「" + _searchText.Trim() + "」"
        : _hasFilters
            ? "当前筛选条件下没有书"
            : "书库还没有书";

    public string EmptyBody => _hasFilters
        ? "换个关键词，或者把筛选条件放宽一点。"
        : "把 EPUB 拖进来，或者点下面的按钮导入。";

    public string EmptyBadge => _hasFilters ? "已筛掉全部结果" : string.Empty;

    public string EmptyActionText => _hasFilters ? "清除筛选" : string.Empty;

    public bool HasEmptyAction => _hasFilters;

    private ICommand? _emptyActionCommand;

    public ICommand EmptyActionCommand => _emptyActionCommand ??= new RelayCommand(_ => ClearAllFilters());

    private void RefreshEmptyState()
    {
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyBody));
        OnPropertyChanged(nameof(EmptyBadge));
        OnPropertyChanged(nameof(EmptyActionText));
        OnPropertyChanged(nameof(HasEmptyAction));
    }

    // ---------- 虚拟化：折行 ----------

    /// <summary>按当前列数把 Books 折成行。结果变了或列数变了都要重建。</summary>
    private void RebuildRows()
    {
        Rows.Clear();

        var columns = Math.Max(1, _columns);

        for (var i = 0; i < Books.Count; i += columns)
        {
            var take = Math.Min(columns, Books.Count - i);
            var cards = new List<BookCardViewModel>(take);

            for (var k = 0; k < take; k++)
            {
                cards.Add(Books[i + k]);
            }

            Rows.Add(new BookRowViewModel(cards));
        }
    }

    /// <summary>
    /// 网格可用宽度变了就重算每行几列（卡片 150 宽 + 右侧 16 间距）。
    /// 只有列数真的变了才重建  拖动窗口时不该每帧把 1000 本重新分组。
    /// </summary>
    public void SetViewportWidth(double width)
    {
        if (width <= 0 || Math.Abs(width - _viewportWidth) < 1)
        {
            return;
        }

        _viewportWidth = width;

        var columns = Math.Max(1, (int)((width + 16) / CardStride));

        if (columns == _columns)
        {
            return;
        }

        _columns = columns;
        RebuildRows();
    }
}
