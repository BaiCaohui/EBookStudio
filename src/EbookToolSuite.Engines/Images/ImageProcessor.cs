using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace EbookToolSuite.Engines.Images;

/// <summary>
/// ImageSharp 实现的封面处理（设计文档 6.4）。
/// 只做缩放与编码，不做文字叠加（文字叠加走 WPF 渲染，见设计文档 6.4 的决策）。
/// </summary>
public sealed class ImageProcessor : IImageService
{
    private const int JpegQualityFull = 85;
    private const int JpegQualityThumb = 82;

    /// <summary>单张封面解码后允许占用的最大像素数，防 zip bomb 式的巨图。</summary>
    private const long MaxPixels = 80_000_000;

    public async Task<bool> WriteCoverVariantsAsync(
        byte[] imageBytes,
        string fullPath,
        string thumbPath,
        int fullMaxEdgePx,
        int thumbWidthPx,
        CancellationToken ct = default)
    {
        if (imageBytes.Length == 0)
        {
            return false;
        }

        Image image;
        try
        {
            image = Image.Load(imageBytes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 封面解不开不是致命错误：调用方会改用占位图（执行计划 T1-2 判据）。
            // 不写任何文件，保证"要么两档都在，要么一个都不在"。
            return false;
        }

        using (image)
        {
            if ((long)image.Width * image.Height > MaxPixels)
            {
                return false;
            }

            ct.ThrowIfCancellationRequested();

            var full = FitLongestEdge(image.Width, image.Height, fullMaxEdgePx);
            var thumb = FitWidth(image.Width, image.Height, thumbWidthPx);

            try
            {
                using var fullImage = image.Clone(x => x.Resize(full.Width, full.Height));
                await SaveJpegAsync(fullImage, fullPath, JpegQualityFull, ct).ConfigureAwait(false);

                using var thumbImage = image.Clone(x => x.Resize(thumb.Width, thumb.Height));
                await SaveJpegAsync(thumbImage, thumbPath, JpegQualityThumb, ct).ConfigureAwait(false);
            }
            catch
            {
                // 写失败时把可能存在的半个产物清掉，不留孤儿文件
                TryDelete(fullPath);
                TryDelete(thumbPath);
                throw;
            }
        }

        return true;
    }

    /// <summary>长边不超过 maxEdge，只缩不放。</summary>
    private static Size FitLongestEdge(int width, int height, int maxEdge)
    {
        if (maxEdge <= 0 || (width <= maxEdge && height <= maxEdge))
        {
            return new Size(Math.Max(1, width), Math.Max(1, height));
        }

        var scale = (double)maxEdge / Math.Max(width, height);
        return new Size(
            Math.Max(1, (int)Math.Round(width * scale)),
            Math.Max(1, (int)Math.Round(height * scale)));
    }

    /// <summary>宽度固定为 targetWidth，高度按比例；原图更窄时不放大。</summary>
    private static Size FitWidth(int width, int height, int targetWidth)
    {
        if (targetWidth <= 0 || width <= targetWidth)
        {
            return new Size(Math.Max(1, width), Math.Max(1, height));
        }

        var scale = (double)targetWidth / width;
        return new Size(targetWidth, Math.Max(1, (int)Math.Round(height * scale)));
    }

    /// <summary>写 .part 再改名：UI 或阅读器永远读不到半张图。</summary>
    private static async Task SaveJpegAsync(Image image, string path, int quality, CancellationToken ct)
    {
        var part = path + ".part";
        try
        {
            await image.SaveAsJpegAsync(part, new JpegEncoder { Quality = quality }, ct)
                .ConfigureAwait(false);
            File.Move(part, path, overwrite: true);
        }
        catch
        {
            TryDelete(part);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // 清理失败不影响主流程：Temp 里的残留由启动清理负责
        }
    }

    public Task<NormalizedImage?> EncodeCoverJpegAsync(
        byte[] imageBytes,
        int maxLongEdgePx,
        int quality,
        CancellationToken ct = default)
    {
        if (imageBytes.Length == 0)
        {
            return Task.FromResult<NormalizedImage?>(null);
        }

        Image image;
        try
        {
            image = Image.Load(imageBytes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Task.FromResult<NormalizedImage?>(null);
        }

        using (image)
        {
            if ((long)image.Width * image.Height > MaxPixels)
            {
                return Task.FromResult<NormalizedImage?>(null);
            }

            ct.ThrowIfCancellationRequested();

            var target = FitLongestEdge(image.Width, image.Height, maxLongEdgePx);

            using var resized = image.Clone(x => x.Resize(target.Width, target.Height));

            // 元数据一律不带走：换封面不该把用户原图的 GPS/相机型号带进书里，
            // 顺带省掉几 KB（EXIF 有时比缩略图本身还大）。
            resized.Metadata.ExifProfile = null;
            resized.Metadata.XmpProfile = null;

            using var buffer = new MemoryStream();
            resized.Save(buffer, new JpegEncoder { Quality = Math.Clamp(quality, 1, 100) });
            return Task.FromResult<NormalizedImage?>(
                new NormalizedImage(buffer.ToArray(), target.Width, target.Height));
        }
    }
}