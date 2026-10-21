namespace EbookToolSuite.Core.Interfaces;

/// <summary>
/// 阅读会话采集（T7-3B）。阅读窗口打开书时开一条会话，阅读事件驱动心跳，
/// 关窗 / 退出时收尾。只记本地，不上报（设计文档 7.9 的隐私约定）。
/// </summary>
/// <remarks>
/// 【口径】时长 = ended_at - started_at；ended_at 记"最后一次有阅读事件的时间"，
/// 所以打开书后挂机不会把它推到关窗时刻。挂机超过空闲阈值（默认 5 分钟）再恢复阅读时，
/// 视为新会话另起一行，两段之间的空白不计时。
/// </remarks>
public interface IReadingSessionService
{
    /// <summary>打开书成功时开一条会话。同一本书已有进行中的会话时什么都不做。</summary>
    Task StartAsync(Guid bookId, CancellationToken ct = default);

    /// <summary>
    /// 上报一次阅读活动（翻页数 / 估算字数增量）。只更新内存，不阻塞 UI、不抛异常；
    /// 落库由服务自己的 1 分钟心跳完成。
    /// </summary>
    void ReportActivity(Guid bookId, int pagesDelta, long charsDelta);

    /// <summary>关闭阅读窗口：收尾当前会话并立即落库。</summary>
    Task EndAsync(Guid bookId, CancellationToken ct = default);

    /// <summary>程序退出前把内存里的会话全部落库（最后一分钟内的活动不丢）。</summary>
    Task FlushAsync(CancellationToken ct = default);

    /// <summary>这本书当前是否有一条进行中的会话（冒烟与单测断言用）。</summary>
    bool IsActive(Guid bookId);
}
