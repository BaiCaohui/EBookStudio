using EbookToolSuite.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace EbookToolSuite.Services.Pdf;

/// <summary>
/// PDF 相关服务的 DI 注册。组合根（App.xaml.cs）只需调一行 <c>services.AddPdfServices()</c>。
/// </summary>
/// <remarks>
/// <para>
/// 为什么要一个扩展方法而不是让组合根逐个 <c>AddSingleton</c>：本类所在的 Pdf 目录
/// 未来还会长出更多 pdfium 相关服务（T6-2B 的分页/缩略图缓存），注册细节应该跟着实现走，
/// 而不是散落在组合根里——组合根只该知道「有一组 PDF 服务」，不该知道它们各自依赖什么。
/// </para>
/// <para>
/// 注册类型全部是单例：<see cref="PdfDocumentService"/> 无状态，
/// 底层 <c>DocLib.Instance</c> 自己就是进程级单例且内部对原生调用加锁，重复构造没有收益。
/// </para>
/// </remarks>
public static class PdfServiceCollectionExtensions
{
    /// <summary>注册 PDF 文档服务（总页数 + 首页渲染 + T6-2B 的按主题分页渲染/文本层/目录）。</summary>
    /// <param name="services">DI 容器。</param>
    /// <returns>同一个 <paramref name="services"/>，便于链式调用。</returns>
    public static IServiceCollection AddPdfServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // IPdfiumLibrary 只是让 PdfDocumentService 可测的接缝；公开注册是为了让
        // 集成测试也能拿到同一个实例验证「真 pdfium 能加载」。
        services.AddSingleton<IPdfiumLibrary, PdfiumLibrary>();

        // 页缓存注册成单例：它的价值全在"跨调用复用"。如果让容器按需给每个
        // PdfDocumentService 新建一个，阅读器预渲染存进去的页、下一页请求就找不到了。
        services.AddSingleton<PdfPageCache>();

        services.AddSingleton<IPdfDocumentService, PdfDocumentService>();

        return services;
    }
}
