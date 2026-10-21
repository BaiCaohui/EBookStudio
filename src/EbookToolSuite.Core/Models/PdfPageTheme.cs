namespace EbookToolSuite.Core.Models;

/// <summary>
/// PDF 自绘引擎的页面主题（T6-2B）。
/// </summary>
/// <remarks>
/// 【为什么是"按主题渲染"而不是渲染后套滤镜】Dark 主题对文字页要做反色，
/// 但反色必须在**编码成 JPEG 之前**在像素上做：套一层 CSS filter 到了 WebView 里
/// 对 <c>img</c> 同样可行，却会把"主题"这件事拆到前后端两处判断，
/// 而且图片页（扫描/照片）不该反色这条规则就没人能统一执行了。
/// </remarks>
public enum PdfPageTheme
{
    /// <summary>原样渲染，不做任何像素改写。</summary>
    Normal,

    /// <summary>文字页反色（近似 CSS <c>invert(1)</c>）；图片页保持原样。</summary>
    Dark,
}
