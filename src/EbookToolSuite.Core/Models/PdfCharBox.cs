namespace EbookToolSuite.Core.Models;

/// <summary>
/// 一个字符的外接框（T6-2B），坐标全部归一化到 <c>[0,1]</c>，**左上角为原点、y 轴向下**。
/// </summary>
/// <param name="Value">字符本身（pdfium 的换行符也会作为 <c>'\n'</c> 出现，框是退化的）。</param>
/// <param name="Left">
/// 左边缘 / 页宽。<see cref="Width"/> 为 0 的退化框（换行）取到的是同一列。
/// </param>
/// <param name="Top">上边缘 / 页高。**数值越小越靠页面上方**（实测结论见服务实现里的注释）。</param>
/// <param name="Width">框宽 / 页宽，恒 ≥ 0。</param>
/// <param name="Height">框高 / 页高，恒 ≥ 0。</param>
/// <remarks>
/// 【为什么归一化而不是给像素】上层要在 WebView 里按任意缩放比例叠文本层，
/// 归一化后与渲染分辨率、与用户的缩放级别都无关。精度上限由 pdfium 给的整数框决定：
/// Docnet 的 <c>BoundBox</c> 是 <c>int</c>（已经乘过渲染缩放），所以 1 个渲染像素
/// 就是归一化误差的下限——服务实现里为此特意用一个较大的探测尺寸来读文本层。
/// </remarks>
public sealed record PdfCharBox(char Value, double Left, double Top, double Width, double Height);
