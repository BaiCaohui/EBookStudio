namespace EbookToolSuite.Core.Models;

/// <summary>
/// 规范化之后的封面图片（执行计划 T3-3）。
/// <see cref="Bytes"/> 是**可以直接写进 EPUB** 的 JPEG 字节，不是用户原图。
/// </summary>
/// <param name="Bytes">JPEG 字节（不含元数据）。</param>
/// <param name="Width">编码后的宽（px）。</param>
/// <param name="Height">编码后的高（px）。</param>
public sealed record NormalizedImage(byte[] Bytes, int Width, int Height);

/// <summary>
/// 一次封面替换的完整结果（执行计划 T3-3）。
/// 既给出"写回 OPF 的 href"，也给出"工作区里文件的绝对路径"，调用方不用自己拼路径。
/// </summary>
/// <param name="Href">写进 OPF manifest 的 href（以 OPF 所在目录为基准的相对路径）。</param>
/// <param name="AbsolutePath">工作区里封面文件的实际路径（% 转义已解码）。</param>
/// <param name="MediaType">写进 manifest 的 media-type；封面恒为 image/jpeg。</param>
/// <param name="ItemId">封面条目的 id（新建时自动分配且保证不与已有 id 冲突）。</param>
/// <param name="Created">true = 原书没有封面条目，这次新建了（判据 5）。</param>
/// <param name="Renamed">true = 原封面不是 JPEG，href 被改成 .jpg（引用已重写、旧文件已清）。</param>
/// <param name="PreviousHref">改动前的封面 href；原书没有封面时为 null。</param>
/// <param name="Width">写进 EPUB 的图宽（px）。</param>
/// <param name="Height">写进 EPUB 的图高（px）。</param>
/// <param name="ByteCount">写进 EPUB 的 JPEG 字节数。</param>
public sealed record CoverReplaceResult(
    string Href,
    string AbsolutePath,
    string MediaType,
    string ItemId,
    bool Created,
    bool Renamed,
    string? PreviousHref,
    int Width,
    int Height,
    int ByteCount);