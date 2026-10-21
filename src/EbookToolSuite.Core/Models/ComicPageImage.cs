namespace EbookToolSuite.Core.Models;

/// <summary>
/// 已经编码成 JPEG、可以直接交给外壳 &lt;img&gt; 的一页漫画图。
/// </summary>
/// <param name="Jpeg">JPEG 字节（宿主内存里只留这个，不留解码后的位图）。</param>
/// <param name="Width">编码后的像素宽（旋转已应用）。</param>
/// <param name="Height">编码后的像素高（旋转已应用）。</param>
/// <param name="EntryKey">这一页在压缩包里的条目名，用于排障与页码对应。</param>
public sealed record ComicPageImage(byte[] Jpeg, int Width, int Height, string EntryKey);
