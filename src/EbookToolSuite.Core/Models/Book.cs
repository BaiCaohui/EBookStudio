using EbookToolSuite.Core.Enums;

namespace EbookToolSuite.Core.Models;

/// <summary>
/// 一本书 = 元数据 + 若干格式文件。字段与 books 表一一对应（设计文档 5.2）。
/// 时间列在库里是 Unix 毫秒 UTC，这里用 DateTimeOffset 承载。
/// </summary>
public sealed class Book
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public string? TitleSort { get; set; }
    public string? Authors { get; set; }
    public string? AuthorSort { get; set; }
    public string? Translators { get; set; }
    public string? Publisher { get; set; }
    public DateOnly? PublishedDate { get; set; }
    public string? Isbn { get; set; }
    public string? Language { get; set; }
    public string? Description { get; set; }
    public string? Series { get; set; }
    public double? SeriesIndex { get; set; }
    public int Rating { get; set; }
    public ReadingStatus Status { get; set; }
    public string? Identifier { get; set; }
    public int? WordCount { get; set; }
    public int? ChapterCount { get; set; }
    public string? CoverPath { get; set; }
    public bool Damaged { get; set; }
    public Guid? ImportBatchId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? LastReadAt { get; set; }

    public List<BookFormat> Formats { get; init; } = new();
    public List<Guid> ShelfIds { get; init; } = new();
    public List<string> Tags { get; init; } = new();
}