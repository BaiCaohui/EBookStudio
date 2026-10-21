using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Core.Interfaces;

/// <summary>
/// 导入队列（执行计划 T2-1）。**单批**语义：一次只跑一个批次，
/// 前一批没结束时 StartAsync 抛 InvalidOperationException（不排队中排队）。
/// </summary>
/// <remarks>
/// 【线程约定】StartAsync 在调用线程上做"展开 + 预检"（慢，建议调用方
/// 自己在后台调），之后立刻返回；真正的导入在内部单线程 Worker 上跑。
/// 所有事件都在**后台线程**触发，UI 订阅者必须自己 Dispatcher.Invoke 回 UI 线程。
/// </remarks>
public interface IImportQueue : IAsyncDisposable
{
    ImportQueueState State { get; }

    /// <summary>当前（或刚结束的）批次。Idle 时为 null。</summary>
    ImportBatch? CurrentBatch { get; }

    /// <summary>本批次的全部任务，按入队顺序。UI 列表用它。</summary>
    IReadOnlyList<ImportJob> Jobs { get; }

    int TotalCount { get; }

    /// <summary>已进入终态（Finished / Canceled）的数量。</summary>
    int CompletedCount { get; }

    /// <summary>某个任务的状态变了（含 Result 写回）。UI 只需刷新那一行。</summary>
    event EventHandler<ImportJob>? JobStateChanged;

    /// <summary>当前任务的细粒度阶段（解析/复制/封面/写库）。</summary>
    event EventHandler<ImportProgress>? JobProgress;

    event EventHandler<ImportQueueState>? StateChanged;

    /// <summary>整批结束（正常跑完或取消收尾完毕）。import_batch 此时已落盘。</summary>
    event EventHandler<ImportBatch>? BatchFinished;

    /// <summary>
    /// 启动一批。返回**预检之后**的批次（Total 已填）。
    /// 路径为空或一个文件都没扫到时不启动，直接抛 ArgumentException。
    /// </summary>
    Task<ImportBatch> StartAsync(
        IReadOnlyList<string> paths, ImportQueueOptions options, CancellationToken ct = default);

    /// <summary>完成当前这本后停住。已暂停时无副作用。</summary>
    void Pause();

    /// <summary>从暂停处继续。</summary>
    void Resume();

    /// <summary>完成当前这本后停住，未开始的标记为 Canceled 并写库收尾。</summary>
    void Cancel();

    /// <summary>等整批结束。批次没在跑时立即返回。</summary>
    Task WaitForCompletionAsync(CancellationToken ct = default);
}