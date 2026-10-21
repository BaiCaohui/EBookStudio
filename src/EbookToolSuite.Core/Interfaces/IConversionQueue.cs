using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Core.Interfaces;

/// <summary>
/// 转换队列（执行计划 T2-4）：把"转一次等一次"变成"排一批一起转"。
///
/// 契约要点：
///   事件都在【后台线程】触发，UI 订阅者自己 Dispatcher.InvokeAsync。
///   终态任务（完成/失败/取消）会一直留在队列里，直到用户手动清除 
///   否则用户看不到失败原因。
/// </summary>
public interface IConversionQueue
{
    /// <summary>队列里现有任务的快照，按入队时间升序。</summary>
    IReadOnlyList<ConversionTask> Tasks { get; }

    int QueuedCount { get; }

    int RunningCount { get; }

    /// <summary>还有任务没跑完（排队中或运行中或等待重试）。</summary>
    bool IsBusy { get; }

    /// <summary>某条任务的状态/进度/错误变了。后台线程触发。</summary>
    event Action<ConversionTask>? TaskChanged;

    /// <summary>任务列表本身变了（入队/移除/清空）。后台线程触发。</summary>
    event Action? TasksChanged;

    /// <summary>
    /// 启动执行器：先把上次崩溃留下的 Running/WaitingRetry 归位成 Queued，
    /// 再把这些任务接着跑。幂等，重复调只起一个 worker。
    /// </summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>把一次转换排进队。同一本书同一目标格式已在队列里（未终态）时拒绝。</summary>
    Task<ConversionTask> EnqueueAsync(ConversionRequest request, CancellationToken ct = default);

    /// <summary>批量入队。返回成功排进去的那些（被拒的抛异常，整体失败）。</summary>
    Task<IReadOnlyList<ConversionTask>> EnqueueManyAsync(
        IReadOnlyList<ConversionRequest> requests, CancellationToken ct = default);

    /// <summary>把失败/取消的任务重新排队（错误清空、重试计数归零）。只接受终态任务。</summary>
    Task<ConversionTask> RetryAsync(Guid taskId, CancellationToken ct = default);

    /// <summary>移除一条任务（只允许终态；运行中的要先取消）。</summary>
    Task RemoveAsync(Guid taskId, CancellationToken ct = default);

    /// <summary>清除所有终态任务，返回清掉几条。</summary>
    Task<int> ClearFinishedAsync(CancellationToken ct = default);

    /// <summary>取消一条：排队中/等待重试的立即取消，运行中的终止 Calibre 进程。</summary>
    Task CancelAsync(Guid taskId, CancellationToken ct = default);

    /// <summary>全部取消。返回时所有外部进程必须已经消失，且不再有人重试。</summary>
    Task<int> CancelAllAsync(CancellationToken ct = default);

    /// <summary>等队列彻底空闲（最后一个任务进终态）。冒烟与退出流程用。</summary>
    Task WaitForIdleAsync(CancellationToken ct = default);

    /// <summary>
    /// 这条任务本次运行的完整外部输出（Calibre 的 stdout + stderr），界面"查看日志"用。
    /// 进程还没跑过或进程已重启则返回 null。
    /// </summary>
    string? GetTaskOutput(Guid taskId);
}
