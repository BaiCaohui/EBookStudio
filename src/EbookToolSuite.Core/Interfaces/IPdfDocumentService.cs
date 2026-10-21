using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Core.Interfaces;

/// <summary>
/// PDF 文档的两个底层能力：总页数、把某一页渲染成图片字节。
/// 实现放 Services/Pdf/PdfDocumentService.cs（pdfium，经 Docnet.Core 封装）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么只有这两个方法：设计文档 §6.5.4「页数与首页封面」要求导入时用 pdfium 打开一次，
/// 拿到总页数写进格式记录（阅读进度算百分比要用），再渲染第 1 页交给既有封面管线。
/// 眼下没有第三个需求，所以不预先抽象「全文检索」「分页缓存」这类还没落地的能力。
/// </para>
/// <para>
/// 为什么是同步的：这两个方法都是对本地文件的纯 CPU/原生调用，pdfium 本身不提供异步接口，
/// 包一层 <c>Task.Run</c> 只会假装异步、还多占一个线程池线程。调用方在 UI 线程上直接调
/// 会卡住界面，所以约定调用方自己用 <c>Task.Run</c> 把整段包起来（封面管线本来就是后台任务）。
/// </para>
/// </remarks>
public interface IPdfDocumentService
{
    /// <summary>取 PDF 总页数。</summary>
    /// <param name="path">PDF 文件路径。</param>
    /// <param name="password">
    /// PDF 口令；null/空表示没口令。**必须支持**（T6-2B）：阶段一加密书由 Chromium 的内置
    /// 密码框接管，自绘引擎没有那一层，不给口令就一页都渲染不出来——而页数是渲染的前提，
    /// 所以加密书问密码的第一站就是这里。
    /// </param>
    /// <returns>总页数，恒 ≥ 1（pdfium 对合法 PDF 不会返回 0 页）。</returns>
    /// <exception cref="EbookToolSuite.Core.Exceptions.AppException">
    /// 文件不存在、无权限、被加密（口令没给或给错 → <c>DrmDetected</c>）、损坏、或根本不是 PDF
    /// 时抛出。**不返回 0 或 null**：返回 0 会让调用方把「读不出来」误当成「这本书是空的」，
    /// 两者在 UI 上要给不同的提示。
    /// </exception>
    int GetPageCount(string path, string? password = null);

    /// <summary>
    /// 把第 <paramref name="pageIndex"/> 页渲染成 JPEG 字节，长边不超过 <paramref name="maxLongEdge"/>。
    /// 返回值直接交给封面管线（<c>IImageService.WriteCoverVariantsAsync</c>）归一化 + 生成缩略图。
    /// </summary>
    /// <param name="path">PDF 文件路径。</param>
    /// <param name="pageIndex">页索引，从 0 开始。</param>
    /// <param name="maxLongEdge">输出图片长边的像素上限，必须 &gt; 0。</param>
    /// <returns>JPEG 编码后的字节，非空。</returns>
    /// <exception cref="EbookToolSuite.Core.Exceptions.AppException">
    /// 与 <see cref="GetPageCount"/> 同一套错误码；另外 <paramref name="pageIndex"/> 越界时抛
    /// <c>UnsupportedFormat</c>（页码越界是调用方算错了，不是文件坏了，见实现里的注释）。
    /// </exception>
    byte[] RenderPage(string path, int pageIndex, int maxLongEdge);

    /// <summary>
    /// 按 <paramref name="options"/> 渲染一页（T6-2B 阅读器自绘引擎的主路径）。
    /// </summary>
    /// <param name="path">PDF 文件路径。</param>
    /// <param name="pageIndex">页索引，从 0 开始。</param>
    /// <param name="options">渲染参数：长边上限、旋转角、主题。不能为 null。</param>
    /// <param name="password">
    /// PDF 口令；null/空表示没口令。**必须支持**：阶段一交给 Chromium 时它有内置密码框，
    /// 阶段二自绘引擎如果不支持，加密书就从"能用"退回"读不了"。
    /// </param>
    /// <returns>
    /// JPEG 字节 + **旋转后**的真实像素尺寸 + 是否图片页。命中 20 页 LRU 时直接返回缓存。
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="options"/> 的旋转角不是 0/90/180/270，或长边上限 &lt; 1。
    /// </exception>
    /// <exception cref="EbookToolSuite.Core.Exceptions.AppException">
    /// 与 <see cref="GetPageCount"/> 同一套错误码。口令给错/没给而文件加密 → <c>DrmDetected</c>。
    /// </exception>
    PdfPageImage RenderPage(string path, int pageIndex, PdfPageRenderOptions options, string? password = null);

    /// <summary>
    /// 读一页的文本层：纯文本 + 逐字符框（坐标归一化到 [0,1]）。
    /// </summary>
    /// <param name="path">PDF 文件路径。</param>
    /// <param name="pageIndex">页索引，从 0 开始。</param>
    /// <param name="password">PDF 口令；null/空表示没口令。</param>
    /// <returns>扫描页返回空文本 + 空字符表，**不抛异常**（"这页没文字"不是错误）。</returns>
    /// <exception cref="EbookToolSuite.Core.Exceptions.AppException">文件级错误，同 <see cref="GetPageCount"/>。</exception>
    PdfPageText ReadPageText(string path, int pageIndex, string? password = null);

    /// <summary>
    /// 读整本书的文本层，每页一条，索引即页号（长度恒等于页数）。**只读文本，不渲染**。
    /// </summary>
    /// <remarks>
    /// 这是阅读器内搜索的索引来源，所以要求快：扫描页返回 <c>""</c> 而不是跳过，
    /// 否则调用方还得自己补页号对齐。
    /// </remarks>
    /// <param name="path">PDF 文件路径。</param>
    /// <param name="password">PDF 口令；null/空表示没口令。</param>
    /// <returns>长度 = 页数的只读列表。</returns>
    /// <exception cref="EbookToolSuite.Core.Exceptions.AppException">文件级错误，同 <see cref="GetPageCount"/>。</exception>
    IReadOnlyList<string> ReadAllText(string path, string? password = null);

    /// <summary>
    /// 读 PDF 目录（书签树拍平），<see cref="PdfOutlineEntry.Level"/> 从 1 起。
    /// </summary>
    /// <remarks>
    /// Docnet.Core **没有任何 outline API**，所以这里直接 P/Invoke 仓库自带的 pdfium.dll
    /// （已实测导出 <c>FPDFBookmark_*</c> 全套）。目录坏了/为空返回空列表，不抛异常——
    /// 目录只是导航辅助，不该让"打开这本书"失败。
    /// </remarks>
    /// <param name="path">PDF 文件路径。</param>
    /// <param name="password">PDF 口令；null/空表示没口令。</param>
    /// <returns>空列表表示这本书确实没有目录（或目录读不出来）。</returns>
    /// <exception cref="EbookToolSuite.Core.Exceptions.AppException">
    /// **文件级**错误照抛，与 <see cref="GetPageCount"/> 同一套错误码（含加密 → <c>DrmDetected</c>）。
    /// </exception>
    IReadOnlyList<PdfOutlineEntry> ReadOutline(string path, string? password = null);
}
