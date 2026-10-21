using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Core.Interfaces;

/// <summary>
/// 一次 EPUB 编辑会话的工作区句柄（执行计划 T3-1，设计文档 6.1.2）。
/// 工作区是 Data/Workspaces/{sessionId}/ 下一份安全解压出来的全书；
/// 编辑只动工作区，原文件在用户点保存前一个字节都不改。
/// </summary>
/// <remarks>
/// 这里只放 T3-1 需要的成员。SaveAsync（T3-4 打包器）、ValidateAsync（T3-6 保存闭环）、
/// 快照（T3-5）在后面阶段往这个接口上长，不要现在塞空实现。
/// </remarks>
public interface IEpubWorkspace : IAsyncDisposable
{
    string SessionId { get; }

    /// <summary>工作区根目录（绝对路径）。解压后的 mimetype/META-INF/OEBPS 都在这里。</summary>
    string WorkDir { get; }

    /// <summary>.session.json 的内存镜像。</summary>
    WorkspaceSession Session { get; }

    /// <summary>放弃编辑：删除整个会话目录（原文件不动）。幂等。</summary>
    Task DiscardAsync(CancellationToken ct = default);
}

/// <summary>EPUB 工作区工厂（执行计划 T3-1，设计文档 6.1.2 的打开流程）。</summary>
public interface IEpubWorkspaceFactory
{
    /// <summary>
    /// 打开一本 EPUB 并建好工作区。任何一步失败都不会留下半成品会话目录，
    /// 且原文件全程只读。
    /// </summary>
    Task<IEpubWorkspace> OpenAsync(WorkspaceOpenRequest request, CancellationToken ct = default);

    /// <summary>恢复一个残留会话；会话不存在或描述损坏返回 null。</summary>
    Task<IEpubWorkspace?> ResumeAsync(string sessionId, CancellationToken ct = default);

    /// <summary>列出可恢复的残留会话（按打开时间倒序）。</summary>
    IReadOnlyList<WorkspaceSession> ListResumable();
}
