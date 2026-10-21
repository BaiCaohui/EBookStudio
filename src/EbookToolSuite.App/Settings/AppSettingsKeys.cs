using System.Globalization;
using System.Text.Json.Nodes;
using EbookToolSuite.Core.Interfaces;

namespace EbookToolSuite.App.Settings;

/// <summary>
/// T7-4：设置页与启动组装共用的配置键、默认值、规范化规则。
///
/// 两条纪律：
/// 1. 键沿用一个点分路径（Config.json 的既有写法），绝不另造第二套；
/// 2. 只通过 <see cref="IConfigStore"/> 读写——它是"绝不丢字段"的唯一入口，
///    设置页每改一项就立刻落盘，用户不需要点保存。
/// </summary>
public static class AppSettingsKeys
{
    // ---------- 已有键（T7-4 只是把它们搬进设置页，语义不变） ----------

    /// <summary>主题六档：system / light / sepia / dark / green / custom（T7-3；米黄与自定义为 2026-10-21 追加）。</summary>
    public const string Theme = "ui.theme";

    public const string AccentFollowSystem = "ui.accentFollowSystem";

    /// <summary>书库视图：grid / list（工具栏按钮与设置页下拉共用）。</summary>
    public const string LibraryView = "ui.libraryView";

    /// <summary>阅读模式：true = 分页，false = 滚动。</summary>
    public const string PaginationMode = "reader.paginationMode";

    public const string ReaderTheme = "reader.theme";

    public const string FontSize = "reader.typography.fontSize";

    public const string FontFamily = "reader.typography.fontFamily";

    public const string LineHeight = "reader.typography.lineHeight";

    /// <summary>PDF / 漫画的预加载页数（原工具栏「预载」下拉，T7-4 起只在设置页改）。</summary>
    public const string PdfPreloadPages = "reader.pdfPreloadPages";

    public const string ConversionTimeoutMinutes = "conversion.timeoutMinutes";

    // ---------- T7-4 新增键 ----------

    /// <summary>界面语言。目前只有 zh-CN；写进配置是为了将来加语言时不丢选择。</summary>
    public const string Language = "ui.language";

    /// <summary>启动时自动打开上次读的那本书（默认关）。上次的书见 <see cref="LastBookId"/>。</summary>
    public const string OpenLastBook = "ui.openLastBook";

    /// <summary>封面尺寸三档：small / medium / large（书库网格）。</summary>
    public const string CoverSize = "ui.coverSize";

    /// <summary>书库目录监听（目录变化后自动重扫提示）开关，默认开。</summary>
    public const string AutoScan = "library.autoScan";

    /// <summary>转换队列并发数（1-4）；排队执行器在 DI 组装时读它，重启生效。</summary>
    public const string ConversionMaxParallel = "conversion.maxParallel";

    /// <summary>转换窗口默认目标格式（epub / mobi / pdf / txt）。</summary>
    public const string ConversionDefaultTarget = "conversion.defaultTargetFormat";

    /// <summary>缓存大小上限（MB）；超出时启动清理最旧的缓存文件。</summary>
    public const string CacheLimitMb = "cache.limitMb";

    /// <summary>备份上限（MB）；还原点清理与界面显示共用。</summary>
    public const string BackupLimitMb = "backup.limitMb";

    /// <summary>日志级别（Verbose / Debug / Information / Warning / Error / Fatal）；重启生效。</summary>
    public const string LogLevel = "advanced.logLevel";

    /// <summary>Config.json 里的数据目录镜像值（真实权威是 datadir.txt 指针文件）。</summary>
    public const string DataDirMirror = "paths.dataDir";

    /// <summary>迁移成功后的旧数据目录（重启后设置页据此提供"删除旧数据目录"）。</summary>
    public const string PreviousDataDir = "paths.previousDataDir";

    /// <summary>首次运行向导已经走完（或数据目录已非空）。</summary>
    public const string FirstRunCompleted = "ui.firstRunCompleted";

    /// <summary>最后打开的那本书（"启动时打开上次的书"用）。</summary>
    public const string LastBookId = "reader.lastBookId";

    // ---------- 预加载页数（用户 2026-10-19 要求：设置页可输，1-1000） ----------

    public const int MinPreloadPages = 1;

    public const int MaxPreloadPages = 1000;

    public const int DefaultPreloadPages = 10;

    /// <summary>文本 → 页数；空 / 非法回退 fallback，越界夹到 [1, 1000]。</summary>
    public static int NormalizePreloadPages(string? raw, int fallback = DefaultPreloadPages)
        => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, MinPreloadPages, MaxPreloadPages)
            : Math.Clamp(fallback, MinPreloadPages, MaxPreloadPages);

    /// <summary>整数 → 页数；越界夹到 [1, 1000]。</summary>
    public static int NormalizePreloadPages(int value)
        => Math.Clamp(value, MinPreloadPages, MaxPreloadPages);

    /// <summary>从配置读预加载页数（没存过 / 存坏了都是默认 10）。</summary>
    public static int ReadPreloadPages(IConfigStore? config)
        => NormalizePreloadPages(config?.GetString(PdfPreloadPages, null), DefaultPreloadPages);

    /// <summary>把预加载页数写进配置并落盘，返回真正写进去的值。</summary>
    /// <remarks>数字按字符串存：阅读器旧实现就这么读（<c>GetString</c>），冒烟也按字符串断言。</remarks>
    public static async Task<int> WritePreloadPagesAsync(
        IConfigStore config,
        int value,
        CancellationToken ct = default)
    {
        var normalized = NormalizePreloadPages(value);

        await config.SetAsync(
                PdfPreloadPages,
                JsonValue.Create(normalized.ToString(CultureInfo.InvariantCulture)),
                ct)
            .ConfigureAwait(true);
        await config.SaveAsync(ct).ConfigureAwait(true);

        return normalized;
    }

    // ---------- 封面尺寸 ----------

    public const string CoverSizeSmall = "small";

    public const string CoverSizeMedium = "medium";

    public const string CoverSizeLarge = "large";

    public const string DefaultCoverSize = CoverSizeMedium;

    public static string NormalizeCoverSize(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        CoverSizeSmall => CoverSizeSmall,
        CoverSizeLarge => CoverSizeLarge,
        _ => DefaultCoverSize,
    };

    /// <summary>卡片宽度（px）。默认档保持 T1-3 定下的 150，视觉不走样。</summary>
    public static double CardWidth(string? size) => NormalizeCoverSize(size) switch
    {
        CoverSizeSmall => 120,
        CoverSizeLarge => 190,
        _ => 150,
    };

    /// <summary>卡片封面区高度（px）：3:4 竖版，跟着卡片宽度走。</summary>
    public static double CoverHeight(string? size) => Math.Round(CardWidth(size) * 4d / 3d);

    // ---------- 转换并发 ----------

    public const int MinMaxParallel = 1;

    public const int MaxMaxParallel = 4;

    public static int NormalizeMaxParallel(int value) => Math.Clamp(value, MinMaxParallel, MaxMaxParallel);

    // ---------- 日志级别 ----------

    public static readonly IReadOnlyList<string> LogLevels =
        new[] { "Verbose", "Debug", "Information", "Warning", "Error", "Fatal" };

    public static string NormalizeLogLevel(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "Information";
        }

        foreach (var level in LogLevels)
        {
            if (string.Equals(level, raw.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return level;
            }
        }

        return "Information";
    }

    // ---------- 缓存 / 备份上限（MB） ----------

    public const int DefaultCacheLimitMb = 2048;

    public const int MinCacheLimitMb = 128;

    public const int MaxCacheLimitMb = 102400;

    public static int NormalizeCacheLimitMb(int value) => Math.Clamp(value, MinCacheLimitMb, MaxCacheLimitMb);

    /// <summary>默认与 <c>IRestorePointService.DefaultMaxTotalBytes</c>（1 GB）对齐。</summary>
    public const int DefaultBackupLimitMb = 1024;

    public const int MinBackupLimitMb = 256;

    public const int MaxBackupLimitMb = 102400;

    public static int NormalizeBackupLimitMb(int value) => Math.Clamp(value, MinBackupLimitMb, MaxBackupLimitMb);

    // ---------- 通用读写助手（数字一律按字符串存，与阅读器既有写法一致） ----------

    /// <summary>读整数：兼容数字与字符串两种存法（Config.json 允许手改）。</summary>
    public static int GetInt(IConfigStore config, string key, int fallback)
    {
        if (config.Get(key) is not JsonValue value)
        {
            return fallback;
        }

        if (value.TryGetValue<int>(out var number))
        {
            return number;
        }

        if (value.TryGetValue<string>(out var text)
            && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        if (value.TryGetValue<double>(out var real))
        {
            return (int)Math.Round(real);
        }

        return fallback;
    }

    /// <summary>读双精度：同样兼容数字与字符串。</summary>
    public static double GetDouble(IConfigStore config, string key, double fallback)
    {
        if (config.Get(key) is not JsonValue value)
        {
            return fallback;
        }

        if (value.TryGetValue<double>(out var number))
        {
            return number;
        }

        return value.TryGetValue<string>(out var text)
               && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    public static async Task SetIntAsync(IConfigStore config, string key, int value, CancellationToken ct = default)
    {
        await config.SetAsync(key, JsonValue.Create(value), ct).ConfigureAwait(true);
        await config.SaveAsync(ct).ConfigureAwait(true);
    }

    public static async Task SetDoubleAsync(IConfigStore config, string key, double value, CancellationToken ct = default)
    {
        await config.SetAsync(key, JsonValue.Create(value), ct).ConfigureAwait(true);
        await config.SaveAsync(ct).ConfigureAwait(true);
    }

    public static async Task SetStringAsync(IConfigStore config, string key, string value, CancellationToken ct = default)
    {
        await config.SetAsync(key, JsonValue.Create(value), ct).ConfigureAwait(true);
        await config.SaveAsync(ct).ConfigureAwait(true);
    }

    public static async Task SetBoolAsync(IConfigStore config, string key, bool value, CancellationToken ct = default)
    {
        await config.SetAsync(key, JsonValue.Create(value), ct).ConfigureAwait(true);
        await config.SaveAsync(ct).ConfigureAwait(true);
    }
}
