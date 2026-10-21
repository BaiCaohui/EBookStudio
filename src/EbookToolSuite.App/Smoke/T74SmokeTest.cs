using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EbookToolSuite.App.Services;
using EbookToolSuite.App.Settings;
using EbookToolSuite.App.ViewModels;
using EbookToolSuite.App.Views;
using EbookToolSuite.App.Views.Pages;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Infrastructure.Config;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EbookToolSuite.App.Smoke;

/// <summary>
/// T7-4（首次运行向导 + 设置页 8 组 + 数据目录迁移）的进程内冒烟。
///
/// 分四块：
///   1. 设置页：8 组标题、预加载页数从阅读器挪到设置页（1-1000、手输 + 上下箭头）、
///      书库视图双向同步、封面尺寸、15 个设置键持久化；
///   2. 首次运行向导：ShouldShow 判定、四步流程、选目录后写指针 + 要求重启；
///   3. 数据目录迁移：前置检查 6 条、复制校验、取消回滚、切到已有目录、删旧目录、指针解析；
///   4. 维护动作：清缓存、导出诊断包、立即备份。
///
/// 【安全边界】迁移 / 指针 / 删旧目录的检查全部在 <c>workDirectory</c> 下造临时目录，
/// 并且每个检查结束都把真实 <c>datadir.txt</c> 与 Config.json 的 <c>paths.*</c> 恢复原状。
/// 向导也用临时 UserLocalRoot 构造独立 AppPaths，绝不写真实 %LOCALAPPDATA%。
/// </summary>
internal static class T74SmokeTest
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly string[] GroupTitleNames =
    {
        "AppearanceGroupTitle",
        "GeneralGroupTitle",
        "LibraryGroupTitle",
        "ConvertGroupTitle",
        "ReadingGroupTitle",
        "ToolsGroupTitle",
        "CacheGroupTitle",
        "BackupGroupTitle",
        "AdvancedGroupTitle",
    };

    public static async Task<SmokeReport> RunAsync(
        IServiceProvider services,
        AppPaths paths,
        string workDirectory,
        CancellationToken ct = default)
    {
        var startedAt = DateTimeOffset.Now;
        var watch = Stopwatch.StartNew();
        var checks = new List<SmokeCheck>();

        Directory.CreateDirectory(workDirectory);

        // 先把真实环境记下来：迁移检查会写 datadir.txt 与 Config.json 的 paths.*。
        var pointer = CapturePointer(paths);
        var config = services.GetRequiredService<IConfigStore>();
        var savedDataDir = config.Get(AppSettingsKeys.DataDirMirror)?.DeepClone();
        var savedPrevious = config.Get(AppSettingsKeys.PreviousDataDir)?.DeepClone();

        try
        {
            checks.AddRange(await RunSettingsChecksAsync(services, paths, workDirectory, ct)
                .ConfigureAwait(true));

            checks.AddRange(await RunWizardChecksAsync(services, workDirectory, ct)
                .ConfigureAwait(true));

            checks.AddRange(await RunMigrationChecksAsync(services, paths, workDirectory, ct)
                .ConfigureAwait(true));

            checks.AddRange(await RunMaintenanceChecksAsync(services, paths, ct)
                .ConfigureAwait(true));
        }
        catch (OperationCanceledException)
        {
            checks.Add(new SmokeCheck("t74-smoke", false, "T7-4 冒烟被取消"));
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "T7-4 冒烟失败");
            checks.Add(new SmokeCheck(
                "t74-smoke",
                false,
                "冒烟过程抛异常：" + ex.GetType().Name + "：" + ex.Message));
        }
        finally
        {
            try
            {
                await RestoreEnvironmentAsync(paths, config, pointer, savedDataDir, savedPrevious, ct)
                    .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                checks.Add(new SmokeCheck(
                    "t74-data-dir-restored",
                    false,
                    "恢复数据目录指针 / 配置失败：" + ex.GetType().Name + "：" + ex.Message));
            }
        }

        checks.Add(CheckEnvironmentRestored(paths, config, pointer, savedDataDir, savedPrevious));

        watch.Stop();

        var report = new SmokeReport(
            startedAt.ToString("O"),
            DateTimeOffset.Now.ToString("O"),
            watch.ElapsedMilliseconds,
            paths.DataRoot,
            paths.WebView2DataDir,
            null,
            checks);

        var jsonPath = Path.Combine(workDirectory, "t74-report.json");
        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(report, JsonOptions), ct)
            .ConfigureAwait(true);

        return report;
    }

    // ==================== 1. 设置页 ====================

    private static async Task<IReadOnlyList<SmokeCheck>> RunSettingsChecksAsync(
        IServiceProvider services,
        AppPaths paths,
        string workDirectory,
        CancellationToken ct)
    {
        var checks = new List<SmokeCheck>();

        SettingsPage page;

        try
        {
            page = services.GetRequiredService<SettingsPage>();
            await page.OnNavigatedToAsync(null, ct).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            checks.Add(new SmokeCheck(
                "t74-settings-page",
                false,
                "构造设置页失败：" + ex.GetType().Name + "：" + ex.Message));
            return checks;
        }

        var config = services.GetRequiredService<IConfigStore>();

        var previousPreload = AppSettingsKeys.ReadPreloadPages(config);
        var previousView = config.GetString(AppSettingsKeys.LibraryView);
        var previousCover = config.GetString(AppSettingsKeys.CoverSize);

        try
        {
            await AddAsync(checks, "t74-settings-groups", () => Task.FromResult(CheckSettingsGroups(page)))
                .ConfigureAwait(true);
            await AddAsync(checks, "t74-preload-removed-from-reader", () =>
                Task.FromResult(CheckPreloadRemovedFromReader(services))).ConfigureAwait(true);
            await AddAsync(checks, "t74-preload-spinner-elements", () =>
                Task.FromResult(CheckPreloadSpinnerElements(page))).ConfigureAwait(true);
            await AddAsync(checks, "t74-preload-clamp", () =>
                CheckPreloadClampAsync(page, config, ct)).ConfigureAwait(true);
            await AddAsync(checks, "t74-preload-step-buttons", () =>
                CheckPreloadStepButtonsAsync(page, config, ct)).ConfigureAwait(true);
            await AddAsync(checks, "t74-preload-persisted", () =>
                CheckPreloadPersistedAsync(services, page, config, paths, ct)).ConfigureAwait(true);
            await AddAsync(checks, "t74-library-view-two-way", () =>
                CheckLibraryViewTwoWayAsync(page, services, config, paths, ct)).ConfigureAwait(true);
            await AddAsync(checks, "t74-settings-persist-all", () =>
                CheckSettingsPersistAllAsync(page, config, paths, ct)).ConfigureAwait(true);
            await AddAsync(checks, "t74-cover-size-clamp", () =>
                CheckCoverSizeClampAsync(page, config, ct)).ConfigureAwait(true);
            await AddAsync(checks, "t74-font-size-spinner", () =>
                CheckFontSizeSpinnerAsync(page, config, ct)).ConfigureAwait(true);
            await AddAsync(checks, "t74-font-family-dropdown", () =>
                CheckFontFamilyDropdownAsync(page, config, ct)).ConfigureAwait(true);
            await AddAsync(checks, "t74-combo-hit-area", () => Task.FromResult(CheckComboHitArea(page)))
                .ConfigureAwait(true);
            await AddAsync(checks, "t74-numeric-steppers", () =>
                CheckNumericSteppersAsync(page, config, ct)).ConfigureAwait(true);

            checks.Add(CaptureReadingGroupShot(page, workDirectory));
            checks.Add(CaptureCacheGroupShot(page, workDirectory));
            checks.Add(CaptureFullSettingsShot(page, workDirectory));
        }
        finally
        {
            // 冒烟改过的设置恢复原值，免得同一次运行里的后续检查（或人工复跑）被带偏。
            // 预加载页数按阅读器既有写法存字符串，恢复时也写字符串。
            try
            {
                await AppSettingsKeys.SetStringAsync(
                    config,
                    AppSettingsKeys.PdfPreloadPages,
                    previousPreload.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ct).ConfigureAwait(true);
                await AppSettingsKeys.SetStringAsync(
                    config,
                    AppSettingsKeys.LibraryView,
                    previousView ?? "grid",
                    ct).ConfigureAwait(true);
                await AppSettingsKeys.SetStringAsync(
                    config,
                    AppSettingsKeys.CoverSize,
                    previousCover ?? AppSettingsKeys.DefaultCoverSize,
                    ct).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "T7-4 冒烟恢复设置原值失败");
            }
        }

        return checks;
    }

    /// <summary>8 组标题必须都在，且文案不为空。</summary>
    private static SmokeCheck CheckSettingsGroups(SettingsPage page)
    {
        var present = new List<string>();
        var missing = new List<string>();

        foreach (var name in GroupTitleNames)
        {
            var title = page.FindName(name) as TextBlock;

            if (title is not null && !string.IsNullOrWhiteSpace(title.Text))
            {
                present.Add(name + "「" + title.Text + "」");
            }
            else
            {
                missing.Add(name);
            }
        }

        return new SmokeCheck(
            "t74-settings-groups",
            missing.Count == 0,
            missing.Count == 0
                ? "9 组标题齐全：" + string.Join("，", present)
                : "缺少或为空的组标题：" + string.Join("，", missing));
    }

    /// <summary>预加载页数已经从阅读器工具栏挪走：html / js / css 三处都不该再有旧的入口。</summary>
    private static SmokeCheck CheckPreloadRemovedFromReader(IServiceProvider services)
    {
        var assets = services.GetRequiredService<IReaderAssets>();
        var html = File.ReadAllText(Path.Combine(assets.Directory, "reader.html"));
        var js = File.ReadAllText(Path.Combine(assets.Directory, "reader.js"));
        var css = File.ReadAllText(Path.Combine(assets.Directory, "reader.css"));

        var htmlHasLegacy = html.Contains("id=\"pdfPreload\"", StringComparison.Ordinal);
        var jsHasLegacy = js.Contains("reader.pdfPreload", StringComparison.Ordinal);
        var cssHasLegacy = css.Contains("#pdfPreload", StringComparison.Ordinal);

        // 范围从旧的 0..40 改成 1..1000：脚本里应出现 1000，且不再有 Math.min(40。
        var jsHasThousand = js.Contains("1000", StringComparison.Ordinal);
        var jsHasOldClamp = js.Contains("Math.min(40", StringComparison.Ordinal);

        var ok = !htmlHasLegacy && !jsHasLegacy && !cssHasLegacy && jsHasThousand && !jsHasOldClamp;

        return new SmokeCheck(
            "t74-preload-removed-from-reader",
            ok,
            $"reader.html 旧入口={htmlHasLegacy}，reader.js 旧消息={jsHasLegacy}，"
            + $"reader.css 旧样式={cssHasLegacy}；reader.js 含 1000={jsHasThousand}，"
            + $"旧上限 Math.min(40={jsHasOldClamp}");
    }

    /// <summary>设置页「阅读」组里的预加载输入框与上下箭头。</summary>
    private static SmokeCheck CheckPreloadSpinnerElements(SettingsPage page)
    {
        var box = page.FindName("PreloadBox") as TextBox;
        var up = page.FindName("PreloadUpButton") as ButtonBase;
        var down = page.FindName("PreloadDownButton") as ButtonBase;

        var ok = box is not null && up is not null && down is not null;

        return new SmokeCheck(
            "t74-preload-spinner-elements",
            ok,
            $"PreloadBox={(box is null ? "缺失" : "TextBox")}，"
            + $"PreloadUpButton={(up is null ? "缺失" : up.GetType().Name)}，"
            + $"PreloadDownButton={(down is null ? "缺失" : down.GetType().Name)}");
    }

    /// <summary>手输 "0" / "-5" / "1001" / "" 分别夹到 1、1、1000、保持上次有效值。</summary>
    private static async Task<SmokeCheck> CheckPreloadClampAsync(
        SettingsPage page,
        IConfigStore config,
        CancellationToken ct)
    {
        var box = page.FindName("PreloadBox") as TextBox;

        if (box is null)
        {
            return new SmokeCheck("t74-preload-clamp", false, "找不到 PreloadBox");
        }

        var cases = new (string Raw, int Expected)[]
        {
            ("0", 1),
            ("-5", 1),
            ("1001", 1000),
        };

        var details = new List<string>();

        foreach (var (raw, expected) in cases)
        {
            box.Text = raw;

            var settled = await WaitUntilAsync(
                () => AppSettingsKeys.ReadPreloadPages(config) == expected
                      && string.Equals(box.Text, expected.ToString(), StringComparison.Ordinal),
                TimeSpan.FromSeconds(2),
                ct).ConfigureAwait(true);

            details.Add(
                $"\"{raw}\"→配置 {AppSettingsKeys.ReadPreloadPages(config)}/框 \"{box.Text}\"（期望 {expected}）");

            if (!settled)
            {
                return new SmokeCheck(
                    "t74-preload-clamp",
                    false,
                    "输入没有稳定到期望值：" + string.Join("；", details));
            }
        }

        // 空串是"正在清空重输"的中间态：不落盘，配置保持上一次有效值 1000；
        // 失焦时应把框内文字回填成 1000。
        box.Text = string.Empty;

        var keptLast = await WaitUntilAsync(
            () => AppSettingsKeys.ReadPreloadPages(config) == 1000,
            TimeSpan.FromSeconds(2),
            ct).ConfigureAwait(true);

        box.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent, box));
        await PumpAsync().ConfigureAwait(true);
        await Task.Delay(100, ct).ConfigureAwait(true);

        var restoredText = string.Equals(box.Text, "1000", StringComparison.Ordinal);
        details.Add($"\"\"→配置 {AppSettingsKeys.ReadPreloadPages(config)}/框 \"{box.Text}\"（期望仍 1000）");

        if (!keptLast || !restoredText)
        {
            return new SmokeCheck("t74-preload-clamp", false, "空输入后没有保持/回填上一次有效值：" + string.Join("；", details));
        }

        return new SmokeCheck("t74-preload-clamp", true, string.Join("；", details));
    }

    /// <summary>向上 / 向下箭头 ±1，且到 1、1000 边界后不再越界。</summary>
    private static async Task<SmokeCheck> CheckPreloadStepButtonsAsync(
        SettingsPage page,
        IConfigStore config,
        CancellationToken ct)
    {
        var box = page.FindName("PreloadBox") as TextBox;
        var up = page.FindName("PreloadUpButton") as ButtonBase;
        var down = page.FindName("PreloadDownButton") as ButtonBase;

        if (box is null || up is null || down is null)
        {
            return new SmokeCheck("t74-preload-step-buttons", false, "预加载输入框或上下箭头缺失");
        }

        var steps = new List<string>();

        box.Text = "250";

        if (!await WaitUntilAsync(() => AppSettingsKeys.ReadPreloadPages(config) == 250, TimeSpan.FromSeconds(2), ct)
                .ConfigureAwait(true))
        {
            return new SmokeCheck("t74-preload-step-buttons", false, "无法把初始值设为 250");
        }

        steps.Add("初始 250");

        RaiseClick(up);

        if (!await WaitUntilAsync(() => AppSettingsKeys.ReadPreloadPages(config) == 251, TimeSpan.FromSeconds(2), ct)
                .ConfigureAwait(true))
        {
            return new SmokeCheck(
                "t74-preload-step-buttons", false, "点 +1 后期望 251，实际 " + AppSettingsKeys.ReadPreloadPages(config));
        }

        steps.Add("+1→251");

        RaiseClick(down);

        if (!await WaitUntilAsync(() => AppSettingsKeys.ReadPreloadPages(config) == 250, TimeSpan.FromSeconds(2), ct)
                .ConfigureAwait(true))
        {
            return new SmokeCheck(
                "t74-preload-step-buttons", false, "点 -1 后期望 250，实际 " + AppSettingsKeys.ReadPreloadPages(config));
        }

        RaiseClick(down);

        if (!await WaitUntilAsync(() => AppSettingsKeys.ReadPreloadPages(config) == 249, TimeSpan.FromSeconds(2), ct)
                .ConfigureAwait(true))
        {
            return new SmokeCheck(
                "t74-preload-step-buttons", false, "再点 -1 后期望 249，实际 " + AppSettingsKeys.ReadPreloadPages(config));
        }

        steps.Add("-1×2→249");

        // 上界：1000 再点 +1 仍是 1000
        box.Text = "1000";

        if (!await WaitUntilAsync(() => AppSettingsKeys.ReadPreloadPages(config) == 1000, TimeSpan.FromSeconds(2), ct)
                .ConfigureAwait(true))
        {
            return new SmokeCheck("t74-preload-step-buttons", false, "无法把值设为 1000");
        }

        RaiseClick(up);
        await PumpAsync().ConfigureAwait(true);
        await Task.Delay(150, ct).ConfigureAwait(true);

        var upper = AppSettingsKeys.ReadPreloadPages(config);
        var upperTextOk = string.Equals(box.Text, "1000", StringComparison.Ordinal);

        if (upper != 1000 || !upperTextOk)
        {
            return new SmokeCheck(
                "t74-preload-step-buttons",
                false,
                $"上界失效：1000 点 +1 后配置={upper}、框=\"{box.Text}\"（期望仍 1000）");
        }

        steps.Add("1000+1→1000");

        // 下界：1 再点 -1 仍是 1
        box.Text = "1";

        if (!await WaitUntilAsync(() => AppSettingsKeys.ReadPreloadPages(config) == 1, TimeSpan.FromSeconds(2), ct)
                .ConfigureAwait(true))
        {
            return new SmokeCheck("t74-preload-step-buttons", false, "无法把值设为 1");
        }

        RaiseClick(down);
        await PumpAsync().ConfigureAwait(true);
        await Task.Delay(150, ct).ConfigureAwait(true);

        var lower = AppSettingsKeys.ReadPreloadPages(config);
        var lowerTextOk = string.Equals(box.Text, "1", StringComparison.Ordinal);

        if (lower != 1 || !lowerTextOk)
        {
            return new SmokeCheck(
                "t74-preload-step-buttons",
                false,
                $"下界失效：1 点 -1 后配置={lower}、框=\"{box.Text}\"（期望仍 1）");
        }

        steps.Add("1-1→1");

        return new SmokeCheck("t74-preload-step-buttons", true, string.Join("；", steps));
    }

    /// <summary>值真的写进 Config.json；重新构一个设置页能读回来。</summary>
    private static async Task<SmokeCheck> CheckPreloadPersistedAsync(
        IServiceProvider services,
        SettingsPage page,
        IConfigStore config,
        AppPaths paths,
        CancellationToken ct)
    {
        var box = page.FindName("PreloadBox") as TextBox;

        if (box is null)
        {
            return new SmokeCheck("t74-preload-persisted", false, "找不到 PreloadBox");
        }

        box.Text = "777";

        var persisted = await WaitUntilAsync(
            () => AppSettingsKeys.ReadPreloadPages(config) == 777,
            TimeSpan.FromSeconds(2),
            ct).ConfigureAwait(true);

        if (!persisted)
        {
            return new SmokeCheck(
                "t74-preload-persisted", false, "设成 777 后配置仍是 " + AppSettingsKeys.ReadPreloadPages(config));
        }

        var fileText = await File.ReadAllTextAsync(paths.ConfigFile, ct).ConfigureAwait(true);
        var fileOk = fileText.Contains("\"pdfPreloadPages\"", StringComparison.Ordinal)
                     && fileText.Contains("777", StringComparison.Ordinal);

        // 重新构造一份设置页（transient）：应该从配置里读回 777。
        SettingsPage second;

        try
        {
            second = services.GetRequiredService<SettingsPage>();
            await second.OnNavigatedToAsync(null, ct).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            return new SmokeCheck(
                "t74-preload-persisted",
                false,
                "重新构造设置页失败：" + ex.GetType().Name + "：" + ex.Message);
        }

        var secondBox = second.FindName("PreloadBox") as TextBox;
        var secondText = secondBox?.Text ?? "<缺失>";

        var ok = fileOk
                 && string.Equals(secondText, "777", StringComparison.Ordinal)
                 && AppSettingsKeys.ReadPreloadPages(config) == 777;

        return new SmokeCheck(
            "t74-preload-persisted",
            ok,
            $"Config.json 含 pdfPreloadPages/777={fileOk}，新设置页 PreloadBox.Text=\"{secondText}\"（期望 777），"
            + $"配置读回={AppSettingsKeys.ReadPreloadPages(config)}");
    }

    /// <summary>设置页下拉 → 书库 VM；工具栏切换 → 设置页下拉。双向同步且落盘。</summary>
    private static async Task<SmokeCheck> CheckLibraryViewTwoWayAsync(
        SettingsPage page,
        IServiceProvider services,
        IConfigStore config,
        AppPaths paths,
        CancellationToken ct)
    {
        var combo = page.FindName("LibraryViewCombo") as ComboBox;
        var vm = services.GetRequiredService<LibraryViewModel>();

        if (combo is null)
        {
            return new SmokeCheck("t74-library-view-two-way", false, "找不到 LibraryViewCombo");
        }

        if (!TrySelectCombo(combo, "list", out var selectedValue))
        {
            return new SmokeCheck(
                "t74-library-view-two-way",
                false,
                "LibraryViewCombo 里没有 Tag=\"list\" 的项（现有：" + DescribeComboItems(combo) + "）");
        }

        var toList = await WaitUntilAsync(
            () => vm.IsListView && string.Equals(config.GetString(AppSettingsKeys.LibraryView), "list", StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(3),
            ct).ConfigureAwait(true);

        var fileText = await File.ReadAllTextAsync(paths.ConfigFile, ct).ConfigureAwait(true);
        var fileList = fileText.Contains("\"libraryView\": \"list\"", StringComparison.Ordinal);

        if (!toList)
        {
            return new SmokeCheck(
                "t74-library-view-two-way",
                false,
                $"下拉选 {selectedValue} 后：VM.IsListView={vm.IsListView}，"
                + $"配置={config.GetString(AppSettingsKeys.LibraryView) ?? "<null>"}，Config.json 含 list={fileList}");
        }

        // 反向：调 VM 的切换（工具栏按钮走的就是它），设置页下拉应该回到网格。
        await vm.ToggleViewAsync(ct).ConfigureAwait(true);

        var backToGrid = await WaitUntilAsync(
            () => !vm.IsListView
                  && string.Equals(config.GetString(AppSettingsKeys.LibraryView), "grid", StringComparison.OrdinalIgnoreCase)
                  && string.Equals(GetSelectedComboValue(combo), "grid", StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(3),
            ct).ConfigureAwait(true);

        return new SmokeCheck(
            "t74-library-view-two-way",
            backToGrid,
            $"下拉选 list → VM.IsListView={true} 且落盘；VM.ToggleViewAsync → "
            + $"VM.IsListView={vm.IsListView}、配置={config.GetString(AppSettingsKeys.LibraryView) ?? "<null>"}、"
            + $"下拉当前={GetSelectedComboValue(combo) ?? "<null>"}（期望 grid）");
    }

    /// <summary>通过真实控件写入 15 个设置键，再读 Config.json 与配置存储断言。</summary>
    private static async Task<SmokeCheck> CheckSettingsPersistAllAsync(
        SettingsPage page,
        IConfigStore config,
        AppPaths paths,
        CancellationToken ct)
    {
        var problems = new List<string>();
        var details = new List<string>();

        // 语言：只有 zh-CN，用来验证"选择落盘"这条链路。
        if (!TrySelectComboByName(page, "LanguageCombo", "zh-CN", out var language, problems))
        {
            return Fail("t74-settings-persist-all", problems);
        }

        details.Add("语言=" + language);

        if (!TrySetCheck(page, "OpenLastBookCheck", true, problems))
        {
            return Fail("t74-settings-persist-all", problems);
        }

        if (!TrySetCheck(page, "AutoScanCheck", false, problems))
        {
            return Fail("t74-settings-persist-all", problems);
        }

        // 封面尺寸先选「小」，t74-cover-size-clamp 再选「大」验证映射。
        if (!TrySelectComboByName(page, "CoverSizeCombo", "small", out var coverSize, problems))
        {
            return Fail("t74-settings-persist-all", problems);
        }

        details.Add("封面尺寸=" + coverSize);

        if (!TrySelectComboByName(page, "DefaultFormatCombo", "mobi", out var format, problems))
        {
            return Fail("t74-settings-persist-all", problems);
        }

        details.Add("默认格式=" + format);

        if (!TrySetText(page, "MaxParallelBox", "3", problems)
            || !TrySetText(page, "TimeoutMinutesBox", "45", problems))
        {
            return Fail("t74-settings-persist-all", problems);
        }

        if (!TrySelectComboByName(page, "ReaderThemeCombo", "dark", out var readerTheme, problems))
        {
            return Fail("t74-settings-persist-all", problems);
        }

        details.Add("阅读主题=" + readerTheme);

        if (!TrySetText(page, "FontSizeBox", "18", problems))
        {
            return Fail("t74-settings-persist-all", problems);
        }

        // 字体是从系统字体列表里选：挑一个和当前配置不同的候选，等页面把它落盘后以实际值为准
        // （不同机器的字体列表不同，所以不写死字体名）。
        if (page.FindName("FontFamilyBox") is not ComboBox fontBox)
        {
            problems.Add("FontFamilyBox 不存在或不是 ComboBox");
            return Fail("t74-settings-persist-all", problems);
        }

        if (fontBox.Items.Count == 0)
        {
            problems.Add("字体下拉没有任何候选（系统字体枚举失败？）");
            return Fail("t74-settings-persist-all", problems);
        }

        var currentFont = config.GetString(AppSettingsKeys.FontFamily);
        var fontPick = fontBox.Items.OfType<string>().FirstOrDefault(
                           family => !string.Equals(family, currentFont, StringComparison.OrdinalIgnoreCase))
                       ?? fontBox.Items.OfType<string>().First();

        fontBox.SelectedItem = fontPick;

        await WaitUntilAsync(
            () => string.Equals(
                config.GetString(AppSettingsKeys.FontFamily),
                fontPick,
                StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(2),
            ct).ConfigureAwait(true);

        var fontFamily = config.GetString(AppSettingsKeys.FontFamily);

        if (string.IsNullOrWhiteSpace(fontFamily))
        {
            problems.Add("字体设置没有落盘");
            return Fail("t74-settings-persist-all", problems);
        }

        details.Add("字体=" + fontFamily);

        if (!TrySetText(page, "LineHeightBox", "1.8", problems)
            || !TrySetCheck(page, "PaginationCheck", true, problems)
            || !TrySetText(page, "CacheLimitBox", "4096", problems)
            || !TrySetText(page, "BackupLimitBox", "2048", problems))
        {
            return Fail("t74-settings-persist-all", problems);
        }

        if (!TrySelectComboByName(page, "LogLevelCombo", "Debug", out var logLevel, problems))
        {
            return Fail("t74-settings-persist-all", problems);
        }

        details.Add("日志级别=" + logLevel);

        // 等所有键都落盘（每个控件各自触发一次 SaveAsync）。
        var expected = new (string Key, Func<bool> Ok)[]
        {
            (AppSettingsKeys.Language, () => string.Equals(config.GetString(AppSettingsKeys.Language), language, StringComparison.OrdinalIgnoreCase)),
            (AppSettingsKeys.OpenLastBook, () => config.GetBool(AppSettingsKeys.OpenLastBook, false)),
            (AppSettingsKeys.CoverSize, () => string.Equals(config.GetString(AppSettingsKeys.CoverSize), AppSettingsKeys.CoverSizeSmall, StringComparison.OrdinalIgnoreCase)),
            (AppSettingsKeys.AutoScan, () => !config.GetBool(AppSettingsKeys.AutoScan, true)),
            (AppSettingsKeys.ConversionDefaultTarget, () => string.Equals(config.GetString(AppSettingsKeys.ConversionDefaultTarget), format, StringComparison.OrdinalIgnoreCase)),
            (AppSettingsKeys.ConversionMaxParallel, () => AppSettingsKeys.GetInt(config, AppSettingsKeys.ConversionMaxParallel, -1) == 3),
            (AppSettingsKeys.ConversionTimeoutMinutes, () => AppSettingsKeys.GetInt(config, AppSettingsKeys.ConversionTimeoutMinutes, -1) == 45),
            (AppSettingsKeys.ReaderTheme, () => string.Equals(config.GetString(AppSettingsKeys.ReaderTheme), readerTheme, StringComparison.OrdinalIgnoreCase)),
            (AppSettingsKeys.FontSize, () => Math.Abs(AppSettingsKeys.GetDouble(config, AppSettingsKeys.FontSize, -1) - 18) < 0.001),
            (AppSettingsKeys.FontFamily, () => string.Equals(config.GetString(AppSettingsKeys.FontFamily), fontFamily, StringComparison.OrdinalIgnoreCase)),
            (AppSettingsKeys.LineHeight, () => Math.Abs(AppSettingsKeys.GetDouble(config, AppSettingsKeys.LineHeight, -1) - 1.8) < 0.001),
            (AppSettingsKeys.PaginationMode, () => config.GetBool(AppSettingsKeys.PaginationMode, false)),
            (AppSettingsKeys.CacheLimitMb, () => AppSettingsKeys.GetInt(config, AppSettingsKeys.CacheLimitMb, -1) == 4096),
            (AppSettingsKeys.BackupLimitMb, () => AppSettingsKeys.GetInt(config, AppSettingsKeys.BackupLimitMb, -1) == 2048),
            (AppSettingsKeys.LogLevel, () => string.Equals(config.GetString(AppSettingsKeys.LogLevel), logLevel, StringComparison.OrdinalIgnoreCase)),
        };

        await WaitUntilAsync(
            () => expected.All(pair => pair.Ok()),
            TimeSpan.FromSeconds(6),
            ct).ConfigureAwait(true);

        var bad = expected.Where(pair => !pair.Ok()).Select(pair => pair.Key).ToList();

        var fileText = await File.ReadAllTextAsync(paths.ConfigFile, ct).ConfigureAwait(true);

        // Config.json 里要能看到这 15 个键的"叶子名"（limitMb 出现两次）。
        var leafTokens = new[]
        {
            "language", "openLastBook", "coverSize", "autoScan", "defaultTargetFormat",
            "maxParallel", "timeoutMinutes", "theme", "fontSize", "fontFamily", "lineHeight",
            "paginationMode", "limitMb", "logLevel",
        };
        var missingLeaf = leafTokens
            .Where(token => !fileText.Contains("\"" + token + "\"", StringComparison.Ordinal))
            .ToList();

        var ok = bad.Count == 0 && missingLeaf.Count == 0;

        return new SmokeCheck(
            "t74-settings-persist-all",
            ok,
            (bad.Count == 0
                ? "15 个键全部落盘："
                : "未落盘的键：" + string.Join("，", bad) + "；")
            + (missingLeaf.Count == 0
                ? "Config.json 含全部键"
                : "Config.json 缺键：" + string.Join("，", missingLeaf))
            + "；" + string.Join("，", details)
            + "（并发数 / 日志级别只验持久化，重启生效）");
    }

    /// <summary>封面尺寸选「大」→ 配置 large、卡片宽 190；默认 medium 仍是 150。</summary>
    private static async Task<SmokeCheck> CheckCoverSizeClampAsync(
        SettingsPage page,
        IConfigStore config,
        CancellationToken ct)
    {
        var combo = page.FindName("CoverSizeCombo") as ComboBox;

        if (combo is null)
        {
            return new SmokeCheck("t74-cover-size-clamp", false, "找不到 CoverSizeCombo");
        }

        if (!TrySelectCombo(combo, "large", out var selected))
        {
            return new SmokeCheck(
                "t74-cover-size-clamp",
                false,
                "CoverSizeCombo 里没有 Tag=\"large\" 的项（现有：" + DescribeComboItems(combo) + "）");
        }

        var persisted = await WaitUntilAsync(
            () => string.Equals(
                config.GetString(AppSettingsKeys.CoverSize),
                AppSettingsKeys.CoverSizeLarge,
                StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(2),
            ct).ConfigureAwait(true);

        var largeWidth = AppSettingsKeys.CardWidth("large");
        var mediumWidth = AppSettingsKeys.CardWidth("medium");
        var defaultStillMedium = string.Equals(
            AppSettingsKeys.NormalizeCoverSize(null),
            AppSettingsKeys.CoverSizeMedium,
            StringComparison.Ordinal);

        var ok = persisted
                 && Math.Abs(largeWidth - 190) < 0.001
                 && Math.Abs(mediumWidth - 150) < 0.001
                 && defaultStillMedium;

        return new SmokeCheck(
            "t74-cover-size-clamp",
            ok,
            $"下拉选 {selected} → 配置={config.GetString(AppSettingsKeys.CoverSize) ?? "<null>"}（期望 large），"
            + $"CardWidth(large)={largeWidth}（期望 190），CardWidth(medium)={mediumWidth}（期望 150），"
            + $"默认档仍未变={defaultStillMedium}");
    }

    /// <summary>字号右侧的上下箭头：±1、夹 12–36、边界处不越界。</summary>
    private static async Task<SmokeCheck> CheckFontSizeSpinnerAsync(
        SettingsPage page,
        IConfigStore config,
        CancellationToken ct)
    {
        if (page.FindName("FontSizeBox") is not TextBox box
            || page.FindName("FontSizeUpButton") is not Button up
            || page.FindName("FontSizeDownButton") is not Button down)
        {
            return new SmokeCheck("t74-font-size-spinner", false, "字号输入框或上下箭头缺失");
        }

        var steps = new List<string>();
        string? Current() => config.GetString(AppSettingsKeys.FontSize);

        async Task<bool> SetAndWaitAsync(string text)
        {
            box.Text = text;
            return await WaitUntilAsync(
                () => string.Equals(Current(), text, StringComparison.Ordinal),
                TimeSpan.FromSeconds(2),
                ct).ConfigureAwait(true);
        }

        if (!await SetAndWaitAsync("17").ConfigureAwait(true))
        {
            return new SmokeCheck("t74-font-size-spinner", false, "无法把字号设为 17，当前=" + (Current() ?? "<null>"));
        }

        RaiseClick(up);
        var upOk = await WaitUntilAsync(() => Current() == "18", TimeSpan.FromSeconds(2), ct).ConfigureAwait(true);
        steps.Add("17 +1 = " + (Current() ?? "<null>") + (upOk ? "" : "（期望 18）"));

        RaiseClick(up);
        await WaitUntilAsync(() => Current() == "19", TimeSpan.FromSeconds(2), ct).ConfigureAwait(true);
        steps.Add("再 +1 = " + (Current() ?? "<null>") + "（期望 19）");

        RaiseClick(down);
        RaiseClick(down);
        var downOk = await WaitUntilAsync(() => Current() == "17", TimeSpan.FromSeconds(2), ct).ConfigureAwait(true);
        steps.Add("再 -1 两次 = " + (Current() ?? "<null>") + (downOk ? "" : "（期望 17）"));

        // 上界：36 时再 +1 不动
        if (!await SetAndWaitAsync("36").ConfigureAwait(true))
        {
            return new SmokeCheck("t74-font-size-spinner", false, "无法把字号设为 36，当前=" + (Current() ?? "<null>"));
        }

        RaiseClick(up);
        await PumpAsync().ConfigureAwait(true);
        var maxOk = Current() == "36";
        steps.Add("36 +1 = " + (Current() ?? "<null>") + "（期望仍 36）");

        // 下界：12 时再 -1 不动
        if (!await SetAndWaitAsync("12").ConfigureAwait(true))
        {
            return new SmokeCheck("t74-font-size-spinner", false, "无法把字号设为 12，当前=" + (Current() ?? "<null>"));
        }

        RaiseClick(down);
        await PumpAsync().ConfigureAwait(true);
        var minOk = Current() == "12";
        steps.Add("12 -1 = " + (Current() ?? "<null>") + "（期望仍 12）");

        return new SmokeCheck(
            "t74-font-size-spinner",
            upOk && downOk && maxOk && minOk,
            string.Join("；", steps));
    }

    /// <summary>
    /// 字体下拉直接读系统字体库（ReaderWindow.FontOptions），候选不能是写死的几个名字；
    /// 选中后立刻写进 reader.typography.fontFamily。
    /// </summary>
    private static async Task<SmokeCheck> CheckFontFamilyDropdownAsync(
        SettingsPage page,
        IConfigStore config,
        CancellationToken ct)
    {
        if (page.FindName("FontFamilyBox") is not ComboBox combo)
        {
            return new SmokeCheck("t74-font-family-dropdown", false, "FontFamilyBox 不存在或不是 ComboBox");
        }

        var options = combo.Items.OfType<string>().Where(f => !string.IsNullOrWhiteSpace(f)).ToList();

        if (options.Count < 10)
        {
            return new SmokeCheck(
                "t74-font-family-dropdown",
                false,
                "系统字体候选太少（" + options.Count + " 个），字体下拉可能没有读系统字体库");
        }

        var current = config.GetString(AppSettingsKeys.FontFamily);
        var chosen = options.FirstOrDefault(
                         family => !string.Equals(family, current, StringComparison.OrdinalIgnoreCase))
                     ?? options[0];

        combo.SelectedItem = chosen;

        var persisted = await WaitUntilAsync(
            () => string.Equals(
                config.GetString(AppSettingsKeys.FontFamily),
                chosen,
                StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(2),
            ct).ConfigureAwait(true);

        return new SmokeCheck(
            "t74-font-family-dropdown",
            persisted,
            $"候选 {options.Count} 个（来自系统字体库）；选中 \"{chosen}\" → 落盘="
            + (config.GetString(AppSettingsKeys.FontFamily) ?? "<null>") + (persisted ? "" : "（期望 " + chosen + "）"));
    }

    /// <summary>
    /// 下拉框整块可点：自定义模板里文字/箭头必须 IsHitTestVisible=False，
    /// 否则点击落在文字上不会冒泡到下面的透明 ToggleButton（用户报的"触点范围很小"）。
    /// </summary>
    private static SmokeCheck CheckComboHitArea(SettingsPage page)
    {
        var combos = new[]
        {
            "CoverSizeCombo", "LibraryViewCombo", "FontFamilyBox",
            "ReaderThemeCombo", "LanguageCombo", "DefaultFormatCombo", "LogLevelCombo",
        };

        var bad = new List<string>();
        var checkedCount = 0;

        foreach (var name in combos)
        {
            if (page.FindName(name) is not ComboBox combo)
            {
                bad.Add(name + "（缺失）");
                continue;
            }

            combo.ApplyTemplate();
            checkedCount++;

            var host = combo.Template.FindName("ContentHost", combo) as ContentPresenter;
            var chevron = combo.Template.FindName("Chevron", combo) as FrameworkElement;

            if (host is null || host.IsHitTestVisible || (chevron is not null && chevron.IsHitTestVisible))
            {
                bad.Add(name);
            }
        }

        return new SmokeCheck(
            "t74-combo-hit-area",
            bad.Count == 0,
            bad.Count == 0
                ? $"{checkedCount} 个下拉框的文字/箭头都不拦截点击（整个选择框都可点开）"
                : "这些下拉框的文字/箭头仍会挡住点击层：" + string.Join("，", bad));
    }

    /// <summary>
    /// 设置页每一个数字输入框右侧都必须有 ▲/▼，且 ±1 真的生效（行高按小数 ±0.1）。
    /// </summary>
    private static async Task<SmokeCheck> CheckNumericSteppersAsync(
        SettingsPage page,
        IConfigStore config,
        CancellationToken ct)
    {
        var problems = new List<string>();

        var pairs = new (string Box, string Up, string Down, string Label)[]
        {
            ("MaxParallelBox", "MaxParallelUpButton", "MaxParallelDownButton", "并发数"),
            ("TimeoutMinutesBox", "TimeoutMinutesUpButton", "TimeoutMinutesDownButton", "超时时间"),
            ("LineHeightBox", "LineHeightUpButton", "LineHeightDownButton", "行高"),
            ("FontSizeBox", "FontSizeUpButton", "FontSizeDownButton", "字号"),
            ("PreloadBox", "PreloadUpButton", "PreloadDownButton", "预加载页数"),
            ("CacheLimitBox", "CacheLimitUpButton", "CacheLimitDownButton", "缓存上限"),
            ("BackupLimitBox", "BackupLimitUpButton", "BackupLimitDownButton", "备份上限"),
        };

        foreach (var pair in pairs)
        {
            if (page.FindName(pair.Box) is not TextBox)
            {
                problems.Add(pair.Label + " 的输入框（" + pair.Box + "）缺失");
            }

            if (page.FindName(pair.Up) is not Button)
            {
                problems.Add(pair.Label + " 缺 ▲ 按钮（" + pair.Up + "）");
            }

            if (page.FindName(pair.Down) is not Button)
            {
                problems.Add(pair.Label + " 缺 ▼ 按钮（" + pair.Down + "）");
            }
        }

        if (problems.Count > 0)
        {
            return new SmokeCheck("t74-numeric-steppers", false, string.Join("；", problems));
        }

        var steps = new List<string>();
        var failures = new List<string>();

        async Task StepAndCheckAsync(
            string boxName,
            string upName,
            string downName,
            string initial,
            string afterUp,
            string afterDown,
            Func<string?> read)
        {
            var box = (TextBox)page.FindName(boxName);
            box.Text = initial;

            await WaitUntilAsync(() => read() == initial, TimeSpan.FromSeconds(2), ct).ConfigureAwait(true);

            RaiseClick((Button)page.FindName(upName));

            if (!await WaitUntilAsync(() => read() == afterUp, TimeSpan.FromSeconds(2), ct).ConfigureAwait(true))
            {
                failures.Add(boxName + " 点▲后=" + (read() ?? "<null>") + "（期望 " + afterUp + "）");
            }

            RaiseClick((Button)page.FindName(downName));

            if (!await WaitUntilAsync(() => read() == afterDown, TimeSpan.FromSeconds(2), ct).ConfigureAwait(true))
            {
                failures.Add(boxName + " 点▼后=" + (read() ?? "<null>") + "（期望 " + afterDown + "）");
            }

            steps.Add(boxName + "：" + initial + " → " + afterUp + " → " + afterDown);
        }

        await StepAndCheckAsync(
            "MaxParallelBox", "MaxParallelUpButton", "MaxParallelDownButton", "1", "2", "1",
            () => AppSettingsKeys.GetInt(config, AppSettingsKeys.ConversionMaxParallel, -1)
                .ToString(CultureInfo.InvariantCulture)).ConfigureAwait(true);

        await StepAndCheckAsync(
            "TimeoutMinutesBox", "TimeoutMinutesUpButton", "TimeoutMinutesDownButton", "60", "61", "60",
            () => AppSettingsKeys.GetInt(config, AppSettingsKeys.ConversionTimeoutMinutes, -1)
                .ToString(CultureInfo.InvariantCulture)).ConfigureAwait(true);

        await StepAndCheckAsync(
            "CacheLimitBox", "CacheLimitUpButton", "CacheLimitDownButton", "2048", "2049", "2048",
            () => AppSettingsKeys.GetInt(config, AppSettingsKeys.CacheLimitMb, -1)
                .ToString(CultureInfo.InvariantCulture)).ConfigureAwait(true);

        await StepAndCheckAsync(
            "BackupLimitBox", "BackupLimitUpButton", "BackupLimitDownButton", "1024", "1025", "1024",
            () => AppSettingsKeys.GetInt(config, AppSettingsKeys.BackupLimitMb, -1)
                .ToString(CultureInfo.InvariantCulture)).ConfigureAwait(true);

        // 行高是小数：±0.1
        await StepAndCheckAsync(
            "LineHeightBox", "LineHeightUpButton", "LineHeightDownButton", "1.75", "1.85", "1.75",
            () => AppSettingsKeys.GetDouble(config, AppSettingsKeys.LineHeight, -1)
                .ToString("0.##", CultureInfo.InvariantCulture)).ConfigureAwait(true);

        return new SmokeCheck(
            "t74-numeric-steppers",
            failures.Count == 0,
            failures.Count == 0
                ? "7 个数字框都有 ▲/▼：" + string.Join("；", steps)
                : string.Join("；", failures));
    }

    /// <summary>
    /// 把设置页「阅读」组渲染成截图（用户 2026-10-19 反馈的落点：字体下拉、字号 ±1 箭头、预加载页数）。
    /// 只是证据，不影响判定：渲染失败记一条失败检查，不抛。
    /// </summary>
    private static SmokeCheck CaptureReadingGroupShot(SettingsPage page, string workDirectory)
        => CaptureScrolledShot(
            page, workDirectory, "ReadingGroupTitle", "settings-reading.png", "t74-settings-reading-shot");

    /// <summary>「缓存 / 备份」组截图：缓存上限、备份上限两个数字框的 ▲/▼ 都在这里。</summary>
    private static SmokeCheck CaptureCacheGroupShot(SettingsPage page, string workDirectory)
        => CaptureScrolledShot(
            page, workDirectory, "CacheGroupTitle", "settings-cache.png", "t74-settings-cache-shot");

    /// <summary>把设置页滚到指定锚点分组后渲染截图（失败只记检查，不抛）。</summary>
    private static SmokeCheck CaptureScrolledShot(
        SettingsPage page,
        string workDirectory,
        string anchorName,
        string fileName,
        string checkName)
    {
        try
        {
            var shots = Path.Combine(workDirectory, "shots");
            Directory.CreateDirectory(shots);

            var window = new Window
            {
                Width = 900,
                Height = 780,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000,
                Top = -10000,
                Content = page,
            };

            window.Show();
            page.UpdateLayout();

            // 设置页根是 ScrollViewer：把锚点分组滚到视口顶部，截到关心的那几行。
            if (page.Content is ScrollViewer scroller
                && page.FindName(anchorName) is FrameworkElement anchor)
            {
                var position = anchor.TransformToAncestor(scroller).Transform(new Point(0, 0));
                scroller.ScrollToVerticalOffset(scroller.VerticalOffset + position.Y - 16);
            }
            else if (page.FindName(anchorName) is FrameworkElement fallbackAnchor)
            {
                fallbackAnchor.BringIntoView();
            }

            page.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);

            var dpi = VisualTreeHelper.GetDpi(page);
            var width = Math.Max(1, (int)Math.Ceiling(page.ActualWidth * dpi.DpiScaleX));
            var height = Math.Max(1, (int)Math.Ceiling(page.ActualHeight * dpi.DpiScaleY));

            var bitmap = new RenderTargetBitmap(
                width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(page);

            var path = Path.Combine(shots, fileName);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            using (var stream = File.Create(path))
            {
                encoder.Save(stream);
            }

            window.Content = null;
            window.Close();

            return new SmokeCheck(checkName, true, "截图=" + path);
        }
        catch (Exception ex)
        {
            return new SmokeCheck(checkName, false, "截图失败：" + ex.GetType().Name + "：" + ex.Message);
        }
    }

    /// <summary>
    /// 把整张设置页渲染成一张长图（人工核对用）：所有数字行的 ▲/▼ 是否都在、行有没有挤在一起。
    /// 只是证据，渲染失败记失败检查，不抛。
    /// </summary>
    private static SmokeCheck CaptureFullSettingsShot(SettingsPage page, string workDirectory)
    {
        try
        {
            var shots = Path.Combine(workDirectory, "shots");
            Directory.CreateDirectory(shots);

            var window = new Window
            {
                Width = 900,
                Height = 900,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000,
                Top = -10000,
                Content = page,
            };

            window.Show();
            page.UpdateLayout();

            if (page.Content is ScrollViewer scroller)
            {
                scroller.ScrollToTop();

                // 先把窗口撑到内容高度，整页一次渲染，避免分两张图看不到全貌
                var target = Math.Min(2600, scroller.ExtentHeight + 8);
                if (target > window.Height)
                {
                    window.Height = target;
                    page.UpdateLayout();
                    scroller.ScrollToTop();
                }
            }

            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);

            var dpi = VisualTreeHelper.GetDpi(page);
            var width = Math.Max(1, (int)Math.Ceiling(page.ActualWidth * dpi.DpiScaleX));
            var height = Math.Max(1, (int)Math.Ceiling(page.ActualHeight * dpi.DpiScaleY));

            var bitmap = new RenderTargetBitmap(
                width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(page);

            var path = Path.Combine(shots, "settings-full.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            using (var stream = File.Create(path))
            {
                encoder.Save(stream);
            }

            window.Content = null;
            window.Close();

            return new SmokeCheck("t74-settings-full-shot", true, "截图=" + path);
        }
        catch (Exception ex)
        {
            return new SmokeCheck(
                "t74-settings-full-shot",
                false,
                "截图失败：" + ex.GetType().Name + "：" + ex.Message);
        }
    }

    // ==================== 2. 首次运行向导 ====================

    private static async Task<IReadOnlyList<SmokeCheck>> RunWizardChecksAsync(
        IServiceProvider services,
        string workDirectory,
        CancellationToken ct)
    {
        var checks = new List<SmokeCheck>();

        await AddAsync(checks, "t74-first-run-predicate", () =>
            CheckFirstRunPredicateAsync(workDirectory, ct)).ConfigureAwait(true);

        await AddAsync(checks, "t74-first-run-wizard-flow", () =>
            CheckWizardFlowAsync(services, workDirectory, ct)).ConfigureAwait(true);

        return checks;
    }

    /// <summary>空目录 → true；写了 Config.json → false。</summary>
    private static async Task<SmokeCheck> CheckFirstRunPredicateAsync(
        string workDirectory,
        CancellationToken ct)
    {
        var root = FreshDirectory(workDirectory, "t74-wizard-predicate");
        var appRoot = Path.Combine(root, "app");
        var userRoot = Path.Combine(root, "userlocal");
        var dataRoot = Path.Combine(root, "data");

        Directory.CreateDirectory(appRoot);
        Directory.CreateDirectory(dataRoot);

        try
        {
            var paths = AppPaths.Create(new AppPathsOptions
            {
                AppRoot = appRoot,
                UserLocalRoot = userRoot,
                CommandLineDataRoot = dataRoot,
            });

            var emptyShouldShow = FirstRunWizardWindow.ShouldShow(paths);

            await File.WriteAllTextAsync(paths.ConfigFile, "{}", ct).ConfigureAwait(true);

            var configShouldShow = FirstRunWizardWindow.ShouldShow(paths);

            var ok = emptyShouldShow && !configShouldShow;

            return new SmokeCheck(
                "t74-first-run-predicate",
                ok,
                $"空目录 ShouldShow={emptyShouldShow}（期望 true），"
                + $"写入 Config.json 后 ShouldShow={configShouldShow}（期望 false）");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    /// <summary>
    /// 四步流程：默认程序 Data → 选临时目录 → 工具步骤可跳过 → 完成写 firstRunCompleted 与指针。
    /// 全程用临时 UserLocalRoot，绝不碰真实 %LOCALAPPDATA%。
    /// 步骤编号按 Lead 契约：0 欢迎 / 1 数据目录 / 2 工具 / 3 完成。
    /// </summary>
    private static async Task<SmokeCheck> CheckWizardFlowAsync(
        IServiceProvider services,
        string workDirectory,
        CancellationToken ct)
    {
        var root = FreshDirectory(workDirectory, "t74-wizard-flow");
        var appRoot = Path.Combine(root, "app");
        var userRoot = Path.Combine(root, "userlocal");
        var selectedDir = Path.Combine(root, "selected-data");

        Directory.CreateDirectory(appRoot);
        Directory.CreateDirectory(selectedDir);

        FirstRunWizardWindow? wizard = null;

        try
        {
            // 不传 CommandLineDataRoot：走"程序目录下的 Data/"默认分支，
            // 这样才验得到"默认数据目录 = 程序 Data"。
            var paths = AppPaths.Create(new AppPathsOptions
            {
                AppRoot = appRoot,
                UserLocalRoot = userRoot,
            });

            var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger<JsonConfigStore>();
            var config = new JsonConfigStore(paths, logger);
            await config.InitializeAsync(ct).ConfigureAwait(true);

            wizard = new FirstRunWizardWindow(
                paths,
                config,
                services.GetRequiredService<IToolLocator>(),
                services.GetRequiredService<IShellLauncher>());

            await PumpAsync().ConfigureAwait(true);

            var startStep = wizard.CurrentStep;

            wizard.GoToStepForTest(1);
            await PumpAsync().ConfigureAwait(true);

            var expectedDefault = Path.GetFullPath(paths.DataRoot);
            var selectedDefault = string.IsNullOrWhiteSpace(wizard.SelectedDataDir)
                ? null
                : Path.GetFullPath(wizard.SelectedDataDir);

            var defaultOk = string.Equals(selectedDefault, expectedDefault, StringComparison.OrdinalIgnoreCase);

            wizard.SelectDataDirForTest(selectedDir);
            await PumpAsync().ConfigureAwait(true);

            // 步骤 1「下一步」：校验 + 写指针；进入工具步骤时会同步把工具检测跑完。
            wizard.AdvanceForTest();
            await PumpAsync().ConfigureAwait(true);

            var toolRows = wizard.ToolItemCountForTest;
            var skippable = wizard.ToolsSkippableForTest;

            // 步骤 2「下一步」→ 完成页；步骤 3「下一步」= 完成并 Close（写 firstRunCompleted）。
            wizard.AdvanceForTest();
            await PumpAsync().ConfigureAwait(true);

            wizard.AdvanceForTest();
            await PumpAsync().ConfigureAwait(true);

            var completed = await WaitUntilAsync(
                () => config.GetBool(AppSettingsKeys.FirstRunCompleted, false),
                TimeSpan.FromSeconds(5),
                ct).ConfigureAwait(true);

            var configFileOk = File.Exists(paths.ConfigFile)
                               && (await File.ReadAllTextAsync(paths.ConfigFile, ct).ConfigureAwait(true))
                               .Contains("firstRunCompleted", StringComparison.Ordinal);

            var pointerFile = Path.Combine(userRoot, AppPaths.PointerFileName);
            var pointerText = File.Exists(pointerFile)
                ? (await File.ReadAllTextAsync(pointerFile, ct).ConfigureAwait(true)).Trim()
                : null;
            var pointerOk = string.Equals(
                pointerText is null ? null : Path.GetFullPath(pointerText),
                Path.GetFullPath(selectedDir),
                StringComparison.OrdinalIgnoreCase);

            var restart = wizard.RestartRequested;

            try
            {
                wizard.Close();
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "关闭冒烟用的首次运行向导失败");
            }

            var ok = startStep == 0
                     && defaultOk
                     && toolRows > 0
                     && skippable
                     && completed
                     && configFileOk
                     && restart
                     && pointerOk;

            return new SmokeCheck(
                "t74-first-run-wizard-flow",
                ok,
                $"起始步骤={startStep}（期望 0），默认数据目录={selectedDefault ?? "<null>"}（期望 {expectedDefault}），"
                + $"工具行={toolRows}、可跳过={skippable}，完成写 firstRunCompleted={completed}、"
                + $"Config.json 含键={configFileOk}，RestartRequested={restart}，"
                + $"指针={pointerText ?? "<缺失>"}（期望 {selectedDir}）");
        }
        finally
        {
            try
            {
                wizard?.Close();
            }
            catch (Exception)
            {
                // 已经关过就算了
            }

            TryDeleteDirectory(root);
        }
    }

    // ==================== 3. 数据目录迁移 ====================

    private static async Task<IReadOnlyList<SmokeCheck>> RunMigrationChecksAsync(
        IServiceProvider services,
        AppPaths paths,
        string workDirectory,
        CancellationToken ct)
    {
        var checks = new List<SmokeCheck>();
        var migration = services.GetRequiredService<IDataDirectoryMigrationService>();
        var config = services.GetRequiredService<IConfigStore>();

        var pointer = CapturePointer(paths);
        var savedDataDir = config.Get(AppSettingsKeys.DataDirMirror)?.DeepClone();
        var savedPrevious = config.Get(AppSettingsKeys.PreviousDataDir)?.DeepClone();

        /* 迁移目标必须放在"程序目录"之外，而 Debug 下 AppRoot 会向上找到仓库根
           （bin 往上找带 Tools/manifest.json 的目录），所以 Data-Test 下的工作目录也在
           程序目录里面，Preflight 的 target-valid 会拒绝。这里把迁移用的临时目录挪到
           系统临时目录；发布版 AppRoot=exe 目录时优先继续用工作目录，方便排查。 */
        var scratchRoot = ResolveMigrationScratch(workDirectory, paths.AppRoot);

        try
        {
            await AddAsync(checks, "t74-migration-preflight", () =>
                CheckMigrationPreflightAsync(migration, paths, scratchRoot, ct)).ConfigureAwait(true);

            await AddAsync(checks, "t74-migration-copy-verify", () =>
                CheckMigrationCopyVerifyAsync(migration, config, paths, scratchRoot, ct)).ConfigureAwait(true);

            await AddAsync(checks, "t74-migration-cancel-rollback", () =>
                CheckMigrationCancelAsync(migration, config, paths, scratchRoot, ct)).ConfigureAwait(true);

            await AddAsync(checks, "t74-migration-switch-existing", () =>
                CheckMigrationSwitchExistingAsync(migration, config, paths, scratchRoot, ct)).ConfigureAwait(true);

            await AddAsync(checks, "t74-migration-old-dir-delete", () =>
                CheckMigrationOldDirDeleteAsync(migration, config, paths, scratchRoot, ct)).ConfigureAwait(true);

            await AddAsync(checks, "t74-apppaths-pointer", () =>
                Task.FromResult(CheckAppPathsPointer(scratchRoot))).ConfigureAwait(true);
        }
        finally
        {
            // 无论成败，指针与 paths.* 都不能停在临时目标上。
            try
            {
                await RestoreEnvironmentAsync(paths, config, pointer, savedDataDir, savedPrevious, ct)
                    .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "T7-4 迁移检查后恢复数据目录失败");
            }

            if (!string.Equals(
                    Path.GetFullPath(scratchRoot),
                    Path.GetFullPath(workDirectory),
                    StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteDirectory(scratchRoot);
            }
        }

        return checks;
    }

    /// <summary>
    /// 选一个"不在 AppRoot 内"的临时根目录给迁移检查用。
    /// 工作目录本身在程序目录外时直接用它；否则用系统临时目录（不污染仓库与真实数据目录）。
    /// </summary>
    private static string ResolveMigrationScratch(string workDirectory, string appRoot)
    {
        var workFull = Path.GetFullPath(workDirectory);
        var appFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appRoot));

        var insideAppRoot = workFull.StartsWith(
            appFull + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

        if (!insideAppRoot)
        {
            Directory.CreateDirectory(workFull);
            return workFull;
        }

        var scratch = Path.Combine(
            Path.GetTempPath(),
            "EBookStudio-t74-migration-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(scratch);
        return scratch;
    }

    private static async Task<SmokeCheck> CheckMigrationPreflightAsync(
        IDataDirectoryMigrationService migration,
        AppPaths paths,
        string workDirectory,
        CancellationToken ct)
    {
        var target = FreshDirectory(workDirectory, "t74-migration-preflight-target");
        var existing = FreshDirectory(workDirectory, "t74-migration-preflight-existing");
        var appSub = Path.Combine(paths.AppRoot, "Data", "t74-preflight-should-not-exist-" + Guid.NewGuid().ToString("N")[..6]);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(existing, "Config.json"),
                "{\"ui\":{\"theme\":\"light\"}}",
                ct).ConfigureAwait(true);

            var preflight = await migration.PreflightAsync(target, ct).ConfigureAwait(true);

            var requiredIds = new[]
            {
                "target-valid",
                "target-not-data-dir",
                "target-space",
                "target-writable",
                "local-drive",
                "no-busy-tasks",
            };

            var ids = preflight.Checks.Select(check => check.Id).ToList();
            var missing = requiredIds.Where(id => !ids.Contains(id, StringComparer.Ordinal)).ToList();

            var appSubPreflight = await migration.PreflightAsync(appSub, ct).ConfigureAwait(true);
            var currentPreflight = await migration.PreflightAsync(paths.DataRoot, ct).ConfigureAwait(true);
            var existingPreflight = await migration.PreflightAsync(existing, ct).ConfigureAwait(true);

            var appSubFailed = appSubPreflight.Checks
                .Any(check => check.Id == "target-valid" && !check.Ok);
            var currentFailed = currentPreflight.Checks
                .Any(check => check.Id == "target-not-data-dir" && !check.Ok);
            var existingFailed = existingPreflight.Checks
                .Any(check => check.Id == "target-not-data-dir" && !check.Ok);

            var looksExisting = migration.LooksLikeDataDir(existing);

            var ok = missing.Count == 0
                     && preflight.CanMigrate
                     && preflight.Failed.Count == 0
                     && appSubFailed
                     && currentFailed
                     && existingFailed
                     && looksExisting;

            return new SmokeCheck(
                "t74-migration-preflight",
                ok,
                $"正向目标：{preflight.Checks.Count} 条检查（缺 Id：{(missing.Count == 0 ? "无" : string.Join("，", missing))}），"
                + $"CanMigrate={preflight.CanMigrate}；反例：程序目录子目录被 target-valid 拒={appSubFailed}，"
                + $"当前数据目录被 target-not-data-dir 拒={currentFailed}，"
                + $"已存在数据目录被拒={existingFailed}（LooksLikeDataDir={looksExisting}）");
        }
        finally
        {
            TryDeleteDirectory(target);
            TryDeleteDirectory(existing);
            TryDeleteDirectory(appSub);
        }
    }

    private static async Task<SmokeCheck> CheckMigrationCopyVerifyAsync(
        IDataDirectoryMigrationService migration,
        IConfigStore config,
        AppPaths paths,
        string workDirectory,
        CancellationToken ct)
    {
        var pointer = CapturePointer(paths);
        var savedDataDir = config.Get(AppSettingsKeys.DataDirMirror)?.DeepClone();
        var savedPrevious = config.Get(AppSettingsKeys.PreviousDataDir)?.DeepClone();

        var target = FreshDirectory(workDirectory, "t74-migration-copy-target");
        var fakeCache = Path.Combine(paths.CacheDir, "t74-fake-cache.bin");
        var movedTo = Path.Combine(paths.DataRoot, "MOVED-TO.txt");

        Directory.CreateDirectory(paths.CacheDir);
        await File.WriteAllBytesAsync(fakeCache, new byte[8192], ct).ConfigureAwait(true);

        try
        {
            var result = await migration.MigrateAsync(
                target,
                includeCaches: false,
                progress: null,
                ct).ConfigureAwait(true);

            var targetConfig = File.Exists(Path.Combine(target, "Config.json"));
            var targetDb = File.Exists(Path.Combine(target, "Library.db"));
            var fakeCopied = File.Exists(Path.Combine(target, "Cache", "t74-fake-cache.bin"));
            var movedOk = File.Exists(movedTo);

            var pointerText = ReadPointer(paths);
            var pointerOk = string.Equals(pointerText, Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase);

            var configDataDir = config.GetString(AppSettingsKeys.DataDirMirror);
            var configPrevious = config.GetString(AppSettingsKeys.PreviousDataDir);

            var dataDirOk = string.Equals(configDataDir, Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase);
            var previousOk = string.Equals(
                configPrevious is null ? null : Path.GetFullPath(configPrevious),
                Path.GetFullPath(paths.DataRoot),
                StringComparison.OrdinalIgnoreCase);

            var ok = result.Success
                     && result.Verified
                     && targetConfig
                     && targetDb
                     && !fakeCopied
                     && pointerOk
                     && movedOk
                     && dataDirOk
                     && previousOk;

            return new SmokeCheck(
                "t74-migration-copy-verify",
                ok,
                $"Success={result.Success}、Verified={result.Verified}、复制 {result.CopiedFiles} 个文件/"
                + $"{result.CopiedBytes} 字节；目标有 Config.json={targetConfig}、Library.db={targetDb}；"
                + $"Cache 假文件未被复制={!fakeCopied}；指针={pointerText ?? "<null>"}；"
                + $"config paths.dataDir={configDataDir ?? "<null>"}、previousDataDir={configPrevious ?? "<null>"}；"
                + $"旧目录留 MOVED-TO.txt={movedOk}");
        }
        finally
        {
            try
            {
                await RestoreEnvironmentAsync(paths, config, pointer, savedDataDir, savedPrevious, ct)
                    .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "迁移复制检查后恢复数据目录失败");
            }

            TryDeleteFile(movedTo);
            TryDeleteFile(fakeCache);
            TryDeleteDirectory(target);
        }
    }

    private static async Task<SmokeCheck> CheckMigrationCancelAsync(
        IDataDirectoryMigrationService migration,
        IConfigStore config,
        AppPaths paths,
        string workDirectory,
        CancellationToken ct)
    {
        var pointer = CapturePointer(paths);
        var savedDataDir = config.Get(AppSettingsKeys.DataDirMirror)?.DeepClone();
        var savedPrevious = config.Get(AppSettingsKeys.PreviousDataDir)?.DeepClone();

        var target = FreshDirectory(workDirectory, "t74-migration-cancel-target");
        var bigDir = Path.Combine(paths.LibraryDir, "t74-cancel-big");
        Directory.CreateDirectory(bigDir);

        // 约 40 MB、约 300 个文件：既占 IO，又用文件数把复制阶段拉长到能稳定取消的窗口。
        for (var i = 0; i < 2; i++)
        {
            var file = Path.Combine(bigDir, "big-" + i + ".bin");

            await using var stream = File.Create(file);
            stream.SetLength(10L * 1024 * 1024);
        }

        var payload = new byte[64 * 1024];

        for (var i = 0; i < 300; i++)
        {
            await File.WriteAllBytesAsync(
                Path.Combine(bigDir, "chunk-" + i.ToString("D3") + ".bin"),
                payload,
                ct).ConfigureAwait(true);
        }

        using var cts = new CancellationTokenSource();

        try
        {
            var task = migration.MigrateAsync(
                target,
                includeCaches: false,
                progress: null,
                cts.Token);

            await Task.Delay(100, ct).ConfigureAwait(true);
            cts.Cancel();

            MigrationResult? result = null;
            var cancelled = false;

            try
            {
                result = await task.ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 实现可能把取消包装成别的异常，只要不是成功就算回滚路径。
                return new SmokeCheck(
                    "t74-migration-cancel-rollback",
                    false,
                    "取消迁移时抛了非 OperationCanceledException：" + ex.GetType().Name + "：" + ex.Message);
            }

            var targetDbExists = File.Exists(Path.Combine(target, "Library.db"));
            var targetEmpty = !Directory.Exists(target) || !Directory.EnumerateFileSystemEntries(target).Any();

            // 失败时把残留条目带进报告，方便判断是"回滚没删干净"还是"测试自己造的东西"。
            var leftovers = Directory.Exists(target)
                ? Directory.EnumerateFileSystemEntries(target, "*", SearchOption.AllDirectories)
                    .Take(6)
                    .Select(entry => Path.GetRelativePath(target, entry))
                    .ToList()
                : new List<string>();

            var pointerText = ReadPointer(paths);
            var pointerOk = string.Equals(pointerText, pointer.Content, StringComparison.OrdinalIgnoreCase)
                            && File.Exists(paths.PointerFile) == pointer.Exists;

            var dataDirOk = string.Equals(
                config.Get(AppSettingsKeys.DataDirMirror)?.ToJsonString(),
                savedDataDir?.ToJsonString(),
                StringComparison.Ordinal);

            var ok = (cancelled || (result is not null && !result.Success))
                     && !targetDbExists
                     && targetEmpty
                     && pointerOk
                     && dataDirOk;

            return new SmokeCheck(
                "t74-migration-cancel-rollback",
                ok,
                $"取消={(cancelled ? "OperationCanceledException" : "Success=false：" + (result?.Error ?? "<无错误信息>"))}；"
                + $"目标无 Library.db={!targetDbExists}、目标为空={targetEmpty}；指针未变={pointerOk}，"
                + $"config paths.dataDir 未变={dataDirOk}"
                + (targetEmpty ? string.Empty : "；残留：" + string.Join("，", leftovers)));
        }
        finally
        {
            try
            {
                await RestoreEnvironmentAsync(paths, config, pointer, savedDataDir, savedPrevious, ct)
                    .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "取消迁移检查后恢复数据目录失败");
            }

            TryDeleteDirectory(bigDir);
            TryDeleteDirectory(target);
        }
    }

    private static async Task<SmokeCheck> CheckMigrationSwitchExistingAsync(
        IDataDirectoryMigrationService migration,
        IConfigStore config,
        AppPaths paths,
        string workDirectory,
        CancellationToken ct)
    {
        var pointer = CapturePointer(paths);
        var savedDataDir = config.Get(AppSettingsKeys.DataDirMirror)?.DeepClone();
        var savedPrevious = config.Get(AppSettingsKeys.PreviousDataDir)?.DeepClone();

        var target = FreshDirectory(workDirectory, "t74-migration-switch-target");

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(target, "Config.json"),
                "{\"ui\":{\"theme\":\"green\"}}",
                ct).ConfigureAwait(true);
            await File.WriteAllBytesAsync(
                Path.Combine(target, "Library.db"),
                new byte[] { 1, 2, 3, 4 },
                ct).ConfigureAwait(true);

            var preflight = await migration.PreflightAsync(target, ct).ConfigureAwait(true);
            var notDataDirCheck = preflight.Checks.FirstOrDefault(check => check.Id == "target-not-data-dir");

            // 目标已经是数据目录：target-not-data-dir 必须为 false，Preflight 不许放行；
            // "直接切换"走 SwitchToExistingAsync（写指针 + Config 镜像）。
            var preflightRefused = notDataDirCheck is { Ok: false } && !preflight.CanMigrate;

            var result = await migration.SwitchToExistingAsync(target, ct).ConfigureAwait(true);

            var pointerText = ReadPointer(paths);
            var pointerOk = string.Equals(pointerText, Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase);

            var configDataDir = config.GetString(AppSettingsKeys.DataDirMirror);
            var configOk = string.Equals(
                configDataDir,
                Path.GetFullPath(target),
                StringComparison.OrdinalIgnoreCase);

            var ok = preflightRefused && result.Success && pointerOk && configOk;

            return new SmokeCheck(
                "t74-migration-switch-existing",
                ok,
                $"已存在数据目录：target-not-data-dir Ok={notDataDirCheck?.Ok}（期望 false）、"
                + $"CanMigrate={preflight.CanMigrate}（期望 false）；"
                + $"SwitchToExisting Success={result.Success}，指针={pointerText ?? "<null>"}，"
                + $"config paths.dataDir={configDataDir ?? "<null>"}");
        }
        finally
        {
            try
            {
                await RestoreEnvironmentAsync(paths, config, pointer, savedDataDir, savedPrevious, ct)
                    .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "切换已有数据目录检查后恢复失败");
            }

            TryDeleteDirectory(target);
        }
    }

    private static async Task<SmokeCheck> CheckMigrationOldDirDeleteAsync(
        IDataDirectoryMigrationService migration,
        IConfigStore config,
        AppPaths paths,
        string workDirectory,
        CancellationToken ct)
    {
        var savedPrevious = config.Get(AppSettingsKeys.PreviousDataDir)?.DeepClone();
        var fakeOld = FreshDirectory(workDirectory, "t74-migration-fake-old");

        try
        {
            await File.WriteAllBytesAsync(
                Path.Combine(fakeOld, "leftover.bin"),
                new byte[4096],
                ct).ConfigureAwait(true);

            await config.SetAsync(
                AppSettingsKeys.PreviousDataDir,
                JsonValue.Create(fakeOld),
                ct).ConfigureAwait(true);
            await config.SaveAsync(ct).ConfigureAwait(true);

            var size = await migration.OldDataSizeAsync(ct).ConfigureAwait(true);
            await migration.DeleteOldDataAsync(ct).ConfigureAwait(true);

            var deleted = !Directory.Exists(fakeOld);
            var keyCleared = string.IsNullOrWhiteSpace(config.GetString(AppSettingsKeys.PreviousDataDir));

            var ok = size > 0 && deleted && keyCleared;

            return new SmokeCheck(
                "t74-migration-old-dir-delete",
                ok,
                $"旧目录占用={size} 字节（期望 >0），删除后目录不存在={deleted}，"
                + $"config paths.previousDataDir 已清空={keyCleared}");
        }
        finally
        {
            try
            {
                await config.SetAsync(
                    AppSettingsKeys.PreviousDataDir,
                    savedPrevious?.DeepClone(),
                    ct).ConfigureAwait(true);
                await config.SaveAsync(ct).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "删旧目录检查后恢复 paths.previousDataDir 失败");
            }

            TryDeleteDirectory(fakeOld);
        }
    }

    /// <summary>独立 UserLocalRoot + 指针文件：AppPaths.Create 应解析出 PointerFile 来源。</summary>
    private static SmokeCheck CheckAppPathsPointer(string workDirectory)
    {
        var root = FreshDirectory(workDirectory, "t74-apppaths-pointer");
        var appRoot = Path.Combine(root, "app");
        var userRoot = Path.Combine(root, "userlocal");
        var dataRoot = Path.Combine(root, "target-data");

        Directory.CreateDirectory(appRoot);
        Directory.CreateDirectory(userRoot);
        Directory.CreateDirectory(dataRoot);

        try
        {
            var pointerFile = Path.Combine(userRoot, AppPaths.PointerFileName);
            File.WriteAllText(pointerFile, dataRoot + Environment.NewLine);

            var paths = AppPaths.Create(new AppPathsOptions
            {
                AppRoot = appRoot,
                UserLocalRoot = userRoot,
            });

            var ok = paths.Source == DataDirSource.PointerFile
                     && string.Equals(paths.DataRoot, Path.GetFullPath(dataRoot), StringComparison.OrdinalIgnoreCase);

            return new SmokeCheck(
                "t74-apppaths-pointer",
                ok,
                $"指针={pointerFile}；解析结果 Source={paths.Source}（期望 PointerFile），"
                + $"DataRoot={paths.DataRoot}（期望 {dataRoot}）");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    // ==================== 4. 维护动作 ====================

    private static async Task<IReadOnlyList<SmokeCheck>> RunMaintenanceChecksAsync(
        IServiceProvider services,
        AppPaths paths,
        CancellationToken ct)
    {
        var checks = new List<SmokeCheck>();
        var maintenance = services.GetRequiredService<IAppMaintenanceService>();

        await AddAsync(checks, "t74-cache-clean", () =>
            CheckCacheCleanAsync(maintenance, paths, ct)).ConfigureAwait(true);
        await AddAsync(checks, "t74-diagnostics-export", () =>
            CheckDiagnosticsExportAsync(maintenance, ct)).ConfigureAwait(true);
        await AddAsync(checks, "t74-immediate-backup", () =>
            CheckImmediateBackupAsync(maintenance, ct)).ConfigureAwait(true);

        return checks;
    }

    private static async Task<SmokeCheck> CheckCacheCleanAsync(
        IAppMaintenanceService maintenance,
        AppPaths paths,
        CancellationToken ct)
    {
        Directory.CreateDirectory(paths.CacheDir);

        var first = Path.Combine(paths.CacheDir, "t74-cache-a.bin");
        var sub = Path.Combine(paths.CacheDir, "nested");
        Directory.CreateDirectory(sub);
        var second = Path.Combine(sub, "t74-cache-b.bin");

        await File.WriteAllBytesAsync(first, new byte[16384], ct).ConfigureAwait(true);
        await File.WriteAllBytesAsync(second, new byte[16384], ct).ConfigureAwait(true);

        var before = await maintenance.GetCacheUsageAsync(ct).ConfigureAwait(true);
        var result = await maintenance.CleanCacheAsync(ct).ConfigureAwait(true);
        var after = await maintenance.GetCacheUsageAsync(ct).ConfigureAwait(true);

        var ok = before.TotalBytes > 0
                 && result.FreedBytes > 0
                 && result.DeletedFiles >= 2
                 && after.TotalBytes == 0;

        return new SmokeCheck(
            "t74-cache-clean",
            ok,
            $"清理前 {before.TotalBytes} 字节/{before.FileCount} 个文件；"
            + $"释放 {result.FreedBytes} 字节、删 {result.DeletedFiles} 个（失败 {result.Failures.Count} 个）；"
            + $"清理后 {after.TotalBytes} 字节/{after.FileCount} 个文件（期望 0）");
    }

    private static async Task<SmokeCheck> CheckDiagnosticsExportAsync(
        IAppMaintenanceService maintenance,
        CancellationToken ct)
    {
        var zipPath = await maintenance.ExportDiagnosticsAsync(ct).ConfigureAwait(true);

        var exists = File.Exists(zipPath);
        var size = exists ? new FileInfo(zipPath).Length : 0;

        if (!exists)
        {
            return new SmokeCheck("t74-diagnostics-export", false, "诊断包没有生成：" + zipPath);
        }

        var entries = new List<string>();

        using (var archive = ZipFile.OpenRead(zipPath))
        {
            entries.AddRange(archive.Entries.Select(entry => entry.FullName));
        }

        var hasConfig = entries.Any(name => name.Contains("config", StringComparison.OrdinalIgnoreCase));
        var hasLogs = entries.Any(name => name.Contains("log", StringComparison.OrdinalIgnoreCase));
        var hasTools = entries.Any(name => name.Contains("tool", StringComparison.OrdinalIgnoreCase));
        var hasDb = entries.Any(name => name.Contains("db", StringComparison.OrdinalIgnoreCase)
                                        || name.Contains("schema", StringComparison.OrdinalIgnoreCase));
        var hasSystem = entries.Any(name => name.Contains("system", StringComparison.OrdinalIgnoreCase));

        var ok = size > 0 && hasConfig && hasLogs && hasTools && hasDb && hasSystem;

        return new SmokeCheck(
            "t74-diagnostics-export",
            ok,
            $"zip={Path.GetFileName(zipPath)}（{size} 字节）；条目 {entries.Count} 个："
            + string.Join("，", entries.Take(12))
            + (entries.Count > 12 ? " …" : string.Empty)
            + $"；config={hasConfig}/logs={hasLogs}/tools={hasTools}/db={hasDb}/system={hasSystem}");
    }

    private static async Task<SmokeCheck> CheckImmediateBackupAsync(
        IAppMaintenanceService maintenance,
        CancellationToken ct)
    {
        var result = await maintenance.CreateImmediateBackupAsync(ct).ConfigureAwait(true);

        var dirOk = Directory.Exists(result.Directory);
        var dbOk = dirOk && File.Exists(Path.Combine(result.Directory, "Library.db"));
        var configOk = dirOk && File.Exists(Path.Combine(result.Directory, "Config.json"));

        var ok = dirOk && dbOk && configOk && result.Bytes > 0 && result.FileCount >= 2;

        return new SmokeCheck(
            "t74-immediate-backup",
            ok,
            $"备份目录={Path.GetFileName(result.Directory)}（存在={dirOk}），"
            + $"Library.db={dbOk}、Config.json={configOk}，"
            + $"{result.FileCount} 个文件/{result.Bytes} 字节（期望 >0）");
    }

    // ==================== 环境快照 / 恢复 ====================

    private sealed record PointerSnapshot(bool Exists, string? Content);

    private static PointerSnapshot CapturePointer(AppPaths paths)
    {
        var exists = File.Exists(paths.PointerFile);
        var content = exists ? File.ReadAllText(paths.PointerFile).Trim() : null;
        return new PointerSnapshot(exists, content);
    }

    private static string? ReadPointer(AppPaths paths)
    {
        return File.Exists(paths.PointerFile)
            ? File.ReadAllText(paths.PointerFile).Trim()
            : null;
    }

    private static async Task RestoreEnvironmentAsync(
        AppPaths paths,
        IConfigStore config,
        PointerSnapshot pointer,
        JsonNode? savedDataDir,
        JsonNode? savedPrevious,
        CancellationToken ct)
    {
        if (pointer.Exists)
        {
            Directory.CreateDirectory(paths.UserLocalRoot);
            await File.WriteAllTextAsync(paths.PointerFile, pointer.Content ?? string.Empty, ct)
                .ConfigureAwait(true);
        }
        else
        {
            TryDeleteFile(paths.PointerFile);
        }

        await config.SetAsync(AppSettingsKeys.DataDirMirror, savedDataDir?.DeepClone(), ct)
            .ConfigureAwait(true);
        await config.SetAsync(AppSettingsKeys.PreviousDataDir, savedPrevious?.DeepClone(), ct)
            .ConfigureAwait(true);
        await config.SaveAsync(ct).ConfigureAwait(true);
    }

    /// <summary>最后再确认一次：指针与 paths.* 已经回到冒烟开始前的状态。</summary>
    private static SmokeCheck CheckEnvironmentRestored(
        AppPaths paths,
        IConfigStore config,
        PointerSnapshot pointer,
        JsonNode? savedDataDir,
        JsonNode? savedPrevious)
    {
        var currentPointerExists = File.Exists(paths.PointerFile);
        var currentPointer = currentPointerExists ? File.ReadAllText(paths.PointerFile).Trim() : null;
        var pointerOk = currentPointerExists == pointer.Exists
                        && string.Equals(currentPointer, pointer.Content, StringComparison.OrdinalIgnoreCase);

        var dataDirOk = string.Equals(
            config.Get(AppSettingsKeys.DataDirMirror)?.ToJsonString(),
            savedDataDir?.ToJsonString(),
            StringComparison.Ordinal);
        var previousOk = string.Equals(
            config.Get(AppSettingsKeys.PreviousDataDir)?.ToJsonString(),
            savedPrevious?.ToJsonString(),
            StringComparison.Ordinal);

        var ok = pointerOk && dataDirOk && previousOk;

        return new SmokeCheck(
            "t74-data-dir-restored",
            ok,
            $"datadir.txt 恢复={pointerOk}（现在 {(currentPointerExists ? currentPointer : "<不存在>")}）；"
            + $"paths.dataDir 恢复={dataDirOk}；paths.previousDataDir 恢复={previousOk}");
    }

    // ==================== 通用工具 ====================

    private static async Task AddAsync(
        List<SmokeCheck> checks,
        string name,
        Func<Task<SmokeCheck>> body)
    {
        try
        {
            checks.Add(await body().ConfigureAwait(true));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Serilog.Log.Error(ex, "T7-4 检查 {Name} 失败", name);
            checks.Add(new SmokeCheck(
                name,
                false,
                "检查抛出异常：" + ex.GetType().Name + "：" + ex.Message));
        }
    }

    /// <summary>在当前 UI 线程上让消息泵转一圈（冒烟里等绑定 / 事件 / 异步写入落定）。</summary>
    private static async Task PumpAsync(DispatcherPriority priority = DispatcherPriority.Background)
        => await Application.Current.Dispatcher.InvokeAsync(() => { }, priority);

    private static async Task<bool> WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();

        while (!condition())
        {
            ct.ThrowIfCancellationRequested();

            if (watch.Elapsed >= timeout)
            {
                return false;
            }

            await PumpAsync().ConfigureAwait(true);
            await Task.Delay(20, ct).ConfigureAwait(true);
        }

        return true;
    }

    private static async Task<TResult> WaitUntilValueAsync<TResult>(
        Func<TResult> read,
        Func<TResult, bool> ready,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var value = read();

        while (!ready(value))
        {
            ct.ThrowIfCancellationRequested();

            if (watch.Elapsed >= timeout)
            {
                return value;
            }

            await PumpAsync().ConfigureAwait(true);
            await Task.Delay(50, ct).ConfigureAwait(true);
            value = read();
        }

        return value;
    }

    private static void RaiseClick(ButtonBase button)
        => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));

    private static bool TrySetText(SettingsPage page, string name, string text, List<string> problems)
    {
        if (page.FindName(name) is not TextBox box)
        {
            problems.Add(name + " 不存在或不是 TextBox");
            return false;
        }

        box.Text = text;
        return true;
    }

    private static bool TrySetCheck(SettingsPage page, string name, bool value, List<string> problems)
    {
        if (page.FindName(name) is not CheckBox check)
        {
            problems.Add(name + " 不存在或不是 CheckBox");
            return false;
        }

        check.IsChecked = value;
        return true;
    }

    private static bool TrySelectComboByName(
        SettingsPage page,
        string name,
        string tag,
        out string selected,
        List<string> problems)
    {
        selected = string.Empty;

        if (page.FindName(name) is not ComboBox combo)
        {
            problems.Add(name + " 不存在或不是 ComboBox");
            return false;
        }

        if (!TrySelectCombo(combo, tag, out selected))
        {
            problems.Add(name + " 里没有 Tag=\"" + tag + "\" 的项（现有：" + DescribeComboItems(combo) + "）");
            return false;
        }

        return true;
    }

    /// <summary>按 Tag 选下拉项；若它已经选中，先清空再选，保证 SelectionChanged 一定触发一次。</summary>
    private static bool TrySelectCombo(ComboBox combo, string tag, out string selected)
    {
        selected = string.Empty;

        foreach (var item in combo.Items)
        {
            var value = GetItemValue(item);

            if (!string.Equals(value, tag, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (ReferenceEquals(combo.SelectedItem, item))
            {
                combo.SelectedIndex = -1;
            }

            combo.SelectedItem = item;
            selected = value ?? tag;
            return true;
        }

        return false;
    }

    private static string? GetSelectedComboValue(ComboBox combo)
        => combo.SelectedItem is null ? null : GetItemValue(combo.SelectedItem);

    /// <summary>取 ComboBoxItem 的 Tag；普通字符串项取自身；绑定对象反射找 Tag/Value/Key/Name。</summary>
    private static string? GetItemValue(object? item)
    {
        switch (item)
        {
            case null:
                return null;
            case ComboBoxItem comboItem:
                return comboItem.Tag?.ToString() ?? comboItem.Content?.ToString();
            case string text:
                return text;
        }

        var type = item.GetType();

        foreach (var propertyName in new[] { "Tag", "Value", "Key", "Name", "Id" })
        {
            var property = type.GetProperty(
                propertyName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);

            if (property is null)
            {
                continue;
            }

            var value = property.GetValue(item)?.ToString();

            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        return item.ToString();
    }

    private static string DescribeComboItems(ComboBox combo)
    {
        var values = combo.Items
            .Cast<object>()
            .Select(GetItemValue)
            .Where(value => !string.IsNullOrEmpty(value))
            .ToList();

        return values.Count == 0 ? "<无有效项>" : string.Join("/", values);
    }

    private static SmokeCheck Fail(string name, List<string> problems)
        => new(name, false, string.Join("；", problems));

    private static string FreshDirectory(string workDirectory, string relative)
    {
        var dir = Path.Combine(workDirectory, relative.Replace('/', Path.DirectorySeparatorChar));

        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }

        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Warning(ex, "冒烟清理文件失败：{Path}", path);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Warning(ex, "冒烟清理目录失败：{Path}", path);
        }
    }
}
