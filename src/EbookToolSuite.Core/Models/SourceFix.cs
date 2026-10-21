namespace EbookToolSuite.Core.Models;

/// <summary>
/// 一处自动修正（T5-2W）：改什么、为什么、在第几行。
/// 确认弹窗逐条展示这些，用户点【确认修改】才动手。
/// </summary>
public sealed record SourceFix(string Kind, int Line, string Before, string After, string Reason)
{
    /// <summary>弹窗里显示的一行说明。</summary>
    public string Display
        => "第 " + Line.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 行："
            + Reason
            + "　原文【" + Shorten(Before) + "】-> 改成【" + Shorten(After) + "】";

    private static string Shorten(string value)
        => value.Length <= 40 ? value : value[..40] + "...";
}
