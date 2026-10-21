using System.Text.Json.Nodes;
using System.Windows;
using EbookToolSuite.Core.Interfaces;
using Serilog;

namespace EbookToolSuite.App.Interop;

/// <summary>
/// 主窗口位置 / 大小 / 最大化状态 / 最后停留页面的持久化。规格见设计文档 9.10.5。
/// </summary>
/// <remarks>
/// 四条规则，每条都对应一个真实会发生的事故：
/// 1. 最大化时取 RestoreBounds  直接用 Left/Top/Width/Height 拿到的是屏幕尺寸，恢复后窗口比屏幕还大。
/// 2. 恢复前做虚拟桌面校验  拖到副屏后拔掉副屏，窗口会出现在不存在的位置，看起来像"程序打不开了"。
/// 3. 校验失败不弹错，回落 1100x720 居中并记 WRN  用户解决不了这种错误框。
/// 4. 只在 OnClosing 保存一次  写进 OnLocationChanged 会在拖动窗口时写几百次文件。
/// </remarks>
public static class WindowPlacementStore
{
    /// <summary>主窗口在 Config.json 里的路径前缀。</summary>
    public const string MainKey = "ui.windowStates.main";

    private const double DefaultWidth = 1100;
    private const double DefaultHeight = 720;

    /// <summary>至少要在虚拟桌面里露出这么多像素，才认为这个坐标还能用。</summary>
    private const double MinVisibleWidth = 100;
    private const double MinVisibleHeight = 40;

    /// <summary>比这还小的尺寸一定是配置文件被改坏了，直接当无效处理。</summary>
    private const double MinUsableWidth = 200;
    private const double MinUsableHeight = 200;

    /// <summary>上次停留的页面。取不到（全新安装）返回 null，由调用方决定回落。</summary>
    public static string? GetLastRoute(IConfigStore config)
        => config.GetString(MainKey + ".lastRoute");

    /// <summary>
    /// 必须在 MainWindow.Show() 之前调用，否则会先闪一下默认位置。
    /// 返回 true 表示用上了存下来的坐标；false 表示回落了默认位置。
    /// </summary>
    public static bool Restore(Window window, IConfigStore config, ILogger log)
    {
        var left = config.GetDouble(MainKey + ".left", double.NaN);
        var top = config.GetDouble(MainKey + ".top", double.NaN);
        var width = config.GetDouble(MainKey + ".width", DefaultWidth);
        var height = config.GetDouble(MainKey + ".height", DefaultHeight);
        var maximized = config.GetBool(MainKey + ".isMaximized", false);

        // 尺寸先自己校验一遍：负值或 NaN 传给 Rect 会抛异常，启动就崩了
        var sizeOk = IsFinite(width) && IsFinite(height)
            && width >= MinUsableWidth && height >= MinUsableHeight;

        if (!IsFinite(left) || !IsFinite(top) || !sizeOk
            || !IsOnAnyScreen(left, top, width, height))
        {
            log.Warning("窗口坐标不可用（首次启动 / 显示器变化），回落默认位置");
            window.Width = DefaultWidth;
            window.Height = DefaultHeight;
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return false;
        }

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = left;
        window.Top = top;
        window.Width = width;
        window.Height = height;

        if (maximized)
        {
            window.WindowState = WindowState.Maximized;
        }

        return true;
    }
    /// <summary>把当前窗口状态写进配置并落盘。保存失败会抛出，由调用方吞掉。</summary>
    public static async Task SaveAsync(
        Window window, IConfigStore config, string currentRoute, CancellationToken ct = default)
    {
        // 最大化时 Left/Top/Width/Height 是屏幕尺寸，必须取还原尺寸
        var bounds = window.WindowState == WindowState.Maximized
            ? window.RestoreBounds
            : new Rect(window.Left, window.Top, window.Width, window.Height);

        await config.SetAsync(MainKey + ".left", JsonValue.Create(bounds.Left), ct).ConfigureAwait(false);
        await config.SetAsync(MainKey + ".top", JsonValue.Create(bounds.Top), ct).ConfigureAwait(false);
        await config.SetAsync(MainKey + ".width", JsonValue.Create(bounds.Width), ct).ConfigureAwait(false);
        await config.SetAsync(MainKey + ".height", JsonValue.Create(bounds.Height), ct).ConfigureAwait(false);

        await config.SetAsync(
            MainKey + ".isMaximized",
            JsonValue.Create(window.WindowState == WindowState.Maximized),
            ct).ConfigureAwait(false);

        await config.SetAsync(MainKey + ".lastRoute", JsonValue.Create(currentRoute), ct).ConfigureAwait(false);

        await config.SaveAsync(ct).ConfigureAwait(false);
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    /// <summary>
    /// 用虚拟桌面包围盒做校验（SystemParameters.VirtualScreen*）。
    /// 局限：两台显示器不相邻时，落在中间空隙的坐标也会被判为可见。
    /// 这个精度足够（真正的失败场景是拔掉副屏），换来的是零 P/Invoke、零 WinForms 依赖。
    /// </summary>
    private static bool IsOnAnyScreen(double left, double top, double width, double height)
    {
        var virtualScreen = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);

        var hit = Rect.Intersect(new Rect(left, top, width, height), virtualScreen);
        return !hit.IsEmpty && hit.Width >= MinVisibleWidth && hit.Height >= MinVisibleHeight;
    }
}