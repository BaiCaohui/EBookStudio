namespace EbookToolSuite.Core.Abstractions;

/// <summary>
/// 转换队列的运行参数（执行计划 T2-4）。
/// 默认值全部来自设计文档：并发 1、最多重试 3 次、退避 5s/10s/20s、超时只重试 1 次。
/// </summary>
public sealed record ConversionQueueOptions
{
    /// <summary>
    /// 同时跑几个 Calibre。默认 1：每个实例吃 1-2 核 + 几百 MB 内存，
    /// 调大可能因磁盘 IO 打架反而更慢。设置页要显式警告"并发 > 1 可能导致内存不足"。
    /// </summary>
    public int MaxParallel { get; init; } = 1;

    /// <summary>临时性错误（文件被占用等）最多重试几次。默认 3。</summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>超时最多重试几次。默认 1（可能是临时卡顿，再多就是白等）。</summary>
    public int TimeoutMaxRetries { get; init; } = 1;

    /// <summary>第 n 次重试前的等待。默认 5s / 10s / 20s（指数退避）。</summary>
    public IReadOnlyList<TimeSpan> RetryDelays { get; init; } = new[]
    {
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(20),
    };

    /// <summary>队列空转时的轮询间隔。默认 200ms。</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(200);

    public static ConversionQueueOptions Default { get; } = new();

    /// <summary>第 attempt 次（从 1 起）重试前等多久。超出表长就用最后一项。</summary>
    public TimeSpan DelayFor(int attempt)
    {
        if (RetryDelays.Count == 0)
        {
            return TimeSpan.FromSeconds(5);
        }

        var index = Math.Clamp(attempt - 1, 0, RetryDelays.Count - 1);
        return RetryDelays[index];
    }
}
