using EbookToolSuite.Core.Enums;

namespace EbookToolSuite.Core.Models;

/// <summary>
/// 单本导入的结果。失败不抛异常，全部用 Outcome 表达（设计文档 7.1.1：
/// 批量导入时一本失败不能拖垮整批）。
/// </summary>
public sealed record ImportResult
{
    public required ImportOutcome Outcome { get; init; }

    /// <summary>用户选的那个源文件的绝对路径（不是库里的路径）。</summary>
    public required string SourcePath { get; init; }

    /// <summary>成功时是新书的 Id；Duplicate 时是已存在的那本书的 Id；其余为 null。</summary>
    public Guid? BookId { get; init; }

    /// <summary>成功时是文件在 Data/Library 下的最终绝对路径。</summary>
    public string? LibraryPath { get; init; }

    /// <summary>面向用户的一句话，可直接显示在提示框里。</summary>
    public string? Message { get; init; }

    /// <summary>失败时的原始异常，仅供日志。不要直接显示给用户。</summary>
    public Exception? Error { get; init; }

    public TimeSpan Duration { get; init; }

    public bool Succeeded => Outcome == ImportOutcome.Imported;

    // ---------- 工厂：调用方不要自己 new，避免漏填 Message ----------

    public static ImportResult Ok(string source, Guid bookId, string libraryPath, TimeSpan d, bool replaced = false) => new()
    {
        Outcome = ImportOutcome.Imported,
        SourcePath = source,
        BookId = bookId,
        LibraryPath = libraryPath,
        Message = replaced ? "替换成功" : "导入成功",
        Duration = d,
    };

    public static ImportResult Duplicate(string source, Guid existingBookId, string message) => new()
    {
        Outcome = ImportOutcome.Duplicate,
        SourcePath = source,
        BookId = existingBookId,
        Message = message,
    };

    public static ImportResult Invalid(string source, string message, Exception? error = null) => new()
    {
        Outcome = ImportOutcome.InvalidEpub,
        SourcePath = source,
        Message = message,
        Error = error,
    };

    public static ImportResult Drm(string source) => new()
    {
        Outcome = ImportOutcome.DrmProtected,
        SourcePath = source,
        Message = "这本书带有 DRM 加密，无法导入。",
    };

    public static ImportResult Unsupported(string source) => new()
    {
        Outcome = ImportOutcome.UnsupportedFormat,
        SourcePath = source,
        Message = "不支持这种文件格式。可以导入 EPUB / TXT / PDF / MOBI / AZW3 / CBZ，"
            + "其它格式请先转换成 EPUB。",
    };

    public static ImportResult Unreadable(string source, string message, Exception? error = null) => new()
    {
        Outcome = ImportOutcome.SourceUnreadable,
        SourcePath = source,
        Message = message,
        Error = error,
    };

    public static ImportResult Canceled(string source) => new()
    {
        Outcome = ImportOutcome.Canceled,
        SourcePath = source,
        Message = "已取消。",
    };

    public static ImportResult Failed(string source, string message, Exception? error = null, TimeSpan d = default) => new()
    {
        Outcome = ImportOutcome.Failed,
        SourcePath = source,
        Message = message,
        Error = error,
        Duration = d,
    };
}