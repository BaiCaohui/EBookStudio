using EbookToolSuite.Core.Enums;

namespace EbookToolSuite.Core.Models;

/// <summary>
/// 书库查询条件（T2-3）。搜索、筛选、排序、分页全都走这一个对象，
/// 由仓储翻译成参数化的动态 WHERE  绝不拼字符串。
/// 每个字段都可以为空，空表示"这一维不过滤"。
/// </summary>
public sealed record BookQuery
{
    /// <summary>关键词最多取这么多个：再多只会让 SQL 更长，用户也记不住。</summary>
    public const int MaxTerms = 8;

    /// <summary>搜索关键词，空格分词，词之间是 AND。</summary>
    public string? SearchText { get; init; }

    /// <summary>只看这个格式（书里有该格式就算命中）。</summary>
    public BookFormatKind? Format { get; init; }

    /// <summary>只看这个阅读状态。</summary>
    public ReadingStatus? Status { get; init; }

    /// <summary>只看有封面的书。</summary>
    public bool OnlyWithCover { get; init; }

    public BookSort Sort { get; init; } = BookSort.Title;

    public bool Descending { get; init; }

    public int Offset { get; init; }

    public int Limit { get; init; } = 200;

    /// <summary>切好词的关键词列表（已去掉空串、封顶 MaxTerms 个）。</summary>
    public IReadOnlyList<string> Terms
    {
        get
        {
            if (string.IsNullOrWhiteSpace(SearchText))
            {
                return Array.Empty<string>();
            }

            var parts = SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            return parts.Length <= MaxTerms ? parts : parts[..MaxTerms];
        }
    }

    /// <summary>有没有任何筛选条件（决定空状态该说"书库是空的"还是"搜不到"）。</summary>
    public bool HasFilters => Terms.Count > 0 || Format is not null || Status is not null || OnlyWithCover;

    /// <summary>有没有关键词（决定空状态显示"没有找到 X"还是"当前筛选条件下没有书"）。</summary>
    public bool HasKeyword => Terms.Count > 0;
}
