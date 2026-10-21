using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Core.Interfaces;

/// <summary>
/// 正文索引的后台构建器（T6-4）。纪律：
/// <list type="bullet">
/// <item>不阻塞导入：导入成功后只 Enqueue，抽取与写库在后台线程。</item>
/// <item>可中断可续：状态落在 content_index_state 表，重启后接着建。</item>
/// <item>进度对界面可见：「索引中 N/M」由 <see cref="Progress"/> + <see cref="ProgressChanged"/> 提供。</item>
/// </list>
/// </summary>
public interface IContentIndexer
{
    ContentIndexProgress Progress { get; }

    event EventHandler<ContentIndexProgress>? ProgressChanged;

    /// <summary>启动后台循环，并自动续建上次没做完的书。</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>停掉后台循环。正在索引的那一本会回到 Pending（下次续建）。</summary>
    Task StopAsync();

    /// <summary>把书排进索引队列（导入成功后调用）。已经在队列里或已完成的书会被跳过。</summary>
    Task EnqueueAsync(IReadOnlyCollection<Guid> bookIds, CancellationToken ct = default);

    /// <summary>补齐所有缺索引的书（启动时调用），返回排入的本数。</summary>
    Task<int> EnqueuePendingAsync(CancellationToken ct = default);

    /// <summary>等到队列走空（冒烟与测试用；界面上不需要）。返回是否在超时前走空。</summary>
    Task<bool> WaitForIdleAsync(TimeSpan timeout, CancellationToken ct = default);
}
