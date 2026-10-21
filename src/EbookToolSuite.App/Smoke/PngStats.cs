using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EbookToolSuite.App.Smoke;

/// <summary>
/// 探针的"眼睛"：把 CapturePreviewAsync 的 PNG 解成像素，算三个可断言的量。
///   1. 墨迹占比 —— 页面上到底有没有画出东西（白屏 ≈ 0）
///   2. 顶部带墨迹占比 —— 内置工具栏若在，就落在这一条
///   3. 两帧差异 —— URL 参数/跳页到底有没有起作用
/// 只做这三件事。阈值不写在这里，判定留在调用处，好让报告写清"凭什么"。
/// </summary>
public sealed class PngStats
{
    /// <summary>亮度低于此值的像素算"墨"。255 = 纯白。</summary>
    private const int InkLumaThreshold = 200;

    /// <summary>两帧比较时，某个通道差超过它才算"这一像素变了"（抗 JPEG/抗锯齿噪声）。</summary>
    private const int DiffChannelThreshold = 24;

    private readonly byte[] _pixels;

    private PngStats(byte[] pixels, int width, int height)
    {
        _pixels = pixels;
        Width = width;
        Height = height;
        InkRatio = ComputeInkRatio(0, height);
        TopBandInkRatio = ComputeInkRatio(0, Math.Max(1, height * 8 / 100));
    }

    public int Width { get; }

    public int Height { get; }

    public double InkRatio { get; }

    public double TopBandInkRatio { get; }

    public static PngStats Load(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);

        using var stream = new MemoryStream(png, writable: false);
        var decoder = new PngBitmapDecoder(
            stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

        var frame = decoder.Frames[0];
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);

        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);

        return new PngStats(pixels, width, height);
    }

    /// <summary>两帧的差异像素占比。尺寸不同一律算 1.0（没得比）。</summary>
    public double DiffRatio(PngStats other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (other.Width != Width || other.Height != Height)
        {
            return 1.0;
        }

        var changed = 0L;

        for (var i = 0; i < _pixels.Length; i += 4)
        {
            var db = Math.Abs(_pixels[i] - other._pixels[i]);
            var dg = Math.Abs(_pixels[i + 1] - other._pixels[i + 1]);
            var dr = Math.Abs(_pixels[i + 2] - other._pixels[i + 2]);

            if (db > DiffChannelThreshold || dg > DiffChannelThreshold || dr > DiffChannelThreshold)
            {
                changed++;
            }
        }

        return (double)changed / (Width * Height);
    }

    private double ComputeInkRatio(int fromRow, int toRow)
    {
        if (toRow <= fromRow)
        {
            return 0;
        }

        var stride = Width * 4;
        var ink = 0L;

        for (var y = fromRow; y < toRow; y++)
        {
            var rowStart = y * stride;

            for (var x = 0; x < stride; x += 4)
            {
                var i = rowStart + x;
                var luma = (_pixels[i] * 114 + _pixels[i + 1] * 587 + _pixels[i + 2] * 299) / 1000;

                if (luma < InkLumaThreshold)
                {
                    ink++;
                }
            }
        }

        return (double)ink / ((toRow - fromRow) * Width);
    }
}
