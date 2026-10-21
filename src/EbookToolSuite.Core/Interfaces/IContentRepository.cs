using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Core.Interfaces;

/// <summary>
/// 正文索引的持久层（T6-4）：content_chunk / content_fts / content_index_state 三张表的读写。
/// 搜索的 SQL 也在这里——SQLite FTS5 的 MATCH 与 snippet 不适合放到服务层拼字符串。
/// </summary>
public interface IContentRepository
{
    /// <summary>
    /// 用新的切片替换这本书的全部旧切片（单事务）。索引重建与首次索引都走它。
    /// 外部内容表的同步由迁移里的触发器负责，这里只管写 content_chunk。
    /// </summary>
    Task ReplaceBookChunksAsync(
        Guid bookId, IReadOnlyList<ContentChunk> chunks, CancellationToken ct = default);

    Task DeleteBookChunksAsync(Guid bookId, CancellationToken ct = default);

    Task SaveStateAsync(ContentIndexState state, CancellationToken ct = default);

    Task<ContentIndexState?> GetStateAsync(Guid bookId, CancellationToken ct = default);

    /// <summary>需要索引的书：没有状态行，或状态是 Pending / Indexing（上次中断）。</summary>
    Task<IReadOnlyList<Guid>> ListBookIdsNeedingIndexAsync(CancellationToken ct = default);

    /// <summary>执行一次全文搜索。查询词的分词方式由服务层决定，这里只负责两种路径的 SQL。</summary>
    Task<ContentSearchResult> SearchAsync(
        ContentSearchQuery query, CancellationToken ct = default);

    /// <summary>索引体积（用于磁盘预算记录，判据 8）。</summary>
    Task<ContentIndexVolume> MeasureVolumeAsync(CancellationToken ct = default);
}
