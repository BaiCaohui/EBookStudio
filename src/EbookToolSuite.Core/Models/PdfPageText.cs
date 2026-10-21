namespace EbookToolSuite.Core.Models;

/// <summary>
/// 一页的文本层（T6-2B）：纯文本 + 逐字符框。
/// </summary>
/// <param name="Text">pdfium 的 <c>GetText()</c> 原文（含换行）。扫描页为 <c>""</c>。</param>
/// <param name="Chars">
/// 逐字符框，与 <paramref name="Text"/> 一一对应（实测 584 字的页面这里也是 584 个框）。
/// 无文本层的页面是空列表。
/// </param>
public sealed record PdfPageText(string Text, IReadOnlyList<PdfCharBox> Chars);
