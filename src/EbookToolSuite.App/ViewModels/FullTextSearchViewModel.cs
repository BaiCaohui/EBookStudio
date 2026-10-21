using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using EbookToolSuite.App.Commands;
using EbookToolSuite.Core.Errors;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Services.Search;
using Microsoft.Extensions.Logging;

namespace EbookToolSuite.App.ViewModels;

/// <summary>
/// 全库正文搜索面板（T6-4）。结果按书分组、每组下是命中片段（关键词高亮），
/// 点一行就打开那本书的那一章并高亮全部命中。
/// </summary>
/// <remarks>
/// 为什么不用主窗口顶栏那个搜索框：顶栏框绑的是 <c>Library.SearchText</c>（元数据过滤），
/// 两个语义共用一个 TextBox 会让"改绑定"牵动 1000 本虚拟化列表与筛选状态。
/// 这里做成书库页内的浮层，输入框在浮层顶部、输入即搜（防抖 300 ms），行为与计划书的
/// 「顶部搜索框」一致，只是这个"顶部"是搜索面板自己的顶部。
/// </remarks>
public sealed class FullTextSearchViewModel : INotifyPropertyChanged
{
    private readonly IContentSearchService? _search;
    private readonly ILogger<FullTextSearchViewModel>? _log;

    private CancellationTokenSource? _debounceCts;
    private string _query = string.Empty;
    private string _statusText = string.Empty;
    private string _noticeText = string.Empty;
    private bool _isOpen;
    private bool _isSearching;
    private Guid? _scopeBookId;
    private string? _scopeTitle;

    public FullTextSearchViewModel(
        IContentSearchService? search = null,
        ILogger<FullTextSearchViewModel>? log = null)
    {
        _search = search;
        _log = log;
        CloseCommand = new RelayCommand(_ => Close());
        ClearScopeCommand = new RelayCommand(_ => SetScope(null, null));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>用户点了某一行命中：书 id、章号、当前查询词（跳过去要高亮它）。</summary>
    public event Action<Guid, int, string>? OpenRequested;

    /// <summary>防抖窗口。冒烟把它调成 0，免得每条判据白等 300 ms。</summary>
    public TimeSpan SearchDebounce { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <summary>按书分组的结果。</summary>
    public ObservableCollection<SearchBookGroupViewModel> Groups { get; } = new();

    public ICommand CloseCommand { get; }

    public ICommand ClearScopeCommand { get; }

    public bool IsOpen
    {
        get => _isOpen;
        private set => Set(ref _isOpen, value);
    }

    /// <summary>输入框绑它。每次改动重排防抖计时器。</summary>
    public string Query
    {
        get => _query;
        set
        {
            if (Set(ref _query, value ?? string.Empty))
            {
                ScheduleSearch();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string NoticeText
    {
        get => _noticeText;
        private set => Set(ref _noticeText, value);
    }

    public bool IsSearching
    {
        get => _isSearching;
        private set => Set(ref _isSearching, value);
    }

    public bool HasResults => Groups.Count > 0;

    public string ScopeText => _scopeBookId is null
        ? "范围：全部书库"
        : "范围：只搜《" + _scopeTitle + "》";

    public bool HasScope => _scopeBookId is not null;

    /// <summary>打开浮层。initialQuery 非空时立即（防抖后）搜一次。</summary>
    public void Open(string? initialQuery = null, Guid? scopeBookId = null, string? scopeTitle = null)
    {
        ScopeBookId = scopeBookId;
        _scopeTitle = scopeTitle;

        IsOpen = true;

        if (!string.IsNullOrWhiteSpace(initialQuery))
        {
            Query = initialQuery.Trim();
        }
        else if (Groups.Count == 0)
        {
            StatusText = "输入关键词，在已索引的书里搜正文";
        }
    }

    public void Close()
    {
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _debounceCts = null;
        IsOpen = false;
    }

    /// <summary>把范围限定到某一本（结果组里的「只搜这本」）。</summary>
    public void SetScope(Guid? bookId, string? title)
    {
        ScopeBookId = bookId;
        _scopeTitle = title;

        if (IsOpen && Query.Trim().Length > 0)
        {
            ScheduleSearch();
        }
    }

    /// <summary>取消防抖、立刻搜（Enter 键与冒烟用）。</summary>
    public Task FlushSearchAsync(CancellationToken ct = default)
    {
        _debounceCts?.Cancel();
        return SearchNowAsync(ct);
    }

    private Guid? ScopeBookId
    {
        get => _scopeBookId;
        set
        {
            if (Set(ref _scopeBookId, value))
            {
                OnPropertyChanged(nameof(ScopeText));
                OnPropertyChanged(nameof(HasScope));
            }
        }
    }

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

        await SearchSafeAsync(cts.Token).ConfigureAwait(true);
    }

    private async Task SearchSafeAsync(CancellationToken ct)
    {
        try
        {
            await SearchNowAsync(ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "全文搜索失败：{Query}", Query);
            StatusText = "搜索失败：" + ex.SafeMessage();
        }
    }

    private async Task SearchNowAsync(CancellationToken ct)
    {
        var query = Query.Trim();

        if (query.Length == 0)
        {
            Groups.Clear();
            StatusText = IsOpen ? "输入关键词，在已索引的书里搜正文" : string.Empty;
            NoticeText = string.Empty;
            OnPropertyChanged(nameof(HasResults));
            return;
        }

        if (_search is null)
        {
            StatusText = "全文搜索服务没装好，请重启程序。";
            return;
        }

        IsSearching = true;

        try
        {
            var result = await _search.SearchAsync(query, _scopeBookId, 200, ct).ConfigureAwait(true);

            BuildGroups(result, query);

            StatusText = result.TotalMatches == 0
                ? "没有命中：换个词试试（索引可能还没建完）"
                : "共 " + result.TotalMatches + " 处命中，" + Groups.Count + " 本书"
                    + (result.Truncated ? "（只显示前 200 处）" : string.Empty);

            NoticeText = result.UsedShortQueryFallback
                ? "短词（少于 3 个字）按逐字匹配，书多时会慢一些。"
                : string.Empty;

            OnPropertyChanged(nameof(HasResults));
        }
        finally
        {
            IsSearching = false;
        }
    }

    private void BuildGroups(ContentSearchResult result, string query)
    {
        Groups.Clear();

        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var group in result.Hits.GroupBy(h => h.BookId))
        {
            var first = group.First();
            var vm = new SearchBookGroupViewModel(group.Key, first.BookTitle, first.BookAuthors);
            var scope = vm;

            vm.ScopeCommand = new RelayCommand(_ => SetScope(scope.BookId, scope.Title));

            foreach (var hit in group)
            {
                var parts = ContentSnippet.Build(hit.Body, terms);
                var spine = hit.SpineIndex;

                vm.Hits.Add(new SearchHitItemViewModel(
                    hit.ChapterTitle,
                    parts,
                    new RelayCommand(_ =>
                    {
                        /* 先关浮层再开阅读窗口：不关的话浮层会盖住阅读窗口，
                           用户看起来像"点了没反应"。 */
                        Close();
                        OpenRequested?.Invoke(hit.BookId, spine, query);
                    })));
            }

            vm.RefreshCount();
            Groups.Add(vm);
        }
    }

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

/// <summary>结果里的一本书：标题 + 命中片段列表。</summary>
public sealed class SearchBookGroupViewModel : INotifyPropertyChanged
{
    public SearchBookGroupViewModel(Guid bookId, string title, string? authors)
    {
        BookId = bookId;
        Title = title;
        Authors = authors;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid BookId { get; }

    public string Title { get; }

    public string? Authors { get; }

    public ObservableCollection<SearchHitItemViewModel> Hits { get; } = new();

    public ICommand? ScopeCommand { get; set; }

    public string CountText { get; private set; } = string.Empty;

    public void RefreshCount() => CountText = Hits.Count + " 处命中";

    public void RaiseCountChanged() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CountText)));
}

/// <summary>一条命中：章节名 + 片段三段（前文 / 命中词 / 后文）。</summary>
public sealed class SearchHitItemViewModel
{
    public SearchHitItemViewModel(string? chapterTitle, ContentSnippet.Parts parts, ICommand openCommand)
    {
        ChapterTitle = string.IsNullOrWhiteSpace(chapterTitle) ? "（无标题章节）" : chapterTitle!;
        Before = parts.Before;
        Match = parts.Match;
        After = parts.After;
        OpenCommand = openCommand;
    }

    public string ChapterTitle { get; }

    public string Before { get; }

    public string Match { get; }

    public string After { get; }

    public ICommand OpenCommand { get; }
}
