using EbookToolSuite.Core.Interfaces;

namespace EbookToolSuite.App.Themes;

/// <summary>
/// 主题档位（T7-3）。配置文件里的取值见 <see cref="ThemeManager.ParseMode"/>。
/// </summary>
public enum AppThemeMode
{
    /// <summary>跟随系统：系统浅色用亮色，系统深色用暗色。默认档。</summary>
    System,

    /// <summary>强制亮色。</summary>
    Light,

    /// <summary>米黄（只能手动选，不随系统）。配色对齐阅读器的米黄档。</summary>
    Sepia,

    /// <summary>强制暗色。</summary>
    Dark,

    /// <summary>护眼绿（只能手动选，不随系统）。</summary>
    Green,

    /// <summary>自定义配色（用户自己选底色/卡片/文字/强调色，深色底按暗色规则推导其余 token）。</summary>
    Custom,
}
