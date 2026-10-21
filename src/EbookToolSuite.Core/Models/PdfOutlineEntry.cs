namespace EbookToolSuite.Core.Models;

/// <summary>
/// PDF 目录里的一条（T6-2B）：标题 + 层级 + 目标页。
/// </summary>
/// <param name="Title">显示标题，已从 pdfium 的 UTF-16LE 解出来。</param>
/// <param name="Level">层级，**从 1 起**（1 = 顶层），由书签树的递归深度得出。</param>
/// <param name="PageIndex">目标页，0 基。<c>-1</c> = 这条解不出目标页（纯分组标题，上层画成不可点的项）。</param>
/// <remarks>
/// 【为什么是平铺列表而不是树】Docnet.Core 没有任何 outline API，这份数据是直接 P/Invoke
/// pdfium 的书签函数遍历出来的。"树"在上层的用途只有缩进和折叠，缩进靠 <see cref="Level"/>、
/// 折叠靠"连续的更高 Level"就能表达；返回树反而要上层再写一遍递归展示逻辑，
/// 而 <c>PdfOutlineEntry</c> 一旦有了 <c>Children</c>，回调式的原生遍历就得先建树再交出去，
/// 多一次中间结构。旧的 EPUB 目录（<c>TocEntry</c>）是树，那是 OPC 本身的形状，两者不冲突。
/// </remarks>
public sealed record PdfOutlineEntry(string Title, int Level, int PageIndex);
