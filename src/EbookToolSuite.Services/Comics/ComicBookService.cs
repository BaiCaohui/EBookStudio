using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Engines.Comics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace EbookToolSuite.Services.Comics;

/// <summary>
/// 漫画（CBZ/CBR）服务：把 <see cref="ComicArchive"/> 的原始图片字节变成
/// 外壳能直接显示的 JPEG。T6-3。
/// </summary>
/// <remarks>
/// <para>
/// 为什么在宿主侧解码/缩放而不把原图原样交给浏览器：漫画页常见 3000×4000，
/// 浏览器解码后一张就是约 48 MB。按最长边 1600 缩放后一张约 1 MB 级，
/// 内存才守得住「快速翻页不 OOM（≤500 MB）」（计划 T6-3 判据 4）。
/// </para>
/// <para>
/// JPEG 质量固定 85，与 PDF 页渲染/封面管线一致：同一套视觉标准，
/// 不为漫画单独发明一套参数。
/// </para>
/// </remarks>
public sealed class ComicBookService : IComicBookService
{
    private const int JpegQuality = 85;

    /// <summary>封面的最长边夹逼（与 PDF 封面同口径：太小看不清、太大白烧内存）。</summary>
    private const int MinCoverLongEdge = 320;
    private const int MaxCoverLongEdge = 1600;

    public ComicProbeResult Probe(string path) => ComicProbe.Inspect(path);

    public IComicBook Open(string path) => new ComicBook(ComicArchive.Open(path));

    public byte[]? RenderCover(string path, int maxLongEdge)
    {
        var longEdge = Math.Clamp(maxLongEdge, MinCoverLongEdge, MaxCoverLongEdge);

        try
        {
            using var book = Open(path);
            return book.RenderPage(0, longEdge, 0).Jpeg;
        }
        catch (Exception ex) when (ex is AppException or IOException)
        {
            /* 与 PDF 同一口径：封面渲不出来就用占位图，**不阻塞导入**
               （设计文档 §6.5.4 的失败策略同样适用于这里）。 */
            return null;
        }
    }

    /// <summary>一本打开着的漫画包。串行使用（阅读器侧只有一条渲染闸门）。</summary>
    private sealed class ComicBook : IComicBook
    {
        private readonly ComicArchive _archive;

        public ComicBook(ComicArchive archive) => _archive = archive;

        public int PageCount => _archive.PageCount;

        public ComicPageImage RenderPage(int index, int maxLongEdge, int rotation)
        {
            var bytes = _archive.ReadPage(index);
            var normalized = NormalizeRotation(rotation);
            var cap = Math.Clamp(maxLongEdge, 200, 4000);

            Image image;

            try
            {
                image = Image.Load(bytes);
            }
            catch (Exception ex)
            {
                throw new AppException(
                    ErrorCode.CorruptFile,
                    "这一页漫画图片解不开（文件可能损坏，或不是支持的图片格式）。",
                    technical: _archive.PageKeys[index],
                    inner: ex);
            }

            using (image)
            {
                image.Mutate(context =>
                {
                    /* 透明区必须先在白底上合成：PNG 的透明像素存成 JPEG 会变黑，
                       一整页看起来就是"黑屏"（T6-2B 在 PDF 页上踩过同一个坑）。
                       不按颜色刷白——那会抹掉真正的黑线稿。 */
                    context.BackgroundColor(Color.White);

                    switch (normalized)
                    {
                        case 90: context.Rotate(RotateMode.Rotate90); break;
                        case 180: context.Rotate(RotateMode.Rotate180); break;
                        case 270: context.Rotate(RotateMode.Rotate270); break;
                    }

                    var width = image.Width;
                    var height = image.Height;
                    var longEdge = Math.Max(width, height);

                    if (longEdge > cap)
                    {
                        var scale = (double)cap / longEdge;
                        var targetWidth = Math.Max(1, (int)Math.Round(width * scale));
                        var targetHeight = Math.Max(1, (int)Math.Round(height * scale));

                        context.Resize(targetWidth, targetHeight, KnownResamplers.Lanczos3);
                    }
                });

                using var output = new MemoryStream();
                image.Save(output, new JpegEncoder { Quality = JpegQuality });

                return new ComicPageImage(
                    output.ToArray(), image.Width, image.Height, _archive.PageKeys[index]);
            }
        }

        public void Dispose() => _archive.Dispose();
    }

    private static int NormalizeRotation(int rotation)
    {
        var normalized = rotation % 360;

        return normalized < 0 ? normalized + 360 : normalized;
    }
}
