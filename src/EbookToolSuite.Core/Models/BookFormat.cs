using EbookToolSuite.Core.Enums;

namespace EbookToolSuite.Core.Models;

/// <summary>
/// 一本书的某个格式文件。FilePath 存规范化后的绝对路径；
/// 库里有 UNIQUE(file_path) 与 UNIQUE(book_id, format) 两道约束。
/// </summary>
public sealed class BookFormat
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid BookId { get; init; }
    public BookFormatKind Format { get; set; }
    public string FilePath { get; set; } = "";
    public long FileSize { get; set; }

    /// <summary>SHA256，导入后可后台补全。</summary>
    public string? FullHash { get; set; }

    /// <summary>头 1MB + 尾 1MB + 长度的快速指纹，用于秒级去重。</summary>
    public string? QuickFingerprint { get; set; }

    public DateTimeOffset? FileModifiedAt { get; set; }
    public bool Missing { get; set; }
    public DateTimeOffset AddedAt { get; set; }
}