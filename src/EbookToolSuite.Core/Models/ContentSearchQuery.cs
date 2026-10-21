namespace EbookToolSuite.Core.Models;

/// <summary>
/// 全文搜索的查询参数（T6-4）。分两种路径：
/// <para>
/// <b>FTS</b>：查询词（去空格后）每个都 ≥3 个字符 → 走 `content_fts MATCH`（trigram 子串匹配）。
/// </para>
/// <para>
/// <b>LIKE 兜底</b>：只要有一个词 &lt;3 个字符 → trigram 分词器产不出 token，
/// 整条查询改走 `content_chunk.body LIKE '%词%'`。2 字中文词很常见（"爱情""妖怪"），
/// 不兜底等于搜不到，比慢更糟。
/// </para>
/// </summary>
public sealed record ContentSearchQuery
{
    /// <summary>已分词的查询词（去空、去重、封顶）。</summary>
    public required IReadOnlyList<string> Terms { get; init; }

    /// <summary>只在这本书里搜；null = 全库。</summary>
    public Guid? BookId { get; init; }

    public int Limit { get; init; } = 200;

    /// <summary>true = 走 LIKE 兜底（服务层按 Terms 里最短的词决定）。</summary>
    public required bool UseLikeFallback { get; init; }
}
