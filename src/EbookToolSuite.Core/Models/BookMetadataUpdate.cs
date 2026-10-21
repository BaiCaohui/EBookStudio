namespace EbookToolSuite.Core.Models;

/// <summary>
/// 编辑保存后要回写 <c>books</c> 表的元数据（T3-6 保存闭环第 10 步）。
///
/// 【为什么要有它】书库网格里的书名/作者读的是数据库，不是 EPUB 的 OPF；
/// 编辑保存只改了 OPF，"列表里还是旧书名"就是这么来的。这个 record 是那次回写的入参。
///
/// TitleSort / AuthorSort 传 null 或空串都行：列表排序用
/// COALESCE(NULLIF(title_sort, ''), title)，退回书名，不会排乱。
/// </summary>
public sealed record BookMetadataUpdate(
    Guid BookId,
    string Title,
    string? TitleSort = null,
    string? Authors = null,
    string? AuthorSort = null,
    string? Publisher = null,
    string? PublishedDate = null,
    string? Isbn = null,
    string? Language = null,
    string? Description = null,
    string? Series = null,
    double? SeriesIndex = null,
    int Rating = 0);
