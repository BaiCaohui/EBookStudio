using System.Globalization;

namespace EbookToolSuite.Core.Models;

/// <summary>
/// 章节的一个拆分点：在正文里的第 <paramref name="SplitAt"/> 个块元素<b>之前</b>切开。
/// </summary>
/// <remarks>
/// 这一块与它后面的块归"后半章"（会写成新章节），前面的留在原章节。
/// 用户界面上按 <see cref="Display"/> 显示成"第 N 块之前：开头几个字"。
/// </remarks>
public sealed record ChapterSplitPoint(int SplitAt, string Preview)
{
    /// <summary>下拉框里显示的文字。</summary>
    public string Display
        => "第 " + (SplitAt + 1).ToString(CultureInfo.InvariantCulture) + " 块之前：" + Preview;
}
