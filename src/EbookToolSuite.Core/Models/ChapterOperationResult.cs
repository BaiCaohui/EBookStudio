namespace EbookToolSuite.Core.Models;

/// <summary>
/// 一次章节结构操作的结果（执行计划 T5-1）。
/// </summary>
/// <remarks>
/// <see cref="ChangedFiles"/> 是给 <c>.session.json</c> 的 <c>changedFiles</c> 用的：
/// 全部用包内相对路径（正斜杠），保存时按这份清单做差异与写回。
/// </remarks>
/// <param name="Operation">操作名：rename / move / delete / add / import / merge。</param>
/// <param name="AffectedIndex">操作涉及的章节序号（0 起）；新增/导入是插入位置。</param>
/// <param name="Href">操作涉及的章节路径；合并是"目标章"的路径。</param>
/// <param name="ChangedFiles">本次操作改动/新增/移走的包内相对路径。</param>
/// <param name="Message">给人看的一句话（状态条直接用）。</param>
public sealed record ChapterOperationResult(
    string Operation,
    int AffectedIndex,
    string? Href,
    IReadOnlyList<string> ChangedFiles,
    string Message);
