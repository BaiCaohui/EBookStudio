using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Services.Editing;

/// <summary>
/// <see cref="IEpubWorkspace"/> 的实现：只持有会话目录与会话描述，
/// 不长期占着 zip 句柄（解压完 ZipArchive 即释放，见设计文档 6.1.2 的"解包的 ZipArchive 未释放"坑）。
/// </summary>
public sealed class EpubWorkspace : IEpubWorkspace
{
    private readonly string _workDir;
    private bool _discarded;

    internal EpubWorkspace(string workDir, WorkspaceSession session)
    {
        _workDir = workDir;
        Session = session;
    }

    public string SessionId => Session.SessionId;

    public string WorkDir => _workDir;

    public WorkspaceSession Session { get; }

    public Task DiscardAsync(CancellationToken ct = default)
    {
        if (_discarded)
        {
            return Task.CompletedTask;
        }

        ct.ThrowIfCancellationRequested();

        try
        {
            EpubWorkspaceFactory.DeleteDirectory(_workDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AppException(
                ErrorCode.AccessDenied,
                "无法清理工作区：" + _workDir + "。可能有程序正占用里面的文件，关掉后再试一次。",
                technical: ex.GetType().Name + ": " + ex.Message,
                inner: ex,
                context: new Dictionary<string, object?> { ["workDir"] = _workDir });
        }

        _discarded = true;
        return Task.CompletedTask;
    }

    /// <summary>
    /// 释放句柄  放弃编辑：目录保留，用户可能还要保存或稍后恢复。
    /// 真正删目录只发生在 <see cref="DiscardAsync"/>。
    /// </summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
