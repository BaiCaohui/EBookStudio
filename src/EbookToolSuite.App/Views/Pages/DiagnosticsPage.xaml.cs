using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EbookToolSuite.App.Navigation;
using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Core.Errors;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Infrastructure.Logging;
using Serilog;

namespace EbookToolSuite.App.Views.Pages;

/// <summary>
/// 诊断页。内容全部来自阶段 0 已经做好、当时只能挂在菜单上的功能（设计文档 9.9.13）。
/// 它默认不出现在左栏，只有 --debug-ui 或 Config.ui.showDiagnostics 时才可见。
/// </summary>
public partial class DiagnosticsPage : UserControl, INavigationPage
{
    private const int LogLines = 200;

    private readonly IAppPaths _paths;
    private readonly IShellLauncher _shell;
    private readonly IToolLocator _tools;

    public DiagnosticsPage(IAppPaths paths, IShellLauncher shell, IToolLocator tools)
    {
        _paths = paths;
        _shell = shell;
        _tools = tools;

        InitializeComponent();

        BuildPathRows();
        ToolsRootText.Text = "工具目录：" + _paths.ToolsRoot;
    }

    /// <summary>每次进页面都刷新一次：日志与健康状态都是会变的。</summary>
    public async Task OnNavigatedToAsync(object? parameter, CancellationToken ct)
    {
        RefreshLog();
        await RefreshHealthAsync(runProbes: false).ConfigureAwait(true);
    }

    public Task<bool> CanNavigateAwayAsync() => Task.FromResult(true);

    // ---------- 路径卡片 ----------

    private void BuildPathRows()
    {
        AddPathRow(0, "程序目录", _paths.AppRoot, null);
        AddPathRow(1, "数据目录", _paths.DataRoot, _paths.DataRoot);
        AddPathRow(2, "日志目录", _paths.LogsDir, _paths.LogsDir);
    }

    private void AddPathRow(int row, string label, string value, string? openTarget)
    {
        var caption = new TextBlock
        {
            Text = label,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 6),
        };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        Grid.SetRow(caption, row);
        Grid.SetColumn(caption, 0);

        var text = new TextBlock
        {
            Text = value,
            Margin = new Thickness(0, 0, 12, 6),
        };
        text.SetResourceReference(StyleProperty, "MonoTextStyle");
        Grid.SetRow(text, row);
        Grid.SetColumn(text, 1);

        PathsGrid.Children.Add(caption);
        PathsGrid.Children.Add(text);

        if (openTarget is null)
        {
            return;
        }

        var button = new Button
        {
            Content = "打开",
            Height = 24,
            Padding = new Thickness(10, 0, 10, 0),
            Margin = new Thickness(0, 0, 0, 6),
        };
        button.SetResourceReference(StyleProperty, "SecondaryButtonStyle");
        button.Click += (_, _) => SafeRun("打开目录", () => _shell.OpenFolder(openTarget));
        Grid.SetRow(button, row);
        Grid.SetColumn(button, 2);
        PathsGrid.Children.Add(button);
    }

    // ---------- 工具健康卡片 ----------

    private async void OnRecheckClick(object sender, RoutedEventArgs e)
    {
        // 手动重检才跑版本探测，并先丢弃缓存（与主窗口的"重新检测"一致）
        if (_tools is Engines.Processes.ToolLocator concrete)
        {
            concrete.InvalidateHealthCache();
        }

        await RefreshHealthAsync(runProbes: true).ConfigureAwait(true);
    }

    private async Task RefreshHealthAsync(bool runProbes)
    {
        // T7-3：先告诉用户"正在检测"，而不是先清空成一片空白再 await（原来会闪一下空表）
        HealthRows.Children.Clear();
        HealthRows.Children.Add(MakeLine("正在检测工具…", "TextSecondaryBrush"));

        ToolHealthReport report;
        try
        {
            report = await _tools.VerifyAllAsync(runProbes).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            HealthRows.Children.Clear();
            HealthRows.Children.Add(MakeLine("检查失败：" + ex.SafeMessage(), "DangerBackgroundBrush"));
            return;
        }

        HealthRows.Children.Clear();

        foreach (var item in report.Items)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // 状态圆点
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 8,
                Height = 8,
                VerticalAlignment = VerticalAlignment.Center,
            };
            dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, DotBrushKey(item));
            Grid.SetColumn(dot, 0);

            var name = MakeCell(item.DisplayName + (item.Required ? "" : "（可选）"), "TextPrimaryBrush");
            Grid.SetColumn(name, 1);

            var state = MakeCell(StateText(item.State), DotBrushKey(item));
            Grid.SetColumn(state, 2);

            var version = MakeCell(item.Version ?? "", "TextTertiaryBrush");
            Grid.SetColumn(version, 3);

            var hint = MakeCell(item.FixHint ?? item.Detail ?? "", "TextSecondaryBrush", wrap: true);
            Grid.SetColumn(hint, 4);

            row.Children.Add(dot);
            row.Children.Add(name);
            row.Children.Add(state);
            row.Children.Add(version);
            row.Children.Add(hint);
            HealthRows.Children.Add(row);
        }

        if (report.Items.Count == 0)
        {
            HealthRows.Children.Add(MakeCell("（还没有体检结果）", "TextTertiaryBrush"));
        }
    }

    private static string DotBrushKey(ToolHealthItem item) => item.State switch
    {
        ToolState.Ok => "SuccessBrush",
        ToolState.OptionalDisabled => "NeutralBrush",
        _ => "DangerBackgroundBrush",
    };

    private static string StateText(ToolState state) => state switch
    {
        ToolState.Ok => "正常",
        ToolState.Missing => "缺失",
        ToolState.VersionMismatch => "版本不符",
        ToolState.ProbeFailed => "探测失败",
        ToolState.OptionalDisabled => "已关闭",
        _ => state.ToString(),
    };

    private static TextBlock MakeCell(string text, string brushKey, bool wrap = false)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            Margin = new Thickness(0, 1, 8, 1),
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return block;
    }

    private static TextBlock MakeLine(string text, string brushKey)
        => MakeCell(text, brushKey, wrap: true);

    // ---------- 日志卡片 ----------

    private void RefreshLog()
    {
        // 必须读内存环形缓冲，不要读文件：日志文件被 Serilog 持有，
        // File.ReadAllText 请求 FileShare.Read，写入瞬间会被拒绝（阶段 0 实测过）。
        var lines = LogRingBuffer.Instance.Snapshot(LogLines);
        LogText.Text = lines.Count == 0 ? "（还没有日志）" : string.Join(Environment.NewLine, lines);
    }

    private void OnCopyLogClick(object sender, RoutedEventArgs e)
        => SafeRun("复制日志", () =>
        {
            var lines = LogRingBuffer.Instance.Snapshot(LogLines);
            if (lines.Count == 0)
            {
                AppDialog.Show("内存缓冲里还没有日志。", "复制日志", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Clipboard.SetText(string.Join(Environment.NewLine, lines));
        });

    private void OnOpenLogDirClick(object sender, RoutedEventArgs e)
        => SafeRun("打开日志目录", () => _shell.OpenFolder(_paths.LogsDir));

    // ---------- 诊断动作 ----------

    private void OnThrowUiClick(object sender, RoutedEventArgs e)
        => throw new InvalidOperationException("诊断：这是从 UI 线程故意抛出的测试异常。");

    private void OnThrowTaskClick(object sender, RoutedEventArgs e)
    {
        _ = Task.Run(() => throw new InvalidOperationException(
            "诊断：这是故意不观察的测试 Task 异常。"));

        AppDialog.Show(
            "已在后台抛出一个不被观察的异常。等 GC 触发后日志里会有记录（约 10-30 秒）。",
            "诊断", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static void SafeRun(string action, Action body)
    {
        try
        {
            body();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "{Action} 失败", action);
            AppDialog.Show(ex.SafeMessage(), action + " 失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}