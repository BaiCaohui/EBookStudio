using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Core.Interfaces;

/// <summary>图片处理。实现放 Engines/Images/ImageProcessor.cs（ImageSharp）。</summary>
public interface IImageService
{
    /// <summary>
    /// 把一张封面原图写成两档 JPEG：详情页用的大图与网格用的缩略图。
    /// </summary>
    /// <returns>
    /// false 表示图片无法解码（损坏或不是图片格式），此时**不写任何文件**，
    /// 调用方应改用占位图。true 表示两个文件都已写好。
    /// </returns>
    /// <remarks>
    /// 实现必须写 .part 再改名，避免 UI 或阅读器读到半张图。
    /// 长边只缩不放：小于 fullMaxEdgePx 的图不会被放大。
    /// </remarks>
    Task<bool> WriteCoverVariantsAsync(
        byte[] imageBytes,
        string fullPath,
        string thumbPath,
        int fullMaxEdgePx,
        int thumbWidthPx,
        CancellationToken ct = default);

    /// <summary>
    /// 把用户选的封面原图规范化成"能写进 EPUB"的 JPEG 字节：长边不超过 <paramref name="maxLongEdgePx"/>、
    /// 质量 <paramref name="quality"/>、丢掉 EXIF 等元数据（设计文档 6.4）。
    /// </summary>
    /// <returns>
    /// null 表示这张图解不开（损坏 / 不是图片 / 像素数超上限），此时**不写任何文件、不改任何状态**，
    /// 调用方直接报"图片读不出来"即可。
    /// </returns>
    /// <remarks>
    /// 与 <see cref="WriteCoverVariantsAsync"/> 的分工：那个是给书库缓存写两档图，
    /// 这个是给 EPUB 包内换封面写单张执行计划 T3-3 判据 3 要的就是这条路。
    /// </remarks>
    Task<NormalizedImage?> EncodeCoverJpegAsync(
        byte[] imageBytes,
        int maxLongEdgePx,
        int quality,
        CancellationToken ct = default);
}