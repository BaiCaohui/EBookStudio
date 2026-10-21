namespace EbookToolSuite.Core.Models;

/// <summary>
/// 章节源码的检查结果（执行计划 T5-2）。
/// </summary>
/// <remarks>
/// <see cref="Ok"/> 为 false 时，<see cref="Line"/>/<see cref="Column"/> 是 1 基的出错位置，
/// <see cref="Hint"/> 是给人看的人话提示（"&lt;br&gt; 要写成 &lt;br/&gt;" 这类），
/// <see cref="Message"/> 保留解析器的原始英文消息，便于排查。
/// </remarks>
public sealed record ChapterSourceCheck(bool Ok, int Line, int Column, string Message, string Hint)
{
    /// <summary>检查通过。</summary>
    public static ChapterSourceCheck Success { get; } = new(true, 0, 0, "ok", "语法检查通过");

    /// <summary>界面状态条用的一行文案。</summary>
    public string Summary => Ok
        ? "语法检查通过"
        : string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"第 {Line} 行第 {Column} 列：{Hint}");
}
