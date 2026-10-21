using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Core.Interfaces;

/// <summary>
/// 跨书全文搜索（T6-4）。只查正文索引，不碰元数据（元数据搜索是 T2-3 的
/// BookQuery.SearchText，两者口径不同，别混）。
/// </summary>
public interface IContentSearchService
{
    /// <summary>
    /// 搜索。查询词按空格分词、词间 AND；命中按书分组由界面做。
    /// </summary>
    /// <param name="text">用户输入的关键词。</param>
    /// <param name="bookId">只在这本书里搜；null = 全库。</param>
    /// <param name="limit">最多返回多少条切片。</param>
    Task<ContentSearchResult> SearchAsync(
        string text,
        Guid? bookId = null,
        int limit = 200,
        CancellationToken ct = default);
}
