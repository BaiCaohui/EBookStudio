using EbookToolSuite.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace EbookToolSuite.Services.Comics;

/// <summary>
/// 漫画（CBZ/CBR）相关服务的 DI 注册。组合根（App.xaml.cs）只需调一行 <c>services.AddComicServices()</c>。
/// </summary>
/// <remarks>
/// 与 <c>AddPdfServices()</c> 同一分工：注册细节跟着实现走，组合根只该知道「有一组漫画服务」。
/// 单例：<see cref="ComicBookService"/> 无状态（每本书的归档句柄由阅读窗口自己持有并释放）。
/// </remarks>
public static class ComicServiceCollectionExtensions
{
    /// <summary>注册漫画读取服务（探测 + 按页给 JPEG + 导入封面）与 CBZ 编辑服务（解压/重打包）。</summary>
    /// <param name="services">DI 容器。</param>
    /// <returns>同一个 <paramref name="services"/>，便于链式调用。</returns>
    public static IServiceCollection AddComicServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IComicBookService, ComicBookService>();

        // 编辑现有 CBZ：无状态（每次编辑一个临时工作区，句柄都在方法内部释放）
        services.AddSingleton<ICbzEditService, CbzEditService>();

        return services;
    }
}
