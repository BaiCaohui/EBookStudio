namespace EbookToolSuite.Core.Models;

/// <summary>
/// 打进 CBZ 的一个条目：哪个源文件 + 在包里叫什么名字。
/// </summary>
/// <param name="SourcePath">磁盘上的源图片绝对路径。打包时只读，绝不改名/移动。</param>
/// <param name="EntryName">
/// 包内条目名（含扩展名）。<b>它同时承载阅读顺序</b>：漫画阅读器按包内文件名自然序排页，
/// 所以调用方要保证这个列表的顺序与文件名自然序一致（见 <see cref="Common.CbzEntryNames"/>）。
/// </param>
public sealed record CbzPackEntry(string SourcePath, string EntryName);
