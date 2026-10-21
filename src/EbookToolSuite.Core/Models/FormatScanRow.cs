using EbookToolSuite.Core.Enums;

namespace EbookToolSuite.Core.Models;

/// <summary>
/// 扫描用的最小投影：一条 book_formats 记录加上书名（一次 JOIN 取回，避免 N+1）。
/// </summary>
public sealed record FormatScanRow(
    Guid FormatId,
    Guid BookId,
    string Title,
    BookFormatKind Format,
    string FilePath,
    long FileSize,
    DateTimeOffset? FileModifiedAt,
    bool Missing);
