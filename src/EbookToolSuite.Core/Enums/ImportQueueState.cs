namespace EbookToolSuite.Core.Enums;

/// <summary>导入队列的整体状态（执行计划 T2-1：队列要能暂停、能取消）。</summary>
public enum ImportQueueState
{
    /// <summary>没有批次在跑。</summary>
    Idle = 0,

    /// <summary>正在展开路径 + 算指纹去重（进队列之前）。此时还没有 import_batch 行。</summary>
    Preparing = 1,

    /// <summary>正在导入。Worker 单线程串行处理。</summary>
    Running = 2,

    /// <summary>用户点了暂停：**完成当前这本后**停在队列头，不再取下一本。</summary>
    Paused = 3,

    /// <summary>用户点了取消：**完成当前这本后**停，未开始的标记为 Canceled。</summary>
    Canceling = 4,

    /// <summary>本批次已结束（正常跑完或取消收尾完毕）。CurrentBatch 仍可读。</summary>
    Finished = 5,
}