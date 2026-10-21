using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Infrastructure.Data.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace EbookToolSuite.Services.Annotations;

/// <summary>T6-5 书签与笔记的 DI 入口。注册在 App.xaml.cs 的 BuildHost 里。</summary>
public static class AnnotationServiceCollectionExtensions
{
    public static IServiceCollection AddAnnotationServices(this IServiceCollection services)
    {
        services.AddSingleton<IAnnotationRepository, AnnotationRepository>();
        services.AddSingleton<IAnnotationService, AnnotationService>();
        return services;
    }
}
