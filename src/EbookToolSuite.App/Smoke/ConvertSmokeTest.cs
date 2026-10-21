using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Services.Conversion;
using EbookToolSuite.Infrastructure.Config;
using EbookToolSuite.Infrastructure.Files;
using Microsoft.Extensions.DependencyInjection;

namespace EbookToolSuite.App.Smoke;

/// <summary>
/// 转换服务的冒烟测试（执行计划 T1-4 的 7 条完成判据）。
///
/// 为什么要有它：转换是全项目最容易出 bug 的外部依赖，
/// 取消/超时/中文路径/孤儿进程这几件事，靠人眼点一遍既慢又容易漏。
/// </summary>
public static class ConvertSmokeTest
{
    /// <summary>等外部进程真正开始产出（第一条输出）的超时。</summary>
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);


    public static async Task<SmokeReport> RunAsync(
        IBookImportService importer,
        IConversionService conversion,
        IConversionQueue queue,
        ConversionTemplateStore templates,
        IBookRepository repo,
        AppPaths paths,
        string sourcePath)
    {
        var startedAt = DateTimeOffset.Now;
        var stopwatch = Stopwatch.StartNew();
        var checks = new List<SmokeCheck>();

        // ---------- 准备：先导入一本，转换必须作用在库里的书上 ----------
        var imported = await importer
            .ImportAsync(sourcePath, ImportOptions.Default, null)
            .ConfigureAwait(true);

        checks.Add(new SmokeCheck(
            "0-import-ok",
            imported.Outcome == ImportOutcome.Imported,
            "结果=" + imported.Outcome));

        if (imported.BookId is not { } bookId)
        {
            return Finish(paths, startedAt, stopwatch, checks);
        }

        // ---------- A：EPUB -> MOBI 成功，产物落盘、库里有记录 ----------
        var endpoint = new List<ConversionProgress>();
        var reports = new SmokeProbe.SynchronousProgress<ConversionProgress>(endpoint.Add);

        ConversionResult? result = null;
        Exception? failure = null;
        try
        {
            result = await conversion
                .ConvertAsync(
                    new ConversionRequest
                    {
                        BookId = bookId,
                        TargetFormat = BookFormatKind.Mobi,
                    },
                    reports,
                    CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        checks.Add(new SmokeCheck(
            "A-convert-ok",
            result is not null && File.Exists(result.OutputPath) && result.SizeBytes > 0,
            failure is null
                ? "产物=" + (result?.OutputPath ?? "(无)")
                    + " 大小=" + (result?.SizeBytes ?? 0) + " 字节"
                    + " 用时=" + (result?.Duration.TotalSeconds ?? 0).ToString("F1") + " 秒"
                : "失败：" + failure.GetType().Name + " " + failure.Message));

        // ---------- A2：book_formats 里有 MOBI 记录 ----------
        var reloaded = await repo.GetByIdAsync(bookId).ConfigureAwait(true);
        var mobiRow = reloaded?.Formats.FirstOrDefault(f => f.Format == BookFormatKind.Mobi);

        checks.Add(new SmokeCheck(
            "A2-db-row",
            mobiRow is not null
                && mobiRow.FileSize > 0
                && File.Exists(mobiRow.FilePath)
                && string.Equals(
                    mobiRow.FilePath, result?.OutputPath, StringComparison.OrdinalIgnoreCase),
            mobiRow is null
                ? "库里没有 MOBI 记录"
                : "路径=" + mobiRow.FilePath + " 大小=" + mobiRow.FileSize));

        // ---------- A3：进度解析（诚实策略） ----------
        var percents = endpoint.Where(p => p.Percent is not null).Select(p => p.Percent!.Value).ToList();
        var sawElapsed = endpoint.Any(p => p.Elapsed >= TimeSpan.Zero);
        var monotonic = percents.Count < 2 || percents.Zip(percents.Skip(1), (a, b) => b >= a).All(x => x);

        checks.Add(new SmokeCheck(
            "A3-progress-honest",
            endpoint.Count > 0 && sawElapsed && monotonic
                && percents.All(p => p is >= 0d and <= 1d),
            "回调 " + endpoint.Count + " 次，其中带百分比 " + percents.Count + " 次"
                + "（实测 Calibre 全程只有 3 行带百分比，所以次数少是正常的）"
                + "，单调=" + monotonic));

        // ---------- A4：同一个格式再转一次要被挡住（不覆盖、不重复登记） ----------
        Exception? duplicateError = null;
        try
        {
            await conversion
                .ConvertAsync(
                    new ConversionRequest { BookId = bookId, TargetFormat = BookFormatKind.Mobi },
                    null,
                    CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            duplicateError = ex;
        }

        checks.Add(new SmokeCheck(
            "A4-duplicate-blocked",
            duplicateError is AppException { Code: ErrorCode.ConversionOutputExists },
            duplicateError is null
                ? "居然又转了一次（应该被拦住）"
                : "错误码=" + (duplicateError as AppException)?.Code));

        // ---------- B：取消（等进程真的开始产出再取消，否则测不到杀进程那段） ----------
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancelCts = new CancellationTokenSource();
        var cancelWatch = Stopwatch.StartNew();
        var cancelProgress = new SmokeProbe.SynchronousProgress<ConversionProgress>(_ =>
        {
            if (!started.Task.IsCompleted)
            {
                started.TrySetResult(true);
            }
        });

        Exception? cancelError = null;
        var cancelTask = Task.Run(async () =>
        {
            try
            {
                await conversion
                    .ConvertAsync(
                        new ConversionRequest
                        {
                            BookId = bookId,
                            TargetFormat = BookFormatKind.Pdf,
                        },
                        cancelProgress,
                        cancelCts.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                cancelError = ex;
            }
        });

        await Task.WhenAny(started.Task, Task.Delay(StartTimeout)).ConfigureAwait(true);
        cancelCts.Cancel();
        await cancelTask.ConfigureAwait(true);
        cancelWatch.Stop();

        checks.Add(new SmokeCheck(
            "B-cancel",
            cancelError is OperationCanceledException && cancelWatch.Elapsed < TimeSpan.FromSeconds(10),
            "进程已开始=" + started.Task.IsCompleted
                + " 取消耗时=" + cancelWatch.Elapsed.TotalSeconds.ToString("F1") + " 秒"
                + " 结果=" + (cancelError?.GetType().Name ?? "没抛异常")));

        checks.Add(new SmokeCheck(
            "B-cancel-no-orphan",
            CountCalibreProcesses(paths) == 0,
            DescribeCalibreProcesses(paths)));

        // 取消后不该留下半成品：库里没有 PDF 记录，临时目录也没有残留 ----------
        var afterCancel = await repo.GetByIdAsync(bookId).ConfigureAwait(true);
        var pdfRow = afterCancel?.Formats.FirstOrDefault(f => f.Format == BookFormatKind.Pdf);
        var leftoverRuns = CountLeftoverRunDirectories(paths);

        checks.Add(new SmokeCheck(
            "B-cancel-no-partial",
            pdfRow is null && leftoverRuns == 0,
            "库里有 PDF 记录=" + (pdfRow is not null) + " 残留临时目录=" + leftoverRuns));

        // ---------- C：超时（超时设成 1 秒，必定触发） ----------
        Exception? timeoutError = null;
        var timeoutWatch = Stopwatch.StartNew();
        try
        {
            await conversion
                .ConvertAsync(
                    new ConversionRequest
                    {
                        BookId = bookId,
                        TargetFormat = BookFormatKind.Pdf,
                        Timeout = TimeSpan.FromSeconds(1),
                    },
                    null,
                    CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            timeoutError = ex;
        }

        timeoutWatch.Stop();

        checks.Add(new SmokeCheck(
            "C-timeout",
            timeoutError is AppException { Code: ErrorCode.ProcessTimeout }
                && timeoutWatch.Elapsed < TimeSpan.FromSeconds(30),
            "耗时=" + timeoutWatch.Elapsed.TotalSeconds.ToString("F1") + " 秒"
                + " 错误码=" + (timeoutError as AppException)?.Code
                + " 类型=" + (timeoutError?.GetType().Name ?? "(没抛异常)")));

        checks.Add(new SmokeCheck(
            "C-timeout-no-orphan",
            CountCalibreProcesses(paths) == 0 && CountLeftoverRunDirectories(paths) == 0,
            DescribeCalibreProcesses(paths)
                + " 残留临时目录=" + CountLeftoverRunDirectories(paths)));

        // ---------- D：中文 + 空格路径（输入与输出都带中文与空格） ----------
        // 注意：不能用"复制一份到中文目录再导入"的办法做这个测试，
        // 因为快速指纹会把副本识别成同一本书（那是正确的去重行为）。
        // 所以改成：把输出目录指到一个中文带空格的目录，输入本来就是中文文件名。
        var chineseDirectory = Path.Combine(paths.TempDir, "中文 目录 测试");
        Directory.CreateDirectory(chineseDirectory);

        Exception? chineseError = null;
        ConversionResult? chineseResult = null;
        try
        {
            chineseResult = await conversion
                .ConvertAsync(
                    new ConversionRequest
                    {
                        BookId = bookId,
                        TargetFormat = BookFormatKind.Txt,
                        OutputDirectory = chineseDirectory,
                    },
                    null,
                    CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            chineseError = ex;
        }

        var chinesePathUsed = chineseResult?.OutputPath ?? string.Empty;
        checks.Add(new SmokeCheck(
            "D-chinese-path",
            chineseResult is not null
                && chinesePathUsed.StartsWith(chineseDirectory, StringComparison.OrdinalIgnoreCase)
                && File.Exists(chinesePathUsed)
                && chineseResult.SizeBytes > 0,
            chineseError is null
                ? "产物=" + chinesePathUsed + " 大小=" + (chineseResult?.SizeBytes ?? 0)
                : "失败：" + chineseError.GetType().Name + " " + chineseError.Message));


        // ---------- E：磁盘预检（用一个大到不可能的源文件大小触发） ----------
        Exception? diskError = null;
        try
        {
            DiskSpaceGuard.Ensure(paths.LibraryDir, long.MaxValue / 4, 2.0);
        }
        catch (Exception ex)
        {
            diskError = ex;
        }

        checks.Add(new SmokeCheck(
            "E-disk-guard",
            diskError is AppException { Code: ErrorCode.DiskFull },
            (diskError as AppException)?.UserMessage ?? "(没有拦住，说明预检失效)"));

        // ---------- F：转换窗口能真的构造出来 ----------
        // 这一步专门抓 XAML 运行时错误（资源键写错、绑定写错），
        // 否则只有用户点「转换」的那一刻才会炸。
        Exception? windowError = null;
        string windowDetail;
        var sourceCount = -1;
        var targetCount = -1;
        var templateCount = -1;
        try
        {
            var book = await repo.GetByIdAsync(bookId).ConfigureAwait(true);
            var window = new Views.ConvertWindow(
                conversion, queue, templates, App.Services.GetRequiredService<IToolLocator>(), book!)
            {
                ShowActivated = false,
            };

            window.Show();
            window.UpdateLayout();
            sourceCount = (window.FindName("SourceCombo") as ComboBox)?.Items.Count ?? -1;
            targetCount = (window.FindName("TargetCombo") as ComboBox)?.Items.Count ?? -1;
            templateCount = (window.FindName("TemplateCombo") as ComboBox)?.Items.Count ?? -1;

            windowDetail = "窗口标题=" + window.Title + "，控件已加载=" + window.IsLoaded;
            window.Close();
        }
        catch (Exception ex)
        {
            windowError = ex;
            windowDetail = ex.GetType().Name + "：" + ex.Message;
        }

        checks.Add(new SmokeCheck("F-convert-window", windowError is null, windowDetail));

        checks.Add(new SmokeCheck(
            "F2-convert-options-populated",
            windowError is null && sourceCount >= 1 && targetCount >= 1 && templateCount >= 1,
            "源格式 " + sourceCount + " 项 / 目标格式 " + targetCount + " 项 / 模板 " + templateCount + " 项"));

        return Finish(paths, startedAt, stopwatch, checks);
    }

    /// <summary>
    /// 崩溃场景：启动一次转换，等进程真的产出第一行输出后写标记文件，然后【故意不结束】。
    /// 由 smoke-convert.ps1 强杀本进程，再检查 Calibre 有没有跟着死（Job Object 判据）。
    /// </summary>
    public static async Task RunCrashAsync(
        IBookImportService importer,
        IConversionService conversion,
        AppPaths paths,
        string sourcePath)
    {
        var imported = await importer
            .ImportAsync(sourcePath, ImportOptions.Default, null)
            .ConfigureAwait(true);

        if (imported.BookId is not { } bookId)
        {
            return;
        }

        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new SmokeProbe.SynchronousProgress<ConversionProgress>(_ => started.TrySetResult(true));

        var conversionTask = Task.Run(() => conversion.ConvertAsync(
            new ConversionRequest { BookId = bookId, TargetFormat = BookFormatKind.Mobi },
            progress,
            CancellationToken.None));

        await Task.WhenAny(started.Task, Task.Delay(StartTimeout)).ConfigureAwait(true);

        var marker = Path.Combine(paths.TempDir, "convert-crash.started");
        Directory.CreateDirectory(paths.TempDir);
        await File.WriteAllTextAsync(marker, DateTimeOffset.Now.ToString("O"), Encoding.UTF8)
            .ConfigureAwait(true);

        // 卡住不退出，等外部脚本来杀。
        await Task.Delay(TimeSpan.FromMinutes(10)).ConfigureAwait(true);
        await conversionTask.ConfigureAwait(true);
    }

    private static SmokeReport Finish(
        AppPaths paths, DateTimeOffset startedAt, Stopwatch stopwatch, List<SmokeCheck> checks)
    {
        var report = new SmokeReport(
            startedAt.ToString("O"),
            DateTimeOffset.Now.ToString("O"),
            stopwatch.ElapsedMilliseconds,
            paths.DataRoot,
            paths.TempDir,
            null,
            checks);

        Directory.CreateDirectory(paths.TempDir);
        File.WriteAllText(
            Path.Combine(paths.TempDir, "convert-smoke.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));

        return report;
    }

    /// <summary>当前有几个属于我们这套 Calibre 的进程还活着（判孤儿用）。</summary>
    private static int CountCalibreProcesses(AppPaths paths)
        => SmokeProbe.CountToolProcesses(paths);

    private static string DescribeCalibreProcesses(AppPaths paths)
        => "残留 Calibre 进程=" + CountCalibreProcesses(paths) + "（判据是 0）";

    /// <summary>临时目录里还剩几个 conv-* 运行目录（判清理用）。</summary>
    private static int CountLeftoverRunDirectories(AppPaths paths)
        => SmokeProbe.CountLeftoverRunDirectories(paths);

}
