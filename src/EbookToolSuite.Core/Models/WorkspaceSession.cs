namespace EbookToolSuite.Core.Models;

/// <summary>
/// 一次编辑工作区的会话描述。序列化后就是
/// <c>Data/Workspaces/{sessionId}/.session.json</c>（执行计划 T3-1）。
/// </summary>
/// <remarks>
/// 字段对齐执行计划 T3-1 给出的 .session.json 示例。注意 bookId/formatId 用 Guid：
/// 计划示例里的 12/34 是早期"自增整数 id"的残留，现库表主键已是 Guid（books.id / book_formats.id）。
/// 快照清单（snapshots）、dirty、cleanExit 属于 T3-5/T3-6，到那个阶段再往这里加。
/// </remarks>
public sealed class WorkspaceSession
{
    /// <summary>8 位十六进制短会话号，同时是工作区目录名。</summary>
    public string SessionId { get; set; } = string.Empty;

    public Guid BookId { get; set; }

    public Guid FormatId { get; set; }

    /// <summary>原 EPUB 的绝对路径。放弃编辑时绝不改动它。</summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>工作区创建时间。</summary>
    public DateTimeOffset OpenedAt { get; set; }

    /// <summary>会话内已改动、尚未写回原文件的包内相对路径（T3-6 保存流程用）。</summary>
    public List<string> ChangedFiles { get; set; } = new();

    /// <summary>OPF 在包内的相对路径，例：OEBPS/content.opf。</summary>
    public string? OpfPath { get; set; }

    /// <summary>package@version，例：2.0 / 3.0。</summary>
    public string EpubVersion { get; set; } = "2.0";

    /// <summary>包里是否存在 META-INF/encryption.xml（字体混淆放行，但会话仍记录）。</summary>
    public bool HasEncryption { get; set; }
}
