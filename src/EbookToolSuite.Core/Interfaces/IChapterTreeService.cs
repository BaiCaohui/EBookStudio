using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Core.Interfaces;

/// <summary>
/// 章节树的会话层（执行计划 T5-1）：读章节树、执行结构操作、把改动记进会话。
/// </summary>
/// <remarks>
/// 引擎（<c>EpubStructureEditor</c>）只负责改工作区里的文件；
/// "这次会话动过哪些文件"以及"回到会话开始"是这一层的职责。
/// </remarks>
public interface IChapterTreeService
{
    /// <summary>读章节树（以 spine 为权威，标题与层级从目录补）。</summary>
    IReadOnlyList<ChapterNode> Load(IEpubWorkspace workspace);

    /// <summary>改章节标题（只改目录文字，不动文件名）。</summary>
    ChapterOperationResult Rename(IEpubWorkspace workspace, int index, string newTitle);

    /// <summary>把章节从 from 位置移到 to 位置（改 spine 顺序 + 目录顶层对齐）。</summary>
    ChapterOperationResult Move(IEpubWorkspace workspace, int from, int to);

    /// <summary>删除章节（spine + manifest + 目录 + 文件进暂存区）。</summary>
    ChapterOperationResult Delete(IEpubWorkspace workspace, int index);

    /// <summary>在指定位置插入一个空章节。</summary>
    ChapterOperationResult AddEmpty(IEpubWorkspace workspace, int insertIndex, string? title);

    /// <summary>从文件导入一章（.xhtml / .html / .txt）。</summary>
    ChapterOperationResult Import(IEpubWorkspace workspace, int insertIndex, string sourcePath, string? title);

    /// <summary>把章节在目录里升一级（从父项里提出来）。</summary>
    ChapterOperationResult Promote(IEpubWorkspace workspace, int index);

    /// <summary>把章节在目录里降一级（挂到前一个同级条目底下）。</summary>
    ChapterOperationResult Demote(IEpubWorkspace workspace, int index);

    /// <summary>列出某章可以切开的位置（拆分章节用）。</summary>
    IReadOnlyList<ChapterSplitPoint> ReadSplitPoints(IEpubWorkspace workspace, int index);

    /// <summary>拆分章节：在第 splitAt 个正文块之前切开，后半章另存为新章并紧跟其后。</summary>
    ChapterOperationResult Split(IEpubWorkspace workspace, int index, int splitAt);

    /// <summary>把下一章合并进当前章。</summary>
    ChapterOperationResult Merge(IEpubWorkspace workspace, int index);

    /// <summary>
    /// 回到会话开始（判据 7 的撤销）：丢掉当前工作区，用原书重新开一个干净会话。
    /// 返回新工作区，调用方要把旧引用换成它。
    /// </summary>
    Task<IEpubWorkspace> RestartAsync(IEpubWorkspace workspace, CancellationToken ct = default);
}
