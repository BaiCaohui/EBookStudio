using System.Text;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.IO;
using EbookToolSuite.Core.Models;
using Microsoft.Extensions.Logging;

namespace EbookToolSuite.Services.Annotations;

/// <summary>
/// 书签与笔记的应用服务（T6-5）。仓储只认 bookId，这里负责两件仓储不该知道的事：
/// 按书分组（导出需要书名）与 Markdown 的排版（判据 6 的产物格式）。
/// </summary>
public sealed class AnnotationService : IAnnotationService
{
    private readonly IAnnotationRepository _repo;
    private readonly IBookRepository _books;
    private readonly IAppPaths _paths;
    private readonly ILogger<AnnotationService> _log;

    public AnnotationService(
        IAnnotationRepository repo,
        IBookRepository books,
        IAppPaths paths,
        ILogger<AnnotationService> log)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _books = books ?? throw new ArgumentNullException(nameof(books));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    // ---------- 书签 ----------

    public Task<IReadOnlyList<Bookmark>> ListBookmarksAsync(Guid bookId, CancellationToken ct = default)
        => _repo.ListBookmarksAsync(bookId, ct);

    public Task AddBookmarkAsync(Bookmark bookmark, CancellationToken ct = default)
        => _repo.AddBookmarkAsync(bookmark, ct);

    public Task<bool> DeleteBookmarkAsync(Guid id, CancellationToken ct = default)
        => _repo.DeleteBookmarkAsync(id, ct);

    // ---------- 笔记 ----------

    public Task<IReadOnlyList<Note>> ListNotesAsync(Guid bookId, CancellationToken ct = default)
        => _repo.ListNotesAsync(bookId, ct);

    public Task AddNoteAsync(Note note, CancellationToken ct = default)
        => _repo.AddNoteAsync(note, ct);

    public Task<bool> UpdateNoteAsync(
        Guid id, string? content, int? style, int? color, CancellationToken ct = default)
        => _repo.UpdateNoteAsync(id, content, style, color, ct);

    public Task<bool> DeleteNoteAsync(Guid id, CancellationToken ct = default)
        => _repo.DeleteNoteAsync(id, ct);

    // ---------- 导出 ----------

    public async Task<string> ExportMarkdownAsync(Guid? bookId, CancellationToken ct = default)
    {
        var exportDir = _paths.ExportsDir;

        var bookmarks = bookId is null
            ? await _repo.ListAllBookmarksAsync(ct).ConfigureAwait(false)
            : await _repo.ListBookmarksAsync(bookId.Value, ct).ConfigureAwait(false);

        var notes = bookId is null
            ? await _repo.ListAllNotesAsync(ct).ConfigureAwait(false)
            : await _repo.ListNotesAsync(bookId.Value, ct).ConfigureAwait(false);

        /* 书名的取法：只查真正出现过的 bookId。全库导出时可能是几十本，
           但不该为此把整个书库拉出来（1000 本的书库有更贵的用法）。 */
        var bookIds = bookmarks.Select(b => b.BookId)
            .Concat(notes.Select(n => n.BookId))
            .Distinct()
            .ToList();

        var titles = new Dictionary<Guid, string>();

        foreach (var id in bookIds)
        {
            var book = await _books.GetByIdAsync(id, ct).ConfigureAwait(false);
            titles[id] = book?.Title ?? "(已删除的书)";
        }

        var markdown = BuildMarkdown(bookmarks, notes, titles);

        Directory.CreateDirectory(exportDir);

        var name = "书签笔记-" + DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss") + ".md";
        var path = Path.Combine(exportDir, name);

        await AtomicFile
            .WriteAllTextAsync(path, markdown, new UTF8Encoding(false), ct)
            .ConfigureAwait(false);

        _log.LogInformation(
            "已导出书签与笔记：{Path}（书签 {Bookmarks} 条，笔记 {Notes} 条）",
            path, bookmarks.Count, notes.Count);

        return path;
    }

    /// <summary>
    /// 生成 Markdown。格式定死在这里（判据 6 的验收对象）：
    /// 一级标题 + 生成时间，每本书一个二级标题，书签一个三级小节、笔记逐条四级小标题。
    /// 引文用 &gt; 引用块，用户输入跟在后面；高亮没有输入内容，就不留空行。
    /// </summary>
    private static string BuildMarkdown(
        IReadOnlyList<Bookmark> bookmarks,
        IReadOnlyList<Note> notes,
        IReadOnlyDictionary<Guid, string> titles)
    {
        var text = new StringBuilder();

        text.AppendLine("# 书签与笔记");
        text.AppendLine();
        text.Append("生成时间：").AppendLine(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm"));
        text.AppendLine();

        if (bookmarks.Count == 0 && notes.Count == 0)
        {
            text.AppendLine("（没有任何书签或笔记。）");
            return text.ToString();
        }

        // 按书分组；书的顺序按"第一次出现"排，避免字典顺序这种对用户无意义的排序
        var order = new List<Guid>();

        foreach (var id in bookmarks.Select(b => b.BookId).Concat(notes.Select(n => n.BookId)))
        {
            if (!order.Contains(id))
            {
                order.Add(id);
            }
        }

        foreach (var id in order)
        {
            text.Append("## 《").Append(titles.TryGetValue(id, out var title) ? title : "(未知的书)").AppendLine("》");
            text.AppendLine();

            var bookMarks = bookmarks.Where(b => b.BookId == id).ToList();

            if (bookMarks.Count > 0)
            {
                text.AppendLine("### 书签");
                text.AppendLine();

                foreach (var bookmark in bookMarks)
                {
                    text.Append("- **")
                        .Append(bookmark.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))
                        .Append("** · ")
                        .AppendLine(BookmarkLabel(bookmark));
                }

                text.AppendLine();
            }

            var bookNotes = notes.Where(n => n.BookId == id).ToList();

            if (bookNotes.Count > 0)
            {
                text.AppendLine("### 笔记");
                text.AppendLine();

                foreach (var note in bookNotes)
                {
                    text.Append("#### ")
                        .Append(note.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))
                        .Append(" · ")
                        .AppendLine(StyleLabel(note));

                    var quote = note.Quote ?? string.Empty;

                    foreach (var line in quote.Split('\n'))
                    {
                        text.Append("> ").AppendLine(line);
                    }

                    if (!string.IsNullOrWhiteSpace(note.Content))
                    {
                        text.AppendLine();
                        text.AppendLine(note.Content);
                    }

                    text.AppendLine();
                }
            }
        }

        return text.ToString();
    }

    /// <summary>笔记显示名：批注一律叫"批注"，纯标记按样式名（T6-5 补短：四种标记）。</summary>
    private static string StyleLabel(Note note)
        => note.Type == NoteType.Annotation
            ? "批注"
            : note.Style switch
            {
                MarkStyle.Underline => "下划线",
                MarkStyle.Wavy => "波浪线",
                MarkStyle.Strikethrough => "删除线",
                _ => "高亮",
            };

    /// <summary>书签显示名：页面给的标签优先，否则按"第 N 章 / 第 N 页"兜底。</summary>
    private static string BookmarkLabel(Bookmark bookmark)
    {
        if (!string.IsNullOrWhiteSpace(bookmark.Label))
        {
            return bookmark.Label;
        }

        if (bookmark.PageIndex is not null)
        {
            return "第 " + (bookmark.PageIndex.Value + 1) + " 页";
        }

        return "第 " + ((bookmark.SpineIndex ?? 0) + 1) + " 章";
    }
}
