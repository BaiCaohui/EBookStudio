using System.Text.Json;
using EbookToolSuite.Core.Common;
using EbookToolSuite.Core.Errors;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using Microsoft.Extensions.Logging;

namespace EbookToolSuite.Services.Editing;

/// <summary>
/// 可视编辑"自动修正"的执行链路（T5-2W 第 5 步 / 判据 9~12）。
///
/// 【为什么先建还原点】修正会把工作区里这一章的源码整篇覆盖掉，覆盖了就回不去。
/// 所以先按 T3-6 的还原点机制把修正前的原文备份一份（type=autofix），
/// 用户要退回时在备份中心还原这个还原点即可（还原目标就是工作区里这一章的文件）。
///
/// 【为什么失败要把刚建的还原点删掉】修正失败 = 原文零改动，这个还原点没用了；
/// 留着它会占 1 GB 总上限，还会挤掉"单本 5 份"的名额（EnforceLimitsAsync 按本计数）。
/// 删的是我们**刚刚**建的、还没被任何日志/界面引用过的那一个目录。
///
/// 【为什么失败不写 operation_log】日志是"改过什么"的账；原文没变就不该记账，
/// 界面上照样会看到失败原因（<see cref="VisualAutoFixOutcome.Message"/>）。
/// </summary>
public sealed class VisualEditAutoFixService : IVisualEditAutoFixService
{
    private readonly IChapterSourceService _source;
    private readonly IRestorePointService _restorePoints;
    private readonly IOperationLogRepository? _operations;
    private readonly ILogger<VisualEditAutoFixService>? _log;

    public VisualEditAutoFixService(
        IChapterSourceService source,
        IRestorePointService restorePoints,
        IOperationLogRepository? operations = null,
        ILogger<VisualEditAutoFixService>? log = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _restorePoints = restorePoints ?? throw new ArgumentNullException(nameof(restorePoints));
        _operations = operations;
        _log = log;
    }

    public async Task<VisualAutoFixOutcome> ApplyAsync(
        IEpubWorkspace workspace,
        string entry,
        string bodyInnerXml,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry);

        // 1) 先扫一遍（不写盘）：确认确实有可修正项。这一步和弹窗里给用户看的是同一条链路。
        var scan = _source.SaveVisual(workspace, entry, bodyInnerXml);

        if (scan.Ok)
        {
            return new VisualAutoFixOutcome(
                true, false, Array.Empty<SourceFix>(), "这一章不需要修正（校验直接通过）", null);
        }

        if (!scan.NeedsFix)
        {
            // 属于"修不了"的那类问题（比如根本不是 XHTML）：不动盘、不建还原点
            return new VisualAutoFixOutcome(false, false, Array.Empty<SourceFix>(), scan.Message, null);
        }

        // 2) 修正前备份：备份的是工作区里这一章的原文
        var chapterFile = Path.Combine(workspace.WorkDir, entry.Replace('/', Path.DirectorySeparatorChar));

        RestorePoint point;

        try
        {
            point = await _restorePoints
                .CreateAsync(
                    RestorePointTypes.AutoFix,
                    new[]
                    {
                        new RestorePointRequest(
                            workspace.Session.BookId, workspace.Session.FormatId, chapterFile),
                    },
                    note: "可视编辑自动修正前备份：" + entry,
                    ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 备份没成功就绝不动盘（宁可拦住保存，也不要"改了退不回去"）
            _log?.LogWarning(ex, "自动修正前建还原点失败 {Entry}", entry);
            return new VisualAutoFixOutcome(
                false,
                false,
                scan.Fixes,
                "自动修正前备份失败，已中止（原文没有被改动）：" + ex.SafeMessage(),
                null);
        }

        // 3) 应用修正：SaveVisual 内部会再校验一次，不合格就一个字节都不写
        var applied = _source.SaveVisual(workspace, entry, bodyInnerXml, applyFixes: true);

        if (!applied.Ok)
        {
            DeleteUnusedPoint(point.Id);
            return new VisualAutoFixOutcome(false, true, applied.Fixes, applied.Message, null);
        }

        // 4) 记账（写日志失败绝不影响已经落盘的结果，和 T3-6 的保存口径一致）
        await LogAsync(workspace, entry, point, applied.Fixes, ct).ConfigureAwait(false);

        return new VisualAutoFixOutcome(true, true, applied.Fixes, applied.Message, point.Id);
    }

    private async Task LogAsync(
        IEpubWorkspace workspace,
        string entry,
        RestorePoint point,
        IReadOnlyList<SourceFix> fixes,
        CancellationToken ct)
    {
        if (_operations is null)
        {
            return;
        }

        try
        {
            var detail = JsonSerializer.Serialize(new
            {
                entry,
                restorePointId = point.Id,
                fixes = fixes.Select(fix => new { fix.Kind, fix.Line, fix.Reason }).ToList(),
            });

            var count = fixes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);

            await _operations
                .AddAsync(
                    new OperationLogEntry(
                        OperationNames.AutoFix,
                        "自动修正 " + entry + "（" + count + " 处）",
                        workspace.Session.BookId,
                        workspace.Session.FormatId,
                        point.Id,
                        detail),
                    ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "写 operation_log（autofix）失败 {Entry}", entry);
        }
    }

    /// <summary>清掉"刚建、但修正没落盘所以没用了"的还原点目录。</summary>
    private void DeleteUnusedPoint(string pointId)
    {
        try
        {
            var directory = _restorePoints.PointDirectory(pointId);

            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
                _log?.LogInformation("修正没落盘，已清掉多余的还原点 {PointId}", pointId);
            }
        }
        catch (Exception ex)
        {
            // 清不掉只是多占一点备份空间，不该把失败原因盖掉
            _log?.LogWarning(ex, "清掉没用的还原点失败 {PointId}", pointId);
        }
    }
}