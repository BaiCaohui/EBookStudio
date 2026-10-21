using System.Globalization;
using System.Text;
using EbookToolSuite.Core.Common;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using Microsoft.Data.Sqlite;

namespace EbookToolSuite.Infrastructure.Data.Repositories;

/// <summary>
/// 手写 ADO 的 Book 仓储，不引 EF Core / Dapper（设计文档 5.4）。
/// 读走共享长连接，写走短事务；SQL 全部参数化。
/// </summary>
public sealed class BookRepository : IBookRepository
{
    private const string BookColumns =
        "id, title, title_sort, authors, author_sort, translators, publisher, published_date, " +
        "isbn, language, description, series, series_index, rating, reading_status, identifier, " +
        "word_count, chapter_count, cover_path, damaged, import_batch_id, " +
        "created_at, updated_at, last_read_at";

    private const string FormatColumns =
        "id, book_id, format, file_path, file_size, full_hash, quick_fingerprint, " +
        "file_modified_at, missing, added_at";

    private const string InsertBookSql =
        """
        INSERT INTO books (
            id, title, title_sort, authors, author_sort, translators, publisher, published_date,
            isbn, language, description, series, series_index, rating, reading_status, identifier,
            word_count, chapter_count, cover_path, damaged, import_batch_id,
            created_at, updated_at, last_read_at)
        VALUES (
            @id, @title, @title_sort, @authors, @author_sort, @translators, @publisher, @published_date,
            @isbn, @language, @description, @series, @series_index, @rating, @reading_status, @identifier,
            @word_count, @chapter_count, @cover_path, @damaged, @import_batch_id,
            @created_at, @updated_at, @last_read_at);
        """;

    private const string InsertFormatSql =
        """
        INSERT INTO book_formats (
            id, book_id, format, file_path, file_size, full_hash, quick_fingerprint,
            file_modified_at, missing, added_at)
        VALUES (
            @id, @book_id, @format, @file_path, @file_size, @full_hash, @quick_fingerprint,
            @file_modified_at, @missing, @added_at);
        """;

    /// <summary>就地更新sample某个格式的体积与指纹（polish 就地替换文件后调用）。</summary>
    private const string UpdateFormatMetadataSql =
        """
        UPDATE book_formats
        SET file_size = @size,
            quick_fingerprint = @fp,
            full_hash = @hash,
            file_modified_at = @now,
            missing = 0
        WHERE book_id = @id AND format = @format;
        """;

    /// <summary>被标为副本的书默认不进任何列表（T2-2 的重复检测会写这张表）。</summary>
    /// <summary>扫描用（T2-6）：全部格式文件 + 书名，一次 JOIN 取回避免 N+1。</summary>
    private const string ScanFormatsSql =
        """
        SELECT f.id, f.book_id, b.title, f.format, f.file_path, f.file_size,
               f.file_modified_at, f.missing
        FROM book_formats f
        JOIN books b ON b.id = f.book_id
        ORDER BY b.title COLLATE NOCASE, f.format;
        """;

    private const string HiddenCopiesFilter = "id NOT IN (SELECT book_id FROM duplicate_copy)";

    /// <summary>LIKE 的转义子句。反斜杠在 C# 里要写两个，在 SQL 里就是一个。</summary>
    private const string LikeEscape = " ESCAPE '\\'";

    /// <summary>关键词要覆盖的元数据列。正文不在其内（那是 T6-4 的 FTS5）。</summary>
    private static readonly string[] SearchColumns =
    {
        "title", "title_sort", "authors", "author_sort", "translators",
        "publisher", "series", "isbn", "identifier",
    };

    /// <summary>删书。外键级联会带走 book_formats 与 reading_progress。</summary>
    private const string DeleteBookSql = "DELETE FROM books WHERE id = @id;";

    /// <summary>
    /// 删一条格式记录（用户 2026-10-20「移除该格式」）。刻意只删 book_formats 这一行：
    /// 文件早已不存在，没东西可删；阅读进度按书 Id 存，也不该跟着动。
    /// </summary>
    private const string DeleteFormatSql = "DELETE FROM book_formats WHERE id = @id;";

    private readonly SqliteConnectionFactory _factory;

    public BookRepository(SqliteConnectionFactory factory) => _factory = factory;

    public async Task AddAsync(Book book, CancellationToken ct = default)
    {
        try
        {
            await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

            await conn.ExecuteAsync(InsertBookSql, BookParams(book), tx, ct).ConfigureAwait(false);

            // book_id 一律以 book.Id 为准：调用方给 BookFormat.BookId 留空也不会写坏外键。
            foreach (var f in book.Formats)
            {
                await conn.ExecuteAsync(InsertFormatSql, FormatParams(f, book.Id), tx, ct)
                    .ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SqliteException ex) when (SqliteErrors.IsInfrastructureFailure(ex))
        {
            // T7-2 第 13 项：另一个进程占着书库时，busy_timeout 等满 5 秒后到这里，
            // 必须翻成"书库正被占用，关掉另一个实例后重试"，而不是英文 SQLITE_BUSY。
            // 唯一约束等程序缺陷仍原样抛（测试与日志需要看到真实原因）。
            throw SqliteErrors.Translate(ex, "写入书库");
        }
    }
    // ---------- 读 ----------

    public Task<Book?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => _factory.WithSharedReadAsync(async conn =>
        {
            var books = await conn.QueryAsync(
                $"SELECT {BookColumns} FROM books WHERE id = @id;",
                MapBook,
                new[] { P("@id", id.ToString()) },
                ct: ct).ConfigureAwait(false);

            if (books.Count == 0)
            {
                return null;
            }

            await AttachFormatsAsync(conn, books, ct).ConfigureAwait(false);
            return books[0];
        }, ct);

    public Task<IReadOnlyList<Book>> ListAsync(int offset, int limit, CancellationToken ct = default)
        => _factory.WithSharedReadAsync(async conn =>
        {
            var books = await conn.QueryAsync(
                $"SELECT {BookColumns} FROM books " +
                "ORDER BY COALESCE(NULLIF(title_sort, ''), title) COLLATE NOCASE, id " +
                "LIMIT @limit OFFSET @offset;",
                MapBook,
                new[] { P("@limit", limit), P("@offset", offset) },
                ct: ct).ConfigureAwait(false);

            await AttachFormatsAsync(conn, books, ct).ConfigureAwait(false);
            return (IReadOnlyList<Book>)books;
        }, ct);

    public Task<int> CountAsync(CancellationToken ct = default)
        => _factory.WithSharedReadAsync(
            async conn => await conn.ScalarAsync<int>("SELECT COUNT(1) FROM books;", ct: ct)
                .ConfigureAwait(false),
            ct);

    public Task<IReadOnlyList<Book>> QueryAsync(BookQuery query, CancellationToken ct = default)
        => _factory.WithSharedReadAsync(async conn =>
        {
            var (where, ps) = BuildFilter(query);

            ps.Add(P("@limit", query.Limit));
            ps.Add(P("@offset", query.Offset));

            var sql =
                "SELECT " + BookColumns + " FROM books " + where +
                " ORDER BY " + OrderByClause(query) +
                " LIMIT @limit OFFSET @offset;";

            var books = await conn.QueryAsync(sql, MapBook, ps, ct: ct).ConfigureAwait(false);

            await AttachFormatsAsync(conn, books, ct).ConfigureAwait(false);
            return (IReadOnlyList<Book>)books;
        }, ct);

    public Task<int> CountQueryAsync(BookQuery query, CancellationToken ct = default)
        => _factory.WithSharedReadAsync(async conn =>
        {
            var (where, ps) = BuildFilter(query);

            return await conn.ScalarAsync<int>(
                "SELECT COUNT(1) FROM books " + where + ";", ps, ct: ct).ConfigureAwait(false);
        }, ct);

    public Task<bool> FormatPathExistsAsync(string filePath, CancellationToken ct = default)
    {
        var normalized = Path.GetFullPath(filePath);
        return _factory.WithSharedReadAsync(async conn =>
        {
            var n = await conn.ScalarAsync<int>(
                "SELECT COUNT(1) FROM book_formats WHERE file_path = @p;",
                new[] { P("@p", normalized) },
                ct: ct).ConfigureAwait(false);
            return n > 0;
        }, ct);
    }

    public Task<Guid?> FindBookIdByFingerprintAsync(string quickFingerprint, CancellationToken ct = default)
        => _factory.WithSharedReadAsync(async conn =>
        {
            var ids = await conn.QueryAsync(
                "SELECT book_id FROM book_formats WHERE quick_fingerprint = @fp LIMIT 1;",
                rdr => rdr.GuidValue(0),
                new[] { P("@fp", quickFingerprint) },
                ct: ct).ConfigureAwait(false);

            return ids.Count == 0 ? (Guid?)null : ids[0];
        }, ct);

    /// <summary>批量按快速指纹找书（T2-1 预检去重）。内部按 900 分块，一次 IN 查询。</summary>
    public Task<IReadOnlyList<FingerprintMatch>> FindBooksByFingerprintsAsync(
        IReadOnlyList<string> quickFingerprints, CancellationToken ct = default)
        => _factory.WithSharedReadAsync(async conn =>
        {
            var hits = new List<FingerprintMatch>();

            if (quickFingerprints.Count == 0)
            {
                return (IReadOnlyList<FingerprintMatch>)hits;
            }

            // SQLite 一条 SQL 的参数上限是 999，和 GetProgressMapAsync 一样按 900 切。
            const int chunkSize = 900;

            for (var offset = 0; offset < quickFingerprints.Count; offset += chunkSize)
            {
                var chunk = quickFingerprints.Skip(offset).Take(chunkSize).ToList();
                var names = new string[chunk.Count];
                var args = new SqliteParameter[chunk.Count];

                for (var i = 0; i < chunk.Count; i++)
                {
                    names[i] = "@f" + i;
                    args[i] = P(names[i], chunk[i]);
                }

                var rows = await conn.QueryAsync(
                    "SELECT f.quick_fingerprint, f.book_id, b.title " +
                    "FROM book_formats f JOIN books b ON b.id = f.book_id " +
                    "WHERE f.quick_fingerprint IN (" + string.Join(", ", names) + ");",
                    r => new FingerprintMatch(r.Text(0) ?? string.Empty, r.GuidValue(1), r.Text(2) ?? string.Empty),
                    args,
                    ct: ct).ConfigureAwait(false);

                hits.AddRange(rows);
            }

            return (IReadOnlyList<FingerprintMatch>)hits;
        }, ct);
    // ---------- 格式 ----------

    public Task<bool> HasFormatAsync(
        Guid bookId, BookFormatKind format, CancellationToken ct = default)
        => _factory.WithSharedReadAsync(async conn =>
        {
            var count = await conn.ScalarAsync<long>(
                "SELECT COUNT(1) FROM book_formats WHERE book_id = @id AND format = @format;",
                new[] { P("@id", bookId.ToString()), P("@format", format.ToStorageString()) },
                null,
                ct).ConfigureAwait(false);

            return count > 0;
        }, ct);

    public async Task AddFormatAsync(BookFormat format, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(InsertFormatSql, FormatParams(format, format.BookId), null, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 删掉一条 book_formats 记录（用户 2026-10-20「移除该格式」）。
    /// 单事务执行：SQLite 里 DELETE 本身是原子的，但仓储的写路径一律走显式事务，
    /// 这样将来要加"顺手改 books.updated_at"之类的同事务动作时不会漏。
    /// </summary>
    /// <returns>是否真的删掉了一行。记录本来就不在时返回 false。</returns>
    public async Task<bool> DeleteFormatAsync(Guid formatId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        var affected = await conn.ExecuteAsync(
            DeleteFormatSql,
            new[] { P("@id", formatId.ToString("D")) },
            tx,
            ct).ConfigureAwait(false);

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return affected > 0;
    }

    /// <summary>
    /// 就地更新sample某个格式的体积与指纹。
    /// 用于「不换格式，只优化」：文件被就地替换了，体积与快速指纹都变了。
    /// fullHash 传 null 表示内容已变、旧的全量哈希作废（等下次精确判重再算）。
    /// </summary>
    /// <returns>是否真的更新了一行。</returns>
    public async Task<bool> UpdateFormatMetadataAsync(
        Guid bookId,
        BookFormatKind format,
        long fileSize,
        string? quickFingerprint,
        string? fullHash,
        DateTimeOffset? modifiedAt = null,
        CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);

        var affected = await conn.ExecuteAsync(
            UpdateFormatMetadataSql,
            new[]
            {
                P("@size", fileSize),
                P("@fp", quickFingerprint),
                P("@hash", fullHash),
                P("@now", UnixTime.ToMillis(modifiedAt ?? DateTimeOffset.UtcNow)),
                P("@id", bookId.ToString("D")),
                P("@format", format.ToStorageString()),
            },
            null,
            ct).ConfigureAwait(false);

        return affected > 0;
    }

    /// <summary>扫描用：一次 JOIN 取回全部格式文件与书名（T2-6）。</summary>
    public Task<IReadOnlyList<FormatScanRow>> ListFormatsForScanAsync(CancellationToken ct = default)
        => _factory.WithSharedReadAsync(async conn =>
        {
            var rows = await conn.QueryAsync(ScanFormatsSql, MapScanRow, null, null, ct)
                .ConfigureAwait(false);

            return (IReadOnlyList<FormatScanRow>)rows;
        }, ct);

    /// <summary>
    /// 扫描之后批量改 missing 标记（T2-6）。两个方向分开传：
    /// markMissing = 文件没了要标成缺失的；clearMissing = 文件回来了要恢复正常的。
    /// </summary>
    /// <returns>实际改动的行数。</returns>
    public async Task<int> UpdateMissingFlagsAsync(
        IReadOnlyList<Guid> markMissing,
        IReadOnlyList<Guid> clearMissing,
        CancellationToken ct = default)
    {
        if (markMissing.Count == 0 && clearMissing.Count == 0)
        {
            return 0;
        }

        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);

        var affected = await SetMissingAsync(conn, markMissing, 1, ct).ConfigureAwait(false);
        affected += await SetMissingAsync(conn, clearMissing, 0, ct).ConfigureAwait(false);

        return affected;
    }

    /// <summary>按 900 个 id 一段拼 IN 子句（SQLite 参数上限 999）。</summary>
    private static async Task<int> SetMissingAsync(
        SqliteConnection conn, IReadOnlyList<Guid> ids, int missing, CancellationToken ct)
    {
        const int Chunk = 900;
        var affected = 0;

        for (var i = 0; i < ids.Count; i += Chunk)
        {
            var slice = ids.Skip(i).Take(Chunk).ToList();
            var names = new string[slice.Count];
            var parameters = new List<SqliteParameter>(slice.Count);

            for (var k = 0; k < slice.Count; k++)
            {
                names[k] = "@m" + k;
                parameters.Add(P(names[k], slice[k].ToString("D")));
            }

            var sql = "UPDATE book_formats SET missing = " + missing +
                      " WHERE id IN (" + string.Join(", ", names) + ");";

            affected += await conn.ExecuteAsync(sql, parameters, null, ct).ConfigureAwait(false);
        }

        return affected;
    }

    // ---------- 删除 ----------

    /// <summary>
    /// 删除一本书。book_formats 与 reading_progress 都是 ON DELETE CASCADE，
    /// 所以删 books 这一行就够了，但**必须**在开着外键的连接上执行
    /// （连接串里 ForeignKeys=true，见 SqliteConnectionFactory）。
    /// </summary>
    /// <returns>是否真的删掉了一行。书本来就不在时返回 false。</returns>
    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);

        var affected = await conn.ExecuteAsync(
            DeleteBookSql,
            new[] { P("@id", id.ToString("D")) },
            null,
            ct).ConfigureAwait(false);

        return affected > 0;
    }
    // ---------- 阅读进度 ----------

    public Task<ReadingProgress?> GetReadingProgressAsync(Guid bookId, CancellationToken ct = default)
        => _factory.WithSharedReadAsync(async conn =>
        {
            /* 用户 2026-10-21：连"这条进度是哪个格式下存的"一起读回来。
               换格式继续读时要靠它判断章号/页码的含义，见 ReadingProgress.SourceFormat。 */
            var rows = await conn.QueryAsync(
                "SELECT p.book_id, p.spine_index, p.page_index, p.total_pages, p.location, p.fingerprint, " +
                "p.percent, p.updated_at, f.progress_format " +
                "FROM reading_progress p " +
                "LEFT JOIN book_reading_format f ON f.book_id = p.book_id " +
                "WHERE p.book_id = @id;",
                r => new ReadingProgress
                {
                    BookId = r.GuidValue(0),
                    SpineIndex = r.Int32OrNull(1),
                    PageIndex = r.Int32OrNull(2),
                    TotalPages = r.Int32OrNull(3),
                    Location = r.Text(4),
                    Fingerprint = r.Text(5),
                    Percent = r.GetDouble(6),
                    UpdatedAt = UnixTime.FromMillis(r.GetInt64(7)),
                    SourceFormat = ParseFormat(r.Text(8)),
                },
                new[] { P("@id", bookId.ToString()) },
                ct: ct).ConfigureAwait(false);

            return rows.Count == 0 ? null : rows[0];
        }, ct);

    /// <summary>一批书的阅读进度，键是书 Id。网格角标用，一次 IN 查询避免 N+1。</summary>
    public Task<IReadOnlyDictionary<Guid, ReadingProgress>> GetProgressMapAsync(
        IReadOnlyList<Guid> bookIds, CancellationToken ct = default)
        => _factory.WithSharedReadAsync(async conn =>
        {
            var map = new Dictionary<Guid, ReadingProgress>();

            if (bookIds.Count == 0)
            {
                return (IReadOnlyDictionary<Guid, ReadingProgress>)map;
            }

            // 一条 SQL 的参数个数上限是 999，按 900 切块，书库很大时也不会炸。
            const int chunkSize = 900;

            for (var offset = 0; offset < bookIds.Count; offset += chunkSize)
            {
                var chunk = bookIds.Skip(offset).Take(chunkSize).ToList();
                var names = new string[chunk.Count];
                var args = new SqliteParameter[chunk.Count];

                for (var i = 0; i < chunk.Count; i++)
                {
                    names[i] = "@b" + i;
                    args[i] = P(names[i], chunk[i].ToString());
                }

                var rows = await conn.QueryAsync(
                    "SELECT book_id, spine_index, page_index, total_pages, location, fingerprint, " +
                    "percent, updated_at FROM reading_progress WHERE book_id IN (" +
                    string.Join(", ", names) + ");",
                    r => new ReadingProgress
                    {
                        BookId = r.GuidValue(0),
                        SpineIndex = r.Int32OrNull(1),
                        PageIndex = r.Int32OrNull(2),
                        TotalPages = r.Int32OrNull(3),
                        Location = r.Text(4),
                        Fingerprint = r.Text(5),
                        Percent = r.GetDouble(6),
                        UpdatedAt = UnixTime.FromMillis(r.GetInt64(7)),
                    },
                    args,
                    ct: ct).ConfigureAwait(false);

                foreach (var row in rows)
                {
                    map[row.BookId] = row;
                }
            }

            return (IReadOnlyDictionary<Guid, ReadingProgress>)map;
        }, ct);

    // ---------- 当前阅读格式（用户 2026-10-21） ----------

    public Task<BookFormatKind?> GetReadingFormatAsync(Guid bookId, CancellationToken ct = default)
        => _factory.WithSharedReadAsync(async conn =>
        {
            var rows = await conn.QueryAsync(
                "SELECT current_format FROM book_reading_format " +
                "WHERE book_id = @id AND current_format IS NOT NULL;",
                r => r.Text(0),
                new[] { P("@id", bookId.ToString()) },
                ct: ct).ConfigureAwait(false);

            return rows.Count == 0 ? null : ParseFormat(rows[0]);
        }, ct);

    public Task<IReadOnlyDictionary<Guid, BookFormatKind>> GetReadingFormatMapAsync(
        IReadOnlyList<Guid> bookIds, CancellationToken ct = default)
        => _factory.WithSharedReadAsync(async conn =>
        {
            var map = new Dictionary<Guid, BookFormatKind>();

            if (bookIds.Count == 0)
            {
                return (IReadOnlyDictionary<Guid, BookFormatKind>)map;
            }

            // 与进度映射同一条纪律：一条 SQL 的参数上限是 999，按 900 切块。
            const int chunkSize = 900;

            for (var offset = 0; offset < bookIds.Count; offset += chunkSize)
            {
                var chunk = bookIds.Skip(offset).Take(chunkSize).ToList();
                var names = new string[chunk.Count];
                var args = new SqliteParameter[chunk.Count];

                for (var i = 0; i < chunk.Count; i++)
                {
                    names[i] = "@b" + i;
                    args[i] = P(names[i], chunk[i].ToString());
                }

                var rows = await conn.QueryAsync(
                    "SELECT book_id, current_format FROM book_reading_format " +
                    "WHERE current_format IS NOT NULL AND book_id IN (" +
                    string.Join(", ", names) + ");",
                    r => (BookId: r.GuidValue(0), Format: r.Text(1)),
                    args,
                    ct: ct).ConfigureAwait(false);

                foreach (var row in rows)
                {
                    if (ParseFormat(row.Format) is { } kind)
                    {
                        map[row.BookId] = kind;
                    }
                }
            }

            return (IReadOnlyDictionary<Guid, BookFormatKind>)map;
        }, ct);

    public async Task SetReadingFormatAsync(
        Guid bookId, BookFormatKind? format, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);

        /* 一行一本：current_format 为 NULL 表示"没选过，回到默认优先级链"，
           所以清选择也是同一句 upsert，只是把值写成 NULL。 */
        await conn.ExecuteAsync(
            """
            INSERT INTO book_reading_format (book_id, current_format, progress_format, updated_at)
            VALUES (@id, @format, NULL, @now)
            ON CONFLICT(book_id) DO UPDATE SET
                current_format = excluded.current_format,
                updated_at     = excluded.updated_at;
            """,
            new[]
            {
                P("@id", bookId.ToString()),
                P("@format", format?.ToStorageString()),
                P("@now", UnixTime.ToMillis(DateTimeOffset.UtcNow)),
            },
            tx: null,
            ct: ct).ConfigureAwait(false);
    }

    /// <summary>存储串 -> 枚举。不认识的值当"没记过"（null），绝不让老数据把界面拖垮。</summary>
    private static BookFormatKind? ParseFormat(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var kind = BookFormatKinds.FromStorageString(value);
        return kind == BookFormatKind.Unknown ? null : kind;
    }

    public async Task SaveReadingProgressAsync(ReadingProgress progress, CancellationToken ct = default)
    {
        var stamp = progress.UpdatedAt == default ? DateTimeOffset.UtcNow : progress.UpdatedAt;
        var now = UnixTime.ToMillis(stamp);

        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        await conn.ExecuteAsync(
            """
            INSERT INTO reading_progress (
                book_id, spine_index, page_index, total_pages, location, fingerprint, percent, updated_at)
            VALUES (
                @book_id, @spine_index, @page_index, @total_pages, @location, @fingerprint, @percent, @updated_at)
            ON CONFLICT(book_id) DO UPDATE SET
                spine_index = excluded.spine_index,
                page_index  = excluded.page_index,
                total_pages = excluded.total_pages,
                location    = excluded.location,
                fingerprint = excluded.fingerprint,
                percent     = excluded.percent,
                updated_at  = excluded.updated_at;
            """,
            new[]
            {
                P("@book_id", progress.BookId.ToString()),
                P("@spine_index", progress.SpineIndex),
                P("@page_index", progress.PageIndex),
                P("@total_pages", progress.TotalPages),
                P("@location", progress.Location),
                P("@fingerprint", progress.Fingerprint),
                P("@percent", progress.Percent),
                P("@updated_at", now),
            },
            tx,
            ct).ConfigureAwait(false);

        /* 阅读状态只有这里能维护：导入时一律是"未读"，除此之外没人写过它。
           规则（设计文档没规定，2026-10-03 定、当晚按用户提问收紧，见 docs/DECISIONS.md）：
             本次保存发生在"书的最末尾"（@is_at_end，由阅读器侧的快照算出来）-> 已读完
             有进度 且 当前是未读或弃读 -> 在读（弃读之后又接着读，说明捡起来了）
             已读完不会被读回去  重读一本读完的书，不该把它变回在读
           刻意不再用"全书进度 >= 99.5%"：那个值按章数加权，855 章的书打开末章就是 100%，
           而末章若短到一屏放得下更是直接就 100%。"读完"的判据必须落在"最后一章 + 章内末尾"。
           常量全部用参数传，SQL 里不留魔法数字。 */
        await conn.ExecuteAsync(
            """
            UPDATE books
               SET last_read_at = @now,
                   reading_status = CASE
                       WHEN @is_at_end = 1 AND reading_status <> @finished THEN @finished
                       WHEN @percent > 0 AND reading_status IN (@unread, @abandoned) THEN @reading
                       ELSE reading_status
                   END
             WHERE id = @id;
            """,
            new[]
            {
                P("@now", now),
                P("@percent", progress.Percent),
                P("@is_at_end", progress.IsAtEnd ? 1 : 0),
                P("@finished", (int)ReadingStatus.Finished),
                P("@reading", (int)ReadingStatus.Reading),
                P("@unread", (int)ReadingStatus.Unread),
                P("@abandoned", (int)ReadingStatus.Abandoned),
                P("@id", progress.BookId.ToString()),
            },
            tx,
            ct).ConfigureAwait(false);

        /* 用户 2026-10-21：顺手记下"这条进度是哪个格式下存的"。
           换格式继续读时必须知道它，否则 EPUB 的章号会被当成 TXT 的章号用。
           同一事务里写：进度与它的来源格式不能出现一个更新了、一个没更新的中间态。 */
        if (progress.SourceFormat is { } sourceFormat)
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO book_reading_format (book_id, current_format, progress_format, updated_at)
                VALUES (@id, NULL, @format, @now)
                ON CONFLICT(book_id) DO UPDATE SET
                    progress_format = excluded.progress_format,
                    updated_at      = excluded.updated_at;
                """,
                new[]
                {
                    P("@id", progress.BookId.ToString()),
                    P("@format", sourceFormat.ToStorageString()),
                    P("@now", now),
                },
                tx,
                ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }
    public async Task<bool> SetReadingStatusAsync(
        Guid bookId,
        ReadingStatus status,
        CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);

        var affected = await conn.ExecuteAsync(
            "UPDATE books SET reading_status = @status, updated_at = @now WHERE id = @id;",
            new[]
            {
                P("@status", (int)status),
                P("@now", UnixTime.ToMillis(DateTimeOffset.UtcNow)),
                P("@id", bookId.ToString()),
            },
            tx: null,
            ct: ct).ConfigureAwait(false);

        return affected > 0;
    }

    public async Task<bool> UpdateMetadataAsync(BookMetadataUpdate update, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);

        // 只改编辑框里能改的那些列：reading_status / 封面 / 词数等不归这条路管。
        // updated_at 一定要动书库靠它判断"这本刚被改过"。
        var affected = await conn.ExecuteAsync(
            """
            UPDATE books
               SET title          = @title,
                   title_sort     = @title_sort,
                   authors        = @authors,
                   author_sort    = @author_sort,
                   publisher      = @publisher,
                   published_date = @published_date,
                   isbn           = @isbn,
                   language       = @language,
                   description    = @description,
                   series         = @series,
                   series_index   = @series_index,
                   rating         = @rating,
                   updated_at     = @now
             WHERE id = @id;
            """,
            new[]
            {
                P("@title", update.Title),
                P("@title_sort", update.TitleSort),
                P("@authors", update.Authors),
                P("@author_sort", update.AuthorSort),
                P("@publisher", update.Publisher),
                P("@published_date", update.PublishedDate),
                P("@isbn", update.Isbn),
                P("@language", update.Language),
                P("@description", update.Description),
                P("@series", update.Series),
                P("@series_index", update.SeriesIndex),
                P("@rating", update.Rating),
                P("@now", UnixTime.ToMillis(DateTimeOffset.UtcNow)),
                P("@id", update.BookId.ToString()),
            },
            tx: null,
            ct: ct).ConfigureAwait(false);

        return affected > 0;
    }

    // ---------- 映射 ----------

    /// <summary>把查询条件翻成参数化的 WHERE。返回的子句里已经含"不含副本"这条铁律。</summary>
    private static (string Where, List<SqliteParameter> Parameters) BuildFilter(BookQuery query)
    {
        var clauses = new List<string> { HiddenCopiesFilter };
        var ps = new List<SqliteParameter>();

        // 关键词：每个词一组 OR（命中任意一个元数据列即可），词之间是 AND
        var terms = query.Terms;

        for (var i = 0; i < terms.Count; i++)
        {
            var name = "@kw" + i.ToString(CultureInfo.InvariantCulture);
            ps.Add(P(name, "%" + EscapeLike(terms[i]) + "%"));

            var ors = new List<string>(SearchColumns.Length);

            foreach (var column in SearchColumns)
            {
                ors.Add(column + " LIKE " + name + LikeEscape);
            }

            clauses.Add("(" + string.Join(" OR ", ors) + ")");
        }

        if (query.Format is { } format)
        {
            ps.Add(P("@fmt", format.ToStorageString()));

            // 书里只要有这个格式（且文件还在）就算命中
            clauses.Add(
                "EXISTS (SELECT 1 FROM book_formats f " +
                "WHERE f.book_id = books.id AND f.format = @fmt AND f.missing = 0)");
        }

        if (query.Status is { } status)
        {
            ps.Add(P("@status", (int)status));
            clauses.Add("reading_status = @status");
        }

        if (query.OnlyWithCover)
        {
            clauses.Add("cover_path IS NOT NULL AND cover_path <> ''");
        }

        return ("WHERE " + string.Join(" AND ", clauses), ps);
    }

    /// <summary>排序子句。一律拿 id 作末位，保证分页顺序稳定（否则同名书每页顺序可能变）。</summary>
    private static string OrderByClause(BookQuery query)
    {
        var dir = query.Descending ? "DESC" : "ASC";
        var tail = ", id " + dir;

        return query.Sort switch
        {
            BookSort.Author => "COALESCE(NULLIF(author_sort, ''), authors) COLLATE NOCASE " + dir + tail,
            BookSort.AddedAt => "created_at " + dir + tail,
            // 没读过的 last_read_at 是 NULL；SQLite 里 NULL 最小，所以升序时"从没读过"排最前、
            // 降序（最近读的在前）时排最后，两个方向都合理。
            BookSort.LastReadAt => "last_read_at " + dir + tail,
            BookSort.FileSize =>
                "(SELECT COALESCE(MAX(file_size), 0) FROM book_formats f WHERE f.book_id = books.id) " + dir + tail,
            _ => "COALESCE(NULLIF(title_sort, ''), title) COLLATE NOCASE " + dir + tail,
        };
    }

    /// <summary>
    /// 转义 LIKE 的通配符。不转义的话用户搜 100% 会变成"以 100 开头"，
    /// 搜 a_b 里的下划线会变成任意单字符。反斜杠也必须转，否则会把新加的转义符再转一遍。
    /// </summary>
    private static string EscapeLike(string value)
    {
        var sb = new StringBuilder(value.Length + 8);

        foreach (var ch in value)
        {
            if (ch is '%' or '_' or '\\')
            {
                sb.Append('\\');
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }

    private static Book MapBook(SqliteDataReader r) => new()
    {
        Id = r.GuidValue(0),
        Title = r.GetString(1),
        TitleSort = r.Text(2),
        Authors = r.Text(3),
        AuthorSort = r.Text(4),
        Translators = r.Text(5),
        Publisher = r.Text(6),
        PublishedDate = UnixTime.DateFromText(r.Text(7)),
        Isbn = r.Text(8),
        Language = r.Text(9),
        Description = r.Text(10),
        Series = r.Text(11),
        SeriesIndex = r.DoubleOrNull(12),
        Rating = r.GetInt32(13),
        Status = (ReadingStatus)r.GetInt32(14),
        Identifier = r.Text(15),
        WordCount = r.Int32OrNull(16),
        ChapterCount = r.Int32OrNull(17),
        CoverPath = r.Text(18),
        Damaged = r.GetInt64(19) != 0,
        ImportBatchId = r.GuidOrNull(20),
        CreatedAt = UnixTime.FromMillis(r.GetInt64(21)),
        UpdatedAt = UnixTime.FromMillis(r.GetInt64(22)),
        LastReadAt = UnixTime.FromMillis(r.Int64OrNull(23)),
    };

    private static BookFormat MapFormat(SqliteDataReader r) => new()
    {
        Id = r.GuidValue(0),
        BookId = r.GuidValue(1),
        Format = BookFormatKinds.FromStorageString(r.GetString(2)),
        FilePath = r.GetString(3),
        FileSize = r.GetInt64(4),
        FullHash = r.Text(5),
        QuickFingerprint = r.Text(6),
        FileModifiedAt = UnixTime.FromMillis(r.Int64OrNull(7)),
        Missing = r.GetInt64(8) != 0,
        AddedAt = UnixTime.FromMillis(r.GetInt64(9)),
    };

    /// <summary>一次 IN 查询取回这一页全部书的格式文件，避免 N+1（设计文档 5.4）。</summary>
    private static async Task AttachFormatsAsync(
        SqliteConnection conn, List<Book> books, CancellationToken ct)
    {
        if (books.Count == 0)
        {
            return;
        }

        var byId = new Dictionary<Guid, Book>();
        var ps = new List<SqliteParameter>();
        var holes = new List<string>();

        for (var i = 0; i < books.Count; i++)
        {
            byId[books[i].Id] = books[i];
            var hole = "@b" + i;
            holes.Add(hole);
            ps.Add(P(hole, books[i].Id.ToString()));
        }

        var sql = $"SELECT {FormatColumns} FROM book_formats " +
                  $"WHERE book_id IN ({string.Join(", ", holes)}) ORDER BY added_at;";

        var formats = await conn.QueryAsync(sql, MapFormat, ps, ct: ct).ConfigureAwait(false);

        foreach (var f in formats)
        {
            if (byId.TryGetValue(f.BookId, out var owner))
            {
                owner.Formats.Add(f);
            }
        }
    }

    // ---------- 参数 ----------

    private static FormatScanRow MapScanRow(SqliteDataReader r) => new(
        r.GuidValue(0),
        r.GuidValue(1),
        r.GetString(2),
        BookFormatKinds.FromStorageString(r.GetString(3)),
        r.GetString(4),
        r.GetInt64(5),
        UnixTime.FromMillis(r.Int64OrNull(6)),
        r.GetInt64(7) != 0);

    private static SqliteParameter P(string name, object? value)
        => new(name, value ?? DBNull.Value);

    private static List<SqliteParameter> BookParams(Book b) => new()
    {
        P("@id", b.Id.ToString()),
        P("@title", b.Title),
        P("@title_sort", b.TitleSort),
        P("@authors", b.Authors),
        P("@author_sort", b.AuthorSort),
        P("@translators", b.Translators),
        P("@publisher", b.Publisher),
        P("@published_date", UnixTime.ToDateText(b.PublishedDate)),
        P("@isbn", b.Isbn),
        P("@language", b.Language),
        P("@description", b.Description),
        P("@series", b.Series),
        P("@series_index", b.SeriesIndex),
        P("@rating", b.Rating),
        P("@reading_status", (int)b.Status),
        P("@identifier", b.Identifier),
        P("@word_count", b.WordCount),
        P("@chapter_count", b.ChapterCount),
        P("@cover_path", b.CoverPath),
        P("@damaged", b.Damaged ? 1 : 0),
        P("@import_batch_id", b.ImportBatchId?.ToString()),
        P("@created_at", UnixTime.ToMillis(b.CreatedAt)),
        P("@updated_at", UnixTime.ToMillis(b.UpdatedAt)),
        P("@last_read_at", UnixTime.ToMillis(b.LastReadAt)),
    };

    private static List<SqliteParameter> FormatParams(BookFormat f, Guid bookId) => new()
    {
        P("@id", f.Id.ToString()),
        P("@book_id", bookId.ToString()),
        P("@format", f.Format.ToStorageString()),
        P("@file_path", f.FilePath),
        P("@file_size", f.FileSize),
        P("@full_hash", f.FullHash),
        P("@quick_fingerprint", f.QuickFingerprint),
        P("@file_modified_at", UnixTime.ToMillis(f.FileModifiedAt)),
        P("@missing", f.Missing ? 1 : 0),
        P("@added_at", UnixTime.ToMillis(f.AddedAt)),
    };
}
