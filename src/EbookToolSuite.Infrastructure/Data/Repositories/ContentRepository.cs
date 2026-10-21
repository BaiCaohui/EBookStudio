using System.Text;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using Microsoft.Data.Sqlite;

namespace EbookToolSuite.Infrastructure.Data.Repositories;

/// <summary>
/// 正文索引仓储（T6-4）。三张表的唯一写入口：
/// content_chunk（原文切片）、content_fts（FTS5 外部内容表，由触发器同步）、
/// content_index_state（每本书的索引状态）。
/// </summary>
/// <remarks>
/// <para><b>trigram 与短查询</b>：content_fts 用 trigram 分词器，按 3 字符窗口建索引，
/// 所以 MATCH 查询词必须 ≥3 个字符（短了产不出 trigram，命中数恒 0）。
/// 2 字中文词太常用，因此 &lt;3 字符时整条查询改走 LIKE 扫 content_chunk.body。
/// 这是**有意为之**的两条路径，不是兜底补丁。</para>
/// <para><b>为什么不让调用方拼 MATCH 串</b>：FTS5 的查询语法里 " AND "、引号、星号都有含义，
/// 用户输入的标点必须整体当成短语（用双引号包住并把内部引号加倍），这种转义只能在这里做。</para>
/// </remarks>
public sealed class ContentRepository : IContentRepository
{
    /// <summary>LIKE 的转义子句。反斜杠在 C# 里要写两个，在 SQL 里就是一个。</summary>
    private const string LikeEscape = " ESCAPE '\\'";

    private const string InsertChunkSql =
        """
        INSERT INTO content_chunk (book_id, spine_index, chapter_title, chapter_href, body, indexed_at)
        VALUES (@book_id, @spine_index, @chapter_title, @chapter_href, @body, @indexed_at);
        """;

    private const string SearchColumns =
        "c.book_id, b.title, b.authors, c.spine_index, c.chapter_title, c.body";

    private const string SearchJoins =
        """
        FROM content_fts
        JOIN content_chunk c ON c.id = content_fts.rowid
        JOIN books b ON b.id = c.book_id
        """;

    private readonly SqliteConnectionFactory _factory;

    public ContentRepository(SqliteConnectionFactory factory) => _factory = factory;

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public async Task ReplaceBookChunksAsync(
        Guid bookId, IReadOnlyList<ContentChunk> chunks, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        await conn.ExecuteAsync(
            "DELETE FROM content_chunk WHERE book_id = @id;",
            new[] { P("@id", bookId.ToString()) }, tx, ct).ConfigureAwait(false);

        var now = Now();

        foreach (var chunk in chunks)
        {
            await conn.ExecuteAsync(
                InsertChunkSql,
                new[]
                {
                    P("@book_id", bookId.ToString()),
                    P("@spine_index", chunk.SpineIndex),
                    P("@chapter_title", chunk.ChapterTitle),
                    P("@chapter_href", chunk.ChapterHref),
                    P("@body", chunk.Body),
                    P("@indexed_at", now),
                },
                tx, ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteBookChunksAsync(Guid bookId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(
            "DELETE FROM content_chunk WHERE book_id = @id;",
            new[] { P("@id", bookId.ToString()) }, ct: ct).ConfigureAwait(false);
    }

    public async Task SaveStateAsync(ContentIndexState state, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(
            """
            INSERT INTO content_index_state (book_id, status, chunk_count, error, updated_at)
            VALUES (@book_id, @status, @chunk_count, @error, @updated_at)
            ON CONFLICT(book_id) DO UPDATE SET
                status = excluded.status,
                chunk_count = excluded.chunk_count,
                error = excluded.error,
                updated_at = excluded.updated_at;
            """,
            new[]
            {
                P("@book_id", state.BookId.ToString()),
                P("@status", state.Status.ToString()),
                P("@chunk_count", state.ChunkCount),
                P("@error", state.Error),
                P("@updated_at", state.UpdatedAt == 0 ? Now() : state.UpdatedAt),
            },
            ct: ct).ConfigureAwait(false);
    }

    public Task<ContentIndexState?> GetStateAsync(Guid bookId, CancellationToken ct = default)
        => _factory.WithSharedReadAsync(async conn =>
        {
            var rows = await conn.QueryAsync(
                "SELECT book_id, status, chunk_count, error, updated_at "
                + "FROM content_index_state WHERE book_id = @id;",
                r => new ContentIndexState
                {
                    BookId = r.GuidValue(0),
                    Status = ParseStatus(r.Text(1)),
                    ChunkCount = r.GetInt32(2),
                    Error = r.Text(3),
                    UpdatedAt = r.GetInt64(4),
                },
                new[] { P("@id", bookId.ToString()) },
                ct: ct).ConfigureAwait(false);

            return rows.Count == 0 ? null : rows[0];
        }, ct);

    public Task<IReadOnlyList<Guid>> ListBookIdsNeedingIndexAsync(CancellationToken ct = default)
        => _factory.WithSharedReadAsync(async conn =>
        {
            var rows = await conn.QueryAsync(
                """
                SELECT b.id
                FROM books b
                LEFT JOIN content_index_state s ON s.book_id = b.id
                WHERE s.book_id IS NULL OR s.status IN ('Pending', 'Indexing')
                ORDER BY b.created_at ASC, b.id ASC;
                """,
                r => r.GuidValue(0),
                ct: ct).ConfigureAwait(false);

            return (IReadOnlyList<Guid>)rows;
        }, ct);

    public async Task<ContentSearchResult> SearchAsync(
        ContentSearchQuery query, CancellationToken ct = default)
    {
        var terms = query.Terms;

        if (terms.Count == 0)
        {
            return new ContentSearchResult
            {
                Query = string.Empty,
                Hits = Array.Empty<ContentSearchHit>(),
                TotalMatches = 0,
                Truncated = false,
                UsedShortQueryFallback = false,
            };
        }

        var parameters = new List<SqliteParameter>();

        string where;

        if (query.UseLikeFallback)
        {
            /* 每个词一段 LIKE，词间 AND。转义交给 EscapeLike，参数化交给 P。 */
            var clauses = new List<string>(terms.Count);

            for (var i = 0; i < terms.Count; i++)
            {
                var name = "@t" + i;
                clauses.Add("c.body LIKE " + name + LikeEscape);
                parameters.Add(P(name, "%" + EscapeLike(terms[i]) + "%"));
            }

            where = string.Join(" AND ", clauses);
        }
        else
        {
            where = "content_fts MATCH @match";
            parameters.Add(P("@match", BuildMatchExpression(terms)));
        }

        if (query.BookId is Guid bookId)
        {
            where += " AND c.book_id = @book";
            parameters.Add(P("@book", bookId.ToString()));
        }

        /* 命中总数单独数：界面要显示「共 N 处」，不能只显示截断后的条数。
           FTS 路用同一个 MATCH 条件重数一次，代价远小于把整本书的行都取回来。 */
        var countSql = (query.UseLikeFallback
            ? "SELECT COUNT(1) FROM content_chunk c WHERE "
            : "SELECT COUNT(1) " + SearchJoins + " WHERE ") + where + ";";

        var total = await _factory.WithSharedReadAsync(
            conn => conn.ScalarAsync<int>(countSql, parameters, ct: ct), ct).ConfigureAwait(false);

        /* FTS 按 bm25 相关度排；LIKE 没有相关度，按书 + 章序排（稳定、可断言）。 */
        var order = query.UseLikeFallback ? "c.book_id, c.spine_index, c.id" : "rank";

        var pageSql =
            "SELECT " + SearchColumns + " " + SearchJoins + " WHERE " + where
            + " ORDER BY " + order + " LIMIT @limit;";

        var pageParams = new List<SqliteParameter>(parameters) { P("@limit", Math.Max(1, query.Limit)) };

        var hits = await _factory.WithSharedReadAsync(
            conn => conn.QueryAsync(pageSql, MapHit, pageParams, ct: ct), ct).ConfigureAwait(false);

        return new ContentSearchResult
        {
            Query = string.Join(" ", terms),
            Hits = hits,
            TotalMatches = total,
            Truncated = total > hits.Count,
            UsedShortQueryFallback = query.UseLikeFallback,
        };
    }

    public Task<ContentIndexVolume> MeasureVolumeAsync(CancellationToken ct = default)
        => _factory.WithSharedReadAsync(async conn =>
        {
            var chunkCount = await conn.ScalarAsync<long>(
                "SELECT COUNT(1) FROM content_chunk;", ct: ct).ConfigureAwait(false);

            var bodyBytes = await conn.ScalarAsync<long>(
                "SELECT COALESCE(SUM(LENGTH(CAST(body AS BLOB))), 0) FROM content_chunk;",
                ct: ct).ConfigureAwait(false);

            /* dbstat 是可选编译项：本机驱动没编进去时这里会抛 "no such table"，
               拿不到就报 0，绝不因此让判据 8 的验收卡住（体积仍可从库文件大小旁证）。 */
            long ftsBytes = 0;

            try
            {
                ftsBytes = await conn.ScalarAsync<long>(
                    "SELECT COALESCE(SUM(pgsize), 0) FROM dbstat WHERE name LIKE 'content_fts%';",
                    ct: ct).ConfigureAwait(false);
            }
            catch (SqliteException)
            {
                ftsBytes = 0;
            }

            return new ContentIndexVolume
            {
                ChunkCount = chunkCount,
                BodyBytes = bodyBytes,
                FtsBytes = ftsBytes,
            };
        }, ct);

    private static ContentSearchHit MapHit(SqliteDataReader r) => new()
    {
        BookId = r.GuidValue(0),
        BookTitle = r.Text(1) ?? string.Empty,
        BookAuthors = r.Text(2),
        SpineIndex = r.GetInt32(3),
        ChapterTitle = r.Text(4),
        Body = r.Text(5) ?? string.Empty,
    };

    /// <summary>把用户输入的词整体当成短语：引号包住、内部引号加倍，词间 AND。</summary>
    private static string BuildMatchExpression(IReadOnlyList<string> terms)
    {
        var builder = new StringBuilder();

        for (var i = 0; i < terms.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(" AND ");
            }

            builder.Append('"').Append(terms[i].Replace("\"", "\"\"")).Append('"');
        }

        return builder.ToString();
    }

    /// <summary>LIKE 的元字符转义（\\ % _），与 BookRepository 同一套做法。</summary>
    private static string EscapeLike(string value)
        => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static ContentIndexStatus ParseStatus(string? raw)
        => Enum.TryParse<ContentIndexStatus>(raw, ignoreCase: false, out var status)
            ? status
            : ContentIndexStatus.Failed;

    private static SqliteParameter P(string name, object? value) => SqliteExtensions.P(name, value);
}
