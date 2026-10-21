using System.IO.Compression;
using EbookToolSuite.Services.Comics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace EbookToolSuite.Services.Tests.Comics;

/// <summary>
/// 漫画服务（T6-3）：真 JPEG 进、真 JPEG 出。验证缩放不改宽高比、旋转会互换宽高、
/// 透明图铺白（不能变黑页）、封面失败不抛。
/// </summary>
public sealed class ComicBookServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "ets-comicbook-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly ComicBookService _service = new();

    public ComicBookServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception)
        {
            // 临时目录清不掉不影响判定。
        }
    }

    private static byte[] EncodeJpeg(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height, Color.White);
        using var buffer = new MemoryStream();

        image.Save(buffer, new JpegEncoder { Quality = 90 });

        return buffer.ToArray();
    }

    private static byte[] EncodeTransparentPng(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height, new Rgba32(0, 0, 0, 0));
        using var buffer = new MemoryStream();

        image.Save(buffer, new PngEncoder());

        return buffer.ToArray();
    }

    private string BuildCbz(string fileName, params (string Name, byte[] Bytes)[] entries)
    {
        var path = Path.Combine(_root, fileName);

        using (var stream = File.Create(path))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var (name, bytes) in entries)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);

                using var entryStream = entry.Open();
                entryStream.Write(bytes);
            }
        }

        return path;
    }

    [Fact]
    public void RenderPage_比上限小的页_不放大()
    {
        var path = BuildCbz("small.cbz", ("1.jpg", EncodeJpeg(200, 300)));

        using var book = _service.Open(path);
        var page = book.RenderPage(0, 1600, 0);

        Assert.Equal(200, page.Width);
        Assert.Equal(300, page.Height);
        Assert.True(page.Jpeg.Length > 0);
        Assert.Equal(0xFF, page.Jpeg[0]);
        Assert.Equal(0xD8, page.Jpeg[1]);
    }

    [Fact]
    public void RenderPage_超过最长边_按比例缩小()
    {
        var path = BuildCbz("big.cbz", ("1.jpg", EncodeJpeg(1000, 1500)));

        using var book = _service.Open(path);
        var page = book.RenderPage(0, 500, 0);

        Assert.Equal(500, Math.Max(page.Width, page.Height));
        Assert.InRange(page.Width, 332, 334);
    }

    [Fact]
    public void RenderPage_旋转90度_宽高互换()
    {
        var path = BuildCbz("rotate.cbz", ("1.jpg", EncodeJpeg(200, 300)));

        using var book = _service.Open(path);
        var page = book.RenderPage(0, 1600, 90);

        Assert.Equal(300, page.Width);
        Assert.Equal(200, page.Height);
    }

    [Fact]
    public void RenderPage_透明PNG_铺白底而不是黑页()
    {
        var path = BuildCbz("alpha.cbz", ("1.png", EncodeTransparentPng(120, 160)));

        using var book = _service.Open(path);
        var page = book.RenderPage(0, 1600, 0);

        using var decoded = Image.Load<Rgb24>(page.Jpeg);
        var pixel = decoded[decoded.Width / 2, decoded.Height / 2];

        // PNG 的透明像素直接存 JPEG 会变黑（T6-2B 在 PDF 上踩过同一个坑），必须铺白。
        Assert.True(pixel.R >= 240 && pixel.G >= 240 && pixel.B >= 240,
            $"中心像素应是白色，实际 ({pixel.R},{pixel.G},{pixel.B})");
    }

    [Fact]
    public void Open_页面按自然序排列()
    {
        var path = BuildCbz(
            "order.cbz",
            ("page10.jpg", EncodeJpeg(40, 60)),
            ("page2.jpg", EncodeJpeg(40, 60)),
            ("page1.jpg", EncodeJpeg(40, 60)));

        using var book = _service.Open(path);

        Assert.Equal(3, book.PageCount);
        Assert.Equal("page1.jpg", book.RenderPage(0, 800, 0).EntryKey);
        Assert.Equal("page2.jpg", book.RenderPage(1, 800, 0).EntryKey);
        Assert.Equal("page10.jpg", book.RenderPage(2, 800, 0).EntryKey);
    }

    [Fact]
    public void RenderCover_正常包_返回JPEG()
    {
        var path = BuildCbz("cover.cbz", ("1.jpg", EncodeJpeg(300, 400)));

        var cover = _service.RenderCover(path, 1200);

        Assert.NotNull(cover);
        Assert.True(cover!.Length > 0);
    }

    [Fact]
    public void RenderCover_坏包_返回null不抛()
    {
        var path = Path.Combine(_root, "broken.cbz");
        File.WriteAllText(path, "不是压缩包");

        Assert.Null(_service.RenderCover(path, 1200));
    }

    [Fact]
    public void Probe_有效包_页数正确()
    {
        var path = BuildCbz("probe.cbz", ("1.jpg", EncodeJpeg(40, 60)), ("2.jpg", EncodeJpeg(40, 60)));

        var result = _service.Probe(path);

        Assert.True(result.LooksLikeComic);
        Assert.Equal(2, result.PageCount);
    }
}
