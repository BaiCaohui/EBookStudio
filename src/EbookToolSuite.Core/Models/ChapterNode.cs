namespace EbookToolSuite.Core.Models;

/// <summary>
/// 章节树上的一个节点（执行计划 T5-1）。
/// </summary>
/// <remarks>
/// 【为什么以 spine 为权威】目录（nav/NCX）只是"标题 + 层级"的来源：
/// 真实电子书里目录可能缺项、可能多出不在 spine 里的项、也可能干脆没有。
/// 所以"有哪些章、第几章"一律以 spine 为准，目录只用来补标题与缩进。
/// </remarks>
public sealed class ChapterNode
{
    /// <summary>spine 里的序号（0 起），就是"第几章"。</summary>
    public int Index { get; set; }

    /// <summary>manifest 里的 id：改 spine、删章节、查引用都靠它。</summary>
    public string Id { get; set; } = "";

    /// <summary>包内相对路径（正斜杠），如 OEBPS/Text/chap01.xhtml。</summary>
    public string Href { get; set; } = "";

    /// <summary>显示标题：目录里的文字，没目录就退回文件名。</summary>
    public string Title { get; set; } = "";

    /// <summary>标题来自目录（true）还是文件名兜底（false）。</summary>
    public bool TitleFromToc { get; set; }

    /// <summary>目录里的层级，1 = 顶层；0 = 这一章不在目录里。</summary>
    public int Level { get; set; }

    /// <summary>spine@linear="no"：辅助章节（正常阅读顺序里跳过，导出时仍保留）。</summary>
    public bool Linear { get; set; } = true;

    /// <summary>这一章在本次会话里已经被改动过（用于树上打标记）。</summary>
    public bool Changed { get; set; }

    /// <summary>文件在工作区里是否真的存在（不存在 = 坏包或已被删）。</summary>
    public bool Exists { get; set; } = true;
}
