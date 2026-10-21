using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Core.Interfaces;

/// <summary>
/// 把图片文件夹打成一个 CBZ。
/// </summary>
/// <remarks>
/// CBZ 就是一个扩展名不同的 zip，没有额外规范，所以用内置的 System.IO.Compression，
/// 不引第三方库。
/// 它**刻意不进转换队列**：队列围绕书目记录（books / book_formats）建，
/// 输入必须是库里的一本书；而 CBZ 的输入是一个图片文件夹，没有书可挂。
/// 详见 docs/DECISIONS.md 里 2026-10-04 那条。
/// </remarks>
public interface ICbzPacker
{
    /// <summary>
    /// 扫描文件夹里的图片并按自然序排序（1.jpg 在 10.jpg 前面）。
    /// 只认扩展名白名单，不读文件内容，不抛异常。
    /// </summary>
    IReadOnlyList<string> ScanImages(string folder, bool recursive = false);

    /// <summary>
    /// 打包。产物的扩展名由调用方决定，这里只管写 zip。
    /// 先写成同目录的临时文件再改名（原子），失败不留半截产物。
    /// </summary>
    Task<CbzPackResult> PackAsync(
        string sourceFolder,
        string outputPath,
        IProgress<ConversionProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// 打包**指定顺序的条目**，并把书籍信息写成包内 <c>ComicInfo.xml</c>。
    /// </summary>
    /// <param name="entries">
    /// 按阅读顺序排好的条目列表（第 1 个就是第 1 页）。条目名必须唯一；
    /// 阅读顺序由条目名自然序承载，所以调用方应保证列表顺序与条目名自然序一致。
    /// </param>
    /// <param name="metadata">
    /// 书籍信息。null 或用户一个字都没填（<see cref="ComicMetadata.HasUserValues"/> 为 false）时不写 ComicInfo.xml。
    /// </param>
    /// <param name="outputPath">产物路径，扩展名由调用方决定。</param>
    /// <remarks>
    /// 它和「按文件夹打包」是同一套原子写逻辑，只是顺序和条目名由调用方决定、
    /// 并且多了 ComicInfo.xml 这个条目（放在包内第一个）。
    /// </remarks>
    Task<CbzPackResult> PackAsync(
        IReadOnlyList<CbzPackEntry> entries,
        ComicMetadata? metadata,
        string outputPath,
        IProgress<ConversionProgress>? progress = null,
        CancellationToken ct = default);
}
