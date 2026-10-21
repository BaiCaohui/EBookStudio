namespace EbookToolSuite.Core.Enums;

/// <summary>
/// 导入队列里单个任务的执行状态（执行计划 T2-1）。
/// 与 ImportOutcome 的分工：Outcome 是"业务结果"（已导入/重复/损坏），
/// State 是"队列进展"（还没轮到/正在处理/已结束）。UI 列表按 State 上色。
/// </summary>
public enum ImportJobState
{
    /// <summary>已入队，还没轮到。串行度 1，等前面的处理完。</summary>
    Queued = 0,

    /// <summary>正在处理。此时 ImportProgress 才有意义。</summary>
    Running = 1,

    /// <summary>已结束，业务结果见 Outcome。</summary>
    Finished = 2,

    /// <summary>批次被取消，且这本还没轮到。**未产生任何副作用**。</summary>
    Canceled = 3,
}