using EbookToolSuite.Core.Common;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using Microsoft.Data.Sqlite;
using static EbookToolSuite.Infrastructure.Data.SqliteExtensions;

namespace EbookToolSuite.Infrastructure.Data.Repositories;

/// <summary>重复检测与「副本 / 忽略」记录的仓储（手写 ADO，参数一律走 SqliteParameter）。</summary>
public sealed class DuplicateRepository : IDuplicateRepository
{
    /// <summary>候选 = 每本书的每个格式一行；已隐藏的副本不参与（否则会被反复报出来）。</summary>
    private const string CandidateSql = """
        SELECT f.id, f.book_id, b.title, b.authors, f.format, f.file_path, f.file_size,
               f.file_modified_at, f.full_hash, b.rating, f.added_at
        FROM book_formats f
        JOIN books b ON b.id = f.book_id
        WHERE f.missing = 0
          AND b.id NOT IN (SELECT book_id FROM duplicate_copy)
        ORDER BY b.title COLLATE NOCASE, f.format;
        """;

    /// <summary>按快速指纹查一条格式行（与候选查询共用列与映射）。</summary>
    private const string FingerprintSql = """
        SELECT f.id, f.book_id, b.title, b.authors, f.format, f.file_path, f.file_size,
               f.file_modified_at, f.full_hash, b.rating, f.added_at
        FROM book_formats f
        JOIN books b ON b.id = f.book_id
        WHERE f.quick_fingerprint = @fp
        ORDER BY f.added_at LIMIT 1;
        """;

    private readonly SqliteConnectionFactory _factory;

    public DuplicateRepository(SqliteConnectionFactory factory) => _factory = factory;

    public Task<IReadOnlyList<DuplicateCandidateRow>> ListCandidatesAsync(CancellationToken ct = default)
        => _factory.WithSharedReadAsync(
            async conn =>
            {
                var rows = await conn.QueryAsync(CandidateSql, MapCandidate, ct: ct).ConfigureAwait(false);
                return (IReadOnlyList<DuplicateCandidateRow>)rows;
            },
            ct);

    private static DuplicateCandidateRow MapCandidate(SqliteDataReader r) => new(
        r.GuidValue(0),
        r.GuidValue(1),
        r.Text(2) ?? string.Empty,
        r.Text(3) ?? string.Empty,
        r.Text(4) ?? string.Empty,
        r.Text(5) ?? string.Empty,
        r.Int64OrNull(6) ?? 0,
        UnixTime.FromMillis(r.Int64OrNull(7)),
        r.Text(8),
        r.Int32OrNull(9) ?? 0,
        UnixTime.FromMillis(r.Int64OrNull(10)) ?? DateTimeOffset.MinValue);


    public Task<DuplicateCandidateRow?> FindByFingerprintAsync(string quickFingerprint, CancellationToken ct = default)
        => _factory.WithSharedReadAsync(
            async conn =>
            {
                var rows = await conn.QueryAsync(
                    FingerprintSql,
                    MapCandidate,
                    new[] { P("@fp", quickFingerprint) },
                    ct: ct).ConfigureAwait(false);

                return rows.Count > 0 ? rows[0] : null;
            },
            ct);

    // ---------- 懒算的 content_hash 回写 ----------

    public async Task SaveContentHashAsync(Guid formatId, string contentHash, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(
            "UPDATE book_formats SET full_hash = @hash WHERE id = @id;",
            new[] { P("@hash", contentHash), P("@id", formatId.ToString()) },
            ct: ct).ConfigureAwait(false);
    }

    // ---------- 忽略组 / 隐藏副本 ----------

    public Task<IReadOnlyList<string>> ListIgnoredSignaturesAsync(CancellationToken ct = default)
        => _factory.WithSharedReadAsync(
            async conn =>
            {
                var rows = await conn.QueryAsync(
                    "SELECT member_ids FROM duplicate_ignore;",
                    r => r.Text(0) ?? string.Empty,
                    ct: ct).ConfigureAwait(false);
                return (IReadOnlyList<string>)rows;
            },
            ct);

    public Task<IReadOnlyList<Guid>> ListHiddenBookIdsAsync(CancellationToken ct = default)
        => _factory.WithSharedReadAsync(
            async conn =>
            {
                var rows = await conn.QueryAsync(
                    "SELECT book_id FROM duplicate_copy;",
                    r => r.GuidValue(0),
                    ct: ct).ConfigureAwait(false);
                return (IReadOnlyList<Guid>)rows;
            },
            ct);


    /// <summary>把 copyBookIds 标成 keptBookId 的副本（重复标记是覆盖写，幂等）。</summary>
    public async Task<int> HideCopiesAsync(
        Guid keptBookId,
        IReadOnlyList<Guid> copyBookIds,
        CancellationToken ct = default)
    {
        var ids = copyBookIds.Where(id => id != keptBookId).Distinct().ToList();
        if (ids.Count == 0)
        {
            return 0;
        }

        var now = UnixTime.ToMillis(DateTimeOffset.UtcNow);
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        var changed = 0;

        foreach (var id in ids)
        {
            changed += await conn.ExecuteAsync(
                """
                INSERT INTO duplicate_copy (book_id, kept_book_id, marked_at)
                VALUES (@book_id, @kept, @at)
                ON CONFLICT(book_id) DO UPDATE SET
                    kept_book_id = excluded.kept_book_id, marked_at = excluded.marked_at;
                """,
                new[] { P("@book_id", id.ToString()), P("@kept", keptBookId.ToString()), P("@at", now) },
                tx,
                ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return changed;
    }

    public async Task<int> RestoreCopiesAsync(IReadOnlyList<Guid> copyBookIds, CancellationToken ct = default)
    {
        var ids = copyBookIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return 0;
        }

        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        var changed = 0;


        foreach (var chunk in ChunkIds(ids))
        {
            var names = new List<string>(chunk.Count);
            var ps = new List<SqliteParameter>(chunk.Count);
            for (var i = 0; i < chunk.Count; i++)
            {
                names.Add("@p" + i);
                ps.Add(P("@p" + i, chunk[i]));
            }

            var sql = "DELETE FROM duplicate_copy WHERE book_id IN (" + string.Join(", ", names) + ");";
            changed += await conn.ExecuteAsync(sql, ps, tx, ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return changed;
    }

    public async Task IgnoreGroupAsync(string memberKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(memberKey))
        {
            return;
        }

        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(
            "INSERT OR IGNORE INTO duplicate_ignore (member_ids, ignored_at) VALUES (@k, @at);",
            new[] { P("@k", memberKey), P("@at", UnixTime.ToMillis(DateTimeOffset.UtcNow)) },
            ct: ct).ConfigureAwait(false);
    }


    /// <summary>把 id 切成每段 <= 900 个参数（SQLite 变量上限 999），拼 IN 子句用。</summary>
    private static List<List<string>> ChunkIds(IReadOnlyList<Guid> ids)
    {
        const int Size = 900;
        var result = new List<List<string>>();
        for (var i = 0; i < ids.Count; i += Size)
        {
            var take = Math.Min(Size, ids.Count - i);
            var chunk = new List<string>(take);
            for (var j = 0; j < take; j++)
            {
                chunk.Add(ids[i + j].ToString());
            }

            result.Add(chunk);
        }

        return result;
    }
}

