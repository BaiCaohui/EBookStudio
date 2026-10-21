using EbookToolSuite.Core.Common;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace EbookToolSuite.Infrastructure.Data.Repositories;

/// <summary>
/// reading_sessions 的读写与统计页只读查询（T7-3B）。写法照 AnnotationRepository 的裸 ADO 风格：
/// 写走 OpenAsync、读走共享长连接，SQL 全部参数化，时间戳统一 UnixTime 毫秒。
/// 聚合口径（时长 / 跨日拆分 / 连续天数）不在这里，这里只保证"库能存什么、能取什么"。
/// </summary>
public sealed class ReadingStatsRepository : IReadingStatsRepository
{
    /// <summary>列顺序就是 MapSession 的下标顺序，改一处必须改另一处。</summary>
    private const string SessionColumns =
        "id, book_id, started_at, ended_at, pages_read, chars_read";

    private const string InsertSessionSql =
        """
        INSERT INTO reading_sessions (book_id, started_at, ended_at, pages_read, chars_read)
        VALUES (@book_id, @started_at, @ended_at, @pages_read, @chars_read);
        """;

    /// <summary>心跳落库只改这三列：会话的归属书与开始时间是开窗时定死的。</summary>
    private const string UpdateSessionSql =
        """
        UPDATE reading_sessions
           SET ended_at = @ended_at,
               pages_read = @pages_read,
               chars_read = @chars_read
         WHERE id = @id;
        """;

    private readonly SqliteConnectionFactory _factory;

    public ReadingStatsRepository(SqliteConnectionFactory factory)
        => _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    // ---------- 写 ----------

    public async Task<long> InsertSessionAsync(ReadingSessionWrite write, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(write);

        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);

        await conn.ExecuteAsync(
            InsertSessionSql,
            new[]
            {
                P("@book_id", write.BookId.ToString()),
                P("@started_at", UnixTime.ToMillis(write.StartedAt)),
                P("@ended_at", UnixTime.ToMillis(write.EndedAt)),
                P("@pages_read", write.PagesRead),
                P("@chars_read", write.CharsRead),
            },
            tx: null,
            ct: ct).ConfigureAwait(false);

        // 写连接是单条 INSERT 后立刻取，同一连接上 rowid 不会串。
        return await conn.ScalarAsync<long>("SELECT last_insert_rowid();", tx: null, ct: ct)
            .ConfigureAwait(false);
    }

    public async Task<bool> UpdateSessionAsync(
        long sessionId,
        DateTimeOffset endedAt,
        int pagesRead,
        long charsRead,
        CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);

        var affected = await conn.ExecuteAsync(
            UpdateSessionSql,
            new[]
            {
                P("@ended_at", UnixTime.ToMillis(endedAt)),
                P("@pages_read", pagesRead),
                P("@chars_read", charsRead),
                P("@id", sessionId),
            },
            tx: null,
            ct: ct).ConfigureAwait(false);

        // 会话可能已被删书的级联带走；调用方（心跳）按"静默丢弃"处理。
        return affected > 0;
    }

    // ---------- 读 ----------

    public Task<IReadOnlyList<ReadingSessionRecord>> ListSessionsAsync(CancellationToken ct = default)
        => _factory.WithSharedReadAsync(async conn =>
        {
            // 统计页一次读全量、在内存里按本地日拆分；按开始时间升序让拆日与趋势都对得上。
            var rows = await conn.QueryAsync(
                $"SELECT {SessionColumns} FROM reading_sessions ORDER BY started_at, id;",
                MapSession,
                ct: ct).ConfigureAwait(false);

            return (IReadOnlyList<ReadingSessionRecord>)rows;
        }, ct);

    public Task<IReadOnlyList<BookReadingState>> ListBookReadingStatesAsync(CancellationToken ct = default)
        => _factory.WithSharedReadAsync(async conn =>
        {
            // LEFT JOIN：没进过阅读器的书也要出现（统计的是整个书库，不是"读过的书"）。
            // 顺序不承载语义（聚合按状态分组），固定按 id 只为让结果可重复。
            var rows = await conn.QueryAsync(
                """
                SELECT b.id, b.reading_status, p.updated_at
                FROM books b
                LEFT JOIN reading_progress p ON p.book_id = b.id
                ORDER BY b.id;
                """,
                r => new BookReadingState(
                    r.GuidValue(0),
                    (ReadingStatus)r.GetInt32(1),
                    UnixTime.FromMillis(r.Int64OrNull(2))),
                ct: ct).ConfigureAwait(false);

            return (IReadOnlyList<BookReadingState>)rows;
        }, ct);

    public Task<IReadOnlyList<ReadingFormatCount>> ListFormatCountsAsync(CancellationToken ct = default)
        => _factory.WithSharedReadAsync(async conn =>
        {
            // 按文件份数统计：一本 EPUB+PDF 的书各算一次（docs/DECISIONS.md T7-3B）。
            // 库里的 format 是规范字串（EPUB / MOBI / AZW3 ...），GROUP BY 原值即可。
            var rows = await conn.QueryAsync(
                "SELECT format, COUNT(1) FROM book_formats GROUP BY format;",
                r => (Format: r.Text(0), Count: r.GetInt32(1)),
                ct: ct).ConfigureAwait(false);

            // 再按枚举归并一次：历史 / 手工写入的 AZW、CBR 等别名会映射到同一个 BookFormatKind，
            // 不能在统计页上裂成两行。
            var merged = new Dictionary<BookFormatKind, int>();

            foreach (var (format, count) in rows)
            {
                var kind = BookFormatKinds.FromStorageString(format);
                merged[kind] = merged.GetValueOrDefault(kind) + count;
            }

            return (IReadOnlyList<ReadingFormatCount>)merged
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => (int)kv.Key)
                .Select(kv => new ReadingFormatCount(kv.Key, kv.Value))
                .ToList();
        }, ct);

    public Task<IReadOnlyList<RecentReadingBook>> ListRecentBooksAsync(
        int take = 8,
        CancellationToken ct = default)
    {
        /* LIMIT 负数在 SQLite 里是"不限"，不能原样传下去；take<=0 当空列表。 */
        if (take <= 0)
        {
            return Task.FromResult<IReadOnlyList<RecentReadingBook>>(Array.Empty<RecentReadingBook>());
        }

        return _factory.WithSharedReadAsync(async conn =>
        {
            // 有进度行才叫"读过"；updated_at 是最近一次翻页 / 定位保存的时间。
            var rows = await conn.QueryAsync(
                """
                SELECT b.id, b.title, p.percent, p.updated_at
                FROM reading_progress p
                JOIN books b ON b.id = p.book_id
                ORDER BY p.updated_at DESC, b.id
                LIMIT @take;
                """,
                r => new RecentReadingBook(
                    r.GuidValue(0),
                    r.GetString(1),
                    r.GetDouble(2),
                    UnixTime.FromMillis(r.GetInt64(3))),
                new[] { P("@take", take) },
                ct: ct).ConfigureAwait(false);

            return (IReadOnlyList<RecentReadingBook>)rows;
        }, ct);
    }

    public Task<int> CountBooksAsync(CancellationToken ct = default)
        => _factory.WithSharedReadAsync(
            async conn => await conn.ScalarAsync<int>("SELECT COUNT(1) FROM books;", ct: ct)
                .ConfigureAwait(false),
            ct);

    // ---------- 映射与参数 ----------

    private static ReadingSessionRecord MapSession(SqliteDataReader r) => new(
        Id: r.GetInt64(0),
        BookId: r.GuidValue(1),
        StartedAt: UnixTime.FromMillis(r.GetInt64(2)),
        EndedAt: UnixTime.FromMillis(r.GetInt64(3)),
        PagesRead: r.GetInt32(4),
        CharsRead: r.GetInt64(5));

    private static SqliteParameter P(string name, object? value)
        => new(name, value ?? DBNull.Value);
}
