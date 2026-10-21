using EbookToolSuite.Core.Enums;

namespace EbookToolSuite.Core.Models;

/// <summary>一次扫描里，某一个格式文件的判定结果（只有非正常状态才会进报告）。</summary>
public sealed record BookFileStatus(
    Guid FormatId,
    Guid BookId,
    string Title,
    BookFormatKind Format,
    string FilePath,
    BookFileState State,
    long ExpectedSize,
    long ActualSize,
    DateTimeOffset? ExpectedModifiedAt,
    DateTimeOffset? ActualModifiedAt)
{
    public bool IsMissing => State == BookFileState.Missing;

    /// <summary>给用户看的一句话。</summary>
    public string Describe() => State switch
    {
        BookFileState.Missing => "文件缺失",
        BookFileState.ExternallyModified => "文件已被外部修改",
        _ => "正常",
    };
}
