using Docnet.Core;
using Docnet.Core.Models;
using Docnet.Core.Readers;

namespace EbookToolSuite.Services.Pdf;

/// <summary>
/// pdfium 的打开入口。存在的唯一理由是**让 <see cref="PdfDocumentService"/> 可测**：
/// <see cref="DocLib.Instance"/> 是静态单例，测试无法替换它去模拟「pdfium 自己初始化失败」
/// 这类场景；抽成接口后，测试可以喂一个假的 reader 检查异常翻译逻辑，
/// 而真实样本的集成测试仍然走下面的 <see cref="PdfiumLibrary"/>。
/// </summary>
/// <remarks>
/// 这个接口**不是**给业务层用的抽象。业务层只认 <c>IPdfDocumentService</c>；
/// 这里暴露 <see cref="IDocReader"/> 这种第三方类型，是为了避免在包装层再复制一遍
/// pdfium 的全部方法签名——那样只会多一层需要同步维护的空壳。
/// </remarks>
public interface IPdfiumLibrary
{
    /// <summary>
    /// 打开一份 PDF（不带密码）。失败时底层抛 Docnet 自己的异常，
    /// 由 <see cref="PdfDocumentService"/> 统一翻译成 AppException。
    /// </summary>
    IDocReader GetDocReader(string filePath, PageDimensions dimensionOptions);

    /// <summary>
    /// 打开一份加密 PDF。T6-2B 起自绘引擎要自己支持口令——阶段一交给 Chromium 时
    /// 它有内置密码框，自绘引擎不支持就等于加密书集体读不了（回归）。
    /// </summary>
    /// <param name="filePath">PDF 路径。</param>
    /// <param name="password">口令。空串与"没有口令"等价；口令错同样抛 Docnet 的加载异常（→ DrmDetected）。</param>
    /// <param name="dimensionOptions">渲染尺寸，语义同另一个重载。</param>
    IDocReader GetDocReader(string filePath, string password, PageDimensions dimensionOptions);
}

/// <summary>
/// <see cref="IPdfiumLibrary"/> 的默认实现：转发到 Docnet.Core 的进程级单例。
/// </summary>
/// <remarks>
/// 公开（而不是 internal）是为了让测试不必用反射去构造 <see cref="PdfDocumentService"/>——
/// 加密文件的异常翻译只能靠假 reader 覆盖，本机没有加密样本。
/// </remarks>
public sealed class PdfiumLibrary : IPdfiumLibrary
{
    /// <inheritdoc />
    public IDocReader GetDocReader(string filePath, PageDimensions dimensionOptions)
        => DocLib.Instance.GetDocReader(filePath, dimensionOptions);

    /// <inheritdoc />
    public IDocReader GetDocReader(string filePath, string password, PageDimensions dimensionOptions)
        => DocLib.Instance.GetDocReader(filePath, password, dimensionOptions);
}
