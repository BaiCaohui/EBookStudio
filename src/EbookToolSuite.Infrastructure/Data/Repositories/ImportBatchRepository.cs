using EbookToolSuite.Core.Common;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using Microsoft.Data.Sqlite;

namespace EbookToolSuite.Infrastructure.Data.Repositories;

/// <summary>
/// import_batch 表的手写 ADO 仓储（执行计划 T2-1）。
/// 活动中心按这张表列导入历史：哪天导的、成了几本、失败几本、为什么。
/// </summary>
public sealed class ImportBatchRepository : IImportBatchRepository
{
    private const string Columns =
        "id, source, started_at, finished_at, total, succeeded, failed, note";

    private readonly SqliteConnectionFactory _factory;

    public ImportBatchRepository(SqliteConnectionFactory factory) => _factory = factory;

    public async Task AddAsync(ImportBatch batch, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(
            "INSERT INTO import_batch (" + Columns + ") VALUES (" +
            "@id, @source, @started_at, @finished_at, @total, @succeeded, @failed, @note);",
            BatchParams(batch),
            null,
            ct).ConfigureAwait(false);
    }

    /// <summary>整行覆盖写。队列在批次结束时调一次，取消收尾时也调一次。</summary>
    public async Task UpdateAsync(ImportBatch batch, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(
            "UPDATE import_batch SET source = @source, finished_at = @finished_at, " +
            "total = @total, succeeded = @succeeded, failed = @failed, note = @note " +
            "WHERE id = @id;",
            BatchParams(batch),
            null,
            ct).ConfigureAwait(false);
    }

    public Task<ImportBatch?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => _factory.WithSharedReadAsync(async conn =>
        {
            var rows = await conn.QueryAsync(
                "SELECT " + Columns + " FROM import_batch WHERE id = @id;",
                Map,
                new[] { P("@id", id.ToString()) },
                ct: ct).ConfigureAwait(false);

            return rows.Count == 0 ? (ImportBatch?)null : rows[0];
        }, ct);

    public Task<IReadOnlyList<ImportBatch>> ListRecentAsync(
        int limit = 50, CancellationToken ct = default)
        => _factory.WithSharedReadAsync(async conn =>
        {
            var rows = await conn.QueryAsync(
                "SELECT " + Columns + " FROM import_batch ORDER BY started_at DESC LIMIT @n;",
                Map,
                new[] { P("@n", limit) },
                ct: ct).ConfigureAwait(false);

            return (IReadOnlyList<ImportBatch>)rows;
        }, ct);

    private static ImportBatch Map(SqliteDataReader r) => new()
    {
        Id = r.GuidValue(0),
        Source = r.Text(1),
        StartedAt = UnixTime.FromMillis(r.GetInt64(2)),
        FinishedAt = UnixTime.FromMillis(r.Int64OrNull(3)),
        Total = r.GetInt32(4),
        Succeeded = r.GetInt32(5),
        Failed = r.GetInt32(6),
        Note = r.Text(7),
    };

    private static List<SqliteParameter> BatchParams(ImportBatch b) => new()
    {
        P("@id", b.Id.ToString()),
        P("@source", b.Source),
        P("@started_at", UnixTime.ToMillis(b.StartedAt)),
        P("@finished_at", UnixTime.ToMillis(b.FinishedAt)),
        P("@total", b.Total),
        P("@succeeded", b.Succeeded),
        P("@failed", b.Failed),
        P("@note", b.Note),
    };

    private static SqliteParameter P(string name, object? value)
        => new(name, value ?? DBNull.Value);
}