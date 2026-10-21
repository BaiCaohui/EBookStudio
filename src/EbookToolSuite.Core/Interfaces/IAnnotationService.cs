using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Core.Interfaces;

/// <summary>
/// 书签与笔记的应用服务（T6-5）：阅读器只需要它，不直接碰仓储。
/// 导出放这里而不是 UI 里：Markdown 的格式是可验收的产物（计划书判据 6），
/// 放在服务层才能被单测钉住。
/// </summary>
public interface IAnnotationService
{
    Task<IReadOnlyList<Bookmark>> ListBookmarksAsync(Guid bookId, CancellationToken ct = default);

    Task AddBookmarkAsync(Bookmark bookmark, CancellationToken ct = default);

    Task<bool> DeleteBookmarkAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<Note>> ListNotesAsync(Guid bookId, CancellationToken ct = default);

    Task AddNoteAsync(Note note, CancellationToken ct = default);

    /// <summary>改笔记：内容总是按传入值写入；样式/颜色传 null = 不动。</summary>
    Task<bool> UpdateNoteAsync(
        Guid id, string? content, int? style, int? color, CancellationToken ct = default);

    Task<bool> DeleteNoteAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// 导出为 Markdown 文件（落点固定为 Data/Exports/，见 <see cref="IAppPaths.ExportsDir"/>）。
    /// <paramref name="bookId"/> 为 null 时导出全库。返回写好的文件绝对路径（调用方负责提示或打开）。
    /// </summary>
    Task<string> ExportMarkdownAsync(Guid? bookId, CancellationToken ct = default);
}
