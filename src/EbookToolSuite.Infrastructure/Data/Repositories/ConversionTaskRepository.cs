using EbookToolSuite.Core.Common;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using Microsoft.Data.Sqlite;

namespace EbookToolSuite.Infrastructure.Data.Repositories;

/// <summary>
/// conversion_tasks 的手写 ADO 仓储（执行计划 T2-4）。
/// 队列每改一次状态就整行覆盖写一次，状态、进度、错误、重试计数都在同一行里。
/// 列表查询 LEFT JOIN books 取书名，书被删了就是 null（由界面回落成"已删除的书"）。
/// </summary>
public sealed class ConversionTaskRepository : IConversionTaskRepository
{
    /// <summary>列顺序就是 Map 的下标顺序，改一处必须改另一处。</summary>
    private const string SelectColumns =
        "t.id, t.book_id, t.batch_id, t.input_path, t.output_path, t.out_format, t.template_id, "
        + "t.arguments_json, t.status, t.progress, t.progress_kind, t.error_code, t.error_message, "
        + "t.retry_count, t.created_at, t.started_at, t.finished_at, "
        + "COALESCE(m.priority, 0), COALESCE(m.register_format, 1), b.title";

    private const string FromClause =
        "FROM conversion_tasks t LEFT JOIN books b ON b.id = t.book_id "
        + "LEFT JOIN conversion_task_meta m ON m.task_id = t.id";

    private readonly SqliteConnectionFactory _factory;

    public ConversionTaskRepository(SqliteConnectionFactory factory) => _factory = factory;

    public async Task AddAsync(ConversionTask task, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(
            "INSERT INTO conversion_tasks (id, book_id, batch_id, input_path, output_path, out_format, "
            + "template_id, arguments_json, status, progress, progress_kind, error_code, error_message, "
            + "retry_count, created_at, started_at, finished_at) VALUES ("
            + "@id, @book_id, @batch_id, @input_path, @output_path, @out_format, @template_id, "
            + "@arguments_json, @status, @progress, @progress_kind, @error_code, @error_message, "
            + "@retry_count, @created_at, @started_at, @finished_at); "
            + "INSERT INTO conversion_task_meta (task_id, priority, register_format) "
            + "VALUES (@id, @priority, @register_format);",
            TaskParams(task),
            null,
            ct).ConfigureAwait(false);
    }

    /// <summary>整行覆盖写。除 id 外每个字段都可能变，所以不做增量 UPDATE。</summary>
    public async Task UpdateAsync(ConversionTask task, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(
            "UPDATE conversion_tasks SET book_id = @book_id, batch_id = @batch_id, "
            + "input_path = @input_path, output_path = @output_path, out_format = @out_format, "
            + "template_id = @template_id, arguments_json = @arguments_json, status = @status, "
            + "progress = @progress, progress_kind = @progress_kind, error_code = @error_code, "
            + "error_message = @error_message, retry_count = @retry_count, created_at = @created_at, "
            + "started_at = @started_at, finished_at = @finished_at WHERE id = @id; "
            + "INSERT INTO conversion_task_meta (task_id, priority, register_format) "
            + "VALUES (@id, @priority, @register_format) "
            + "ON CONFLICT(task_id) DO UPDATE SET priority = excluded.priority, "
            + "register_format = excluded.register_format;",
            TaskParams(task),
            null,
            ct).ConfigureAwait(false);
    }

    public Task<ConversionTask?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => _factory.WithSharedReadAsync(async conn =>
        {
            var rows = await conn.QueryAsync(
                "SELECT " + SelectColumns + " " + FromClause + " WHERE t.id = @id;",
                Map,
                new[] { P("@id", id.ToString()) },
                ct: ct).ConfigureAwait(false);

            return rows.Count == 0 ? (ConversionTask?)null : rows[0];
        }, ct);

    public Task<IReadOnlyList<ConversionTask>> ListActiveAsync(CancellationToken ct = default)
        => _factory.WithSharedReadAsync(async conn =>
        {
            // 未终态 = 排队中 / 转换中 / 等待重试。按优先级降序、同级先来先转（T6-1）。
            var rows = await conn.QueryAsync(
                "SELECT " + SelectColumns + " " + FromClause
                + " WHERE t.status IN (@queued, @running, @waiting) "
                + "ORDER BY COALESCE(m.priority, 0) DESC, t.created_at ASC;",
                Map,
                new[]
                {
                    P("@queued", (int)ConversionTaskStatus.Queued),
                    P("@running", (int)ConversionTaskStatus.Running),
                    P("@waiting", (int)ConversionTaskStatus.WaitingRetry),
                },
                ct: ct).ConfigureAwait(false);

            return (IReadOnlyList<ConversionTask>)rows;
        }, ct);

    public Task<IReadOnlyList<ConversionTask>> ListRecentAsync(
        int limit = 200, CancellationToken ct = default)
        => _factory.WithSharedReadAsync(async conn =>
        {
            var rows = await conn.QueryAsync(
                "SELECT " + SelectColumns + " " + FromClause
                + " ORDER BY t.created_at DESC LIMIT @n;",
                Map,
                new[] { P("@n", limit) },
                ct: ct).ConfigureAwait(false);

            return (IReadOnlyList<ConversionTask>)rows;
        }, ct);

    /// <summary>
    /// 崩溃恢复：Running / WaitingRetry 一律回 Queued，并清掉进度与开始时间。
    /// 绝不尝试续传（Calibre 没有断点续传，半截产物由队列自己删）。
    /// 幂等：第二次调没有匹配行，返回 0。
    /// </summary>
    public async Task<int> ResetInterruptedAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        return await conn.ExecuteAsync(
            "UPDATE conversion_tasks SET status = @queued, progress = NULL, progress_kind = @unknown, "
            + "started_at = NULL WHERE status IN (@running, @waiting);",
            new[]
            {
                P("@queued", (int)ConversionTaskStatus.Queued),
                P("@unknown", (int)ConversionTaskProgressKind.Unknown),
                P("@running", (int)ConversionTaskStatus.Running),
                P("@waiting", (int)ConversionTaskStatus.WaitingRetry),
            },
            null,
            ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(
            "DELETE FROM conversion_task_meta WHERE task_id = @id; "
            + "DELETE FROM conversion_tasks WHERE id = @id;",
            new[] { P("@id", id.ToString()) },
            null,
            ct).ConfigureAwait(false);
    }

    public async Task<int> DeleteFinishedAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var args = new[]
        {
            P("@completed", (int)ConversionTaskStatus.Completed),
            P("@failed", (int)ConversionTaskStatus.Failed),
            P("@canceled", (int)ConversionTaskStatus.Canceled),
        };

        // 先清旁表，返回的行数只算第二次删除（多语句 ExecuteAsync 会把两条的计数相加）。
        await conn.ExecuteAsync(
            "DELETE FROM conversion_task_meta WHERE task_id IN ("
            + "SELECT id FROM conversion_tasks WHERE status IN (@completed, @failed, @canceled));",
            args,
            null,
            ct).ConfigureAwait(false);

        return await conn.ExecuteAsync(
            "DELETE FROM conversion_tasks WHERE status IN (@completed, @failed, @canceled);",
            args,
            null,
            ct).ConfigureAwait(false);
    }

    private static ConversionTask Map(SqliteDataReader r) => new()
    {
        Id = r.GuidValue(0),
        BookId = r.GuidOrNull(1),
        BatchId = r.Text(2),
        InputPath = r.GetString(3),
        OutputPath = r.GetString(4),
        OutFormat = BookFormatKinds.FromStorageString(r.GetString(5)),
        TemplateId = r.Text(6),
        ArgumentsJson = r.GetString(7),
        Status = (ConversionTaskStatus)r.GetInt32(8),
        Progress = r.DoubleOrNull(9),
        ProgressKind = (ConversionTaskProgressKind)r.GetInt32(10),
        ErrorCode = r.Text(11),
        ErrorMessage = r.Text(12),
        RetryCount = r.GetInt32(13),
        CreatedAt = UnixTime.FromMillis(r.GetInt64(14)),
        StartedAt = UnixTime.FromMillis(r.Int64OrNull(15)),
        FinishedAt = UnixTime.FromMillis(r.Int64OrNull(16)),
        Priority = r.GetInt32(17),
        RegisterFormat = r.GetInt32(18) != 0,
        BookTitle = r.Text(19),
        SourceFormat = BookFormatKinds.FromExtension(Path.GetExtension(r.GetString(3))),
    };

    private static List<SqliteParameter> TaskParams(ConversionTask t) => new()
    {
        P("@id", t.Id.ToString()),
        P("@book_id", t.BookId?.ToString()),
        P("@batch_id", t.BatchId),
        P("@input_path", t.InputPath),
        P("@output_path", t.OutputPath),
        P("@out_format", BookFormatKinds.ToStorageString(t.OutFormat)),
        P("@template_id", t.TemplateId),
        P("@arguments_json", t.ArgumentsJson),
        P("@status", (int)t.Status),
        P("@progress", t.Progress),
        P("@progress_kind", (int)t.ProgressKind),
        P("@error_code", t.ErrorCode),
        P("@error_message", t.ErrorMessage),
        P("@retry_count", t.RetryCount),
        P("@created_at", UnixTime.ToMillis(t.CreatedAt)),
        P("@started_at", UnixTime.ToMillis(t.StartedAt)),
        P("@finished_at", UnixTime.ToMillis(t.FinishedAt)),
        P("@priority", t.Priority),
        P("@register_format", t.RegisterFormat ? 1 : 0),
    };

    private static SqliteParameter P(string name, object? value)
        => new(name, value ?? DBNull.Value);
}
