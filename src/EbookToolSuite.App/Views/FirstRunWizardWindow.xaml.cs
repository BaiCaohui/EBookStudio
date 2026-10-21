using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using EbookToolSuite.App.Settings;
using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.IO;
using EbookToolSuite.Infrastructure.Config;

namespace EbookToolSuite.App.Views;

/// <summary>
/// 首次运行向导（执行计划 T7-4 / 设计文档 §13.5）。
///
/// 4 步：欢迎 → 选择数据目录 → 工具检查 → 完成。
/// 三条纪律：
///   1. 所有步骤都能跳过，缺工具绝不允许挡住启动（降级模式）；
///   2. 窗口构造时不写盘；只有"离开第 2 步 / 关闭"才写指针，关闭时写 ui.firstRunCompleted；
///   3. 若把数据目录指到别处，除了写指针，还要在新目录放一份 Config.json，
///      否则重启后新目录仍被判为"首次运行"，向导会反复出现。
/// </summary>
public partial class FirstRunWizardWindow : Window
{
    /// <summary>预计占用 = 缓存上限 + 备份上限（设计文档 §13.5 写的是约 3 GB）。</summary>
    private static readonly long ExpectedUsageBytes =
        (AppSettingsKeys.DefaultCacheLimitMb + AppSettingsKeys.DefaultBackupLimitMb) * 1024L * 1024L;

    /// <summary>低于这个剩余空间给黄色警告（仍允许继续）。</summary>
    private const long RecommendedFreeBytes = 10L * 1024 * 1024 * 1024;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly IAppPaths _paths;
    private readonly IConfigStore _config;
    private readonly IToolLocator _tools;
    private readonly IShellLauncher _shell;

    private readonly DispatcherTimer _validateTimer;
    private readonly bool _hadPointerAtStart;

    /// <summary>0-based：0 欢迎 / 1 数据目录 / 2 工具检查 / 3 完成。</summary>
    private int _step;
    private bool _firstRunSaved;
    private bool _pointerApplied;
    private bool _dataDirConfirmed;
    private IReadOnlyList<ToolHealthItem> _toolItems = Array.Empty<ToolHealthItem>();
    private Task? _toolsLoadTask;

    public FirstRunWizardWindow(
        IAppPaths paths,
        IConfigStore config,
        IToolLocator tools,
        IShellLauncher shell)
    {
        _paths = paths;
        _config = config;
        _tools = tools;
        _shell = shell;
        _hadPointerAtStart = File.Exists(paths.PointerFile);

        InitializeComponent();

        // 输入路径时不要每敲一个字符就跑一次真写探测：停手 350ms 再校验
        _validateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _validateTimer.Tick += OnValidateTimerTick;

        DataDirBox.Text = paths.DataRoot;
        UpdateSelectedDataDir();
        ShowStep(0);
    }

    // ---------- 对外属性 ----------

    /// <summary>数据目录里既没有 Config.json 也没有 Library.db 才算首次运行。</summary>
    public static bool ShouldShow(IAppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        return !File.Exists(paths.ConfigFile) && !File.Exists(paths.DbFile);
    }

    /// <summary>用户改到了别的数据目录：需要重启进程才生效。</summary>
    public bool RestartRequested { get; private set; }

    /// <summary>完成页点了「导入书籍」。</summary>
    public bool ImportRequested { get; private set; }

    /// <summary>最终选择的数据目录（绝对路径；路径非法时是用户输入的原文）。</summary>
    public string SelectedDataDir { get; private set; } = string.Empty;

    // ---------- 测试钩子（冒烟用：全部 internal，不弹模态框） ----------

    /// <summary>当前步骤的 0 基索引（0 欢迎 / 1 数据目录 / 2 工具 / 3 完成）。</summary>
    internal int CurrentStep => _step;

    /// <summary>工具检查列表里实际渲染了几项。</summary>
    internal int ToolItemCountForTest => _toolItems.Count;

    /// <summary>设计文档要求所有步骤可跳过：工具缺失也允许继续。</summary>
    internal bool ToolsSkippableForTest => true;

    /// <summary>当前步骤的「下一步」是否可用（冒烟可用来断言不可写目录被拦下）。</summary>
    internal bool NextEnabledForTest => NextButton.IsEnabled;

    /// <summary>数据目录校验提示原文（红色错误 / 黄色警告）。</summary>
    internal string DataDirHintForTest => DataDirHintText.Text;

    /// <summary>跳步骤（仅测试用；0-3，越界忽略）。</summary>
    internal void GoToStepForTest(int step)
    {
        if (step is < 0 or > 3)
        {
            return;
        }

        ShowStep(step);

        if (_step == 2)
        {
            // 同步把工具检测跑完，冒烟读 ToolItemCountForTest 才是确定值
            PumpUntil(EnsureToolsLoadedAsync());
        }
    }

    /// <summary>填数据目录（仅测试用），数据目录那一步会立即重新校验。</summary>
    internal void SelectDataDirForTest(string dir)
    {
        DataDirBox.Text = dir;
        UpdateSelectedDataDir();

        if (_step == 1)
        {
            _validateTimer.Stop();
            RefreshDataDirStatus();
        }
    }

    /// <summary>
    /// 模拟点「下一步」：数据目录那一步先校验（不可写/空间不足则停在原地），
    /// 工具检查允许跳过；最后一步 = 完成并关闭（关闭时写 ui.firstRunCompleted）。
    /// </summary>
    internal void AdvanceForTest()
    {
        if (!AdvanceCore())
        {
            return;
        }

        if (_step >= 3)
        {
            ImportRequested = false;
            Close();
            return;
        }

        ShowStep(_step + 1);

        if (_step == 2)
        {
            PumpUntil(EnsureToolsLoadedAsync());
        }
    }

    /// <summary>等工具检测完成（仅测试用，供冒烟显式 await）。</summary>
    internal Task EnsureToolsLoadedForTestAsync() => EnsureToolsLoadedAsync();

    // ---------- 生命周期 ----------

    protected override void OnClosed(EventArgs e)
    {
        _validateTimer.Stop();

        try
        {
            if (_dataDirConfirmed)
            {
                SyncPointerOnClose();
            }

            SaveFirstRunCompleted();
            InitializeNewDataDirIfNeeded();
        }
        catch (Exception ex)
        {
            // 收尾失败不能挡住进入主界面
            Serilog.Log.Warning(ex, "首次运行向导收尾失败（不影响本次进入主界面）");
        }

        base.OnClosed(e);
    }

    // ---------- 步骤切换 ----------

    private void ShowStep(int step)
    {
        _step = Math.Clamp(step, 0, 3);

        Step1Panel.Visibility = _step == 0 ? Visibility.Visible : Visibility.Collapsed;
        Step2Panel.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step3Panel.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step4Panel.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;

        StepIndicatorText.Text = $"第 {_step + 1} 步，共 4 步";
        BackButton.IsEnabled = _step > 0;
        NextButton.Visibility = _step < 3 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.IsEnabled = true;
        SkipButton.Visibility = _step is 0 or 1 ? Visibility.Visible : Visibility.Collapsed;

        BottomHintText.Text = _step switch
        {
            1 => "数据目录里会放书库、配置、缓存与备份，建议选择空间充足的本地固定盘。",
            2 => "工具缺失只禁用对应功能，不影响阅读；可以稍后到「设置 → 工具」里配置。",
            3 => "完成后随时可以在「设置」里修改数据目录、工具路径等选项。",
            _ => "所有步骤都可以跳过；缺什么工具都不会导致启动失败。",
        };

        if (_step == 1)
        {
            RefreshDataDirStatus();
        }

        if (_step == 2)
        {
            _ = EnsureToolsLoadedAsync();
        }

        if (_step == 3)
        {
            UpdateFinishSummary();
        }
    }

    private bool AdvanceCore()
    {
        switch (_step)
        {
            case 1:
                _validateTimer.Stop();

                if (!RefreshDataDirStatus())
                {
                    return false;
                }

                if (!ApplyPointerForSelection())
                {
                    return false;
                }

                _dataDirConfirmed = true;
                return true;

            default:
                // 欢迎页、工具检查页、完成页都不阻塞：工具缺失也允许继续（降级模式）
                return true;
        }
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (_step > 0)
        {
            ShowStep(_step - 1);
        }
    }

    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (!AdvanceCore())
        {
            return;
        }

        if (_step < 3)
        {
            ShowStep(_step + 1);
        }
    }

    private void OnSkipClick(object sender, RoutedEventArgs e)
    {
        // 数据目录那一步的「跳过」= 继续用当前数据目录：把刚才可能写过的指针恢复原样
        if (_step == 1 && _pointerApplied)
        {
            try
            {
                RestoreOriginalPointer();
                _pointerApplied = false;
                RestartRequested = false;
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "首次运行向导：跳过数据目录步骤时恢复指针失败");
            }
        }

        if (_step < 3)
        {
            ShowStep(_step + 1);
        }
    }

    // ---------- 第 2 步：数据目录 ----------

    private enum DataDirStatus
    {
        Neutral,
        Ok,
        Warning,
        Error,
    }

    private void OnDataDirTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateSelectedDataDir();

        if (_step == 1)
        {
            NextButton.IsEnabled = false;
            ShowDataDirHint("正在检查目录…", DataDirStatus.Neutral);

            _validateTimer.Stop();
            _validateTimer.Start();
        }
    }

    private void OnValidateTimerTick(object? sender, EventArgs e)
    {
        _validateTimer.Stop();
        RefreshDataDirStatus();
    }

    private void OnDataDirLostFocus(object sender, RoutedEventArgs e)
    {
        _validateTimer.Stop();

        if (_step == 1)
        {
            RefreshDataDirStatus();
        }
    }

    private void OnBrowseDataDirClick(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择数据目录",
            Multiselect = false,
        };

        if (picker.ShowDialog(this) != true || string.IsNullOrWhiteSpace(picker.FolderName))
        {
            return;
        }

        SelectDataDirForTest(picker.FolderName);
    }

    private bool RefreshDataDirStatus()
    {
        var valid = ValidateDataDir(out var message, out var status);
        ShowDataDirHint(message, status);

        if (_step == 1)
        {
            NextButton.IsEnabled = valid;
        }

        return valid;
    }

    private bool ValidateDataDir(out string message, out DataDirStatus status)
    {
        message = string.Empty;
        status = DataDirStatus.Error;

        UpdateSelectedDataDir();

        var raw = (DataDirBox.Text ?? string.Empty).Trim().Trim('"').Trim();

        if (raw.Length == 0)
        {
            message = "请填写数据目录路径。";
            return false;
        }

        try
        {
            if (!Path.IsPathFullyQualified(raw))
            {
                message = "请填写完整的绝对路径（例如 D:\\EbookData），不要只写相对目录。";
                return false;
            }

            var full = Path.GetFullPath(raw);
            var root = Path.GetPathRoot(full);

            if (string.IsNullOrEmpty(root))
            {
                message = "这个路径没有盘符，请选择本机固定盘上的目录。";
                return false;
            }

            if (full.StartsWith(@"\\", StringComparison.Ordinal))
            {
                message = "网络路径不能作为数据目录：SQLite 的 WAL 需要本机固定盘的文件锁语义，"
                    + "网络盘上可能出现数据库损坏。请选择本机固定盘。";
                return false;
            }

            var drive = new DriveInfo(root);

            if (!drive.IsReady)
            {
                message = $"目标盘 {root} 当前不可用（可能已拔出或未就绪），请换一个位置。";
                return false;
            }

            var free = drive.AvailableFreeSpace;

            DataDirSpaceText.Text =
                $"所在盘剩余 {FormatBytes(free)}；预计占用约 {FormatBytes(ExpectedUsageBytes)}"
                + $"（缓存上限 {AppSettingsKeys.DefaultCacheLimitMb} MB + 备份上限 {AppSettingsKeys.DefaultBackupLimitMb} MB）。";

            if (drive.DriveType != DriveType.Fixed)
            {
                var typeText = drive.DriveType switch
                {
                    DriveType.Network => "网络盘",
                    DriveType.Removable => "可移动盘（U 盘 / 移动硬盘）",
                    DriveType.CDRom => "光盘",
                    DriveType.Ram => "内存盘",
                    _ => drive.DriveType.ToString(),
                };

                message = $"{typeText}不能作为数据目录：SQLite 的 WAL 不支持网络盘/可移动盘的文件锁语义。"
                    + "请选择本机固定盘（程序本身仍可以放在 U 盘里运行）。";
                return false;
            }

            if (!AppPaths.IsWritable(full, out var writeError))
            {
                message = "这个目录当前不可写，请换一个位置或检查权限。技术原因：" + (writeError ?? "未知");
                return false;
            }

            if (free < ExpectedUsageBytes)
            {
                message = $"剩余空间不足：预计需要约 {FormatBytes(ExpectedUsageBytes)}，"
                    + $"当前只剩 {FormatBytes(free)}。请清理磁盘或换一个空间更大的位置。";
                return false;
            }

            if (free < RecommendedFreeBytes)
            {
                message = $"剩余空间 {FormatBytes(free)}，少于建议的 10 GB。可以继续，但书库变大后可能吃紧。";
                status = DataDirStatus.Warning;
                return true;
            }

            message = $"目录可写，剩余空间 {FormatBytes(free)}，空间充足。";
            status = DataDirStatus.Ok;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException
                                       or NotSupportedException
                                       or PathTooLongException
                                       or IOException
                                       or UnauthorizedAccessException)
        {
            message = "检查数据目录时出错：" + ex.Message;
            return false;
        }
    }

    private void ShowDataDirHint(string message, DataDirStatus status)
    {
        DataDirHintText.Text = message;
        DataDirHintText.Foreground = status switch
        {
            DataDirStatus.Error => FindBrush("DangerBackgroundBrush", Brushes.Firebrick),
            DataDirStatus.Warning => FindBrush("WarningBrush", Brushes.DarkOrange),
            DataDirStatus.Ok => FindBrush("SuccessBrush", Brushes.Green),
            _ => FindBrush("TextSecondaryBrush", Brushes.Gray),
        };
    }

    private void UpdateSelectedDataDir()
    {
        var raw = (DataDirBox.Text ?? string.Empty).Trim().Trim('"').Trim();

        try
        {
            SelectedDataDir = raw.Length > 0 && Path.IsPathFullyQualified(raw)
                ? Path.GetFullPath(raw)
                : raw;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            SelectedDataDir = raw;
        }
    }

    /// <summary>
    /// 第 2 步点「下一步」时应用指针：目标 != 当前数据目录就写指针并标记需要重启；
    /// 改回当前目录则把指针恢复原样（原来没有指针文件就删掉）。
    /// </summary>
    private bool ApplyPointerForSelection()
    {
        var selected = SelectedDataDir;

        if (string.IsNullOrWhiteSpace(selected))
        {
            ShowDataDirHint("请填写数据目录路径。", DataDirStatus.Error);
            return false;
        }

        try
        {
            if (IsSameDirectory(selected, _paths.DataRoot))
            {
                if (_pointerApplied)
                {
                    RestoreOriginalPointer();
                    _pointerApplied = false;
                    RestartRequested = false;
                }

                return true;
            }

            _paths.WritePointerFile(selected);
            _pointerApplied = true;
            RestartRequested = true;
            return true;
        }
        catch (Exception ex)
        {
            ShowDataDirHint("写数据目录指针失败：" + ex.Message, DataDirStatus.Error);
            NextButton.IsEnabled = false;
            return false;
        }
    }

    private void RestoreOriginalPointer()
    {
        if (_hadPointerAtStart)
        {
            _paths.WritePointerFile(_paths.DataRoot);
        }
        else
        {
            _paths.DeletePointerFile();
        }
    }

    /// <summary>关闭时把"用户最后看到的目录"与指针对齐（中途返回改了路径的情况）。</summary>
    private void SyncPointerOnClose()
    {
        var selected = SelectedDataDir;

        if (string.IsNullOrWhiteSpace(selected))
        {
            return;
        }

        try
        {
            if (IsSameDirectory(selected, _paths.DataRoot))
            {
                if (_pointerApplied)
                {
                    RestoreOriginalPointer();
                    _pointerApplied = false;
                    RestartRequested = false;
                }

                return;
            }

            if (!AppPaths.IsWritable(selected, out _))
            {
                // 最后看到的路径不可写：保留上次已经应用的选择，别把指针写坏
                return;
            }

            _paths.WritePointerFile(selected);
            _pointerApplied = true;
            RestartRequested = true;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "首次运行向导关闭时同步数据目录指针失败");
        }
    }

    /// <summary>
    /// 数据目录换到别处时，新目录里要有一份 Config.json，否则下次启动仍会被判为
    /// "全新目录"而再次弹向导。这里把刚保存好的当前配置复制过去（含 firstRunCompleted）。
    /// </summary>
    private void InitializeNewDataDirIfNeeded()
    {
        if (!_pointerApplied || !RestartRequested)
        {
            return;
        }

        var selected = SelectedDataDir;

        if (string.IsNullOrWhiteSpace(selected))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(selected);

            var targetConfig = Path.Combine(selected, "Config.json");

            if (File.Exists(_paths.ConfigFile))
            {
                // 原子写（T3 铁律）：Config.json 是下次启动读的第一份文件，
                // 绝不能出现"复制到一半"的半截内容。
                AtomicFile.Copy(_paths.ConfigFile, targetConfig);
            }
            else
            {
                AtomicFile.WriteAllText(targetConfig, "{\n}\n", Utf8NoBom);
            }

            // 镜像值改成新目录：否则下次启动会先打一条"镜像与指针不一致"的 WARNING
            try
            {
                if (JsonNode.Parse(File.ReadAllText(targetConfig)) is JsonObject root)
                {
                    if (root["paths"] is not JsonObject pathsNode)
                    {
                        pathsNode = new JsonObject();
                        root["paths"] = pathsNode;
                    }

                    pathsNode["dataDir"] = selected;

                    AtomicFile.WriteAllText(
                        targetConfig,
                        root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n",
                        Utf8NoBom);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // 启动时会按指针自动纠正镜像，这里失败不影响使用
                Serilog.Log.Debug(ex, "首次运行向导：改写新数据目录的 Config.json 镜像失败（不影响使用）");
            }

            Serilog.Log.Information("首次运行向导已把数据目录设为 {Dir}，并写入初始配置", selected);
        }
        catch (Exception ex)
        {
            // 写不进去也不挡启动：目标目录为空时下次启动会再弹向导让用户重选
            Serilog.Log.Warning(ex, "首次运行向导初始化新数据目录失败：{Dir}", selected);
        }
    }

    private void SaveFirstRunCompleted()
    {
        if (_firstRunSaved)
        {
            return;
        }

        try
        {
            // 关闭路径是同步的：直接写内存 + 落盘，避免 async void 在进程重启前还没跑完
            _config.SetAsync(AppSettingsKeys.FirstRunCompleted, JsonValue.Create(true))
                .GetAwaiter()
                .GetResult();
            _config.SaveAsync().GetAwaiter().GetResult();
            _firstRunSaved = true;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "首次运行向导：写 ui.firstRunCompleted 失败");
        }
    }

    // ---------- 第 3 步：工具检查 ----------

    private Task EnsureToolsLoadedAsync() => _toolsLoadTask ??= LoadToolsAsync();

    private async Task LoadToolsAsync()
    {
        RecheckButton.IsEnabled = false;
        ToolsStatusText.Text = "正在检测外部工具…";

        try
        {
            var report = await _tools.VerifyAllAsync(runProbes: true);
            _toolItems = report.Items;
            ApplyToolReport(report);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "首次运行向导：工具检测失败");
            ToolsStatusText.Text = "工具检测失败：" + ex.Message
                + "。可以先跳过，稍后在「设置 → 工具」里重新检测。";
        }
        finally
        {
            RecheckButton.IsEnabled = true;
        }
    }

    private void ApplyToolReport(ToolHealthReport report)
    {
        var okBrush = FindBrush("SuccessBrush", Brushes.Green);
        var failBrush = FindBrush("DangerBackgroundBrush", Brushes.Firebrick);
        var warningBrush = FindBrush("WarningBrush", Brushes.DarkOrange);
        var neutralBrush = FindBrush("NeutralBrush", Brushes.Gray);
        var rows = new List<ToolRowUi>(report.Items.Count);

        foreach (var item in report.Items)
        {
            var (mark, brush) = item.State switch
            {
                ToolState.Ok => ("✓", okBrush),
                ToolState.OptionalDisabled => ("–", neutralBrush),
                _ => item.Required ? ("✗", failBrush) : ("!", warningBrush),
            };

            rows.Add(new ToolRowUi(
                mark,
                brush,
                item.DisplayName,
                StateTextOf(item.State),
                string.IsNullOrWhiteSpace(item.Version) ? string.Empty : "版本 " + item.Version,
                FixTextOf(item),
                string.IsNullOrWhiteSpace(item.Detail) ? string.Empty : item.Detail));
        }

        ToolList.ItemsSource = rows;

        if (rows.Count == 0)
        {
            ToolsStatusText.Text = "没有读到工具清单（Tools/manifest.json 可能缺失）；可以先跳过，稍后再配置。";
            return;
        }

        var okCount = report.Items.Count(item => item.State == ToolState.Ok);
        ToolsStatusText.Text =
            $"检测完成：{okCount} 个正常，{rows.Count - okCount} 个需要关注。"
            + "缺失的工具只会禁用对应功能，不影响阅读；可以稍后配置。";
    }

    private static string StateTextOf(ToolState state) => state switch
    {
        ToolState.Ok => "正常",
        ToolState.Missing => "未找到",
        ToolState.VersionMismatch => "版本不符",
        ToolState.ProbeFailed => "检测失败",
        ToolState.OptionalDisabled => "已关闭",
        _ => state.ToString(),
    };

    /// <summary>
    /// "如何安装"文案必须非空：缺少 FixHint 时也给出去哪配置的兜底指引，
    /// 判据是"工具缺失有明确指引"，不能只显示一个红叉。
    /// </summary>
    private static string FixTextOf(ToolHealthItem item)
    {
        if (item.State == ToolState.Ok)
        {
            return "已就绪。";
        }

        if (item.State == ToolState.OptionalDisabled)
        {
            return "已在设置里关闭；不影响阅读。";
        }

        var hint = string.IsNullOrWhiteSpace(item.FixHint)
            ? "在「设置 → 工具」里指定它的路径，或把它解压到程序目录的 Tools 下。"
            : item.FixHint.Trim();

        return "如何安装 / 修复：" + hint;
    }

    private async void OnRecheckClick(object sender, RoutedEventArgs e)
    {
        if (!RecheckButton.IsEnabled)
        {
            return;
        }

        _toolsLoadTask = null;
        await EnsureToolsLoadedAsync();
    }

    private void OnOpenToolsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _shell.OpenFolder(_paths.ToolsRoot);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "首次运行向导：打开 Tools 目录失败");
            AppDialog.Error(this, ex.Message, "打开 Tools 目录失败");
        }
    }

    // ---------- 第 4 步：完成 ----------

    private void UpdateFinishSummary()
    {
        FinishSummaryText.Text = RestartRequested
            ? $"数据目录：{SelectedDataDir}\n新的数据目录会在重启后生效；外部工具可以稍后继续配置。"
            : $"数据目录：{SelectedDataDir}\n外部工具可以稍后到「设置 → 工具」里继续配置。";
    }

    private void OnImportClick(object sender, RoutedEventArgs e)
    {
        ImportRequested = true;
        Close();
    }

    private void OnEmptyLibraryClick(object sender, RoutedEventArgs e)
    {
        ImportRequested = false;
        Close();
    }

    // ---------- 小工具 ----------

    private Brush FindBrush(string resourceKey, Brush fallback)
        => TryFindResource(resourceKey) as Brush ?? fallback;

    private static bool IsSameDirectory(string left, string right)
    {
        try
        {
            var leftFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left));
            var rightFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));

            return string.Equals(leftFull, rightFull, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string FormatBytes(long bytes)
    {
        const double kb = 1024d;

        if (bytes >= kb * kb * kb)
        {
            return (bytes / kb / kb / kb).ToString("F2") + " GB";
        }

        if (bytes >= kb * kb)
        {
            return (bytes / kb / kb).ToString("F1") + " MB";
        }

        if (bytes >= kb)
        {
            return (bytes / kb).ToString("F0") + " KB";
        }

        return bytes + " B";
    }

    /// <summary>
    /// 测试钩子专用：在 UI 线程上边泵消息边等任务完成，避免"阻塞 UI 等 UI 续体"的死锁，
    /// 也让冒烟在 GoToStepForTest(3) 返回时工具检测已经跑完。
    /// </summary>
    private static void PumpUntil(Task task)
    {
        if (task.IsCompleted)
        {
            task.GetAwaiter().GetResult();
            return;
        }

        var frame = new DispatcherFrame();

        _ = task.ContinueWith(
            _ => frame.Continue = false,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }
}

/// <summary>工具检查列表里的一行。</summary>
internal sealed record ToolRowUi(
    string Mark,
    Brush MarkBrush,
    string DisplayName,
    string StateText,
    string VersionText,
    string FixText,
    string Detail);
