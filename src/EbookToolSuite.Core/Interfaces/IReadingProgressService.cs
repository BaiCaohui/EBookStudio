using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Core.Interfaces;

/// <summary>
/// 阅读进度的保存与恢复。规格见执行计划 T1-6、设计文档 8.4。
/// </summary>
/// <remarks>
/// 【为什么防抖要做在服务里，而不是阅读窗口里】
/// 一是"退出程序时把没写完的进度补上"需要一个集中队列，散在窗口里做不到；
/// 二是防抖是纯逻辑，放在服务里可以不启动界面就测出"滚 30 秒到底写了几次库"。
/// </remarks>
public interface IReadingProgressService
{
    /// <summary>
    /// 读上次读到哪了。没有记录返回 null。
    /// chapterCount 只用于换算全书比例；书换了导致章数变化时不在这里纠正，
    /// 由前端按章内比例与文本指纹逐级回退。
    /// </summary>
    Task<ReaderProgressSnapshot?> LoadAsync(Guid bookId, int chapterCount, CancellationToken ct = default);

    /// <summary>
    /// 记下最新位置并安排一次防抖保存（默认 1.5 秒内的多次调用只写一次库）。
    /// 不抛异常：进度存不下来不该影响阅读，失败只记日志。
    /// </summary>
    void ScheduleSave(ReaderProgressSnapshot snapshot);

    /// <summary>
    /// 立刻写库（切章、关窗这类"确定性节点"用，不走防抖）。
    /// </summary>
    Task SaveNowAsync(ReaderProgressSnapshot snapshot, CancellationToken ct = default);

    /// <summary>
    /// 把待写队列全部落盘。程序退出时调用，否则最后 1.5 秒内的滚动会丢。
    /// </summary>
    Task FlushAsync(CancellationToken ct = default);

    /// <summary>实际写库次数。用来验证防抖真的生效（判据：连续滚 30 秒 <= 5 次）。</summary>
    long SavedCount { get; }

    /// <summary>还等着写的快照条数（一本书最多一条，后写的覆盖先写的）。</summary>
    int PendingCount { get; }
}