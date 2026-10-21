namespace EbookToolSuite.Core.Models;

/// <summary>
/// 左栏的一个导航项。纯数据，UI 只负责渲染它。
/// </summary>
/// <param name="Route">路由，取自 <c>Routes</c> 里的常量。</param>
/// <param name="Title">左栏显示的标题，也是顶栏标题。</param>
/// <param name="IconGlyph">
/// 图标字形。来自 Segoe Fluent Icons（Win11 内置），码位已在
/// 2026-10-02 用 GlyphTypeface.CharacterToGlyphMap 实测存在，见 App/Navigation/NavIcons.cs。
/// </param>
/// <param name="ShortcutText">快捷键提示文字，如 "Ctrl+1"。仅用于显示。</param>
public sealed record NavigationItem(
    string Route,
    string Title,
    string IconGlyph,
    string ShortcutText);