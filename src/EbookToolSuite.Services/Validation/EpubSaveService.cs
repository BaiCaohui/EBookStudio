using EbookToolSuite.Core.Common;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Engines.Epub;
using EbookToolSuite.Services.Editing;
using Microsoft.Extensions.Logging;

namespace EbookToolSuite.Services.Validation;

/// <summary>
/// 带门禁的 EPUB 保存（T3-5）。两段式：
///
///   1) StageAsync：打包到 <c>{目标}.ets-stage</c>，然后拿 EpubCheck 校验**这个暂存文件**。
///      打包器自己会做「写 .part -> EpubPackageVerifier 自检 -> 原子替换到暂存路径」，
///      所以目标文件在这一步一个字节都没被碰过。
///   2) CommitAsync：用户拍板（或校验通过）后，把暂存文件原子替换到目标文件。
///
/// 这样「校验没过」/「用户取消」/「超时后用户放弃」时，原文件天然完好这就是
/// 计划里「保存前必须跑到临时文件上、绝不能对原文件跑」这条硬约束的落点。
/// </summary>
public sealed class EpubSaveService : IEpubSaveService
{
    /// <summary>
    /// 暂存文件后缀。挑 .ets- 前缀是为了和打包器的 .ets-tmp/.ets-bak 同族，一眼能认出来。
    ///
    /// 【为什么末尾必须是 .epub】EpubCheck 靠扩展名判断输入格式，实测（2026-10-08）：
    ///   <c>book.epub.ets-stage</c>    -> 退出码 1、**不写 JSON 报告**、stderr 还一片空白（静默失败）；
    ///   <c>book.epub.ets-stage.epub</c> -> 退出码 0、正常出报告。
    /// 也就是说后缀少个 .epub 会让"校验"变成"永远读不出报告"，保存流程会被门禁莫名拦下。
    /// 别改成别的形状，除非先拿真工具验一遍。
    /// </summary>
    public const string StagedSuffix = ".ets-stage.epub";

    private readonly EpubPackager _packager;
    private readonly IEpubChecker _checker;
    private readonly IRestorePointService? _restorePoints;
    private readonly ILogger<EpubSaveService>? _log;

    public EpubSaveService(
        EpubPackager packager,
        IEpubChecker checker,
        IRestorePointService? restorePoints = null,
        ILogger<EpubSaveService>? log = null)
    {
        _packager = packager;
        _checker = checker;
        _restorePoints = restorePoints;
        _log = log;
    }

    public async Task<EpubSaveStage> StageAsync(
        string workDir,
        string targetEpubPath,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(targetEpubPath))
        {
            throw new AppException(ErrorCode.InvalidPath, "目标文件路径为空，无法保存。");
        }

        var target = Path.GetFullPath(targetEpubPath);
        var staged = target + StagedSuffix;

        // 上一轮留下的残渣先清掉：占着会挡住 File.Move/File.Replace。
        TryDelete(staged);

        EpubPackageResult package;
        try
        {
            package = await _packager
                .PackageAsync(workDir, staged, backupDirectory: null, progress: progress, ct: ct)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            TryDelete(staged);
            throw;
        }

        var decision = await DecideAsync(staged, ct).ConfigureAwait(false);
        return new EpubSaveStage(target, staged, package, decision);
    }

    public async Task<EpubPackageResult> CommitAsync(
        EpubSaveStage stage,
        RestorePointRequest? backup = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stage);

        if (!File.Exists(stage.StagedPath))
        {
            throw new AppException(
                ErrorCode.FileNotFound,
                "暂存的打包产物不见了，请重新保存一次（原文件没有被改动）。",
                stage.StagedPath);
        }

        // 【T3-6 第 6 步】替换之前先把原文件备份成还原点。
        // 这一步失败就直接往上抛：没有备份就替换，等于把"任何修改都能还原"这条保证弄丢。
        RestorePoint? point = null;
        if (backup is not null && _restorePoints is not null && File.Exists(stage.TargetPath))
        {
            try
            {
                point = await _restorePoints
                    .CreateAsync(RestorePointTypes.Save, new[] { backup }, note: null, ct)
                    .ConfigureAwait(false);
            }
            catch (AppException)
            {
                // 业务异常（比如源文件不见了）原样往上抛
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 目标文件被独占占用时，**先失败的其实是备份这一步**（连读都读不到）。
                // 这里必须翻译成人话，否则界面只会说"未知错误"。
                throw new AppException(
                    ErrorCode.FileLocked,
                    "书籍文件正被占用（阅读器或杀毒软件），连备份都做不了，所以没有替换"
                    + "原文件一个字节都没动。请关掉占用它的程序后重试。",
                    "备份 " + stage.TargetPath + " 失败：" + ex.Message,
                    ex);
            }
        }

        try
        {
            // 【第 7 步】原子替换
            if (File.Exists(stage.TargetPath))
            {
                File.Replace(stage.StagedPath, stage.TargetPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(stage.StagedPath, stage.TargetPath, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 【第 8 步】替换失败 -> 按备份把原文件恢复原样 -> 报错
            var recovered = point is null
                ? (bool?)null
                : await TryRecoverAsync(point, stage.TargetPath, ct).ConfigureAwait(false);

            var message = recovered switch
            {
                true => "书籍文件正被占用（阅读器或杀毒软件），没能保存；原文件已与还原点核对一致"
                        + "（没有被改动），请关掉占用它的程序后重试。",
                false => "书籍文件正被占用，没能保存；原文件没能自动恢复，请用还原点「"
                         + point!.Id + "」手工还原（备份在 Data/Backup/RestorePoints 下）。",
                null => "书籍文件正被占用（阅读器或杀毒软件），没能保存；原文件没有被改动，"
                        + "请关掉占用它的程序后重试。",
            };

            throw new AppException(
                ErrorCode.FileLocked,
                message,
                "替换 " + stage.TargetPath + " 失败：" + ex.Message,
                ex);
        }

        var length = new FileInfo(stage.TargetPath).Length;
        return stage.Package with
        {
            OutputPath = stage.TargetPath,
            ByteCount = length,
            RestorePointId = point?.Id,
        };
    }

    /// <summary>
    /// 替换失败后的补救：拿还原点里的备份和磁盘现状比，不一致就把备份写回去。
    /// 返回 false = 没能恢复原样（调用方要如实告诉用户去手工还原，不能谎报"原文件没动"）。
    /// </summary>
    private async Task<bool> TryRecoverAsync(RestorePoint point, string target, CancellationToken ct)
    {
        var item = point.Items.FirstOrDefault(
            candidate => string.Equals(candidate.OriginalPath, target, StringComparison.OrdinalIgnoreCase));

        if (item is null)
        {
            return true;   // 这个文件本来就没被备份过，说明我们没碰过它
        }

        try
        {
            if (File.Exists(target))
            {
                var current = await RestorePointService.HashAsync(target, ct).ConfigureAwait(false);
                if (string.Equals(current, item.OriginalHash, StringComparison.OrdinalIgnoreCase))
                {
                    return true;   // 还是原样，不用动
                }
            }

            var backupFile = Path.Combine(_restorePoints!.PointDirectory(point.Id), item.BackupFile);
            if (!File.Exists(backupFile))
            {
                return false;
            }

            var temp = target + ".ets-recover.tmp";
            File.Copy(backupFile, temp, overwrite: true);

            if (File.Exists(target))
            {
                File.Replace(temp, target, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, target, overwrite: true);
            }

            var restored = await RestorePointService.HashAsync(target, ct).ConfigureAwait(false);
            return string.Equals(restored, item.OriginalHash, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.LogWarning(ex, "从还原点恢复原文件失败 {Target}", target);
            return false;
        }
    }


    public void Discard(EpubSaveStage stage)
    {
        if (stage is null)
        {
            return;
        }

        TryDelete(stage.StagedPath);
    }

    /// <summary>跑校验并把异常翻译成门禁结论校验环节的任何失败都不该让「保存」整个崩掉。</summary>
    private async Task<ValidationDecision> DecideAsync(string stagedPath, CancellationToken ct)
    {
        try
        {
            var report = await _checker.ValidateAsync(stagedPath, ct).ConfigureAwait(false);
            var decision = ValidationGate.Decide(report);

            if (decision.Outcome == ValidationOutcome.PassWithWarnings)
            {
                // T3-6 会把警告写进 operation_log；在那之前至少落到应用日志里，别丢。
                _log?.LogWarning(
                    "EpubCheck 放行但报出 {WarningCount} 条警告：{Messages}",
                    report.WarningCount,
                    string.Join(" | ", report.TopMessages(5)));
            }

            return decision;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AppException ex) when (ex.Code == ErrorCode.ProcessTimeout)
        {
            _log?.LogWarning("EpubCheck 超时，等用户决定是否跳过校验。");
            return ValidationGate.Timeout();
        }
        catch (AppException ex) when (ex.Code is ErrorCode.JavaMissing or ErrorCode.MissingTool)
        {
            _log?.LogWarning("没装校验工具，跳过校验继续保存：{Message}", ex.UserMessage);
            return ValidationGate.ToolMissing(ex.UserMessage);
        }
        catch (AppException ex)
        {
            _log?.LogWarning(ex.InnerException ?? ex, "校验工具运行失败，等用户决定是否跳过校验。");
            return ValidationGate.ToolFailed(ex.UserMessage);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // 删不掉就留着，下次 StageAsync 开头还会再试一次
        }
    }
}
