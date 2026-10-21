using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Infrastructure.Data.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace EbookToolSuite.Services.Search;

/// <summary>T6-4 全文搜索的 DI 入口。注册在 App.xaml.cs 的 BuildHost 里。</summary>
public static class ContentSearchServiceCollectionExtensions
{
    public static IServiceCollection AddContentSearchServices(this IServiceCollection services)
    {
        services.AddSingleton<ContentTextExtractor>();
        services.AddSingleton<IContentRepository, ContentRepository>();
        services.AddSingleton<IContentSearchService, ContentSearchService>();
        services.AddSingleton<IContentIndexer, ContentIndexer>();
        return services;
    }
}
