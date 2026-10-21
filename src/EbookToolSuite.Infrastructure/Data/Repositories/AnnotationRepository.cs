using EbookToolSuite.Core.Common;
using EbookToolSuite.Infrastructure.Data;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using Microsoft.Data.Sqlite;

namespace EbookToolSuite.Infrastructure.Data.Repositories;

/// <summary>
/// 书签 / 笔记仓储（T6-5）。写法照 BookRepository 的裸 ADO 风格：
/// 全部走 SqliteParameter，时间戳统一 UnixTime 毫秒。
/// </summary>
public sealed class AnnotationRepository : IAnnotationRepository
{
    private const string BookmarkColumns =
        "id, book_id, spine_index, page_index, location, label, created_at";

    private const string NoteColumns =
        "n.id, n.book_id, n.spine_index, n.page_index, n.anchor, n.color, n.type, n.quote, n.content, " +
        "n.created_at, n.updated_at, COALESCE(s.style, 0)";

    /// <summary>笔记查询：样式在旁表 note_style 里（迁移 0009），没有行按高亮（0）读。</summary>
    private const string NoteFrom =
        "FROM notes n LEFT JOIN note_style s ON s.note_id = n.id";

    private readonly SqliteConnectionFactory _factory;

    public AnnotationRepository(SqliteConnectionFactory factory)
        => _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    // ---------- 书签 ----------

    public async Task<IReadOnlyList<Bookmark>> ListBookmarksAsync(Guid bookId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);

        return await conn.QueryAsync(
            $"SELECT {BookmarkColumns} FROM bookmarks WHERE book_id = @book_id ORDER BY created_at DESC, id;",
            MapBookmark,
            new[] { SqliteExtensions.P("@book_id", bookId.ToString()) },
            ct: ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Bookmark>> ListAllBookmarksAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);

        return await conn.QueryAsync(
            $"SELECT {BookmarkColumns} FROM bookmarks ORDER BY created_at DESC, id;",
            MapBookmark,
            ct: ct).ConfigureAwait(false);
    }

    public async Task AddBookmarkAsync(Bookmark bookmark, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bookmark);

        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);

        await conn.ExecuteAsync(
            """
            INSERT INTO bookmarks (id, book_id, spine_index, page_index, location, label, created_at)
            VALUES (@id, @book_id, @spine_index, @page_index, @location, @label, @created_at);
            """,
            new[]
            {
                SqliteExtensions.P("@id", bookmark.Id.ToString()),
                SqliteExtensions.P("@book_id", bookmark.BookId.ToString()),
                SqliteExtensions.P("@spine_index", bookmark.SpineIndex),
                SqliteExtensions.P("@page_index", bookmark.PageIndex),
                SqliteExtensions.P("@location", bookmark.Location?.ToJson()),
                SqliteExtensions.P("@label", bookmark.Label),
                SqliteExtensions.P("@created_at", UnixTime.ToMillis(bookmark.CreatedAt)),
            },
            ct: ct).ConfigureAwait(false);
    }

    public async Task<bool> DeleteBookmarkAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);

        var affected = await conn.ExecuteAsync(
            "DELETE FROM bookmarks WHERE id = @id;",
            new[] { SqliteExtensions.P("@id", id.ToString()) },
            ct: ct).ConfigureAwait(false);

        return affected > 0;
    }

    // ---------- 笔记 ----------

    public async Task<IReadOnlyList<Note>> ListNotesAsync(Guid bookId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);

        return await conn.QueryAsync(
            $"SELECT {NoteColumns} {NoteFrom} WHERE n.book_id = @book_id ORDER BY n.created_at ASC, n.id;",
            MapNote,
            new[] { SqliteExtensions.P("@book_id", bookId.ToString()) },
            ct: ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Note>> ListAllNotesAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);

        return await conn.QueryAsync(
            $"SELECT {NoteColumns} {NoteFrom} ORDER BY n.created_at ASC, n.id;",
            MapNote,
            ct: ct).ConfigureAwait(false);
    }

    public async Task AddNoteAsync(Note note, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(note);

        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        await conn.ExecuteAsync(
            """
            INSERT INTO notes (
                id, book_id, spine_index, page_index, anchor, color, type, quote, content,
                created_at, updated_at)
            VALUES (
                @id, @book_id, @spine_index, @page_index, @anchor, @color, @type, @quote, @content,
                @created_at, @updated_at);
            """,
            new[]
            {
                SqliteExtensions.P("@id", note.Id.ToString()),
                SqliteExtensions.P("@book_id", note.BookId.ToString()),
                SqliteExtensions.P("@spine_index", note.SpineIndex),
                SqliteExtensions.P("@page_index", note.PageIndex),
                SqliteExtensions.P("@anchor", note.Anchor?.ToJson()),
                SqliteExtensions.P("@color", note.Color),
                SqliteExtensions.P("@type", (int)note.Type),
                SqliteExtensions.P("@quote", note.Quote),
                SqliteExtensions.P("@content", note.Content),
                SqliteExtensions.P("@created_at", UnixTime.ToMillis(note.CreatedAt)),
                SqliteExtensions.P("@updated_at", UnixTime.ToMillis(note.UpdatedAt)),
            },
            tx,
            ct).ConfigureAwait(false);

        /* 样式在旁表（迁移 0009）。ON CONFLICT 让"同 id 再写一次"也安全，
           比如测试里同一份 Note 连写两遍。 */
        await conn.ExecuteAsync(
            """
            INSERT INTO note_style (note_id, style) VALUES (@note_id, @style)
            ON CONFLICT(note_id) DO UPDATE SET style = excluded.style;
            """,
            new[]
            {
                SqliteExtensions.P("@note_id", note.Id.ToString()),
                SqliteExtensions.P("@style", (int)note.Style),
            },
            tx,
            ct).ConfigureAwait(false);

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> UpdateNoteAsync(
        Guid id, string? content, int? style, int? color, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        /* 内容总是写（空 = 清空）；颜色传 null 表示"不动"（COALESCE 保留旧值）。 */
        var affected = await conn.ExecuteAsync(
            "UPDATE notes SET content = @content, color = COALESCE(@color, color), updated_at = @now "
            + "WHERE id = @id;",
            new[]
            {
                SqliteExtensions.P("@content", content),
                SqliteExtensions.P("@color", color),
                SqliteExtensions.P("@now", UnixTime.ToMillis(DateTimeOffset.UtcNow)),
                SqliteExtensions.P("@id", id.ToString()),
            },
            tx,
            ct).ConfigureAwait(false);

        if (style is not null)
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO note_style (note_id, style) VALUES (@note_id, @style)
                ON CONFLICT(note_id) DO UPDATE SET style = excluded.style;
                """,
                new[]
                {
                    SqliteExtensions.P("@note_id", id.ToString()),
                    SqliteExtensions.P("@style", style.Value),
                },
                tx,
                ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return affected > 0;
    }

    public async Task<bool> DeleteNoteAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);

        var affected = await conn.ExecuteAsync(
            "DELETE FROM notes WHERE id = @id;",
            new[] { SqliteExtensions.P("@id", id.ToString()) },
            ct: ct).ConfigureAwait(false);

        return affected > 0;
    }

    // ---------- 映射 ----------

    private static Bookmark MapBookmark(SqliteDataReader r) => new()
    {
        Id = r.GuidValue(0),
        BookId = r.GuidValue(1),
        SpineIndex = r.Int32OrNull(2),
        PageIndex = r.Int32OrNull(3),
        Location = ReaderLocator.TryParse(r.Text(4)),
        Label = r.Text(5),
        CreatedAt = UnixTime.FromMillis(r.GetInt64(6)),
    };

    private static Note MapNote(SqliteDataReader r) => new()
    {
        Id = r.GuidValue(0),
        BookId = r.GuidValue(1),
        SpineIndex = r.Int32OrNull(2),
        PageIndex = r.Int32OrNull(3),
        Anchor = NoteAnchor.TryParse(r.Text(4)),
        Color = r.GetInt32(5),
        Type = (Core.Enums.NoteType)r.GetInt32(6),
        Quote = r.Text(7),
        Content = r.Text(8),
        CreatedAt = UnixTime.FromMillis(r.GetInt64(9)),
        UpdatedAt = UnixTime.FromMillis(r.Int64OrNull(10)),
        Style = (Core.Enums.MarkStyle)r.GetInt32(11),
    };
}
