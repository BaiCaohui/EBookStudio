namespace EbookToolSuite.App.Navigation;

/// <summary>
/// 图标字形。**全工程唯一一处定义图标的地方**（设计文档 9.9.4）。
/// </summary>
/// <remarks>
/// 字体用 Segoe Fluent Icons（Windows 11 内置）。
/// 下面每个码位都在 2026-10-02 用
/// <c>GlyphTypeface.CharacterToGlyphMap.ContainsKey(codepoint)</c> 实测存在，
/// 不是从文档抄的。换图标时请重新实测，别凭记忆写码位。
/// 测试工程的 NavIconsTests 会守住这一点。
/// </remarks>
public static class NavIcons
{
    /// <summary>书库 E8F1 Library。</summary>
    public const string Library = "\uE8F1";

    /// <summary>转换队列 E895 Sync。</summary>
    public const string Queue = "\uE895";

    /// <summary>活动中心 E81C History。</summary>
    public const string Activity = "\uE81C";

    /// <summary>统计 E9D2 BarChart。</summary>
    public const string Stats = "\uE9D2";

    /// <summary>设置 E713 Settings。</summary>
    public const string Settings = "\uE713";

    /// <summary>
    /// 备份与还原 E9D5 Clock（**故意沿用已验证的 Clock 码位**）。
    /// 本文件的规矩是"换图标必须重新实测码位、别凭记忆写"，而归档型还原点本身就是"时间点"，
    /// 所以这里直接用已经验过的 Clock，不去赌一个没验过的"撤销"码位。
    /// </summary>
    public const string Backup = "\uE9D5";

    /// <summary>诊断 EBE8 Bug。</summary>
    public const string Diagnostics = "\uEBE8";

    // ---------- 非导航用途 ----------

    /// <summary>危险/警告提示 E7BA Warning，用于工具健康横幅。</summary>
    public const string Warning = "\uE7BA";

    /// <summary>信息 E946 Info。</summary>
    public const string Info = "\uE946";

    /// <summary>刷新 E72C Refresh。</summary>
    public const string Refresh = "\uE72C";

    /// <summary>文件夹 E8B7 Folder。</summary>
    public const string Folder = "\uE8B7";

    /// <summary>汉堡 E700 GlobalNavButton，用于折叠/展开左栏。</summary>
    public const string GlobalNav = "\uE700";

    /// <summary>文档 E9F9 Document，用于"还没有书"的空状态。</summary>
    public const string Document = "\uE9F9";

    /// <summary>时钟 E9D5 Clock。</summary>
    public const string Clock = "\uE9D5";
}