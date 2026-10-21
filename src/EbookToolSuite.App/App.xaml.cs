using System.IO;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using EbookToolSuite.App.Commands;
using EbookToolSuite.App.Interop;
using EbookToolSuite.App.Logging;
using EbookToolSuite.App.Services;
using EbookToolSuite.App.Settings;
using EbookToolSuite.App.Navigation;
using EbookToolSuite.App.Themes;
using EbookToolSuite.App.ViewModels;
using EbookToolSuite.App.Views.Pages;
using EbookToolSuite.App.Smoke;
using EbookToolSuite.App.Views;
using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Engines.Epub;
using EbookToolSuite.Engines.Images;
using EbookToolSuite.Infrastructure.Files;
using EbookToolSuite.Engines.Comics;
using EbookToolSuite.Services.Conversion;
using EbookToolSuite.Services.Library;
using EbookToolSuite.Services.Pdf;
using EbookToolSuite.Services.Comics;
using EbookToolSuite.Reader;
using EbookToolSuite.Reader.WebView;
using EbookToolSuite.Services.Reader;
using EbookToolSuite.Services.Editing;
using EbookToolSuite.Services.Scanning;
using EbookToolSuite.Services.Reading;
using EbookToolSuite.Services.Search;
using EbookToolSuite.Services.Annotations;
using EbookToolSuite.Services.Validation;
using EbookToolSuite.Engines.Processes;
using EbookToolSuite.Infrastructure.Config;
using EbookToolSuite.Infrastructure.Data;
using EbookToolSuite.Infrastructure.Data.Repositories;
using EbookToolSuite.Infrastructure.Logging;
using EbookToolSuite.Infrastructure.SystemIntegration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace EbookToolSuite.App;

public enum RunMode
{
    Normal,
    SmokeWebView,
    SmokeLogging,
    RenderProbe,
    SmokeImport,
    SmokeShell,
    SmokeConvert,
    SmokeConvertCrash,
    SmokeLibrary,
    SmokeReader,
    SmokeVs1A,
    SmokeBatchImport,
    SmokeConversionQueue,
    SmokeVs1B,
    SmokeDuplicates,
    SmokeGrid,
    SmokeEdit,
    SmokeTxt,
    SmokeReaderRefresh,
    SmokeTemplates,
    SmokeQueueCrash,
    SmokeQueueResume,
    SmokeScan,
    SmokeVs2A,
    SmokeT61,
    T62Probe,
    SmokeT62,

    /// <summary>T6-2B：移动端图片通道探针（内存虚拟主机 vs data URL vs 渲染吞吐）。</summary>
    T62BProbe,

    /// <summary>T6-2B：自绘 PDF 引擎验收（判据 ①~⑥）。</summary>
    SmokeT62B,

    /// <summary>T6-3：漫画 CBZ/CBR 验收（判据 ①~⑦）。</summary>
    SmokeT63,

    SmokeT64,

    /// <summary>T6-5：书签与笔记验收（判据 1~7）。</summary>
    SmokeT65,

    /// <summary>T6-6：阶段 6 汇总验收（六条端到端场景）。</summary>
    SmokeT66,

    /// <summary>T7-1：性能达标验收（1000 本首屏、搜索、滚动、大书首屏、取消、句柄、内存）。</summary>
    SmokeT71,

    /// <summary>T7-1：只测一次启动耗时并退出，供脚本跨进程量冷/暖启动。</summary>
    SmokeT71Boot,

    /// <summary>T7-2：错误处理与 16 项边界验收。</summary>
    SmokeT72,

    /// <summary>T7-3：UI 统一与美化验收（主题四档 / 隐式样式 / 窗口 / 对话框 / 阅读器接线）。</summary>
    SmokeT73,

    /// <summary>T7-3B：阅读统计页验收（迁移 v10 / 会话采集 / 聚合口径 / 统计页 / 书库状态持久化）。</summary>
    SmokeT73B,

    /// <summary>T7-4：首次运行引导与设置验收（设置持久化 / 预加载迁移 / 向导 / 数据目录迁移）。</summary>
    SmokeT74,

    /// <summary>T7-5：全量回归（40 个构造样本 × 导入→阅读→转换→编辑→保存→校验）。</summary>
    SmokeT75,
}

public partial class App : Application
{
    public const string SmokeWebViewSwitch = "--smoke-webview";
    public const string SmokeLoggingSwitch = "--smoke-logging";
    public const string RenderProbeOption = "--render-probe=";
    public const string SmokeImportOption = "--smoke-import=";
    public const string SmokeEditOption = "--smoke-edit=";

    /// <summary>T4-5：TXT 端到端冒烟，值是待导入的 .txt 绝对路径。</summary>
    public const string SmokeTxtOption = "--smoke-txt=";

    public const string SmokeConvertOption = "--smoke-convert=";

    public const string SmokeConvertCrashOption = "--smoke-convert-crash=";

    public const string SmokeReaderOption = "--smoke-reader=";
    public const string SmokeReaderRefreshOption = "--smoke-reader-refresh=";
    public const string SmokeVs1AOption = "--smoke-vs1a=";
    public const string SmokeBatchImportOption = "--smoke-batch-import=";
    public const string SmokeConversionQueueOption = "--smoke-convert-queue=";
    public const string SmokeQueueCrashOption = "--smoke-queue-crash=";
    public const string SmokeQueueResumeSwitch = "--smoke-queue-resume";
    public const string SmokeTemplatesEpubOption = "--smoke-templates-epub=";
    public const string SmokeTemplatesBOption = "--smoke-templates-b=";

    public const string SmokeVs1BOption = "--smoke-vs1b=";


    /// <summary>显示诊断页（默认隐藏）。T1-3b 起还会读 Config.ui.showDiagnostics。</summary>
    public const string DebugUiSwitch = "--debug-ui";
    public const string SmokeShellSwitch = "--smoke-shell";
    public const string SmokeDuplicatesSwitch = "--smoke-duplicates";
    public const string SmokeGridSwitch = "--smoke-grid";
    public const string SmokeLibrarySwitch = "--smoke-library";
    public const string SmokeScanOption = "--smoke-scan=";
    public const string SmokeVs2AOption = "--smoke-vs2-a=";
    public const string SmokeT61Option = "--smoke-t61=";

    /// <summary>T6-2 探针（设计文档 §6.5.3 九项）：值是 PDF 样本目录。</summary>
    public const string T62ProbeOption = "--t62-probe=";

    /// <summary>T6-2 验收（判据 1~7）：值是 PDF 样本目录。</summary>
    public const string SmokeT62Option = "--smoke-t62=";

    /// <summary>T6-2B 移动端图片通道探针：值是**一个 PDF 的绝对路径**。</summary>
    public const string T62BProbeOption = "--t62b-probe=";

    /// <summary>T6-2B 验收（判据 ①~⑥）：值是 PDF 样本目录。</summary>
    public const string SmokeT62BOption = "--smoke-t62b=";

    /// <summary>T6-3 验收（判据 ①~⑦）：值是漫画样本目录（不存在时脚本会先建再传）。</summary>
    public const string SmokeT63Option = "--smoke-t63=";

    /// <summary>T6-4 验收：参数值是"样本放到哪个目录"（脚本会先删重建）。</summary>
    public const string SmokeT64Option = "--smoke-t64=";

    /// <summary>T6-4 验收：真实 PDF 的绝对路径（验 T6-2B 转交的"PDF 正文可被全库搜索"）。</summary>
    public const string T64PdfOption = "--t64-pdf=";

    /// <summary>T6-5 验收：样本目录（脚本会先删重建，书与库都从零开始）。</summary>
    public const string SmokeT65Option = "--smoke-t65=";

    /// <summary>T6-6 验收：PDF 样本目录（TestCorpus）。</summary>
    public const string SmokeT66Option = "--smoke-t66=";

    /// <summary>T6-6 验收：MOBI / CBZ 样本根（Data-Test，下面还有 t61-fixtures 与 t63-corpus）。</summary>
    public const string T66FixturesOption = "--t66-fixtures=";

    /// <summary>T7-1 性能验收：样本目录（10 本小书 + 1 本大书）。</summary>
    public const string SmokeT71Option = "--smoke-t71=";

    /// <summary>T7-1 启动耗时探针：参数值是输出 JSON 的绝对路径。</summary>
    public const string SmokeT71BootOption = "--smoke-t71-boot=";

    /// <summary>T7-2 边界验收：参数值是工作目录（脚本会先清空重建）。</summary>
    public const string SmokeT72Option = "--smoke-t72=";

    /// <summary>T7-3 UI 验收：参数值是工作目录（截图落在其 shots 子目录）。</summary>
    public const string SmokeT73Option = "--smoke-t73=";

    /// <summary>T7-3B 统计页验收：参数值是工作目录（截图落在其 shots 子目录）。</summary>
    public const string SmokeT73BOption = "--smoke-t73b=";

    /// <summary>T7-4 首次运行引导与设置验收：参数值是工作目录。</summary>
    public const string SmokeT74Option = "--smoke-t74=";

    /// <summary>T7-5 全量回归：参数值是 40 样本样本目录（里面有 make-test-samples.ps1 生成的 manifest.json）。</summary>
    public const string SmokeT75Option = "--smoke-t75=";

    /// <summary>T7-5 只跑其中一个样本（快速回归脚本用），值是样本 id，如 A01。</summary>
    public const string SmokeT75FilterOption = "--smoke-t75-filter=";

    public const string ScrollSecondsOption = "--scroll-seconds=";
    private const string DataDirPrefix = "--datadir=";

    private IHost? _host;
    private ILoggerFactory? _bootstrapLoggers;
    private GlobalExceptionHandler? _guards;

    /// <summary>T7-4：首次运行向导在第 4 步点了「导入书籍」，等主窗口出来后再弹文件选择框。</summary>
    private bool _pendingImportAfterStartup;

    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>
    /// 从进程开始（App 类型首次被触碰）到主窗口 Loaded 的耗时。
    /// 执行计划 4.3 的冷/暖启动门槛量的就是它（T1-7 的 VS1 验收用）。
    /// </summary>
    private static readonly Stopwatch StartupWatch = Stopwatch.StartNew();

    /// <summary>主窗口可交互时的启动耗时（毫秒）。0 表示还没就绪。</summary>
    public static long StartupMs { get; private set; }

    /// <summary>主窗口首次完成布局时调用。只认第一次，重复调用无效果。</summary>
    public static void MarkStartupComplete()
    {
        if (StartupMs == 0)
        {
            StartupMs = StartupWatch.ElapsedMilliseconds;
        }
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var mode = DetectMode(e.Args);

        // 主题在配置读出来之后才定（要读 ui.theme），但仍然早于任何窗口。见下方 ThemeManager.Initialize()。


        // 冒烟模式可能一个窗口都不建，而 WPF 默认是 OnLastWindowClose，
        // 那样进程会在 OnStartup 返回后立刻退出。必须显式控制。
        if (mode != RunMode.Normal)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }

        var paths = ResolvePaths(e.Args);
        paths.EnsureCreated();

        // 日志必须最先起来，否则启动阶段的失败查不到。
        // T7-4：日志级别来自 Config.json（设置页「高级」组），所以建 logger 之前先读一眼原始文件；
        // 读坏了按默认 Information，绝不因为读设置失败而挡住启动。
        Log.Logger = LoggingSetup.Create(paths, ReadConfiguredLogLevel(paths)).CreateLogger();
        Log.Information(
            "=== 启动 === 模式={Mode} 程序目录={AppRoot} 数据目录={DataRoot}（来源 {Source}）",
            mode,
            paths.AppRoot,
            paths.DataRoot,
            paths.Source);

        _guards = new GlobalExceptionHandler(paths.LogsDir)
        {
            SuppressDialogs = mode != RunMode.Normal,
        };
        _guards.Hook(this);

        // 配置必须在建窗口之前读出来：窗口位置、上次停留的页面、
        // 以及"诊断页是否可见"都依赖它，而诊断页可见性要在 DI 容器构造时就知道。
        // 这里用的是临时的日志工厂：DI 里的 ILogger 这时还不存在，但配置读坏了必须能记下来。
        _bootstrapLoggers = LoggerFactory.Create(b => b.AddSerilog(Log.Logger, dispose: false));
        var config = new JsonConfigStore(paths, _bootstrapLoggers.CreateLogger<JsonConfigStore>());
        await config.InitializeAsync().ConfigureAwait(true);

        // T7-3：主题四档（跟随系统 / 亮 / 暗 / 护眼）在这里生效。
        // 必须在建任何窗口之前，否则窗口会先按 App.xaml 里的浅色资源画出来再跳变。
        ThemeManager.Initialize(config);

        /* T7-4：首次运行判断必须**先取快照**，再写任何配置文件。
           下面的数据目录镜像同步会 SaveAsync（首次启动会创建 Config.json），
           建库会创建 Library.db；晚一步判断就永远看不出"全新目录"了（wizards 队友发现的缺陷）。 */
        var isFirstRun = mode == RunMode.Normal && FirstRunWizardWindow.ShouldShow(paths);

        // T7-4：把数据目录镜像值同步进 Config.json。
        // 权威来源始终是指针文件（设计文档 §4.4）；两者不一致时以指针为准，并在日志里留一条 WARNING。
        await SyncDataDirectoryMirrorAsync(config, paths).ConfigureAwait(true);

        // 诊断页默认隐藏：--debug-ui（开发用）或 Config.ui.showDiagnostics 才显示。
        var showDiagnostics = HasSwitch(e.Args, DebugUiSwitch)
            || config.GetBool("ui.showDiagnostics", false);

        try
        {
            _host = BuildHost(paths, showDiagnostics, config);
            Services = _host.Services;
            await _host.StartAsync().ConfigureAwait(true);
            Log.Information("DI 主机已启动");

            /* T7-4 首次运行向导：数据目录里既没有 Config.json 也没有 Library.db 才出现。
               必须放在建库之前判断——IDatabase.InitializeAsync 会立刻创建 Library.db，
               之后再判断就永远看不出"全新目录"了。
               向导期间临时改成 OnExplicitShutdown：否则向导关掉时它是唯一窗口，
               WPF 会认为"最后一个窗口关了"直接退出，后面的主窗口根本没机会显示。 */
            if (mode == RunMode.Normal
                && !config.GetBool(AppSettingsKeys.FirstRunCompleted, false)
                && isFirstRun)
            {
                var wizard = new FirstRunWizardWindow(
                    paths,
                    config,
                    Services.GetRequiredService<IToolLocator>(),
                    Services.GetRequiredService<IShellLauncher>());

                var previousShutdownMode = ShutdownMode;
                ShutdownMode = ShutdownMode.OnExplicitShutdown;

                try
                {
                    wizard.ShowDialog();
                }
                finally
                {
                    ShutdownMode = previousShutdownMode;
                }

                if (wizard.RestartRequested)
                {
                    // 数据目录换了：路径/配置库/DI 都已经绑在旧目录上，只能重启进程
                    Log.Information("首次运行向导选择了新的数据目录，重启进程生效：{Dir}", wizard.SelectedDataDir);
                    await Log.CloseAndFlushAsync().ConfigureAwait(true);
                    Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true });
                    Shutdown(0);
                    return;
                }

                if (wizard.ImportRequested)
                {
                    _pendingImportAfterStartup = true;
                }
            }

            // 建库 / 迁移必须早于任何窗口与后台任务
            await Services.GetRequiredService<IDatabase>()
                .InitializeAsync()
                .ConfigureAwait(true);

            // 转换队列：先把上次崩溃留下的 Running / WaitingRetry 归位成排队中，
            // 然后接着把它们跑完（Calibre 没有断点续传，所以是从头重转）。
            await Services.GetRequiredService<IConversionQueue>()
                .StartAsync()
                .ConfigureAwait(true);

            // 转换模板：先把用户「另存为」的模板读进内存。失败只记 WRN，
            // 降级成「只有内置模板」，绝不因此挡住启动。
            await Services.GetRequiredService<ConversionTemplateStore>()
                .EnsureLoadedAsync()
                .ConfigureAwait(true);

            // T6-4：正文索引续建。启动时把上次中断（Pending / Indexing）的书捡回来；
            // 真正抽取在后台线程，不挡窗口，也不挡下面各冒烟模式。
            await Services.GetRequiredService<IContentIndexer>()
                .StartAsync()
                .ConfigureAwait(true);

            switch (mode)
            {
                case RunMode.SmokeWebView:
                    await RunWebViewSmokeAsync(paths).ConfigureAwait(true);
                    break;

                case RunMode.SmokeLogging:
                    await RunLoggingSmokeAsync(paths).ConfigureAwait(true);
                    break;

                case RunMode.SmokeLibrary:
                    await RunLibrarySmokeAsync(paths).ConfigureAwait(true);
                    break;

                case RunMode.SmokeReader:
                    await RunReaderSmokeAsync(paths, GetOption(e.Args, SmokeReaderOption)!)
                        .ConfigureAwait(true);
                    break;

                case RunMode.SmokeReaderRefresh:
                    await RunReaderRefreshSmokeAsync(paths, GetOption(e.Args, SmokeReaderRefreshOption)!)
                        .ConfigureAwait(true);
                    break;

                case RunMode.SmokeVs1A:
                    await RunVs1PhaseAAsync(paths, GetOption(e.Args, SmokeVs1AOption)!)
                        .ConfigureAwait(true);
                    break;

                case RunMode.SmokeVs1B:
                    await RunVs1PhaseBAsync(paths, GetOption(e.Args, SmokeVs1BOption)!)
                        .ConfigureAwait(true);
                    break;

                case RunMode.SmokeTemplates:
                    await RunTemplateSmokeAsync(
                        paths,
                        GetOption(e.Args, SmokeTemplatesEpubOption)!,
                        GetOption(e.Args, SmokeTemplatesBOption)!)
                        .ConfigureAwait(true);
                    break;

                case RunMode.SmokeBatchImport:
                    await RunBatchImportSmokeAsync(paths, GetOption(e.Args, SmokeBatchImportOption)!)
                        .ConfigureAwait(true);
                    break;
            case RunMode.SmokeConversionQueue:
                await RunConversionQueueSmokeAsync(paths, GetOption(e.Args, SmokeConversionQueueOption)!)
                    .ConfigureAwait(true);
                break;

            case RunMode.SmokeQueueCrash:
                await RunQueueCrashSmokeAsync(paths, GetOption(e.Args, SmokeQueueCrashOption)!)
                    .ConfigureAwait(true);
                break;

            case RunMode.SmokeQueueResume:
                await RunQueueResumeSmokeAsync(paths).ConfigureAwait(true);
                break;


                case RunMode.SmokeGrid:
                    await RunGridSmokeAsync(paths).ConfigureAwait(true);
                    break;

                case RunMode.SmokeScan:
                    await RunScanSmokeAsync(paths, GetOption(e.Args, SmokeScanOption)!)
                        .ConfigureAwait(true);
                    break;

                case RunMode.SmokeVs2A:
                    await RunVs2PhaseAAsync(
                            paths,
                            GetOption(e.Args, SmokeVs2AOption)!,
                            GetOption(e.Args, ScrollSecondsOption))
                        .ConfigureAwait(true);
                    break;

                case RunMode.SmokeT61:
                    await RunT61SmokeAsync(paths, GetOption(e.Args, SmokeT61Option)!)
                        .ConfigureAwait(true);
                    break;

                case RunMode.SmokeDuplicates:
                    await RunDuplicateSmokeAsync(paths).ConfigureAwait(true);
                    break;

                case RunMode.SmokeShell:
                    await RunShellSmokeAsync(paths).ConfigureAwait(true);
                    break;

                case RunMode.SmokeTxt:
                    await RunTxtSmokeAsync(paths, GetOption(e.Args, SmokeTxtOption)!).ConfigureAwait(true);
                    break;

                case RunMode.SmokeEdit:
                    await RunEditSmokeAsync(paths, GetOption(e.Args, SmokeEditOption)!).ConfigureAwait(true);
                    break;

                case RunMode.SmokeImport:
                    await RunImportSmokeAsync(paths, GetOption(e.Args, SmokeImportOption)!)
                        .ConfigureAwait(true);
                    break;

                case RunMode.SmokeConvert:
                    await RunConvertSmokeAsync(paths, GetOption(e.Args, SmokeConvertOption)!)
                        .ConfigureAwait(true);
                    break;

                case RunMode.SmokeConvertCrash:
                    await RunConvertCrashAsync(paths, GetOption(e.Args, SmokeConvertCrashOption)!)
                        .ConfigureAwait(true);
                    break;

                case RunMode.RenderProbe:
                    await RunRenderProbeAsync(
                        paths, GetOption(e.Args, RenderProbeOption)!)
                        .ConfigureAwait(true);
                    break;

                case RunMode.T62Probe:
                    await RunT62ProbeAsync(paths, GetOption(e.Args, T62ProbeOption)!)
                        .ConfigureAwait(true);
                    break;

                case RunMode.SmokeT62:
                    await RunT62SmokeAsync(paths, GetOption(e.Args, SmokeT62Option)!)
                        .ConfigureAwait(true);
                    break;

                case RunMode.T62BProbe:
                    await RunT62BProbeAsync(paths, GetOption(e.Args, T62BProbeOption)!)
                        .ConfigureAwait(true);
                    break;

                case RunMode.SmokeT62B:
                    await RunT62BSmokeAsync(paths, GetOption(e.Args, SmokeT62BOption)!)
                        .ConfigureAwait(true);
                    break;

                case RunMode.SmokeT63:
                    await RunT63SmokeAsync(paths, GetOption(e.Args, SmokeT63Option)!)
                        .ConfigureAwait(true);
                    break;

                case RunMode.SmokeT64:
                    await RunT64SmokeAsync(
                        paths,
                        GetOption(e.Args, SmokeT64Option)!,
                        GetOption(e.Args, T64PdfOption)).ConfigureAwait(true);
                    break;

                case RunMode.SmokeT65:
                    await RunT65SmokeAsync(paths, GetOption(e.Args, SmokeT65Option)!)
                        .ConfigureAwait(true);
                    break;

                case RunMode.SmokeT66:
                    await RunT66SmokeAsync(
                        paths,
                        GetOption(e.Args, SmokeT66Option)!,
                        GetOption(e.Args, T66FixturesOption) ?? "Data-Test")
                        .ConfigureAwait(true);
                    break;

                case RunMode.SmokeT71:
                    await RunT71SmokeAsync(paths, GetOption(e.Args, SmokeT71Option)!)
                        .ConfigureAwait(true);
                    break;

                case RunMode.SmokeT71Boot:
                    await RunT71BootAsync(paths, GetOption(e.Args, SmokeT71BootOption)!)
                        .ConfigureAwait(true);
                    break;

                case RunMode.SmokeT72:
                    await RunT72SmokeAsync(paths, GetOption(e.Args, SmokeT72Option)!)
                        .ConfigureAwait(true);
                    break;

                case RunMode.SmokeT73:
                    await RunT73SmokeAsync(paths, GetOption(e.Args, SmokeT73Option)!)
                        .ConfigureAwait(true);
                    break;

                case RunMode.SmokeT73B:
                    await RunT73BSmokeAsync(paths, GetOption(e.Args, SmokeT73BOption)!)
                        .ConfigureAwait(true);
                    break;

                case RunMode.SmokeT74:
                    await RunT74SmokeAsync(paths, GetOption(e.Args, SmokeT74Option)!)
                        .ConfigureAwait(true);
                    break;

                case RunMode.SmokeT75:
                    await RunT75SmokeAsync(
                            paths,
                            GetOption(e.Args, SmokeT75Option)!,
                            GetOption(e.Args, SmokeT75FilterOption))
                        .ConfigureAwait(true);
                    break;

                default:
                    // 书库目录监听（T2-6）只在正常模式下开：冒烟模式会自己造/删大量文件，
                    // 让 watcher 来插一脚只会污染性能判据。它只当提示用，起不来也不影响功能。
                    // T7-4：设置页「书库 → 自动扫描」可以关掉它（默认开）。
                    if (config.GetBool(AppSettingsKeys.AutoScan, true))
                    {
                        Services.GetRequiredService<ILibraryWatcher>().Start();
                    }

                    var main = Services.GetRequiredService<MainWindow>();

                    // 窗口显示之前定位，否则会先闪一下默认位置
                    WindowPlacementStore.Restore(main, config, Log.Logger);

                    // 上次停在诊断页、而这次它是隐藏的，SetInitialRoute 会拒绝并落回书库
                    Services.GetRequiredService<ShellViewModel>()
                        .SetInitialRoute(WindowPlacementStore.GetLastRoute(config));

                    main.Show();

                    // T7-4：设置页「常规 → 启动时打开上次的书」（默认关，开了才会自动弹阅读窗口）
                    await TryOpenLastBookAsync(main, config).ConfigureAwait(true);

                    if (_pendingImportAfterStartup)
                    {
                        _pendingImportAfterStartup = false;
                        await Services.GetRequiredService<ImportCommand>()
                            .RunAsync(main)
                            .ConfigureAwait(true);
                    }

                    // T7-4：缓存超过设置页上限时，后台清掉最旧的文件（不挡启动，失败也不报错）
                    _ = EnforceCacheLimitInBackgroundAsync();

                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "启动失败");
            await Log.CloseAndFlushAsync().ConfigureAwait(true);
            Shutdown(1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_host is not null)
            {
                /* 退出前把还没落盘的阅读进度写掉（T1-6）。
                   必须在 StopAsync 之前：容器一停就取不到服务了。 */
                await FlushReadingProgressAsync().ConfigureAwait(true);

                /* T7-3B：会话是 1 分钟心跳落库的，退出时把最后一分钟内的活动补上。 */
                await FlushReadingSessionsAsync().ConfigureAwait(true);

                /* T7-3B 用户需求：检索 / 排序状态平时在防抖窗口结束时写，
                   敲完关键词立刻关程序时最后 200 ms 内的改动会丢，这里补一次。 */
                await FlushLibraryQueryStateAsync().ConfigureAwait(true);

                await _host.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
                _host.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "主机关闭时出错");
        }
        finally
        {
            _bootstrapLoggers?.Dispose();
            Log.Information("=== 退出，代码 {Code} ===", e.ApplicationExitCode);
            await Log.CloseAndFlushAsync().ConfigureAwait(true);
            base.OnExit(e);
        }
    }

    /// <summary>把阅读进度队列里剩下的全部写掉（T1-6）。失败只记日志，绝不阻塞退出。</summary>
    private async Task FlushReadingProgressAsync()
    {
        try
        {
            var progress = _host?.Services.GetService<IReadingProgressService>();

            if (progress is not null)
            {
                await progress.FlushAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "退出前冲刷阅读进度失败");
        }
    }

    /// <summary>把进行中的阅读会话收尾落库（T7-3B）。失败只记日志，绝不阻塞退出。</summary>
    private async Task FlushReadingSessionsAsync()
    {
        try
        {
            var sessions = _host?.Services.GetService<IReadingSessionService>();

            if (sessions is not null)
            {
                await sessions.FlushAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "退出前冲刷阅读会话失败");
        }
    }

    /// <summary>把书库检索 / 排序状态补写落盘（T7-3B 用户需求）。失败只记日志，绝不阻塞退出。</summary>
    private async Task FlushLibraryQueryStateAsync()
    {
        try
        {
            var library = _host?.Services.GetService<LibraryViewModel>();

            if (library is not null)
            {
                await library.FlushQueryStateAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "退出前冲刷书库检索状态失败");
        }
    }

    // ---------- 两个冒烟模式 ----------

    private async Task RunWebViewSmokeAsync(AppPaths paths)
    {
        var reportPath = Path.Combine(paths.TempDir, "webview-smoke.json");
        var window = new SmokeWebViewWindow(paths, reportPath);
        window.Show();
        var report = await window.RunAsync().ConfigureAwait(true);
        window.Close();
        Shutdown(report.Passed ? 0 : 2);
    }

    private async Task RunLoggingSmokeAsync(AppPaths paths)
    {
        var report = await LoggingSmokeTest
            .RunAsync(paths, _guards!, Dispatcher)
            .ConfigureAwait(true);

        Shutdown(report.Passed ? 0 : 2);
    }

    /// <summary>
    /// T0-9 渲染探针：把一个文件直接交给 WebView2 打开，把现象写成 JSON。
    /// 退出码 0 表示探针跑完了（能不能渲染写在报告里），1 表示探针自己炸了。
    /// </summary>
    private async Task RunRenderProbeAsync(AppPaths paths, string targetFile)
    {
        if (!Path.IsPathFullyQualified(targetFile) || !File.Exists(targetFile))
        {
            Log.Error("渲染探针：目标不是绝对路径或文件不存在：{Target}", targetFile);
            await Log.CloseAndFlushAsync().ConfigureAwait(true);
            Shutdown(1);
            return;
        }

        var reportPath = Path.Combine(paths.TempDir, "render-probe.json");
        var window = new RenderProbeWindow(paths, targetFile, reportPath);
        window.Show();
        var report = await window.RunAsync().ConfigureAwait(true);
        window.Close();

        Log.Information(
            "渲染探针结束：{File} 导航成功={Nav} 类型={Type} 正文长度={Len} 触发下载={Dl} 判定={Verdict}",
            Path.GetFileName(targetFile),
            report.NavigationSuccess,
            report.ContentType,
            report.BodyTextLength,
            report.DownloadTriggered,
            report.RenderedAsDocument ? "可渲染" : "不可渲染");

        Shutdown(0);
    }

    /// <summary>
    /// T6-2 验收（判据 1~7）：导入 PDF、开真阅读窗口、走真进度落库、量 100 MB 首屏与内存。
    /// 退出码 0 = 全部通过，2 = 有失败，1 = 跑不起来。
    /// </summary>
    private async Task RunT62SmokeAsync(AppPaths paths, string pdfDirectory)
    {
        if (!Path.IsPathFullyQualified(pdfDirectory) || !Directory.Exists(pdfDirectory))
        {
            Log.Error("T6-2 验收：样本目录不是绝对路径或不存在：{Dir}", pdfDirectory);
            await Log.CloseAndFlushAsync().ConfigureAwait(true);
            Shutdown(1);
            return;
        }

        var report = await Smoke.T62SmokeTest.RunAsync(
            Services.GetRequiredService<IBookImportService>(),
            Services.GetRequiredService<IReaderService>(),
            Services.GetRequiredService<IBookRepository>(),
            Services.GetRequiredService<IWebViewEnvironmentProvider>(),
            Services.GetRequiredService<IReadingProgressService>(),
            Services.GetRequiredService<IPdfDocumentService>(),
            Services.GetRequiredService<ILoggerFactory>(),
            paths,
            pdfDirectory).ConfigureAwait(true);

        foreach (var check in report.Checks)
        {
            Log.Information("T6-2 验收 {State} {Name} -> {Detail}",
                check.Ok ? "OK  " : "FAIL", check.Name, check.Detail);
        }

        var passed = report.Checks.Count(c => c.Ok);
        Log.Information("T6-2 验收结束：{Passed}/{Total} 通过，用时 {Ms} ms",
            passed, report.Checks.Count, report.DurationMs);

        await Log.CloseAndFlushAsync().ConfigureAwait(true);
        Shutdown(report.Passed ? 0 : 2);
    }

    /// <summary>
    /// T6-2B 验收（判据 ①~⑥）：自绘 PDF 引擎的六条判据一次跑完。
    /// 退出码 0 = 全部通过，2 = 有失败（含判据④⑤两条"未验收，已转交"），1 = 跑不起来。
    /// </summary>
    private async Task RunT62BSmokeAsync(AppPaths paths, string pdfDirectory)
    {
        if (!Path.IsPathFullyQualified(pdfDirectory) || !Directory.Exists(pdfDirectory))
        {
            Log.Error("T6-2B 验收：样本目录不是绝对路径或不存在：{Dir}", pdfDirectory);
            await Log.CloseAndFlushAsync().ConfigureAwait(true);
            Shutdown(1);
            return;
        }

        var report = await Smoke.T62BSmokeTest.RunAsync(
            Services.GetRequiredService<IBookImportService>(),
            Services.GetRequiredService<IReaderService>(),
            Services.GetRequiredService<IBookRepository>(),
            Services.GetRequiredService<IWebViewEnvironmentProvider>(),
            Services.GetRequiredService<IReadingProgressService>(),
            Services.GetRequiredService<IPdfDocumentService>(),
            Services.GetRequiredService<ILoggerFactory>(),
            paths,
            pdfDirectory).ConfigureAwait(true);

        foreach (var check in report.Checks)
        {
            Log.Information("T6-2B 验收 {State} {Name} -> {Detail}",
                check.Ok ? "OK  " : "FAIL", check.Name, check.Detail);
        }

        var passed = report.Checks.Count(c => c.Ok);
        Log.Information("T6-2B 验收结束：{Passed}/{Total} 通过，用时 {Ms} ms",
            passed, report.Checks.Count, report.DurationMs);

        await Log.CloseAndFlushAsync().ConfigureAwait(true);
        Shutdown(report.Passed ? 0 : 2);
    }

    /// <summary>
    /// T6-4 验收：全文搜索（FTS5）的八条判据端到端。
    /// 退出码 0 = 全部通过，2 = 有判据不成立（报告里逐条写明），1 = 跑不起来。
    /// </summary>
    private async Task RunT64SmokeAsync(AppPaths paths, string corpusDirectory, string? pdfPath)
    {
        if (!Path.IsPathFullyQualified(corpusDirectory))
        {
            Log.Error("T6-4 验收：样本目录不是绝对路径：{Dir}", corpusDirectory);
            await Log.CloseAndFlushAsync().ConfigureAwait(true);
            Shutdown(1);
            return;
        }

        Directory.CreateDirectory(corpusDirectory);

        var report = await Smoke.T64SmokeTest.RunAsync(
            Services.GetRequiredService<IImportQueue>(),
            Services.GetRequiredService<IBookImportService>(),
            Services.GetRequiredService<IReaderService>(),
            Services.GetRequiredService<IBookRepository>(),
            Services.GetRequiredService<IWebViewEnvironmentProvider>(),
            Services.GetRequiredService<IReadingProgressService>(),
            Services.GetRequiredService<IContentIndexer>(),
            Services.GetRequiredService<IContentSearchService>(),
            Services.GetRequiredService<IContentRepository>(),
            Services.GetRequiredService<IPdfDocumentService>(),
            Services.GetRequiredService<ILoggerFactory>(),
            paths,
            corpusDirectory,
            pdfPath,
            Services.GetRequiredService<Views.Pages.LibraryPage>()).ConfigureAwait(true);

        foreach (var check in report.Checks)
        {
            Log.Information("T6-4 验收 {State} {Name} -> {Detail}",
                check.Ok ? "OK  " : "FAIL", check.Name, check.Detail);
        }

        var passed = report.Checks.Count(c => c.Ok);
        Log.Information("T6-4 验收结束：{Passed}/{Total} 通过，用时 {Ms} ms",
            passed, report.Checks.Count, report.DurationMs);

        await Log.CloseAndFlushAsync().ConfigureAwait(true);
        Shutdown(report.Passed ? 0 : 2);
    }

    private async Task RunT65SmokeAsync(AppPaths paths, string corpusDirectory)
    {
        if (!Path.IsPathFullyQualified(corpusDirectory))
        {
            Log.Error("T6-5 验收：样本目录不是绝对路径：{Dir}", corpusDirectory);
            await Log.CloseAndFlushAsync().ConfigureAwait(true);
            Shutdown(1);
            return;
        }

        Directory.CreateDirectory(corpusDirectory);

        var report = await Smoke.T65SmokeTest.RunAsync(
            Services.GetRequiredService<IBookImportService>(),
            Services.GetRequiredService<IReaderService>(),
            Services.GetRequiredService<IBookRepository>(),
            Services.GetRequiredService<IWebViewEnvironmentProvider>(),
            Services.GetRequiredService<IReadingProgressService>(),
            Services.GetRequiredService<IAnnotationService>(),
            Services.GetRequiredService<ILoggerFactory>(),
            paths,
            corpusDirectory).ConfigureAwait(true);

        foreach (var check in report.Checks)
        {
            Log.Information("T6-5 验收 {State} {Name} -> {Detail}",
                check.Ok ? "OK  " : "FAIL", check.Name, check.Detail);
        }

        var passed = report.Checks.Count(c => c.Ok);
        Log.Information("T6-5 验收结束：{Passed}/{Total} 通过，用时 {Ms} ms",
            passed, report.Checks.Count, report.DurationMs);

        await Log.CloseAndFlushAsync().ConfigureAwait(true);
        Shutdown(report.Passed ? 0 : 2);
    }


    /// <summary>
    /// T6-6 阶段 6 汇总验收：六条端到端场景（MOBI 缓存 / PDF / CBZ / 搜索 / 书签 / 笔记）。
    /// 退出码 0 = 全部通过，2 = 有检查不成立（报告逐条写明），1 = 跑不起来。
    /// </summary>
    private async Task RunT66SmokeAsync(AppPaths paths, string pdfDir, string fixturesRoot)
    {
        if (!Path.IsPathFullyQualified(pdfDir) || !Path.IsPathFullyQualified(fixturesRoot))
        {
            Log.Error("T6-6 验收：样本目录必须是绝对路径：pdf={Pdf} fixtures={Fixtures}", pdfDir, fixturesRoot);
            await Log.CloseAndFlushAsync().ConfigureAwait(true);
            Shutdown(1);
            return;
        }

        var report = await Smoke.T66SmokeTest.RunAsync(
            Services.GetRequiredService<IBookImportService>(),
            Services.GetRequiredService<IReaderService>(),
            Services.GetRequiredService<IBookRepository>(),
            Services.GetRequiredService<IComicBookService>(),
            Services.GetRequiredService<IContentIndexer>(),
            Services.GetRequiredService<IContentSearchService>(),
            Services.GetRequiredService<IAnnotationService>(),
            Services.GetRequiredService<IReadingProgressService>(),
            Services.GetRequiredService<ReadCommand>(),
            Services.GetRequiredService<ILoggerFactory>(),
            paths,
            pdfDir,
            fixturesRoot).ConfigureAwait(true);

        foreach (var check in report.Checks)
        {
            Log.Information("T6-6 验收 {State} {Name} -> {Detail}",
                check.Ok ? "OK  " : "FAIL", check.Name, check.Detail);
        }

        var passed = report.Checks.Count(c => c.Ok);
        Log.Information("T6-6 验收结束：{Passed}/{Total} 通过，用时 {Ms} ms",
            passed, report.Checks.Count, report.DurationMs);

        await Log.CloseAndFlushAsync().ConfigureAwait(true);
        Shutdown(report.Passed ? 0 : 2);
    }

    /// <summary>
    /// T7-1 启动耗时探针：与正常启动走同一条启动路径（配置、DI、建库、队列、索引），
    /// 主窗口 Loaded 后把 <see cref="App.StartupMs"/> 写成 JSON 再退出。
    /// 脚本用全新数据目录跑一次（冷启动）、同一目录再跑 20 次（暖启动）取分位。
    /// </summary>
    private async Task RunT71BootAsync(AppPaths paths, string outputPath)
    {
        if (!Path.IsPathFullyQualified(outputPath))
        {
            Log.Error("T7-1 启动探针：输出路径不是绝对路径：{Path}", outputPath);
            Shutdown(1);
            return;
        }

        // 与正常启动一致：先恢复窗口位置与上次停留页面，再 Show。
        var config = Services.GetRequiredService<IConfigStore>();
        var main = Services.GetRequiredService<MainWindow>();

        WindowPlacementStore.Restore(main, config, Log.Logger);
        Services.GetRequiredService<ShellViewModel>()
            .SetInitialRoute(WindowPlacementStore.GetLastRoute(config));

        main.Show();

        var deadline = DateTimeOffset.Now.AddSeconds(30);
        while (App.StartupMs == 0 && DateTimeOffset.Now < deadline)
        {
            await Task.Delay(10).ConfigureAwait(true);
        }

        var loaded = App.StartupMs > 0;

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(
            outputPath,
            JsonSerializer.Serialize(new
            {
                startupMs = App.StartupMs,
                loaded,
                at = DateTimeOffset.Now.ToString("O"),
            }),
            new UTF8Encoding(false));

        Log.Information(
            "T7-1 启动探针：{Ms} ms（loaded={Loaded}）-> {Path}",
            App.StartupMs,
            loaded,
            outputPath);

        main.Close();
        await Log.CloseAndFlushAsync().ConfigureAwait(true);
        Shutdown(loaded ? 0 : 1);
    }

    /// <summary>
    /// T7-1 性能验收：1000 本首屏、搜索、60 秒滚动、大 EPUB 首屏、
    /// 20 次取消的响应/残留/句柄增长、连续读 10 本的峰值工作集。
    /// 冷/暖启动由脚本跨进程另测。
    /// </summary>
    private async Task RunT71SmokeAsync(AppPaths paths, string fixturesDirectory)
    {
        if (!Path.IsPathFullyQualified(fixturesDirectory) || !Directory.Exists(fixturesDirectory))
        {
            Log.Error("T7-1 性能验收：样本目录不存在或不是绝对路径：{Dir}", fixturesDirectory);
            await Log.CloseAndFlushAsync().ConfigureAwait(true);
            Shutdown(1);
            return;
        }

        var report = await Smoke.T71SmokeTest
            .RunAsync(Services, paths, fixturesDirectory)
            .ConfigureAwait(true);

        LogChecks("T7-1 性能", report);

        await Log.CloseAndFlushAsync().ConfigureAwait(true);
        Shutdown(report.Passed ? 0 : 2);
    }

    /// <summary>
    /// T7-2 验收：错误处理与 16 项边界逐条造场景。
    /// 退出码 0 = 全部通过，2 = 有项目不成立（报告里逐条写明），1 = 跑不起来。
    /// </summary>
    /// <summary>
    /// T7-3 UI 统一与美化验收：主题四档 / 隐式样式 / 窗口与对话框 / 设置页 / 阅读器接线，
    /// 并给三套主题各截一张主窗口图。退出码 0 = 全过，2 = 有项目不成立，1 = 跑不起来。
    /// </summary>
    private async Task RunT73SmokeAsync(AppPaths paths, string workDirectory)
    {
        if (!Path.IsPathFullyQualified(workDirectory))
        {
            Log.Error("T7-3 验收：工作目录不是绝对路径：{Dir}", workDirectory);
            await Log.CloseAndFlushAsync().ConfigureAwait(true);
            Shutdown(1);
            return;
        }

        Directory.CreateDirectory(workDirectory);

        var report = await Smoke.T73SmokeTest
            .RunAsync(Services, paths, workDirectory)
            .ConfigureAwait(true);

        LogChecks("T7-3 UI 统一与美化", report);

        await Log.CloseAndFlushAsync().ConfigureAwait(true);
        Shutdown(report.Passed ? 0 : 2);
    }

    /// <summary>
    /// T7-3B 阅读统计页验收：迁移 v10 / 会话采集 / 聚合口径 / 统计页 UI / 书库检索与排序状态持久化。
    /// 退出码 0 = 全过，2 = 有项目不成立（报告里逐条写明），1 = 跑不起来。
    /// </summary>
    private async Task RunT73BSmokeAsync(AppPaths paths, string workDirectory)
    {
        if (!Path.IsPathFullyQualified(workDirectory))
        {
            Log.Error("T7-3B 验收：工作目录不是绝对路径：{Dir}", workDirectory);
            await Log.CloseAndFlushAsync().ConfigureAwait(true);
            Shutdown(1);
            return;
        }

        Directory.CreateDirectory(workDirectory);

        var report = await Smoke.T73BSmokeTest
            .RunAsync(Services, paths, workDirectory)
            .ConfigureAwait(true);

        LogChecks("T7-3B 阅读统计页", report);

        await Log.CloseAndFlushAsync().ConfigureAwait(true);
        Shutdown(report.Passed ? 0 : 2);
    }

    /// <summary>
    /// T7-4 首次运行引导与设置验收：8 组设置持久化 / 预加载挪到设置页并夹 1~1000 /
    /// 书库视图双向同步 / 首次运行向导 / 数据目录迁移（复制+校验+取消回滚+切换指针）。
    /// 退出码 0 = 全过，2 = 有项目不成立，1 = 跑不起来。
    /// </summary>
    private async Task RunT74SmokeAsync(AppPaths paths, string workDirectory)
    {
        if (!Path.IsPathFullyQualified(workDirectory))
        {
            Log.Error("T7-4 验收：工作目录不是绝对路径：{Dir}", workDirectory);
            await Log.CloseAndFlushAsync().ConfigureAwait(true);
            Shutdown(1);
            return;
        }

        Directory.CreateDirectory(workDirectory);

        var report = await Smoke.T74SmokeTest
            .RunAsync(Services, paths, workDirectory)
            .ConfigureAwait(true);

        LogChecks("T7-4 首次运行引导与设置", report);

        await Log.CloseAndFlushAsync().ConfigureAwait(true);
        Shutdown(report.Passed ? 0 : 2);
    }

    /// <summary>
    /// T7-5 全量回归：40 个构造样本逐个跑「导入 -> 阅读 -> 转换 -> 编辑 -> 保存 -> 校验」。
    /// 样本目录必须是绝对路径，且里面有 build\make-test-samples.ps1 生成的 manifest.json。
    /// 退出码 0 = 全部样本跑通，2 = 有样本失败，1 = 跑不起来。
    /// </summary>
    private async Task RunT75SmokeAsync(AppPaths paths, string corpusDirectory, string? onlySampleId)
    {
        if (!Path.IsPathFullyQualified(corpusDirectory) || !Directory.Exists(corpusDirectory))
        {
            Log.Error("T7-5 回归：样本目录必须是存在的绝对路径：{Dir}", corpusDirectory);
            await Log.CloseAndFlushAsync().ConfigureAwait(true);
            Shutdown(1);
            return;
        }

        var report = await Smoke.T75RegressionTest
            .RunAsync(
                Services,
                paths,
                corpusDirectory,
                onlySampleId,
                Services.GetRequiredService<ILoggerFactory>().CreateLogger("T7-5"))
            .ConfigureAwait(true);

        LogChecks("T7-5 全量回归", report);

        await Log.CloseAndFlushAsync().ConfigureAwait(true);
        Shutdown(report.Passed ? 0 : 2);
    }

    /// <summary>
    /// 建 logger 之前读一眼 Config.json 里的日志级别（设置页「高级」组写进去的）。
    /// 读不到 / 读坏了 / 值不认识都按 Information，绝不挡住启动。
    /// </summary>
    private static LogEventLevel ReadConfiguredLogLevel(AppPaths paths)
    {
        try
        {
            if (!File.Exists(paths.ConfigFile))
            {
                return LogEventLevel.Information;
            }

            using var doc = JsonDocument.Parse(
                File.ReadAllText(paths.ConfigFile),
                new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });

            if (doc.RootElement.TryGetProperty("advanced", out var advanced)
                && advanced.TryGetProperty("logLevel", out var level)
                && level.ValueKind == JsonValueKind.String
                && Enum.TryParse<LogEventLevel>(level.GetString(), ignoreCase: true, out var parsed))
            {
                return parsed;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 配置读坏了不该影响日志：用默认级别继续
        }

        return LogEventLevel.Information;
    }

    /// <summary>
    /// 把数据目录镜像写进 Config.json（设计文档 §4.4：指针文件才是权威）。
    /// 镜像与指针不一致时打 WARNING 并以指针为准。
    /// </summary>
    private static async Task SyncDataDirectoryMirrorAsync(IConfigStore config, AppPaths paths)
    {
        var actual = Path.GetFullPath(paths.DataRoot);
        var mirror = config.GetString(AppSettingsKeys.DataDirMirror, null);

        if (!string.IsNullOrWhiteSpace(mirror))
        {
            try
            {
                if (string.Equals(Path.GetFullPath(mirror), actual, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                Log.Warning(
                    "Config.json 的数据目录镜像与指针不一致，以指针为准：镜像={Mirror}，实际={Actual}",
                    mirror,
                    actual);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                Log.Warning(ex, "Config.json 的数据目录镜像不是合法路径，忽略：{Mirror}", mirror);
            }
        }

        try
        {
            await config.SetAsync(AppSettingsKeys.DataDirMirror, JsonValue.Create(actual)).ConfigureAwait(true);
            await config.SaveAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "数据目录镜像没能写进 Config.json（不影响使用）");
        }
    }

    /// <summary>
    /// T7-4：设置页「常规 → 启动时打开上次的书」。默认关；书已经被删就静默跳过。
    /// </summary>
    private static async Task TryOpenLastBookAsync(MainWindow main, IConfigStore config)
    {
        try
        {
            if (!config.GetBool(AppSettingsKeys.OpenLastBook, false))
            {
                return;
            }

            if (!Guid.TryParse(config.GetString(AppSettingsKeys.LastBookId, null), out var bookId))
            {
                return;
            }

            var book = await Services.GetRequiredService<IBookRepository>()
                .GetByIdAsync(bookId)
                .ConfigureAwait(true);

            if (book is null)
            {
                // 书被删了：清掉记录，下次启动不用白查一次
                await config.SetAsync(AppSettingsKeys.LastBookId, null).ConfigureAwait(true);
                await config.SaveAsync().ConfigureAwait(true);
                return;
            }

            Services.GetRequiredService<ReadCommand>().Run(main, book);

            Log.Information("启动时打开上次的书：{Title}（{BookId}）", book.Title, book.Id);
        }
        catch (Exception ex)
        {
            // 自动打开失败不能影响主界面使用
            Log.Warning(ex, "启动时打开上次的书失败（不影响正常使用）");
        }
    }

    /// <summary>T7-4：缓存超上限时后台清最旧的文件，失败只记日志。</summary>
    private static async Task EnforceCacheLimitInBackgroundAsync()
    {
        try
        {
            await Services.GetRequiredService<IAppMaintenanceService>()
                .EnforceCacheLimitAsync()
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "缓存超限清理失败（不影响使用）");
        }
    }

    private async Task RunT72SmokeAsync(AppPaths paths, string workDirectory)
    {
        if (!Path.IsPathFullyQualified(workDirectory))
        {
            Log.Error("T7-2 验收：工作目录不是绝对路径：{Dir}", workDirectory);
            await Log.CloseAndFlushAsync().ConfigureAwait(true);
            Shutdown(1);
            return;
        }

        Directory.CreateDirectory(workDirectory);

        var report = await Smoke.T72SmokeTest
            .RunAsync(Services, paths, workDirectory)
            .ConfigureAwait(true);

        LogChecks("T7-2 错误处理与边界", report);

        await Log.CloseAndFlushAsync().ConfigureAwait(true);
        Shutdown(report.Passed ? 0 : 2);
    }

    /// <summary>
    /// T6-3 验收：漫画 CBZ 的六条判据 + 一条"缓存不落盘"。
    /// 退出码 0 = 全部检查通过，2 = 有检查不成立（报告里逐条写明），1 = 跑不起来。
    /// </summary>
    private async Task RunT63SmokeAsync(AppPaths paths, string corpusDirectory)
    {
        if (!Path.IsPathFullyQualified(corpusDirectory))
        {
            Log.Error("T6-3 验收：样本目录不是绝对路径：{Dir}", corpusDirectory);
            await Log.CloseAndFlushAsync().ConfigureAwait(true);
            Shutdown(1);
            return;
        }

        Directory.CreateDirectory(corpusDirectory);

        var report = await Smoke.T63SmokeTest.RunAsync(
            Services.GetRequiredService<IBookImportService>(),
            Services.GetRequiredService<IReaderService>(),
            Services.GetRequiredService<IWebViewEnvironmentProvider>(),
            Services.GetRequiredService<IReadingProgressService>(),
            Services.GetRequiredService<IComicBookService>(),
            Services.GetRequiredService<ILoggerFactory>(),
            paths,
            corpusDirectory).ConfigureAwait(true);

        foreach (var check in report.Checks)
        {
            Log.Information("T6-3 验收 {State} {Name} -> {Detail}",
                check.Ok ? "OK  " : "FAIL", check.Name, check.Detail);
        }

        var passed = report.Checks.Count(c => c.Ok);
        Log.Information("T6-3 验收结束：{Passed}/{Total} 通过，用时 {Ms} ms",
            passed, report.Checks.Count, report.DurationMs);

        await Log.CloseAndFlushAsync().ConfigureAwait(true);
        Shutdown(report.Passed ? 0 : 2);
    }

    /// <summary>
    /// T6-2 探针：设计文档 §6.5.3 的九项一次跑完，并给每一步存一张 PNG。
    /// 退出码 0 = 九项都跑出了结论（结论本身可能是"无效/置灰"，那也是合格结论），
    /// 2 = 有项没跑出来，1 = 跑不起来。
    /// </summary>
    private async Task RunT62ProbeAsync(AppPaths paths, string pdfDirectory)
    {
        if (!Path.IsPathFullyQualified(pdfDirectory) || !Directory.Exists(pdfDirectory))
        {
            Log.Error("T6-2 探针：样本目录不是绝对路径或不存在：{Dir}", pdfDirectory);
            await Log.CloseAndFlushAsync().ConfigureAwait(true);
            Shutdown(1);
            return;
        }

        var reportPath = Path.Combine(paths.TempDir, "t62-probe.json");
        var markdownPath = Path.Combine(paths.TempDir, "t62-probe.md");

        var window = new T62ProbeWindow(paths, pdfDirectory, reportPath, markdownPath);
        window.Show();

        var report = await window.RunAsync().ConfigureAwait(true);
        window.Close();

        foreach (var finding in report.Findings)
        {
            Log.Information(
                "T6-2 探针 {Id} 判定={Verdict} 分支={Branch}",
                finding.Id, finding.Verdict, finding.Branch);
        }

        Shutdown(report.Passed ? 0 : 2);
    }

    /// <summary>
    /// T6-2B 移动端图片通道探针：内存虚拟主机通道 / postMessage data URL 通道 /
    /// 渲染吞吐三问一次跑完，结果写 Data\Temp\t62b-probe.json。
    /// 退出码 0 = 三问都拿到了数字，2 = 有断言不成立，1 = 跑不起来。
    /// </summary>
    private async Task RunT62BProbeAsync(AppPaths paths, string pdfPath)
    {
        if (!Path.IsPathFullyQualified(pdfPath) || !File.Exists(pdfPath))
        {
            Log.Error("T6-2B 探针：PDF 不是绝对路径或不存在：{Path}", pdfPath);
            await Log.CloseAndFlushAsync().ConfigureAwait(true);
            Shutdown(1);
            return;
        }

        var report = await Smoke.T62BProbeTest.RunAsync(
            paths,
            pdfPath,
            Services.GetRequiredService<IPdfDocumentService>(),
            Services.GetRequiredService<IWebViewEnvironmentProvider>()).ConfigureAwait(true);

        Log.Information(
            "T6-2B 问题1 内存虚拟主机：{Verdict}", report.Memory?.Verdict ?? "未测到");
        Log.Information(
            "T6-2B 问题2 data URL（屏幕外）：{Verdict}", report.DataUrlOffScreen?.Verdict ?? "未测到");
        Log.Information(
            "T6-2B 问题2 data URL（屏幕内）：{Verdict}", report.DataUrlOnScreen?.Verdict ?? "未测到");
        Log.Information(
            "T6-2B 问题2 对照（纯洁环境）：{Version} IsWebMessageEnabled={Enabled} 宿主收到回信={Ready} " +
            "页面侧统计={State}",
            report.PlainEnv?.BrowserVersion ?? "未测到",
            report.PlainEnv?.IsWebMessageEnabled,
            report.PlainEnv?.ReadySignalReceived,
            report.PlainEnv?.PageReportedState ?? "未测到");
        Log.Information(
            "T6-2B 问题3 渲染：{Count} 页 min/avg/max={Min}/{Avg}/{Max} ms；重复渲染 {Repeat}；扫描件 {Scan}",
            report.Pages.Count,
            report.PageMinMs,
            report.PageAvgMs,
            report.PageMaxMs,
            report.Repeat?.Verdict ?? "未测到",
            report.Scan is null
                ? "未测到"
                : $"1600={report.Scan.LongEdge1600.Ms} ms/{report.Scan.LongEdge1600.Bytes} B，" +
                  $"2400={report.Scan.LongEdge2400.Ms} ms/{report.Scan.LongEdge2400.Bytes} B");

        foreach (var note in report.Notes)
        {
            Log.Information("T6-2B 备注：{Note}", note);
        }

        await Log.CloseAndFlushAsync().ConfigureAwait(true);

        Shutdown(report.Passed ? 0 : 2);
    }

    /// <summary>
    /// T1-3a 外壳与导航冒烟：六个路由、页面缓存、未知路由回落、历史回退、
    /// 页面异常被兜住、诊断页可见性、窄窗折叠、手动折叠优先。
    /// 视觉项（六态观感、ToolTip 延迟、焦点环）只能人眼验，报告里会注明。
    /// </summary>
    /// <summary>
    /// T1-3 书库网格冒烟：50 本样本 + 封面加载 + 双击详情 + 滚动位置 + 删封面目录，
    /// 报告写到 Data/Temp/library-smoke.json。退出码 0 全过，2 有失败，1 跑不起来。
    /// </summary>
    /// <summary>
    /// T2-6 文件监控与缺失检测冒烟：删文件 / 改文件 / 改回来 / 1000 本扫描性能。
    /// 报告写到 Data/Temp/scan-smoke.json 供脚本判定。
    /// </summary>
    /// <summary>
    /// T2-7（VS2）第 1 阶段：造 1000 本样本 -> 批量导入 -> 首屏 -> 真滚 N 秒 -> 搜索。
    /// 报告写到 Data/Temp/vs2-smoke.json 供脚本判定。滚动秒数默认 60（验收口径），
    /// 回归时可以传小一点的值。
    /// </summary>
    private async Task RunVs2PhaseAAsync(AppPaths paths, string templateEpub, string? scrollSeconds)
    {
        var window = Services.GetRequiredService<MainWindow>();
        var page = Services.GetRequiredService<LibraryPage>();
        var seconds = 60;

        if (int.TryParse(scrollSeconds, out var parsed) && parsed > 0)
        {
            seconds = parsed;
        }

        var report = await Smoke.Vs2SmokeTest
            .RunPhaseAAsync(Services, paths, window, page, templateEpub, seconds)
            .ConfigureAwait(true);

        foreach (var check in report.Checks)
        {
            Log.Information("VS2 冒烟 {State} {Name} -> {Detail}",
                check.Ok ? "OK  " : "FAIL", check.Name, check.Detail);
        }

        var passed = report.Checks.Count(c => c.Ok);
        Log.Information("VS2 冒烟结束：{Passed}/{Total} 通过，用时 {Ms} ms",
            passed, report.Checks.Count, report.DurationMs);

        Shutdown(report.Passed ? 0 : 2);
    }
    private async Task RunScanSmokeAsync(AppPaths paths, string sourceFile)
    {
        var window = Services.GetRequiredService<MainWindow>();
        var page = Services.GetRequiredService<LibraryPage>();
        var repo = Services.GetRequiredService<IBookRepository>();
        var import = Services.GetRequiredService<IBookImportService>();
        var scan = Services.GetRequiredService<ILibraryScanService>();

        var report = await Smoke.LibraryScanSmokeTest
            .RunAsync(paths, window, page, repo, import, scan, sourceFile)
            .ConfigureAwait(true);

        foreach (var check in report.Checks)
        {
            Log.Information("扫描冒烟 {State} {Name} -> {Detail}",
                check.Ok ? "OK  " : "FAIL", check.Name, check.Detail);
        }

        var passed = report.Checks.Count(c => c.Ok);
        Log.Information("扫描冒烟结束：{Passed}/{Total} 通过，用时 {Ms} ms",
            passed, report.Checks.Count, report.DurationMs);

        Shutdown(report.Passed ? 0 : 2);
    }
    private async Task RunLibrarySmokeAsync(AppPaths paths)
    {
        var window = Services.GetRequiredService<MainWindow>();
        var nav = Services.GetRequiredService<NavigationService>();
        var registry = Services.GetRequiredService<PageRegistry>();
        var repo = Services.GetRequiredService<IBookRepository>();
        var conversion = Services.GetRequiredService<IConversionService>();

        var report = await Smoke.LibrarySmokeTest
            .RunAsync(window, nav, registry, paths, repo, conversion)
            .ConfigureAwait(true);

        foreach (var check in report.Checks)
        {
            Log.Information("书库冒烟 {State} {Name} -> {Detail}",
                check.Ok ? "OK  " : "FAIL", check.Name, check.Detail);
        }

        var passed = report.Checks.Count(c => c.Ok);
        Log.Information("书库冒烟结束：{Passed}/{Total} 通过，用时 {Ms} ms",
            passed, report.Checks.Count, report.DurationMs);

        Shutdown(report.Passed ? 0 : 2);
    }

    /// <summary>
    /// T1-5 阅读器冒烟：导入真实电子书、建缓存、开窗口、渲染、翻章、二次打开命中缓存。
    /// 报告写到 Data/Temp/reader-smoke.json 供脚本判定。
    /// </summary>
    /// <summary>
    /// T4-5 TXT 端到端冒烟：导入 TXT -> 打开 -> 界面手选编码 -> 立即重排 -> 恢复。
    /// 报告写到 Data/Temp/txt-smoke.json。退出码 0 全过，2 有失败，1 跑不起来。
    /// </summary>
    private async Task RunTxtSmokeAsync(AppPaths paths, string sourceFile)
    {
        if (!Path.IsPathFullyQualified(sourceFile) || !File.Exists(sourceFile))
        {
            Log.Error("TXT 冒烟：目标必须是存在的绝对路径：{Target}", sourceFile);
            await Log.CloseAndFlushAsync().ConfigureAwait(true);
            Shutdown(1);
            return;
        }

        var report = await TxtSmokeTest.RunAsync(
            Services.GetRequiredService<IBookImportService>(),
            Services.GetRequiredService<IReaderService>(),
            Services.GetRequiredService<IWebViewEnvironmentProvider>(),
            Services.GetRequiredService<IReadingProgressService>(),
            Services.GetRequiredService<ILoggerFactory>(),
            paths,
            paths,
            sourceFile,
            Services.GetRequiredService<IConfigStore>(),
            Services.GetRequiredService<IBookRepository>(),
            Services.GetRequiredService<EditCommand>(),
            Services.GetRequiredService<ILibraryScanService>()).ConfigureAwait(true);

        foreach (var check in report.Checks)
        {
            Log.Information("TXT 冒烟 {State} {Name} -> {Detail}",
                check.Ok ? "OK  " : "FAIL", check.Name, check.Detail);
        }

        var passed = report.Checks.Count(c => c.Ok);
        Log.Information("TXT 冒烟结束：{Passed}/{Total} 通过，用时 {Ms} ms",
            passed, report.Checks.Count, report.DurationMs);

        Shutdown(report.Passed ? 0 : 2);
    }

    private async Task RunReaderSmokeAsync(AppPaths paths, string sourceFile)
    {
        if (!Path.IsPathFullyQualified(sourceFile) || !File.Exists(sourceFile))
        {
            Log.Error("阅读冒烟：目标必须是存在的绝对路径：{Target}", sourceFile);
            await Log.CloseAndFlushAsync().ConfigureAwait(true);
            Shutdown(1);
            return;
        }

        var report = await ReaderSmokeTest.RunAsync(
            Services.GetRequiredService<IBookImportService>(),
            Services.GetRequiredService<IReaderService>(),
            Services.GetRequiredService<IWebViewEnvironmentProvider>(),
            Services.GetRequiredService<IReadingProgressService>(),
            Services.GetRequiredService<IBookRepository>(),
            Services.GetRequiredService<IPdfDocumentService>(),
            Services.GetRequiredService<ILoggerFactory>(),
            paths,
                sourceFile,
            Services.GetRequiredService<IEpubWorkspaceFactory>(),
            Services.GetRequiredService<IChapterSourceService>(),
            Services.GetRequiredService<IReaderAssets>(),
            Services.GetRequiredService<IVisualEditAutoFixService>(),
            Services.GetRequiredService<IChapterFindReplaceService>(),
            Services.GetRequiredService<IWorkspaceSnapshotService>()).ConfigureAwait(true);

        foreach (var check in report.Checks)
        {
            Log.Information("阅读冒烟 {State} {Name} -> {Detail}",
                check.Ok ? "OK  " : "FAIL", check.Name, check.Detail);
        }

        var passed = report.Checks.Count(c => c.Ok);
        Log.Information("阅读冒烟结束：{Passed}/{Total} 通过，用时 {Ms} ms",
            passed, report.Checks.Count, report.DurationMs);

        Shutdown(report.Passed ? 0 : 2);
    }

    /// <summary>
    /// 「关掉阅读窗口后书库网格自己刷新进度角标」的回归冒烟。
    /// 导入 -> 书库页 -> ReadCommand 开窗 -> 翻到可滚动章滚一半 -> 立刻关窗 -> 卡片必须自己跟上。
    /// 报告写到 Data/Temp/reader-refresh-smoke.json 供脚本判定。
    /// </summary>
    private async Task RunReaderRefreshSmokeAsync(AppPaths paths, string sourceFile)
    {
        if (!Path.IsPathFullyQualified(sourceFile) || !File.Exists(sourceFile))
        {
            Log.Error("关窗刷新冒烟：目标必须是存在的绝对路径：{Target}", sourceFile);
            await Log.CloseAndFlushAsync().ConfigureAwait(true);
            Shutdown(1);
            return;
        }

        var report = await Smoke.ReaderRefreshSmokeTest
            .RunAsync(
                Services,
                Services.GetRequiredService<MainWindow>(),
                paths,
                sourceFile)
            .ConfigureAwait(true);

        LogChecks("关窗刷新", report);

        Shutdown(report.Passed ? 0 : 2);
    }

    /// <summary>
    /// T1-7 VS1 端到端验收的**阶段 A**（脚本步骤 1-5）：
    /// 全新数据目录 -> 冷启动计时 -> 导入 EPUB -> 网格出现封面 ->
    /// 开阅读器读到第 2 章中间 -> 等进度真的落盘 -> 关窗退出。
    /// 报告写到 Data/Temp/vs1-a.json。退出码 0 全过，2 有失败，1 跑不起来。
    /// </summary>
    private async Task RunVs1PhaseAAsync(AppPaths paths, string sourceFile)
    {
        if (!Path.IsPathFullyQualified(sourceFile) || !File.Exists(sourceFile))
        {
            Log.Error("VS1 阶段 A：目标必须是存在的绝对路径：{Target}", sourceFile);
            await Log.CloseAndFlushAsync().ConfigureAwait(true);
            Shutdown(1);
            return;
        }

        var report = await Smoke.Vs1SmokeTest
            .RunPhaseAAsync(Services, paths, sourceFile)
            .ConfigureAwait(true);

        LogChecks("VS1-A", report);
        Shutdown(report.Passed ? 0 : 2);
    }

    /// <summary>
    /// T1-7 VS1 端到端验收的**阶段 B**（脚本步骤 6-11）：
    /// 暖启动计时 -> 打开同一本书校验回到原位 -> 转 MOBI 并校验产物 ->
    /// 再转一次中途取消（量响应、查残留进程）-> 退出。报告写到 Data/Temp/vs1-b.json。
    /// </summary>
    private async Task RunVs1PhaseBAsync(AppPaths paths, string sourceFile)
    {
        if (!Path.IsPathFullyQualified(sourceFile) || !File.Exists(sourceFile))
        {
            Log.Error("VS1 阶段 B：目标必须是存在的绝对路径：{Target}", sourceFile);
            await Log.CloseAndFlushAsync().ConfigureAwait(true);
            Shutdown(1);
            return;
        }

        var report = await Smoke.Vs1SmokeTest
            .RunPhaseBAsync(Services, paths, sourceFile)
            .ConfigureAwait(true);

        LogChecks("VS1-B", report);
        Shutdown(report.Passed ? 0 : 2);
    }

    /// <summary>把冒烟结果逐条写进日志，并汇总一行。多个冒烟共用。</summary>
    /// <summary>T6-1 冒烟：MOBI/AZW3 惰性转 EPUB 缓存（真 Calibre，判据 1~6）。</summary>
    private async Task RunT61SmokeAsync(AppPaths paths, string fixtureDir)
    {
        if (!Path.IsPathFullyQualified(fixtureDir) || !Directory.Exists(fixtureDir))
        {
            Log.Error("T6-1 冒烟：样本目录必须是存在的绝对路径：{Dir}", fixtureDir);
            await Log.CloseAndFlushAsync().ConfigureAwait(true);
            Shutdown(1);
            return;
        }

        var report = await T61SmokeTest.RunAsync(
            Services.GetRequiredService<IBookImportService>(),
            Services.GetRequiredService<IReaderService>(),
            Services.GetRequiredService<IEpubCacheService>(),
            Services.GetRequiredService<IConversionQueue>(),
            Services.GetRequiredService<IBookRepository>(),
            Services.GetRequiredService<IConfigStore>(),
            Services.GetRequiredService<IWebViewEnvironmentProvider>(),
            Services.GetRequiredService<IReadingProgressService>(),
            Services.GetRequiredService<ILoggerFactory>(),
            paths,
            fixtureDir).ConfigureAwait(true);

        LogChecks("T6-1 冒烟", report);
        Shutdown(report.Passed ? 0 : 2);
    }

    private static void LogChecks(string label, SmokeReport report)
    {
        foreach (var check in report.Checks)
        {
            Log.Information("{Label} {State} {Name} -> {Detail}",
                label, check.Ok ? "OK  " : "FAIL", check.Name, check.Detail);
        }

        var passed = report.Checks.Count(c => c.Ok);
        Log.Information("{Label} 结束：{Passed}/{Total} 通过，用时 {Ms} ms",
            label, passed, report.Checks.Count, report.DurationMs);
    }

    /// <summary>
    /// T1-3a 外壳与导航冒烟：六个路由、页面缓存、未知路由回落、历史回退、
    /// 页面异常被兜住、诊断页可见性、窄窗折叠、手动折叠优先。
    /// </summary>
    /// <summary>
    /// T2-2 重复检测冒烟：造样本 -> 扫描出组 -> 合并/忽略 -> 1000 本性能判据。
    /// 报告写到 Data/Temp/duplicate-smoke.json 供脚本判定。
    /// </summary>
    /// <summary>
    /// T2-3 网格冒烟：造 1000 本 -> 首屏计时 -> 滚动心跳 -> 搜索计时 -> 组合筛选 -> 三种空状态。
    /// 报告写到 Data/Temp/grid-smoke.json 供脚本判定。
    /// </summary>
    private async Task RunGridSmokeAsync(AppPaths paths)
    {
        var window = Services.GetRequiredService<MainWindow>();
        var page = Services.GetRequiredService<LibraryPage>();
        var repo = Services.GetRequiredService<IBookRepository>();

        var report = await Smoke.GridSmokeTest
            .RunAsync(paths, window, page, repo)
            .ConfigureAwait(true);

        foreach (var check in report.Checks)
        {
            Log.Information("网格冒烟 {State} {Name} -> {Detail}",
                check.Ok ? "OK  " : "FAIL", check.Name, check.Detail);
        }

        var passed = report.Checks.Count(c => c.Ok);
        Log.Information("网格冒烟结束：{Passed}/{Total} 通过，用时 {Ms} ms",
            passed, report.Checks.Count, report.DurationMs);

        Shutdown(report.Passed ? 0 : 2);
    }

    private async Task RunDuplicateSmokeAsync(AppPaths paths)
    {
        var repo = Services.GetRequiredService<IBookRepository>();
        var scanVm = Services.GetRequiredService<DuplicateScanViewModel>();
        var libraryVm = Services.GetRequiredService<LibraryViewModel>();

        var report = await Smoke.DuplicateSmokeTest
            .RunAsync(paths, repo, scanVm, libraryVm)
            .ConfigureAwait(true);

        foreach (var check in report.Checks)
        {
            Log.Information("重复检测冒烟 {State} {Name} -> {Detail}",
                check.Ok ? "OK  " : "FAIL", check.Name, check.Detail);
        }

        var passed = report.Checks.Count(c => c.Ok);
        Log.Information("重复检测冒烟结束：{Passed}/{Total} 通过，用时 {Ms} ms",
            passed, report.Checks.Count, report.DurationMs);

        Shutdown(report.Passed ? 0 : 2);
    }


    private async Task RunShellSmokeAsync(AppPaths paths)
    {
        var window = Services.GetRequiredService<MainWindow>();
        var nav = Services.GetRequiredService<NavigationService>();
        var registry = Services.GetRequiredService<PageRegistry>();

        var report = await Smoke.ShellSmokeTest
            .RunAsync(window, nav, registry, paths)
            .ConfigureAwait(true);

        foreach (var check in report.Checks)
        {
            Log.Information("外壳冒烟 {State} {Name} -> {Detail}",
                check.Ok ? "OK  " : "FAIL", check.Name, check.Detail);
        }

        var passed = report.Checks.Count(c => c.Ok);
        Log.Information("外壳冒烟结束：{Passed}/{Total} 通过，用时 {Ms} ms",
            passed, report.Checks.Count, report.DurationMs);

        Shutdown(report.Passed ? 0 : 2);
    }

    /// <summary>
    /// T1-2 导入冒烟：一次跑完 真实电子书导入 / 重复识别 / 假书拒绝 / 无孤儿 四个场景，
    /// 报告写到 Data/Temp/import-smoke.json。退出码 0 全过，2 有失败，1 跑不起来。
    /// </summary>
    /// <summary>
    /// 阶段 3 编辑 UI 入口冒烟：入口 -> 编辑器窗口 -> 改元数据 / 换封面 -> 保存 -> 网格刷新。
    /// 报告写到 Data/Temp/edit-smoke.json 供脚本判定。
    /// </summary>
    private async Task RunEditSmokeAsync(AppPaths paths, string sourceFile)
    {
        var window = Services.GetRequiredService<MainWindow>();
        var page = Services.GetRequiredService<LibraryPage>();

        var report = await Smoke.EditSmokeTest
            .RunAsync(
                paths,
                window,
                page,
                Services.GetRequiredService<EditCommand>(),
                Services.GetRequiredService<IBookImportService>(),
                Services.GetRequiredService<IBookRepository>(),
                Services.GetRequiredService<ILibraryCoverService>(),
                Services.GetRequiredService<IEpubWorkspaceFactory>(),
                Services.GetRequiredService<IRestorePointService>(),
                Services.GetRequiredService<IOperationLogRepository>(),
                Services.GetRequiredService<IChapterTreeService>(),
                Services.GetRequiredService<IChapterSourceService>(),
                Services.GetRequiredService<IWebViewEnvironmentProvider>(),
                Services.GetRequiredService<IReaderAssets>(),
                Services.GetRequiredService<IVisualEditAutoFixService>(),
                sourceFile)
            .ConfigureAwait(true);

        foreach (var check in report.Checks)
        {
            Log.Information("编辑入口冒烟 {State} {Name} -> {Detail}",
                check.Ok ? "OK  " : "FAIL", check.Name, check.Detail);
        }

        var passed = report.Checks.Count(c => c.Ok);
        Log.Information("编辑入口冒烟结束：{Passed}/{Total} 通过，用时 {Ms} ms",
            passed, report.Checks.Count, report.DurationMs);

        Shutdown(report.Passed ? 0 : 2);
    }

    private async Task RunImportSmokeAsync(AppPaths paths, string sourceFile)
    {
        if (!Path.IsPathFullyQualified(sourceFile) || !File.Exists(sourceFile))
        {
            Log.Error("导入冒烟：目标必须是存在的绝对路径：{Target}", sourceFile);
            await Log.CloseAndFlushAsync().ConfigureAwait(true);
            Shutdown(1);
            return;
        }

        var report = await ImportSmokeTest.RunAsync(
            Services.GetRequiredService<IBookImportService>(),
            Services.GetRequiredService<IBookRepository>(),
            paths,
            sourceFile).ConfigureAwait(true);

        foreach (var check in report.Checks)
        {
            Log.Information("导入冒烟 {State} {Name} -> {Detail}",
                check.Ok ? "OK  " : "FAIL", check.Name, check.Detail);
        }

        var passed = report.Checks.Count(c => c.Ok);
        Log.Information("导入冒烟结束：{Passed}/{Total} 通过，用时 {Ms} ms",
            passed, report.Checks.Count, report.DurationMs);

        Shutdown(report.Passed ? 0 : 2);
    }

    /// <summary>
    /// T2-1 批量导入冒烟：递归扫目录 -> 队列导入 -> 重复只问一次 -> 取消 -> 一本坏书不阻塞。
    /// 报告写到 Data/Temp/batch-import-smoke.json。退出码 0 全过，2 有失败，1 跑不起来。
    /// </summary>
    private async Task RunBatchImportSmokeAsync(AppPaths paths, string sourceFolder)
    {
        if (!Path.IsPathFullyQualified(sourceFolder) || !File.Exists(sourceFolder))
        {
            Log.Error("批量导入冒烟：模板必须是一本存在的绝对路径 EPUB：{Target}", sourceFolder);
            await Log.CloseAndFlushAsync().ConfigureAwait(true);
            Shutdown(1);
            return;
        }

        var report = await BatchImportSmokeTest.RunAsync(Services, paths, sourceFolder)
            .ConfigureAwait(true);

        foreach (var check in report.Checks)
        {
            Log.Information("批量导入冒烟 {State} {Name} -> {Detail}",
                check.Ok ? "OK  " : "FAIL", check.Name, check.Detail);
        }

        Log.Information("批量导入冒烟结束：{Passed}/{Total} 通过，用时 {Ms} ms",
            report.Checks.Count(c => c.Ok), report.Checks.Count, report.DurationMs);

        Shutdown(report.Passed ? 0 : 2);
    }


    /// <summary>
    /// 转换模板冒烟：执行计划 T2-5 的 5 条判据。
    /// 需要两个样本：一本 EPUB 与一本 TXT（EPUB 输出那条要用 TXT 作源，原因见 TemplateSmokeTest）。
    /// </summary>
    private async Task RunTemplateSmokeAsync(AppPaths paths, string epubFile, string epub2File)
    {
        foreach (var (label, path) in new[] { ("EPUB-A", epubFile), ("EPUB-B", epub2File) })
        {
            if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
            {
                Log.Error("模板冒烟：{Label} 样本必须是存在的绝对路径：{Target}", label, path);
                await Log.CloseAndFlushAsync().ConfigureAwait(true);
                Shutdown(1);
                return;
            }
        }

        var report = await TemplateSmokeTest.RunAsync(Services, paths, epubFile, epub2File)
            .ConfigureAwait(true);

        foreach (var check in report.Checks)
        {
            Log.Information("模板冒烟 {State} {Name} -> {Detail}",
                check.Ok ? "OK  " : "FAIL", check.Name, check.Detail);
        }

        Shutdown(report.Passed ? 0 : 2);
    }

    /// <summary>转换队列冒烟：执行计划 T2-4 的 6 条判据。源目录里要准备好 10 个以上 EPUB。</summary>
    private async Task RunConversionQueueSmokeAsync(AppPaths paths, string sourceFolder)
    {
        if (!Path.IsPathFullyQualified(sourceFolder) || !Directory.Exists(sourceFolder))
        {
            Log.Error("队列冒烟：源目录必须是存在的绝对路径：{Target}", sourceFolder);
            await Log.CloseAndFlushAsync().ConfigureAwait(true);
            Shutdown(1);
            return;
        }

        var report = await ConversionQueueSmokeTest.RunAsync(Services, paths, sourceFolder)
            .ConfigureAwait(true);

        foreach (var check in report.Checks)
        {
            Log.Information("队列冒烟 {State} {Name} -> {Detail}",
                check.Ok ? "OK  " : "FAIL", check.Name, check.Detail);
        }

        Log.Information("队列冒烟结束：{Passed}/{Total} 通过，用时 {Ms} ms",
            report.Checks.Count(c => c.Ok), report.Checks.Count, report.DurationMs);

        Shutdown(report.Passed ? 0 : 2);
    }

    /// <summary>判据 b 第一阶段：排 10 本后被强杀（非 0 返回，脚本据此判定确实崩了）。</summary>
    private async Task RunQueueCrashSmokeAsync(AppPaths paths, string sourceFolder)
    {
        if (!Path.IsPathFullyQualified(sourceFolder) || !Directory.Exists(sourceFolder))
        {
            Log.Error("崩溃冒烟：源目录必须是存在的绝对路径：{Target}", sourceFolder);
            await Log.CloseAndFlushAsync().ConfigureAwait(true);
            Shutdown(1);
            return;
        }

        var code = await ConversionQueueSmokeTest
            .RunCrashPhaseAsync(Services, paths, sourceFolder).ConfigureAwait(true);

        Shutdown(code);
    }

    /// <summary>判据 b 第二阶段：同一数据目录再启动，启动时已恢复残留任务并接着跑。</summary>
    private async Task RunQueueResumeSmokeAsync(AppPaths paths)
    {
        var report = await ConversionQueueSmokeTest.RunResumeAsync(Services, paths)
            .ConfigureAwait(true);

        foreach (var check in report.Checks)
        {
            Log.Information("恢复冒烟 {State} {Name} -> {Detail}",
                check.Ok ? "OK  " : "FAIL", check.Name, check.Detail);
        }

        Log.Information("恢复冒烟结束：{Passed}/{Total} 通过，用时 {Ms} ms",
            report.Checks.Count(c => c.Ok), report.Checks.Count, report.DurationMs);

        Shutdown(report.Passed ? 0 : 2);
    }

    /// <summary>转换冒烟：执行计划 T1-4 的 7 条判据。</summary>
    private async Task RunConvertSmokeAsync(AppPaths paths, string sourceFile)
    {
        if (!Path.IsPathFullyQualified(sourceFile) || !File.Exists(sourceFile))
        {
            Log.Error("转换冒烟：目标必须是存在的绝对路径：{Target}", sourceFile);
            await Log.CloseAndFlushAsync().ConfigureAwait(true);
            Shutdown(1);
            return;
        }

        var report = await ConvertSmokeTest.RunAsync(
            Services.GetRequiredService<IBookImportService>(),
            Services.GetRequiredService<IConversionService>(),
            Services.GetRequiredService<IConversionQueue>(),
            Services.GetRequiredService<ConversionTemplateStore>(),
            Services.GetRequiredService<IBookRepository>(),
            paths,
            sourceFile).ConfigureAwait(true);

        foreach (var check in report.Checks)
        {
            Log.Information("转换冒烟 {State} {Name} -> {Detail}",
                check.Ok ? "OK  " : "FAIL", check.Name, check.Detail);
        }

        var passed = report.Checks.Count(c => c.Ok);
        Log.Information("转换冒烟结束：{Passed}/{Total} 通过，用时 {Ms} ms",
            passed, report.Checks.Count, report.DurationMs);

        Shutdown(report.Passed ? 0 : 2);
    }

    /// <summary>崩溃冒烟：起一次转换后卡住，等外部脚本强杀本进程来验证 Job Object。</summary>
    private async Task RunConvertCrashAsync(AppPaths paths, string sourceFile)
    {
        if (!Path.IsPathFullyQualified(sourceFile) || !File.Exists(sourceFile))
        {
            Log.Error("崩溃冒烟：目标必须是存在的绝对路径：{Target}", sourceFile);
            await Log.CloseAndFlushAsync().ConfigureAwait(true);
            Shutdown(1);
            return;
        }

        await ConvertSmokeTest.RunCrashAsync(
            Services.GetRequiredService<IBookImportService>(),
            Services.GetRequiredService<IConversionService>(),
            paths,
            sourceFile).ConfigureAwait(true);

        Shutdown(0);
    }
    // ---------- 组装 ----------

    private static IHost BuildHost(AppPaths paths, bool showDiagnostics, IConfigStore config)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = AppContext.BaseDirectory,
        });

        // 用 Serilog 顶掉默认的日志提供者，避免日志打两份
        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(Log.Logger, dispose: false);

        var services = builder.Services;
        services.AddSingleton<IAppPaths>(paths);
        services.AddSingleton<IConfigStore>(config);

        /* T7-4：转换并发数来自设置页（1-4，默认 1）。启动时就定死（信号量在队列构造时建），
           所以设置页标注"重启后生效"。 */
        services.AddSingleton(new ConversionQueueOptions
        {
            MaxParallel = AppSettingsKeys.NormalizeMaxParallel(
                AppSettingsKeys.GetInt(config, AppSettingsKeys.ConversionMaxParallel, 1)),
        });

        /* T7-4：设置页的"清缓存 / 立即备份 / 导出诊断"动作，以及数据目录迁移向导。
           迁移服务只做磁盘与指针文件的事，窗口由设置页 new 出来。 */
        services.AddSingleton<IAppMaintenanceService, AppMaintenanceService>();
        services.AddSingleton<IDataDirectoryMigrationService, DataDirectoryMigrationService>();

        services.AddSingleton<IClock>(SystemClock.Instance);
        services.AddSingleton<IProcessHost, ProcessHost>();
        services.AddSingleton<IToolLocator, ToolLocator>();
        services.AddSingleton<IShellLauncher, ShellLauncher>();
        services.AddSingleton<IHashUtil, HashUtil>();
        services.AddSingleton<IEpubProbe, EpubProbe>();
        services.AddSingleton<IImageService, ImageProcessor>();

        // 书库封面缓存的刷新（T3-3 判据 4）：换封面后网格缩略图要跟着变
        services.AddSingleton<ILibraryCoverService, LibraryCoverService>();
        services.AddSingleton<IImportScanner, ImportScanner>();
        services.AddSingleton<IImportBatchRepository, ImportBatchRepository>();
        services.AddSingleton<IDuplicateResolver, DuplicatePromptResolver>();
        services.AddSingleton<IImportQueue, ImportQueue>();
        services.AddSingleton<IBookImportService, ImportPipeline>();

        // 删书：物理删除库记录 + 正式区文件 + 封面 + 阅读缓存（界面负责二次确认）
        services.AddSingleton<IBookDeletionService, BookDeletionService>();
        services.AddSingleton<IConversionService, ConversionService>();

        // 文件监控与缺失检测（T2-6）：全量扫描是判定依据，watcher 只负责"提醒该扫了"
        services.AddSingleton<ILibraryScanService, LibraryScanService>();
        services.AddSingleton<ILibraryWatcher, LibraryFolderWatcher>();

        // 转换队列：单例。真正的转换工作复用 IConversionService，
        // 这里只管排队、限并发、重试与取消。
        services.AddSingleton<IConversionTaskRepository, ConversionTaskRepository>();
        services.AddSingleton<IConversionQueue, ConversionQueue>();

        // 转换模板：内置模板来自代码，用户「另存为」的模板落 conversion_templates 表。
        // store 必须单例：它缓存用户模板，改完要立刻能看到（Save/Delete 会重读）。
        // CBZ 打包（漫画）：独立入口，不进队列。
        services.AddSingleton<ICbzPacker, CbzPacker>();
        services.AddSingleton<IConversionTemplateRepository, ConversionTemplateRepository>();
        services.AddSingleton<ConversionTemplateStore>();

        // 阅读器：界面文件定位 + 内容准备 + WebView2 环境（环境必须单例：
        // 同一个 userDataFolder 不能创建两个环境）
        services.AddSingleton<IReaderAssets, ReaderAssets>();
        services.AddSingleton<IWebViewEnvironmentProvider, WebViewEnvironmentProvider>();

        // T6-1：MOBI/AZW3 的惰性转 EPUB 缓存。它要往 IConversionQueue 里插一条
        // 高优先级、不入库的任务，所以必须在 IReaderService 之前/同批注册（DI 懒解析，顺序不敏感）。
        services.AddSingleton<IEpubCacheService, EpubCacheService>();
        services.AddSingleton<IReaderService, ReaderService>();
        services.AddSingleton<IReadingProgressService, ReadingProgressService>();

        /* T7-3B：阅读统计。sessions 由阅读窗口驱动采集（IReadingSessionService），
           统计页只读聚合结果（IReadingStatsService）；仓储与聚合分开是为了让口径
           能在没有界面的单测里验。 */
        services.AddSingleton<IReadingStatsRepository, ReadingStatsRepository>();
        services.AddSingleton<IReadingSessionService, ReadingSessionService>();
        services.AddSingleton<IReadingStatsService, ReadingStatsService>();

        // T6-2：pdfium 相关（总页数 + 首页渲染）。注册成单例——pdfium 原生调用内部串行化，
        // 开多份实例没有收益；封面生成是低优先级后台任务。
        services.AddPdfServices();

        // T6-3：漫画（CBZ/CBR）读取。单例同理由：每次请求重开归档会反复解压（RAR 尤其贵），
        // 句柄由阅读窗口自己持有并释放。
        services.AddComicServices();

        // T6-4：全文搜索（content_chunk + FTS5 trigram + 后台索引器）。
        services.AddContentSearchServices();

        // T6-5：书签与笔记（bookmarks / notes 两张表 + Markdown 导出）。
        services.AddAnnotationServices();

        // 编辑工作区（T3-1）：把 EPUB 安全解压到 Data/Workspaces/{sessionId}/，
        // 之后阶段 3 的元数据编辑/打包都在这份副本上做。
        services.AddSingleton<IEpubWorkspaceFactory, EpubWorkspaceFactory>();
        services.AddSingleton<IWorkspaceSnapshotService, WorkspaceSnapshotService>();
        services.AddSingleton<IResourceScanService, ResourceScanService>();
        services.AddSingleton<IChapterTreeService, ChapterTreeService>();
        services.AddSingleton<IChapterSourceService, ChapterSourceService>();

        // T5-3 跨章查找替换：阅读器工具栏入口用；只写工作区，落书仍走主窗口【保存】。
        services.AddSingleton<IChapterFindReplaceService, ChapterFindReplaceService>();

        // 书籍编辑器（阶段 3 的 UI 入口）：窗口要用打包器与封面替换引擎。
        // EpubPackager 的注册本来留给 T3-6 的保存闭环，这里被 UI 先用上了。
        services.AddSingleton<CoverService>();
        services.AddSingleton<EpubPackager>();

        // 【T3-5】EpubCheck 集成与门禁：
        //   IEpubChecker = 进程封装（走 IProcessHost，超时杀进程树，退出码 0/1 都算正常跑完）；
        //   IEpubSaveService = 两段式保存（先打包到 .ets-stage 并校验，用户拍板后才替换原文件）。
        services.AddSingleton<IEpubChecker>(sp => new EpubCheckRunner(
            sp.GetRequiredService<IToolLocator>(),
            sp.GetRequiredService<IProcessHost>(),
            sp.GetRequiredService<ILogger<EpubCheckRunner>>()));
        services.AddSingleton<IEpubSaveService, EpubSaveService>();

        // 【T3-6】保存闭环与备份还原：
        //   IOperationLogRepository = operation_log 的读写（活动中心的时间线也读它）；
        //   IRestorePointService = 还原点（替换前先备份、能还原、按 1 GB / 单本 5 份清理）。
        //   EpubSaveService 拿到 IRestorePointService 后，CommitAsync 会自动"先备份再替换"。
        services.AddSingleton<IOperationLogRepository, OperationLogRepository>();
        services.AddSingleton<IRestorePointService, RestorePointService>();

        // 【T5-2W】可视编辑的"自动修正"链路：用户点【确认修改】后先建还原点（备份这一章的原文）
        //   再改工作区，最后写 operation_log(autofix)。失败时原文零改动、还原点也会清掉。
        services.AddSingleton<IVisualEditAutoFixService, VisualEditAutoFixService>();

        // 数据层：连接工厂是单例（持有共享读连接）；迁移器与仓储无状态
        services.AddSingleton<SqliteConnectionFactory>();
        services.AddSingleton<SchemaMigrator>();
        services.AddSingleton<IDatabase, Database>();
        services.AddSingleton<IBookRepository, BookRepository>();
        services.AddSingleton<IDuplicateRepository, DuplicateRepository>();
        services.AddSingleton<IDuplicateDetectionService, DuplicateDetectionService>();
        // 窗口与外壳
        services.AddSingleton<MainWindow>();

        // 导入命令：菜单与书库空状态共用同一份
        services.AddSingleton<ImportCommand>();
        services.AddSingleton<ConvertCommand>();
        services.AddSingleton<ReadCommand>();

        // 编辑命令：工具栏、卡片右键菜单、详情浮层三处入口共用同一份。
        services.AddSingleton<EditCommand>();

        // 页面（懒加载：只有被导航到才会构造）
        // T2-3：书库 VM 必须是单例  主窗口顶栏的搜索框和书库页绑的是同一个实例，
        // 否则搜索框改的是一份状态、网格看的是另一份。
        services.AddSingleton<LibraryViewModel>();
        services.AddSingleton<ConversionQueueViewModel>();
        services.AddTransient<DuplicateScanViewModel>();
        // 书库页必须单例：主窗口顶栏的搜索框、以及冒烟脚本，都要拿到"外壳正在显示的那个实例"。
        // 导航服务本来就把它缓存着，单例只是把这件事写明确。
        services.AddSingleton<LibraryPage>();
        services.AddTransient<QueuePage>();
        services.AddTransient<ActivityPage>();
        services.AddTransient<StatsPage>();
        services.AddTransient<BackupPage>();
        services.AddTransient<SettingsPage>();
        services.AddTransient<DiagnosticsPage>();

        // 导航：注册表存工厂而不是 Type，页面的依赖由 DI 显式给出
        services.AddSingleton(sp =>
        {
            var registry = new PageRegistry();
            registry.Register(Routes.Library, () => sp.GetRequiredService<LibraryPage>());
            registry.Register(Routes.Queue, () => sp.GetRequiredService<QueuePage>());
            registry.Register(Routes.Activity, () => sp.GetRequiredService<ActivityPage>());
            registry.Register(Routes.Stats, () => sp.GetRequiredService<StatsPage>());
            registry.Register(Routes.Backup, () => sp.GetRequiredService<BackupPage>());
            registry.Register(Routes.Settings, () => sp.GetRequiredService<SettingsPage>());
            registry.Register(Routes.Diagnostics, () => sp.GetRequiredService<DiagnosticsPage>());
            return registry;
        });

        services.AddSingleton<NavigationService>();

        services.AddSingleton(sp => new ShellViewModel(
            sp.GetRequiredService<NavigationService>(),
            sp.GetRequiredService<LibraryViewModel>(),
            NavigationCatalog.Build(showDiagnostics),
            sp.GetRequiredService<ILogger<ShellViewModel>>()));

        return builder.Build();
    }

    private static RunMode DetectMode(string[] args)
    {
        if (HasSwitch(args, SmokeWebViewSwitch))
        {
            return RunMode.SmokeWebView;
        }

        if (HasSwitch(args, SmokeLoggingSwitch))
        {
            return RunMode.SmokeLogging;
        }

        if (HasSwitch(args, SmokeLibrarySwitch))
        {
            return RunMode.SmokeLibrary;
        }

        if (HasSwitch(args, SmokeGridSwitch))
        {
            return RunMode.SmokeGrid;
        }

        if (HasSwitch(args, SmokeDuplicatesSwitch))
        {
            return RunMode.SmokeDuplicates;
        }

        if (HasSwitch(args, SmokeShellSwitch))
        {
            return RunMode.SmokeShell;
        }

        if (GetOption(args, SmokeTxtOption) is not null)
        {
            return RunMode.SmokeTxt;
        }

        if (GetOption(args, SmokeEditOption) is not null)
        {
            return RunMode.SmokeEdit;
        }

        if (GetOption(args, SmokeImportOption) is not null)
        {
            return RunMode.SmokeImport;
        }

        if (GetOption(args, SmokeTemplatesEpubOption) is not null
            && GetOption(args, SmokeTemplatesBOption) is not null)
        {
            return RunMode.SmokeTemplates;
        }

        if (GetOption(args, SmokeBatchImportOption) is not null)
        {
            return RunMode.SmokeBatchImport;
        }

        if (HasSwitch(args, SmokeQueueResumeSwitch))
        {
            return RunMode.SmokeQueueResume;
        }

        if (GetOption(args, SmokeQueueCrashOption) is not null)
        {
            return RunMode.SmokeQueueCrash;
        }

        if (GetOption(args, SmokeConversionQueueOption) is not null)
        {
            return RunMode.SmokeConversionQueue;
        }

        if (GetOption(args, SmokeConvertCrashOption) is not null)
        {
            return RunMode.SmokeConvertCrash;
        }

        if (GetOption(args, SmokeReaderOption) is not null)
        {
            return RunMode.SmokeReader;
        }

        if (GetOption(args, SmokeReaderRefreshOption) is not null)
        {
            return RunMode.SmokeReaderRefresh;
        }

        if (GetOption(args, SmokeScanOption) is not null)
        {
            return RunMode.SmokeScan;
        }

        if (GetOption(args, SmokeVs2AOption) is not null)
        {
            return RunMode.SmokeVs2A;
        }

        if (GetOption(args, SmokeVs1AOption) is not null)
        {
            return RunMode.SmokeVs1A;
        }

        if (GetOption(args, SmokeVs1BOption) is not null)
        {
            return RunMode.SmokeVs1B;
        }

        if (GetOption(args, SmokeConvertOption) is not null)
        {
            return RunMode.SmokeConvert;
        }

        if (GetOption(args, RenderProbeOption) is not null)
        {
            return RunMode.RenderProbe;
        }

        if (GetOption(args, SmokeT61Option) is not null)
        {
            return RunMode.SmokeT61;
        }

        if (GetOption(args, T62ProbeOption) is not null)
        {
            return RunMode.T62Probe;
        }

        if (GetOption(args, SmokeT62Option) is not null)
        {
            return RunMode.SmokeT62;
        }

        if (GetOption(args, T62BProbeOption) is not null)
        {
            return RunMode.T62BProbe;
        }

        if (GetOption(args, SmokeT62BOption) is not null)
        {
            return RunMode.SmokeT62B;
        }

        if (GetOption(args, SmokeT63Option) is not null)
        {
            return RunMode.SmokeT63;
        }

        if (GetOption(args, SmokeT64Option) is not null)
        {
            return RunMode.SmokeT64;
        }

        if (GetOption(args, SmokeT65Option) is not null)
        {
            return RunMode.SmokeT65;
        }

        if (GetOption(args, SmokeT66Option) is not null)
        {
            return RunMode.SmokeT66;
        }

        if (GetOption(args, SmokeT71BootOption) is not null)
        {
            return RunMode.SmokeT71Boot;
        }

        if (GetOption(args, SmokeT71Option) is not null)
        {
            return RunMode.SmokeT71;
        }

        if (GetOption(args, SmokeT72Option) is not null)
        {
            return RunMode.SmokeT72;
        }

        if (GetOption(args, SmokeT73Option) is not null)
        {
            return RunMode.SmokeT73;
        }

        if (GetOption(args, SmokeT73BOption) is not null)
        {
            return RunMode.SmokeT73B;
        }

        if (GetOption(args, SmokeT74Option) is not null)
        {
            return RunMode.SmokeT74;
        }

        if (GetOption(args, SmokeT75Option) is not null)
        {
            return RunMode.SmokeT75;
        }

        return RunMode.Normal;
    }

    private static bool HasSwitch(string[] args, string name)
        => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>取 --key=value 形式的选项值；没有该选项返回 null。</summary>
    private static string? GetOption(string[] args, string prefix)
    {
        foreach (var arg in args)
        {
            if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return arg[prefix.Length..].Trim('"');
            }
        }

        return null;
    }

    private static AppPaths ResolvePaths(string[] args)
    {
        string? dataDir = null;
        foreach (var arg in args)
        {
            if (arg.StartsWith(DataDirPrefix, StringComparison.OrdinalIgnoreCase))
            {
                dataDir = arg[DataDirPrefix.Length..].Trim('"');
            }
        }

        var userLocalRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EbookToolSuite");

        return AppPaths.Create(new AppPathsOptions
        {
            AppRoot = ResolveAppRoot(),
            UserLocalRoot = userLocalRoot,
            CommandLineDataRoot = dataDir,
            Fallback = DataDirFallback.UseUserLocalAppData,
        });
    }

    /// <summary>
    /// 开发期从 bin/ 往上找带 Tools/manifest.json 的目录当程序根；
    /// 发布版直接返回 exe 所在目录。找不到就返回 exe 目录（由后续检查报错）。
    /// </summary>
    private static string ResolveAppRoot()
    {
#if DEBUG
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Tools", "manifest.json")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }
#endif
        return AppContext.BaseDirectory;
    }
}