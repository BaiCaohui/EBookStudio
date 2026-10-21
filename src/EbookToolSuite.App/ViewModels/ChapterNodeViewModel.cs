using EbookToolSuite.Core.Models;

namespace EbookToolSuite.App.ViewModels;

/// <summary>
/// 章节树里的一行（T5-1）。
/// </summary>
/// <remarks>
/// 只做显示适配，不放业务：缩进按目录层级算，位置号按 spine 顺序算。
/// </remarks>
public sealed class ChapterNodeViewModel
{
    public ChapterNodeViewModel(ChapterNode node) => Node = node;

    public ChapterNode Node { get; }

    public int Index => Node.Index;

    public string Title => Node.Title;

    public string Href => Node.Href;

    public bool TitleFromToc => Node.TitleFromToc;

    /// <summary>目录里没有这一章时为 true：树上要标出来，否则用户会以为目录坏了。</summary>
    public bool TitleFromFileName => !Node.TitleFromToc;

    /// <summary>多级缩进：目录层级每深一级缩进 16 像素（判据 1）。</summary>
    public double IndentWidth => Math.Max(0, Node.Level - 1) * 16d;

    /// <summary>目录里的层级（1 = 顶层，0 = 不在目录里）。</summary>
    public int Level => Node.Level;

    /// <summary>缩进（WPF 的 Margin 直接绑它，省一个转换器）：每深一级缩进 16 像素。</summary>
    public System.Windows.Thickness Indent => new(Math.Max(0, Node.Level - 1) * 16d, 0, 0, 0);

    /// <summary>第几章（spine 顺序，1 起）。</summary>
    public string Position => (Node.Index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string Hint => Node.TitleFromToc
        ? Node.Href + "（目录层级 " + Node.Level + "）"
        : Node.Href + "（目录里没有这一章，标题按文件名显示；用「重命名」可以把它补进目录）";

    /// <summary>这一章在本次会话里被改过。</summary>
    public bool Changed => Node.Changed;

    /// <summary>文件在工作区里存在（不存在 = 坏包）。</summary>
    public bool Exists => Node.Exists;
}
