namespace EbookToolSuite.Core.Models;

/// <summary>
/// 一次 PDF 页面渲染的三个参数（T6-2B）。
/// </summary>
/// <param name="MaxLongEdge">
/// 交给 pdfium 的渲染长边上限（像素），必须 &gt; 0。
/// 与旧接口 <c>RenderPage(path, pageIndex, maxLongEdge)</c> 的语义一致。
/// </param>
/// <param name="RotationDegrees">
/// 页面的视觉旋转角，只接受 0/90/180/270（顺时针，其它值抛 <c>ArgumentOutOfRangeException</c>）。
/// </param>
/// <param name="Theme">渲染主题，见 <see cref="PdfPageTheme"/>。</param>
/// <remarks>
/// 【为什么旋转放在这里而不是让上层转】pdfium 的 Docnet 封装没有暴露设页面旋转的公开路径
/// （<c>FPDFPage_SetRotation</c> 没被包出来）。与其自己 P/Invoke 去改文档状态
/// （那会污染后续所有渲染，还要负责改回来），不如把结果位图转过来——视觉等价，且无副作用。
/// </remarks>
public sealed record PdfPageRenderOptions(int MaxLongEdge, int RotationDegrees, PdfPageTheme Theme);
