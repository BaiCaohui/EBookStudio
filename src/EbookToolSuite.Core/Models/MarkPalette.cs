namespace EbookToolSuite.Core.Models;

/// <summary>
/// 标记调色板（T6-5 补短四）。notes.color 存这里的 <see cref="Id"/>：
/// 0 = 默认色（跟随样式自带外观，兼容旧数据），1..14 = 具体颜色。
///
/// 用户 2026-10-18 第三轮要求：红橙黄绿青蓝紫七个基色 + 相邻基色的中间色。
/// 颜色点显示的是**原色**；只有"荧光"样式在渲染时会把底色降调（与白色混合变淡），
/// 其余样式（下划线/波浪线/虚线/删除线/背景色/字体色）一律用原色。
///
/// 为什么存索引而不是 RGB：颜色是 UI 概念，索引让"主题/对比度调整"能统一改，
/// 也让数据库里的值可读；将来要自定义颜色再扩成 24 位色也不影响旧值。
/// </summary>
public static class MarkPalette
{
    public sealed record Entry(int Id, string Name, string Hex);

    public static readonly IReadOnlyList<Entry> Entries = new[]
    {
        new Entry(1, "红", "#E53935"),
        new Entry(2, "红橙", "#F4511E"),
        new Entry(3, "橙", "#FB8C00"),
        new Entry(4, "橙黄", "#F9A825"),
        new Entry(5, "黄", "#FDD835"),
        new Entry(6, "黄绿", "#9CCC65"),
        new Entry(7, "绿", "#43A047"),
        new Entry(8, "绿青", "#26A69A"),
        new Entry(9, "青", "#00ACC1"),
        new Entry(10, "青蓝", "#039BE5"),
        new Entry(11, "蓝", "#1E88E5"),
        new Entry(12, "蓝紫", "#5E35B1"),
        new Entry(13, "紫", "#8E24AA"),
        new Entry(14, "灰", "#757575"),
    };
}
