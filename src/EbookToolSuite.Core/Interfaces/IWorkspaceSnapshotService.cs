using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Core.Interfaces;

/// <summary>
/// 编辑会话的文件级快照（T5-4，设计文档 7.3 / 执行计划 T5-4）。
/// <para>
/// 存储约定：<c>{workspace.WorkDir}/.snapshots/{snapshotId}/</c>，里面是
/// <c>manifest.json</c>（唯一权威清单）与 <c>files/</c>（备份本体）。
/// 放在工作区里、会话结束随工作区一起删；打包器已把 <c>.snapshots/**</c> 排除，绝不会打进 EPUB。
/// </para>
/// <para>
/// 【增量】除 <see cref="WorkspaceSnapshotKinds.Initial"/> 外，快照只含"调用方点名的那几个文件"，
/// 所以一次 20 章的编辑只占这几章的字节数，不是整本书。
/// </para>
/// </summary>
public interface IWorkspaceSnapshotService
{
    /// <summary>默认保留的"非基线"快照份数（计划 T5-4 表：保存前增量最近 10 个）。</summary>
    public const int DefaultMaxSnapshots = 10;

    /// <summary>打开工作区时的全量基线快照（所有文件）。</summary>
    Task<WorkspaceSnapshot> TakeFullAsync(
        IEpubWorkspace workspace,
        string? note = null,
        CancellationToken ct = default);

    /// <summary>
    /// 增量快照：只备份 <paramref name="entries"/>（包内相对路径）这几个文件的**当前内容**。
    /// 文件此刻不存在也没关系，会记成"不存在"，还原时把目标删掉。
    /// </summary>
    Task<WorkspaceSnapshot> TakeIncrementalAsync(
        IEpubWorkspace workspace,
        IReadOnlyList<string> entries,
        string kind = WorkspaceSnapshotKinds.Save,
        string? note = null,
        CancellationToken ct = default);

    /// <summary>列出全部快照，新的在前。</summary>
    Task<IReadOnlyList<WorkspaceSnapshot>> ListAsync(
        IEpubWorkspace workspace,
        CancellationToken ct = default);

    Task<WorkspaceSnapshot?> GetAsync(
        IEpubWorkspace workspace,
        string snapshotId,
        CancellationToken ct = default);

    /// <summary>同类型里最新的那个（"撤销上一次替换"用它找最新的替换前快照）。</summary>
    Task<WorkspaceSnapshot?> FindLatestAsync(
        IEpubWorkspace workspace,
        string kind,
        CancellationToken ct = default);

    /// <summary>
    /// 还原到某个快照：**先给当前状态建一份反向快照**，再按上面的语义覆盖回去。
    /// 任一条目失败会如实记进 <see cref="WorkspaceRestoreResult.Failures"/>，不谎报成功。
    /// </summary>
    Task<WorkspaceRestoreResult> RestoreAsync(
        IEpubWorkspace workspace,
        string snapshotId,
        CancellationToken ct = default);

    /// <summary>
    /// 按类型清理：保存前留最近 <paramref name="maxSnapshots"/> 份（默认 10）、
    /// 替换前 / 删除前 / 还原前各留 1 份、手动标记留 5 份；全量基线永远保留。
    /// </summary>
    Task<WorkspacePruneReport> PruneAsync(
        IEpubWorkspace workspace,
        int maxSnapshots = DefaultMaxSnapshots,
        CancellationToken ct = default);

    /// <summary>快照总占用（含基线）。</summary>
    long TotalBytes(IEpubWorkspace workspace);

    /// <summary>非基线快照（真正的"编辑增量"）总占用；判据"编辑 20 章 <= 原书 50%"看这个。</summary>
    long IncrementalBytes(IEpubWorkspace workspace);

    /// <summary>快照目录（{WorkDir}/.snapshots/{snapshotId}）。编号非法时抛 InvalidPath。</summary>
    string SnapshotDirectory(IEpubWorkspace workspace, string snapshotId);
}