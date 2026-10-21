using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Core.Interfaces;

/// <summary>
/// 书签与笔记的持久化（T6-5，设计文档 5.2 的 bookmarks / notes 两张表）。
/// </summary>
/// <remarks>
/// 为什么没有"更新书签"：书签只有位置与标签，改了就是另一个书签；用户要改就删了重加。
/// 笔记留了一个更新内容的方法（补写/改错字），这也是设计文档里唯一有意义的编辑形态。
/// </remarks>
public interface IAnnotationRepository
{
    // ---------- 书签 ----------

    /// <summary>sample的书签，按加入时间倒序（新的在上）。</summary>
    Task<IReadOnlyList<Bookmark>> ListBookmarksAsync(Guid bookId, CancellationToken ct = default);

    Task AddBookmarkAsync(Bookmark bookmark, CancellationToken ct = default);

    Task<bool> DeleteBookmarkAsync(Guid id, CancellationToken ct = default);

    /// <summary>全库书签（导出用）。</summary>
    Task<IReadOnlyList<Bookmark>> ListAllBookmarksAsync(CancellationToken ct = default);

    // ---------- 笔记 / 高亮 ----------

    /// <summary>sample的笔记，按加入时间正序（阅读顺序感；同章按加入先后）。</summary>
    Task<IReadOnlyList<Note>> ListNotesAsync(Guid bookId, CancellationToken ct = default);

    Task AddNoteAsync(Note note, CancellationToken ct = default);

    /// <summary>
    /// 改笔记：内容总是按传入值写入（空 = 清空）；样式与颜色传 null 表示"不动"。
    /// 高亮改成有内容就是批注，所以不做类型限制。
    /// </summary>
    Task<bool> UpdateNoteAsync(
        Guid id, string? content, int? style, int? color, CancellationToken ct = default);

    Task<bool> DeleteNoteAsync(Guid id, CancellationToken ct = default);

    /// <summary>全库笔记（导出用）。</summary>
    Task<IReadOnlyList<Note>> ListAllNotesAsync(CancellationToken ct = default);
}
