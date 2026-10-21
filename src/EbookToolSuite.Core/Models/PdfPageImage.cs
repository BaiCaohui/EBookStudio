namespace EbookToolSuite.Core.Models;

/// <summary>
/// 渲染好的一页（T6-2B）：JPEG 字节 + 真实像素尺寸 + 它是不是"图片页"。
/// </summary>
/// <param name="Jpeg">JPEG 编码后的字节；直接喂给前端 <c>&lt;img src="data:image/jpeg;base64,..."&gt;</c>。</param>
/// <param name="Width">**旋转之后**的真实像素宽。</param>
/// <param name="Height">**旋转之后**的真实像素高。</param>
/// <param name="IsImagePage">
/// 这一页是不是"图片页"（无文本层，或抽样像素里彩色占比过高）。
/// 上层用它决定要不要在这页上叠文本层、以及要不要允许"选中文字"。
/// 注意：**渲染主题为 Dark 时图片页不会被反色**，但这个标志位如实回传，
/// 上层不该靠"字节有没有变"去反推。
/// </param>
public sealed record PdfPageImage(byte[] Jpeg, int Width, int Height, bool IsImagePage);
