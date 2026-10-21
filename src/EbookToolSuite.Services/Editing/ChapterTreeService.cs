using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Engines.Epub;
using Microsoft.Extensions.Logging;

namespace EbookToolSuite.Services.Editing;

/// <summary>
/// 章节树的会话层（执行计划 T5-1）：执行结构操作 + 会话记账 + 回到会话开始。
/// </summary>
/// <remarks>
/// 记账是这一层唯一的额外职责：引擎只管改工作区里的文件，改过哪些文件要写进
/// <see cref="WorkspaceSession.ChangedFiles"/>，保存时才清楚这次会话动了什么。
/// </remarks>
public sealed class ChapterTreeService : IChapterTreeService
{
    private readonly IEpubWorkspaceFactory _factory;
    private readonly ILogger<ChapterTreeService>? _logger;

    public ChapterTreeService(IEpubWorkspaceFactory factory, ILogger<ChapterTreeService>? logger = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _logger = logger;
    }

    public IReadOnlyList<ChapterNode> Load(IEpubWorkspace workspace) => Editor(workspace).ReadChapters();

    public ChapterOperationResult Rename(IEpubWorkspace workspace, int index, string newTitle)
        => Run(workspace, editor => editor.Rename(index, newTitle));

    public ChapterOperationResult Move(IEpubWorkspace workspace, int from, int to)
        => Run(workspace, editor => editor.Move(from, to));

    public ChapterOperationResult Delete(IEpubWorkspace workspace, int index)
        => Run(workspace, editor => editor.Delete(index));

    public ChapterOperationResult AddEmpty(IEpubWorkspace workspace, int insertIndex, string? title)
        => Run(workspace, editor => editor.AddEmpty(insertIndex, title));

    public ChapterOperationResult Import(IEpubWorkspace workspace, int insertIndex, string sourcePath, string? title)
        => Run(workspace, editor => editor.Import(insertIndex, sourcePath, title));

    public ChapterOperationResult Promote(IEpubWorkspace workspace, int index)
        => Run(workspace, editor => editor.Promote(index));

    public ChapterOperationResult Demote(IEpubWorkspace workspace, int index)
        => Run(workspace, editor => editor.Demote(index));

    public IReadOnlyList<ChapterSplitPoint> ReadSplitPoints(IEpubWorkspace workspace, int index)
        => Editor(workspace).ReadSplitPoints(index);

    public ChapterOperationResult Split(IEpubWorkspace workspace, int index, int splitAt)
        => Run(workspace, editor => editor.Split(index, splitAt));

    public ChapterOperationResult Merge(IEpubWorkspace workspace, int index)
        => Run(workspace, editor => editor.Merge(index));

    public async Task<IEpubWorkspace> RestartAsync(IEpubWorkspace workspace, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        var session = workspace.Session;

        // 先把新的开起来再丢旧的：万一原书已经不在了，至少不会把用户的工作区也弄丢。
        var fresh = await _factory
            .OpenAsync(
                new WorkspaceOpenRequest
                {
                    EpubPath = session.SourcePath,
                    BookId = session.BookId,
                    FormatId = session.FormatId,
                },
                ct)
            .ConfigureAwait(false);

        await workspace.DiscardAsync().ConfigureAwait(false);

        _logger?.LogInformation(
            "章节结构回到会话开始：{Old} -> {New}（{Source}）",
            session.SessionId,
            fresh.SessionId,
            session.SourcePath);

        return fresh;
    }

    private static EpubStructureEditor Editor(IEpubWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        // OpfPath 是老会话可能没有的字段（早期版本的 .session.json），给个兜底值。
        return new EpubStructureEditor(workspace.WorkDir, workspace.Session.OpfPath ?? "content.opf");
    }

    private ChapterOperationResult Run(
        IEpubWorkspace workspace,
        Func<EpubStructureEditor, ChapterOperationResult> action)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        var result = action(Editor(workspace));

        if (result.ChangedFiles.Count > 0)
        {
            var session = workspace.Session;

            foreach (var file in result.ChangedFiles)
            {
                if (!session.ChangedFiles.Contains(file, StringComparer.OrdinalIgnoreCase))
                {
                    session.ChangedFiles.Add(file);
                }
            }

            // .session.json 是工作区自己的账本；写回走工厂里的内部助手（同一程序集）。
            EpubWorkspaceFactory.WriteSession(workspace.WorkDir, session);
        }

        _logger?.LogInformation(
            "章节操作 {Operation}：{Message}（改动 {Count} 个文件）",
            result.Operation,
            result.Message,
            result.ChangedFiles.Count);

        return result;
    }
}
