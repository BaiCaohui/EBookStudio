using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Services.Search;

/// <summary>
/// 跨书全文搜索（T6-4）：把用户输入切成词、决定走 FTS5 还是 LIKE 兜底，再交给仓储。
/// 片段切分不在这一层（界面需要 before/match/after 三段，见 <see cref="ContentSnippet"/>）。
/// </summary>
public sealed class ContentSearchService : IContentSearchService
{
    /// <summary>与元数据搜索同一上限：再多只会让 SQL 更长，用户也记不住。</summary>
    internal const int MaxTerms = 8;

    /// <summary>trigram 分词器只对 ≥3 字符的查询产出 token。</summary>
    internal const int TrigramMinChars = 3;

    private readonly IContentRepository _repo;

    public ContentSearchService(IContentRepository repo) => _repo = repo;

    public Task<ContentSearchResult> SearchAsync(
        string text,
        Guid? bookId = null,
        int limit = 200,
        CancellationToken ct = default)
    {
        var terms = SplitTerms(text);

        if (terms.Count == 0)
        {
            return Task.FromResult(new ContentSearchResult
            {
                Query = string.Empty,
                Hits = Array.Empty<ContentSearchHit>(),
                TotalMatches = 0,
                Truncated = false,
                UsedShortQueryFallback = false,
            });
        }

        var fallback = terms.Any(t => t.Length < TrigramMinChars);

        return _repo.SearchAsync(new ContentSearchQuery
        {
            Terms = terms,
            BookId = bookId,
            Limit = limit,
            UseLikeFallback = fallback,
        }, ct);
    }

    /// <summary>空格分词、去空、去重（大小写不敏感）、封顶 <see cref="MaxTerms"/> 个。</summary>
    internal static IReadOnlyList<string> SplitTerms(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<string>();
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var terms = new List<string>();

        foreach (var raw in text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (seen.Add(raw))
            {
                terms.Add(raw);
            }

            if (terms.Count >= MaxTerms)
            {
                break;
            }
        }

        return terms;
    }
}
