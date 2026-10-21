using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace EbookToolSuite.App.Views;

/// <summary>
/// 单张图片预览窗（CBZ 信息编辑窗里双击某一行 / 某一页打开）。
/// </summary>
/// <remarks>
/// <para>
/// 【为什么用 Show 而不是 ShowDialog】预览是「边看边改」的辅助窗：开着它还要能回去改名、调序，
/// 所以非模态、不阻塞主流程；关掉它（Esc / 关闭按钮 / 窗口 X）不影响编辑窗。
/// </para>
/// <para>
/// 【为什么不用 WPF 默认的整屏黑】窗口背景跟整站一样取主题的 WindowBackgroundBrush，
/// 只有预览区铺一层主题里的深色遮罩（灯箱效果），这样四套主题下都不会白底晃眼，
/// 也不会有写死的颜色。
/// </para>
/// </remarks>
public partial class CbzImagePreviewWindow : Window
{
    /// <summary>
    /// 解码宽度上限：预览窗最大也就一千多像素宽，再高的分辨率只是白占内存
    /// （漫画单页动辄 3000+ 像素，全解码一张就上百 MB）。
    /// </summary>
    private const int PreviewDecodeWidth = 2400;

    private const double MinZoom = 0.1;

    private const double MaxZoom = 8.0;

    private readonly string _sourcePath;

    private int _naturalWidth;
    private int _naturalHeight;
    private double _zoom = 1;

    /// <summary>
    /// 是否还处于「适合窗口」模式：用户滚过轮之后就不再跟着窗口大小重新适配，
    /// 否则拉一下窗口就把用户放大的倍率冲掉了。
    /// </summary>
    private bool _fitToWindow = true;

    /// <param name="sourcePath">源图片绝对路径（只读，不锁文件）。</param>
    /// <param name="caption">抬头文案：第几张 / 共几张 + 包内条目名。</param>
    public CbzImagePreviewWindow(string sourcePath, string caption)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(caption);

        _sourcePath = sourcePath;

        InitializeComponent();

        CaptionText.Text = caption;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var image = LoadImage(_sourcePath);

        if (image is null)
        {
            // WebP / 少见格式 WPF 解不了：这不是错误，给一句话就够，别把用户挡在这里。
            PreviewImage.Visibility = Visibility.Collapsed;
            FallbackText.Text = "这张图打不开（可能是 WPF 解码不了的格式，比如 WebP）。\n源文件没有被动过，可以关掉预览继续编辑。";
            FallbackText.Visibility = Visibility.Visible;
            ZoomText.Text = string.Empty;
            FitButton.IsEnabled = false;
            return;
        }

        _naturalWidth = image.PixelWidth;
        _naturalHeight = image.PixelHeight;
        PreviewImage.Source = image;
        FitToWindow();
    }

    /// <summary>按「适合窗口」的大小显示整张图。</summary>
    private void FitToWindow()
    {
        if (_naturalWidth <= 0 || _naturalHeight <= 0)
        {
            return;
        }

        // 去掉滚动条本身占的宽度，否则「适合窗口」会刚好差一点点、横竖各出一条滚动条。
        var viewportWidth = Math.Max(1, ImageScroll.ActualWidth - SystemParameters.VerticalScrollBarWidth);
        var viewportHeight = Math.Max(1, ImageScroll.ActualHeight - SystemParameters.HorizontalScrollBarHeight);

        _fitToWindow = true;

        // 不放大小图：放大只会糊，用户要放大可以滚轮。
        SetZoom(Math.Min(1.0, Math.Min(viewportWidth / _naturalWidth, viewportHeight / _naturalHeight)));
    }

    private void SetZoom(double zoom)
    {
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);

        PreviewImage.Width = Math.Max(1, _naturalWidth * _zoom);
        PreviewImage.Height = Math.Max(1, _naturalHeight * _zoom);
        ZoomText.Text = "缩放 " + (int)Math.Round(_zoom * 100) + "%";
    }

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_fitToWindow)
        {
            FitToWindow();
        }
    }

    private void OnWindowPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_naturalWidth <= 0 || _naturalHeight <= 0)
        {
            return;
        }

        _fitToWindow = false;

        // 每格滚轮 10%：太快会一滚就过头，太慢又得滚半天。
        SetZoom(e.Delta > 0 ? _zoom * 1.1 : _zoom / 1.1);
        e.Handled = true;
    }

    private void OnFitClick(object sender, RoutedEventArgs e) => FitToWindow();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    /// <summary>
    /// 从磁盘读一张图。<c>OnLoad</c> 会把像素整个读进内存并关掉文件句柄，
    /// 所以预览开着的时候源文件不会被锁住（用户可能还要拿别的工具处理它）。
    /// </summary>
    private static BitmapImage? LoadImage(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.DecodePixelWidth = PreviewDecodeWidth;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            // 解码失败不是异常路径：调用方显示「打不开」的提示即可。
            return null;
        }
    }
}
