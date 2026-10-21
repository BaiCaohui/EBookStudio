using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Core.Interfaces;

/// <summary>
/// 跨章节查找替换（执行计划 T5-3）：先预览、用户确认、再执行；**只写工作区**，落书仍走主窗口【保存】。
/// </summary>
/// <remarks>
/// 安全口径（2026-10-12 定）：
/// <list type="bullet">
/// <item>只用 XML 遍历 <c>XText</c> 文本节点替换，标签名/属性名一个字节都不碰；</item>
/// <item>先在工作区副本上算好全部改动 -> 逐章良构校验 -> 全通过才写盘，任一章不过就一个字都不写；</item>
/// <item>非法正则不执行也不崩；单章 2 秒 / 整轮 20 秒超时、整轮 5 万处命中上限，任一触发即中止且不写盘；</item>
/// <item>"全部替换前快照"与"撤销上一次替换"按 2026-10-12 决定在 T5-4 实现，这里不重复做。</item>
/// </list>
/// </remarks>
public interface IChapterFindReplaceService
{
    /// <summary>
    /// 扫描（**不写盘**）：统计命中数、生成前 N 处预览、算出每章替换后的正文。
    /// 返回值同时就是执行计划：<see cref="FindReplacePlan.Ok"/> 为 false 时不得执行。
    /// </summary>
    FindReplacePlan Preview(
        IEpubWorkspace workspace,
        IReadOnlyList<FindReplaceChapter> chapters,
        FindReplaceOptions options,
        CancellationToken ct = default);

    /// <summary>
    /// 执行计划：逐章校验全通过才写盘（写工作区 + 记 changedFiles）；失败时一个字节都不写。
    /// </summary>
    FindReplaceOutcome Apply(IEpubWorkspace workspace, FindReplacePlan plan, CancellationToken ct = default);
}