using EbookToolSuite.Core.Abstractions;

namespace EbookToolSuite.Core.Interfaces;

/// <summary>
/// 全应用唯一允许启动外部进程的地方。
/// 任何层都不许裸写 Process.Start（设计文档 3.1 的规则表）。
/// </summary>
public interface IProcessHost
{
    /// <summary>
    /// 运行一个外部进程并等它退出。
    /// 实现约定：取消或超时必须终止整棵进程树并等它真正退出后再返回，不能留下孤儿进程。
    /// </summary>
    Task<ProcessResult> RunAsync(
        ProcessSpec spec,
        IProgress<ProcessOutputLine>? output = null,
        CancellationToken ct = default);
}