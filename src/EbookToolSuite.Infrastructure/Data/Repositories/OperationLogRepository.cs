using EbookToolSuite.Core.Common;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using Microsoft.Data.Sqlite;

namespace EbookToolSuite.Infrastructure.Data.Repositories;

/// <summary>
/// operation_log 的手写 ADO 仓储（执行计划 T3-6）。
/// 只追加不更新（日志被改就失去意义），列表按时间倒序。
/// </summary>
public sealed class OperationLogRepository : IOperationLogRepository
{
    /// <summary>列顺序就是 Map 的下标顺序，改一处必须改另一处。</summary>
    private const string Columns =
        "id, at, operation, book_id, format_id, restore_point, summary, detail_json";

    private readonly SqliteConnectionFactory _factory;

    public OperationLogRepository(SqliteConnectionFactory factory) => _factory = factory;

    public async Task<long> AddAsync(OperationLogEntry entry, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(
            "INSERT INTO operation_log (at, operation, book_id, format_id, restore_point, summary, detail_json) "
            + "VALUES (@at, @operation, @book_id, @format_id, @restore_point, @summary, @detail_json);",
            Parameters(entry),
            null,
            ct).ConfigureAwait(false);

        return await conn
            .ScalarAsync<long>("SELECT last_insert_rowid();", null, null, ct)
            .ConfigureAwait(false);
    }

    public Task<IReadOnlyList<OperationLogEntry>> ListRecentAsync(int take = 100, CancellationToken ct = default)
        => _factory.WithSharedReadAsync(async conn =>
        {
            var rows = await conn.QueryAsync(
                "SELECT " + Columns + " FROM operation_log ORDER BY at DESC, id DESC LIMIT @n;",
                Map,
                new[] { P("@n", take) },
                ct: ct).ConfigureAwait(false);

            return (IReadOnlyList<OperationLogEntry>)rows;
        }, ct);

    public Task<IReadOnlyList<OperationLogEntry>> ListForBookAsync(Guid bookId, int take = 50, CancellationToken ct = default)
        => _factory.WithSharedReadAsync(async conn =>
        {
            var rows = await conn.QueryAsync(
                "SELECT " + Columns + " FROM operation_log WHERE book_id = @book_id "
                + "ORDER BY at DESC, id DESC LIMIT @n;",
                Map,
                new[] { P("@book_id", bookId.ToString()), P("@n", take) },
                ct: ct).ConfigureAwait(false);

            return (IReadOnlyList<OperationLogEntry>)rows;
        }, ct);

    public Task<int> CountAsync(CancellationToken ct = default)
        => _factory.WithSharedReadAsync(
            async conn => await conn
                .ScalarAsync<int>("SELECT COUNT(*) FROM operation_log;", null, null, ct)
                .ConfigureAwait(false),
            ct);

    private static OperationLogEntry Map(SqliteDataReader r) => new(
        Operation: r.GetString(2),
        Summary: r.GetString(6),
        BookId: r.GuidOrNull(3),
        FormatId: r.GuidOrNull(4),
        RestorePointId: r.Text(5),
        Detail: r.Text(7),
        At: UnixTime.FromMillis(r.GetInt64(1)),
        Id: r.GetInt64(0));

    private static List<SqliteParameter> Parameters(OperationLogEntry entry) => new()
    {
        P("@at", UnixTime.ToMillis(entry.At ?? DateTimeOffset.Now)),
        P("@operation", entry.Operation),
        P("@book_id", entry.BookId?.ToString()),
        P("@format_id", entry.FormatId?.ToString()),
        P("@restore_point", entry.RestorePointId),
        P("@summary", entry.Summary),
        P("@detail_json", entry.Detail),
    };

    private static SqliteParameter P(string name, object? value)
        => new(name, value ?? DBNull.Value);
}
