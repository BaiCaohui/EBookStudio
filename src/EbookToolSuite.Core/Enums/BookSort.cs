namespace EbookToolSuite.Core.Enums;

/// <summary>书库网格的排序方式（T2-3）。</summary>
public enum BookSort
{
    /// <summary>按书名（默认）。</summary>
    Title = 0,

    /// <summary>按作者。</summary>
    Author = 1,

    /// <summary>按加入书库的时间。</summary>
    AddedAt = 2,

    /// <summary>按最近阅读时间（没读过的排最后）。</summary>
    LastReadAt = 3,

    /// <summary>按文件大小（取这本书所有格式里最大的那个）。</summary>
    FileSize = 4,
}
