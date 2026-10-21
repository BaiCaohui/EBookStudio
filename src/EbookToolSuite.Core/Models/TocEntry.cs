namespace EbookToolSuite.Core.Models;

/// <summary>
/// 目录里的一条（T4-4）：标题 + 跳转目标 + 层级。
/// </summary>
/// <remarks>
/// 【为什么不复用 ReaderChapter】章节标题只需要"这一章叫什么"（平铺），
/// 目录项还要"跳到这一章的哪一段"（fragment）和"在第几层"（缩进）。
/// 两者形状不同，硬塞在一起会让清单里全是 null 字段。
/// </remarks>
public sealed class TocEntry
{
    /// <summary>显示标题。</summary>
    public string Title { get; set; } = "";

    /// <summary>
    /// spine 序号（0 起）。
    /// -1 = 这条目录指向的文档不在 spine 里，或它只是个分组标题（EPUB3 的 span），不可跳转。
    /// </summary>
    public int SpineIndex { get; set; } = -1;

    /// <summary>章内锚点（href 里 # 之后那截，已 URL 解码）。没有就是 null。</summary>
    public string? Fragment { get; set; }

    /// <summary>层级，1 = 顶层。原书超过 3 级的被拍平成 3 级。</summary>
    public int Level { get; set; } = 1;

    /// <summary>包内原始路径（已去 fragment、已解码），只用于诊断。</summary>
    public string SourceHref { get; set; } = "";

    public List<TocEntry> Children { get; set; } = new();
}
