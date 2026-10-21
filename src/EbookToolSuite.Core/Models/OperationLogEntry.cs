namespace EbookToolSuite.Core.Models;

/// <summary>
/// operation_log 的一行。Id = 0 表示还没入库（写库时由 SQLite 自增分配）。
/// At 为 null 表示"现在"（由仓储兜底取当前时间，避免调用方漏传）。
/// </summary>
public sealed record OperationLogEntry(
    string Operation,
    string Summary,
    Guid? BookId = null,
    Guid? FormatId = null,
    string? RestorePointId = null,
    string? Detail = null,
    DateTimeOffset? At = null,
    long Id = 0);
