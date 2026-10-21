using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using EbookToolSuite.App.Commands;
using EbookToolSuite.App.Navigation;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Errors;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;

namespace EbookToolSuite.App.Views.Pages;

/// <summary>
/// 阅读统计（T7-3B）：范围切换 + 四张指标卡 + 近 14 天自绘条形图 + 书库构成 + 最近在读。
///
/// 【口径】全部来自 <see cref="IReadingStatsService"/> 的快照，页面不做聚合：
/// 时长 = ended_at - started_at（挂机不计时、跨本地日按日拆分）、
/// 连续天数是本地自然日连续段、平均速度没有字数数据时显示"—"。
/// 见 docs/DECISIONS.md 的「2026-10-19 T7-3B 阅读统计页」。
///
/// 【三态】加载 / 空 / 错误与活动、备份页同一套：构造只做 InitializeComponent，
/// 真正的查库在 OnNavigatedToAsync；"空"指从来没读过（HasSessions=false），
/// 不是"这个范围没数据"——后者照常显示全 0 的卡片。
/// </summary>
public partial class StatsPage : UserControl, INavigationPage
{
    /// <summary>最高的那根条的高度（像素）。其余按当日时长与它等比。趋势固定看近 14 天。</summary>
    private const double TrendMaxBarHeight = 96;

    /// <summary>零时长的日子也留一根 2px 的底线，否则看起来像"这根柱缺了"。</summary>
    private const double TrendMinBarHeight = 2;

    /// <summary>书库构成横条的固定列宽（标签 / 数字），中间那列是条本身。</summary>
    private const double CompositionLabelWidth = 64;
    private const double CompositionCountWidth = 56;

    private readonly IReadingStatsService _stats;
    private readonly ReadCommand _read;
    private readonly NavigationService _navigation;

    /// <summary>空态里的「去书库」按钮。放在代码里建是因为 EmptyState 的 Extra 是内容槽，
    /// XAML 里嵌套给它起名会撞上 EmptyState 自己的命名范围。</summary>
    private readonly Button _goLibraryButton;

    private readonly List<Border> _trendBars = new();
    private readonly List<int> _statusCounts = new();
    private readonly List<int> _formatCounts = new();

    private ReadingStatsRange _range = ReadingStatsRange.Today;
    private ReadingStatsSnapshot? _snapshot;

    /// <summary>
    /// 加载代次：范围按钮连点会并发发起几次查询，只有最后一次的结果能落到界面上。
    /// 不用 CancellationTokenSource 是因为仓储查询很快，取消反而让状态更难推理。
    /// </summary>
    private int _loadGeneration;

    public StatsPage(
        IReadingStatsService stats,
        ReadCommand read,
        NavigationService navigation)
    {
        // 构造函数只做无副作用的事：页面是懒加载的，这里查库会卡住第一次点击。
        InitializeComponent();

        _stats = stats;
        _read = read;
        _navigation = navigation;

        _goLibraryButton = new Button
        {
            Content = "去书库",
            Style = (Style)FindResource("PrimaryButtonStyle"),
        };
        _goLibraryButton.Click += OnGoLibraryClick;
        EmptyView.Extra = _goLibraryButton;

        ErrorView.Retry += (_, _) => _ = ReloadAsync(_range, CancellationToken.None);
    }

    public async Task OnNavigatedToAsync(object? parameter, CancellationToken ct)
        => await ReloadAsync(_range, ct).ConfigureAwait(true);

    public Task<bool> CanNavigateAwayAsync() => Task.FromResult(true);

    // ==================== 供冒烟断言用的内部探针 ====================

    /// <summary>最近一次成功渲染的快照；还没加载过时为 null。</summary>
    internal ReadingStatsSnapshot? LastSnapshot => _snapshot;

    /// <summary>当前选中的范围。</summary>
    internal ReadingStatsRange CurrentRange => _range;

    /// <summary>
    /// 内容区是否可见（有会话时才是 true，空态 / 错误态为 false）。
    /// 有意隐藏 ContentControl.HasContent——那个是"有没有逻辑子元素"，语义不同。
    /// </summary>
    internal new bool HasContent => ContentHost.Visibility == Visibility.Visible;

    /// <summary>趋势图里的 14 根条（冒烟用来断言数量与 tooltip）。</summary>
    internal IReadOnlyList<Border> TrendBars => _trendBars;

    /// <summary>状态分布四行渲染出来的数字，顺序固定：未读 / 在读 / 已读完 / 弃读。</summary>
    internal IReadOnlyList<int> StatusBarCounts => _statusCounts;

    /// <summary>格式分布各行渲染出来的数字。</summary>
    internal IReadOnlyList<int> FormatBarCounts => _formatCounts;

    internal string DurationText => DurationValue.Text;

    internal string StreakText => StreakValue.Text;

    internal string FinishedText => FinishedValue.Text;

    internal string SpeedText => SpeedValue.Text;

    internal int RecentRowCount => RecentList.Children.Count;

    /// <summary>空态的「去书库」按钮（冒烟用来确认空态可操作）。</summary>
    internal Button GoLibraryButton => _goLibraryButton;

    /// <summary>冒烟用：等同于用户点某个范围按钮（含"立即刷新"）。</summary>
    internal Task SelectRangeForProbeAsync(ReadingStatsRange range)
        => SelectRangeAsync(range);

    /// <summary>冒烟用：等同于用户点「最近在读」里的第 index 行。</summary>
    internal bool OpenRecentForProbe(int index)
    {
        if (index < 0 || index >= RecentList.Children.Count)
        {
            return false;
        }

        return RecentList.Children[index] is Button { Tag: RecentReadingBook book }
            && OpenRecent(book);
    }

    // ==================== 加载与渲染 ====================

    /// <summary>取一次快照并按结果落到三态之一。</summary>
    private async Task ReloadAsync(ReadingStatsRange range, CancellationToken ct)
    {
        var generation = ++_loadGeneration;

        ShowOnly(LoadingView);

        ReadingStatsSnapshot snapshot;

        try
        {
            snapshot = await _stats.GetAsync(range, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 页面被切走 / 程序退出：保留现状就行
            return;
        }
        catch (Exception ex)
        {
            if (generation != _loadGeneration)
            {
                return;
            }

            Serilog.Log.Error(ex, "读取阅读统计失败，范围 {Range}", range);
            ErrorView.Message = ex.SafeMessage();
            ShowOnly(ErrorView);
            return;
        }

        // 期间又点了一次范围：这次的结果已经过期，丢掉，别覆盖更新的界面
        if (generation != _loadGeneration)
        {
            return;
        }

        _range = range;
        _snapshot = snapshot;

        ApplyRangeStyles();
        Render(snapshot);

        // HasSessions 是"从来没读过"，与"这个范围没数据"不是一回事：
        // 选了没读过的范围照常显示全 0 的卡片，用户能看出范围确实没数据。
        ShowOnly(snapshot.HasSessions ? ContentHost : EmptyView);
    }

    /// <summary>同一时刻只显示一个状态（内容 / 空 / 加载 / 错误）。</summary>
    private void ShowOnly(UIElement target)
    {
        ContentHost.Visibility = ReferenceEquals(target, ContentHost)
            ? Visibility.Visible
            : Visibility.Collapsed;
        EmptyView.Visibility = ReferenceEquals(target, EmptyView)
            ? Visibility.Visible
            : Visibility.Collapsed;
        LoadingView.Visibility = ReferenceEquals(target, LoadingView)
            ? Visibility.Visible
            : Visibility.Collapsed;
        ErrorView.Visibility = ReferenceEquals(target, ErrorView)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async Task SelectRangeAsync(ReadingStatsRange range)
    {
        if (range == _range && _snapshot is not null)
        {
            return;
        }

        await ReloadAsync(range, CancellationToken.None).ConfigureAwait(true);
    }

    private void Render(ReadingStatsSnapshot snapshot)
    {
        DurationValue.Text = FormatDuration(snapshot.Totals.Duration);
        StreakValue.Text = snapshot.StreakDays.ToString(CultureInfo.InvariantCulture) + " 天";
        FinishedValue.Text = snapshot.Totals.FinishedBooks.ToString(CultureInfo.InvariantCulture) + " 本";

        // 没有字数（PDF / 漫画）或时长太短时服务给 null：显示"—"，不硬算成 0。
        SpeedValue.Text = snapshot.CharsPerMinute is { } speed
            ? Math.Round(speed).ToString("0", CultureInfo.InvariantCulture) + " 字/分"
            : "—";

        SubtitleText.Text = "范围：" + RangeName(snapshot.Range)
            + " · 书库共 " + snapshot.TotalBooks.ToString(CultureInfo.InvariantCulture) + " 本"
            + " · 统计于 " + snapshot.GeneratedAt.LocalDateTime.ToString("HH:mm", CultureInfo.InvariantCulture)
            + "（数据只在本机统计，不上传）";

        RenderTrend(snapshot.Trend);
        RenderStatus(snapshot.StatusCounts);
        RenderFormats(snapshot.FormatCounts);
        RenderRecent(snapshot.RecentBooks);
    }

    private void ApplyRangeStyles()
    {
        var primary = (Style)FindResource("PrimaryButtonStyle");
        var secondary = (Style)FindResource("SecondaryButtonStyle");

        TodayButton.Style = _range == ReadingStatsRange.Today ? primary : secondary;
        WeekButton.Style = _range == ReadingStatsRange.Week ? primary : secondary;
        MonthButton.Style = _range == ReadingStatsRange.Month ? primary : secondary;
        AllButton.Style = _range == ReadingStatsRange.All ? primary : secondary;
    }

    // ---------- 趋势：自绘条形图（Border 高度映射，不引图表库） ----------

    private void RenderTrend(IReadOnlyList<DailyReading> trend)
    {
        TrendHost.Children.Clear();
        _trendBars.Clear();

        if (trend.Count == 0)
        {
            TrendMaxText.Text = "暂无数据";
            return;
        }

        var maxSeconds = trend.Max(day => day.Duration.TotalSeconds);
        if (maxSeconds <= 0)
        {
            maxSeconds = 1;
        }

        TrendMaxText.Text = "最高 " + FormatDuration(TimeSpan.FromSeconds(maxSeconds));

        var columns = new UniformGrid { Rows = 1 };

        foreach (var day in trend)
        {
            var seconds = day.Duration.TotalSeconds;
            var height = seconds <= 0
                ? TrendMinBarHeight
                : Math.Clamp(seconds / maxSeconds * TrendMaxBarHeight, 6, TrendMaxBarHeight);

            var bar = new Border
            {
                Height = height,
                CornerRadius = new CornerRadius(4),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(2, 0, 2, 0),
            };
            bar.SetResourceReference(
                Border.BackgroundProperty,
                seconds <= 0 ? "SurfacePressedBrush" : "AccentColorBrush");
            ToolTipService.SetToolTip(
                bar,
                day.Day.ToString("M月d日", CultureInfo.InvariantCulture)
                + " · " + FormatDuration(day.Duration));

            var barHost = new Grid { Height = TrendMaxBarHeight };
            barHost.Children.Add(bar);

            var dayText = new TextBlock
            {
                Text = day.Day.ToString("M/d", CultureInfo.InvariantCulture),
                FontSize = 11,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0),
            };
            dayText.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");

            var cell = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
            cell.Children.Add(barHost);
            cell.Children.Add(dayText);

            columns.Children.Add(cell);
            _trendBars.Add(bar);
        }

        TrendHost.Children.Add(columns);
    }

    // ---------- 书库构成：横条 + 数字 ----------

    private void RenderStatus(IReadOnlyList<ReadingStatusCount> counts)
    {
        StatusHost.Children.Clear();
        _statusCounts.Clear();

        var byStatus = counts.ToDictionary(count => count.Status, count => count.Count);
        var max = Math.Max(1, counts.Count == 0 ? 0 : counts.Max(count => count.Count));

        // 固定四行，缺的补 0（服务本身也会补；这里再兜一层，避免将来换数据源时少一行）。
        AddStatusRow(byStatus, ReadingStatus.Unread, "未读", max, "TextTertiaryBrush");
        AddStatusRow(byStatus, ReadingStatus.Reading, "在读", max, "AccentColorBrush");
        AddStatusRow(byStatus, ReadingStatus.Finished, "已读完", max, "SuccessBrush");
        AddStatusRow(byStatus, ReadingStatus.Abandoned, "弃读", max, "NeutralBrush");
    }

    private void AddStatusRow(
        IReadOnlyDictionary<ReadingStatus, int> byStatus,
        ReadingStatus status,
        string label,
        int max,
        string brushKey)
    {
        var count = byStatus.TryGetValue(status, out var value) ? value : 0;
        _statusCounts.Add(count);
        StatusHost.Children.Add(BuildCompositionRow(label, count, max, "本", brushKey));
    }

    private void RenderFormats(IReadOnlyList<ReadingFormatCount> counts)
    {
        FormatHost.Children.Clear();
        _formatCounts.Clear();

        if (counts.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = "还没有登记任何格式文件。",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");
            FormatHost.Children.Add(empty);
            return;
        }

        var max = Math.Max(1, counts.Max(count => count.Count));

        foreach (var item in counts)
        {
            _formatCounts.Add(item.Count);
            FormatHost.Children.Add(
                BuildCompositionRow(FormatName(item.Format), item.Count, max, "份", "AccentColorBrush"));
        }
    }

    /// <summary>
    /// 一行"标签 + 横条 + 数字"。条宽不写死像素：用两列星号按 count : max-count 分配，
    /// 这样窗口缩放、卡片宽度变化都不用重新算。
    /// </summary>
    private static FrameworkElement BuildCompositionRow(
        string label, int count, int max, string unit, string brushKey)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(CompositionLabelWidth) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(CompositionCountWidth) });

        var name = new TextBlock
        {
            Text = label,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        name.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        row.Children.Add(name);

        var track = new Grid
        {
            Height = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0),
        };

        var trackBackground = new Border { CornerRadius = new CornerRadius(4) };
        trackBackground.SetResourceReference(Border.BackgroundProperty, "SurfaceHoverBrush");
        Grid.SetColumnSpan(trackBackground, 2);
        track.Children.Add(trackBackground);

        // count=0 时前一列宽度是 0，填充条自然不可见；max=count 时后一列是 0，条占满。
        track.ColumnDefinitions.Add(
            new ColumnDefinition { Width = new GridLength(count, GridUnitType.Star) });
        track.ColumnDefinitions.Add(
            new ColumnDefinition { Width = new GridLength(Math.Max(max - count, 0), GridUnitType.Star) });

        var fill = new Border { CornerRadius = new CornerRadius(4) };
        fill.SetResourceReference(Border.BackgroundProperty, brushKey);
        Grid.SetColumn(fill, 0);
        track.Children.Add(fill);

        Grid.SetColumn(track, 1);
        row.Children.Add(track);

        var number = new TextBlock
        {
            Text = count.ToString(CultureInfo.InvariantCulture) + " " + unit,
            FontSize = 12,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        number.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");
        Grid.SetColumn(number, 2);
        row.Children.Add(number);

        return row;
    }

    // ---------- 最近在读：点一行打开那本书 ----------

    private void RenderRecent(IReadOnlyList<RecentReadingBook> recent)
    {
        RecentList.Children.Clear();
        RecentEmptyText.Visibility = recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var book in recent)
        {
            var row = new Button
            {
                Style = (Style)FindResource("RecentRowStyle"),
                Tag = book,
                Margin = new Thickness(0, 0, 0, 4),
                Content = BuildRecentRowContent(book),
            };
            row.Click += OnRecentClick;
            ToolTipService.SetToolTip(row, "打开《" + book.Title + "》");
            RecentList.Children.Add(row);
        }
    }

    private static FrameworkElement BuildRecentRowContent(RecentReadingBook book)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel();
        var title = new TextBlock
        {
            Text = book.Title,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        var percent = Math.Clamp(book.Percent, 0, 100);
        var progress = new TextBlock
        {
            Text = "已读 " + Math.Round(percent).ToString("0", CultureInfo.InvariantCulture) + "%",
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 0),
        };
        progress.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");

        text.Children.Add(title);
        text.Children.Add(progress);
        row.Children.Add(text);

        var when = new TextBlock
        {
            Text = book.LastReadAt.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
        };
        when.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");
        Grid.SetColumn(when, 1);
        row.Children.Add(when);

        return row;
    }

    private void OnRecentClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: RecentReadingBook book })
        {
            OpenRecent(book);
        }
    }

    /// <summary>用只带 Id / 标题的壳对象打开阅读窗口（ReadCommand 只用 Id）。</summary>
    private bool OpenRecent(RecentReadingBook book)
    {
        var owner = Window.GetWindow(this);

        if (owner is null)
        {
            // 页面还没挂到窗口上（理论上不会发生）：静默忽略，不弹框
            return false;
        }

        _read.Run(owner, new Book { Id = book.BookId, Title = book.Title });
        return true;
    }

    // ==================== 范围切换与空态按钮 ====================

    private async void OnRangeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }
            || !Enum.TryParse<ReadingStatsRange>(tag, out var range))
        {
            return;
        }

        await SelectRangeAsync(range).ConfigureAwait(true);
    }

    private async void OnGoLibraryClick(object sender, RoutedEventArgs e)
        => await _navigation.NavigateAsync(Routes.Library).ConfigureAwait(true);

    // ==================== 格式化 ====================

    private static string RangeName(ReadingStatsRange range) => range switch
    {
        ReadingStatsRange.Today => "今日",
        ReadingStatsRange.Week => "本周",
        ReadingStatsRange.Month => "本月",
        _ => "全部",
    };

    private static string FormatName(BookFormatKind kind) => kind == BookFormatKind.Unknown
        ? "未知"
        : kind.ToStorageString();

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return "0 分钟";
        }

        if (duration.TotalMinutes < 1)
        {
            return "不到 1 分钟";
        }

        var minutes = (int)Math.Round(duration.TotalMinutes);

        if (minutes < 60)
        {
            return minutes.ToString(CultureInfo.InvariantCulture) + " 分钟";
        }

        // 指标卡只有一张卡宽：超过 48 小时换成"天"表达，避免"1234 小时 56 分"这种长串被截断。
        if (minutes >= 48 * 60)
        {
            var days = minutes / (24 * 60);
            var restHours = (minutes % (24 * 60)) / 60;

            return restHours == 0
                ? days.ToString(CultureInfo.InvariantCulture) + " 天"
                : days.ToString(CultureInfo.InvariantCulture) + " 天 "
                  + restHours.ToString(CultureInfo.InvariantCulture) + " 小时";
        }

        var hours = minutes / 60;
        var rest = minutes % 60;

        return rest == 0
            ? hours.ToString(CultureInfo.InvariantCulture) + " 小时"
            : hours.ToString(CultureInfo.InvariantCulture) + " 小时 "
              + rest.ToString(CultureInfo.InvariantCulture) + " 分";
    }
}
