using System.Windows;
using EbookToolSuite.App.Views;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using Microsoft.Extensions.Logging;

namespace EbookToolSuite.App.Services;

/// <summary>
/// 把队列的"重复了怎么办"接到 WPF 对话框上。放 App 层是因为 Services 层不许引用 WPF。
/// </summary>
/// <remarks>
/// 【为什么这里可以同步阻塞】ImportQueue.StartAsync 的预检刻意保留调用方上下文，
/// 所以本方法跑在 UI 线程上，ShowDialog 是合法的。队列不会在后台线程上调它。
/// </remarks>
public sealed class DuplicatePromptResolver : IDuplicateResolver
{
    private readonly ILogger<DuplicatePromptResolver> _log;

    public DuplicatePromptResolver(ILogger<DuplicatePromptResolver> log)
    {
        _log = log;
    }

    public Task<DuplicatePolicy> ResolveBatchAsync(
        IReadOnlyList<ImportJob> duplicates, CancellationToken ct = default)
    {
        var policy = DuplicatePromptWindow.AskBatch(ActiveOwner(), duplicates);
        _log.LogInformation("重复对话框（整批）返回 {Policy}，重复 {Count} 本", policy, duplicates.Count);
        return Task.FromResult(policy);
    }

    public Task<DuplicatePolicy> ResolveSingleAsync(
        ImportJob duplicate, int index, int total, CancellationToken ct = default)
    {
        var keep = DuplicatePromptWindow.AskSingle(ActiveOwner(), duplicate, index, total);
        _log.LogInformation("重复对话框（逐项）第 {Index}/{Total} 本返回 {Keep}", index, total, keep);
        return Task.FromResult(keep);
    }

    /// <summary>拿当前活动窗口当宿主，没有就退回主窗口（对话框居中好看一点）。</summary>
    private static Window? ActiveOwner()
    {
        var app = Application.Current;
        if (app is null)
        {
            return null;
        }

        foreach (Window window in app.Windows)
        {
            if (window.IsActive)
            {
                return window;
            }
        }

        return app.MainWindow;
    }
}