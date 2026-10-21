namespace EbookToolSuite.Core.Models;

/// <summary>全文搜索的结果（T6-4）。</summary>
public sealed record ContentSearchResult
{
    public required string Query { get; init; }

    /// <summary>命中的切片（已按 limit 截断）。</summary>
    public required IReadOnlyList<ContentSearchHit> Hits { get; init; }

    /// <summary>命中总数（可能大于 Hits.Count，用于显示「共 N 处」）。</summary>
    public required int TotalMatches { get; init; }

    /// <summary>true = 命中数超过 limit，界面要提示「只显示前 N 处」。</summary>
    public required bool Truncated { get; init; }

    /// <summary>
    /// true = 查询词短于 3 个字符，走了 LIKE 兜底（trigram 分词器对 &lt;3 字符的查询
    /// 产出不了任何 token）。界面可据此提示「短词搜索较慢」。
    /// </summary>
    public required bool UsedShortQueryFallback { get; init; }
}
