using EbookToolSuite.Engines.Images;
using EbookToolSuite.Services.Library;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace EbookToolSuite.Services.Tests.Library;

/// <summary>
/// T3-3 判据 4 的 Services 侧：换了封面之后，书库缓存的两档 JPEG（{bookId:D}.jpg 大图与
/// {bookId:D}_t.jpg 网格缩略图）必须跟着换，否则网格还是老图。
/// 全部真实文件系统 + 真实 ImageSharp，不 mock（笔者的测试约定）。
/// </summary>
public sealed class LibraryCoverServiceTests
{
    [Fact]
    public async Task 刷新封面_两档缓存都会被重写且尺寸口径与导入一致()
    {
        await using var sandbox = new ImportSandbox();
        var service = new LibraryCoverService(sandbox.Paths, new ImageProcessor());
        var bookId = Guid.NewGuid();

        // 先塞一张"旧封面"
        Assert.True(await service.RefreshAsync(bookId, MakeJpeg(600, 900, 40)));
        var fullBefore = File.ReadAllBytes(service.FullPath(bookId));
        var thumbBefore = File.ReadAllBytes(service.ThumbPath(bookId));

        // 再换一张长宽比完全不同的
        Assert.True(await service.RefreshAsync(bookId, MakeJpeg(400, 1000, 200)));

        Assert.False(File.ReadAllBytes(service.FullPath(bookId)).SequenceEqual(fullBefore));
        Assert.False(File.ReadAllBytes(service.ThumbPath(bookId)).SequenceEqual(thumbBefore));

        using (var thumb = Image.Load(service.ThumbPath(bookId)))
        {
            Assert.Equal(LibraryCoverService.ThumbWidthPx, thumb.Width);
            Assert.Equal(800, thumb.Height);   // 400x1000 等比缩到宽 320 -> 320x800
        }

        using (var full = Image.Load(service.FullPath(bookId)))
        {
            Assert.Equal(400, full.Width);     // 长边 1000 < 1200：只缩不放
            Assert.Equal(1000, full.Height);
        }

        // 路径口径必须和导入时一模一样，否则网格根本找不到图
        Assert.Equal(
            Path.Combine(sandbox.Paths.CoversCacheDir, bookId.ToString("D") + ".jpg"),
            service.FullPath(bookId));
        Assert.Equal(
            Path.Combine(sandbox.Paths.CoversCacheDir, bookId.ToString("D") + "_t.jpg"),
            service.ThumbPath(bookId));
    }

    [Fact]
    public async Task 刷新封面_图解不开时两张旧图一个字节都不动()
    {
        await using var sandbox = new ImportSandbox();
        var service = new LibraryCoverService(sandbox.Paths, new ImageProcessor());
        var bookId = Guid.NewGuid();

        Assert.True(await service.RefreshAsync(bookId, MakeJpeg(600, 900, 40)));
        var fullBefore = File.ReadAllBytes(service.FullPath(bookId));
        var thumbBefore = File.ReadAllBytes(service.ThumbPath(bookId));

        // 用户选了个不是图片的文件
        Assert.False(await service.RefreshAsync(bookId, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));

        Assert.True(File.ReadAllBytes(service.FullPath(bookId)).SequenceEqual(fullBefore));
        Assert.True(File.ReadAllBytes(service.ThumbPath(bookId)).SequenceEqual(thumbBefore));
    }

    [Fact]
    public async Task 刷新封面_空图直接返回false且不写任何文件()
    {
        await using var sandbox = new ImportSandbox();
        var service = new LibraryCoverService(sandbox.Paths, new ImageProcessor());
        var bookId = Guid.NewGuid();

        Assert.False(await service.RefreshAsync(bookId, Array.Empty<byte>()));

        Assert.False(File.Exists(service.FullPath(bookId)));
        Assert.False(File.Exists(service.ThumbPath(bookId)));
    }

    private static byte[] MakeJpeg(int width, int height, byte tone)
    {
        using var image = new Image<Rgb24>(width, height, new Rgb24(tone, (byte)(255 - tone), 128));
        using var stream = new MemoryStream();
        image.SaveAsJpeg(stream);
        return stream.ToArray();
    }
}