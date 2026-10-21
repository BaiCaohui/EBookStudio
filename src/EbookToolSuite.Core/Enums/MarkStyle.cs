namespace EbookToolSuite.Core.Enums;

/// <summary>
/// 标记样式（note_style.style 列，T6-5）。七种都只是"给原文套一层显示效果"，不改原文件：
/// 荧光（高亮）、实线、波浪线、虚线、删除线、背景色、字体色。
/// </summary>
public enum MarkStyle
{
    /// <summary>荧光/高亮：淡色底。</summary>
    Highlight = 0,

    /// <summary>实线下划线。</summary>
    Underline = 1,

    /// <summary>波浪线。</summary>
    Wavy = 2,

    /// <summary>删除线。</summary>
    Strikethrough = 3,

    /// <summary>虚线下划线（用户 2026-10-18 第二轮反馈补充）。</summary>
    Dashed = 4,

    /// <summary>背景色：用所选颜色铺底（不带文字改色）。</summary>
    Background = 5,

    /// <summary>字体色：用所选颜色改文字颜色。</summary>
    Foreground = 6,
}
