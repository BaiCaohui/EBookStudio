using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using EbookToolSuite.App.Logging;
using EbookToolSuite.Infrastructure.Config;
using EbookToolSuite.Infrastructure.Logging;
using Serilog;

namespace EbookToolSuite.App.Smoke;

/// <summary>
/// 日志与全局异常冒烟测试（T0-7）。验证三件事：
///   1. UI 线程未处理异常不会闪退，且日志里有完整堆栈；
///   2. 未观察的 Task 异常会被 TaskScheduler.UnobservedTaskException 捕获；
///   3. 日志文件按天生成、格式正确、中文不乱码、无 BOM。
/// </summary>
public static class LoggingSmokeTest
{
    private static readonly Regex LineFormat = new(
        @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} \[(VRB|DBG|INF|WRN|ERR|FTL)\] ",
        RegexOptions.Compiled | RegexOptions.Multiline);

    public static async Task<SmokeReport> RunAsync(
        AppPaths paths,
        GlobalExceptionHandler guards,
        Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(guards);
        ArgumentNullException.ThrowIfNull(dispatcher);

        var startedAt = DateTimeOffset.Now;
        var stopwatch = Stopwatch.StartNew();
        var checks = new List<SmokeCheck>();

        var uiMarker = "SMOKE-UI-" + Guid.NewGuid().ToString("N")[..8];
        var taskMarker = "SMOKE-TASK-" + Guid.NewGuid().ToString("N")[..8];
        const string chineseMarker = "中文编码检查：你好，世界。《Sample Book I》";

        // --- 场景 1：UI 线程未处理异常 ---
        // 必须用 BeginInvoke（异步）。用 Invoke 的话异常会直接抛回调用方，
        // 走不到 DispatcherUnhandledException。
        _ = dispatcher.BeginInvoke(new Action(() =>
            throw new InvalidOperationException(uiMarker)));

        // --- 场景 2：未观察的 Task 异常 ---
        _ = Task.Run(() => throw new InvalidOperationException(taskMarker));

        // --- 场景 3：中文日志 ---
        Log.Information("{Marker}", chineseMarker);

        await WaitForAsync(() => guards.UiExceptionCount > 0, 10).ConfigureAwait(true);
        checks.Add(new SmokeCheck(
            "ui-exception-caught",
            guards.UiExceptionCount > 0,
            "DispatcherUnhandledException 触发 " + guards.UiExceptionCount + " 次"));

        // UnobservedTaskException 要等 Task 被 GC + finalizer，所以要主动催
        for (var i = 0; i < 30 && guards.UnobservedTaskExceptionCount == 0; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await Task.Delay(500).ConfigureAwait(true);
        }

        checks.Add(new SmokeCheck(
            "unobserved-task-caught",
            guards.UnobservedTaskExceptionCount > 0,
            "UnobservedTaskException 触发 " + guards.UnobservedTaskExceptionCount + " 次"));

        // 等异步 sink 落盘
        await Task.Delay(2000).ConfigureAwait(true);

        // --- 校验日志文件 ---
        var logFile = LoggingSetup.CurrentFilePath(paths, DateTimeOffset.Now);
        var exists = File.Exists(logFile);
        checks.Add(new SmokeCheck("log-file-created", exists, logFile));

        // 读文件失败就退回内存环形缓冲：既能拿到同样的内容，
        // 也顺便验证了「文件被占用」这种情况不会让诊断功能瘫痪。
        var text = string.Empty;
        var readError = string.Empty;
        if (exists)
        {
            try
            {
                text = ReadAllTextShared(logFile);
            }
            catch (Exception ex)
            {
                readError = ex.GetType().Name + ": " + ex.Message;
                text = string.Join(Environment.NewLine, LogRingBuffer.Instance.Snapshot(5000));
            }
        }

        checks.Add(new SmokeCheck(
            "ui-stack-in-log",
            text.Contains(uiMarker, StringComparison.Ordinal)
                && text.Contains("DispatcherUnhandledException", StringComparison.Ordinal) == false
                && text.Contains("InvalidOperationException", StringComparison.Ordinal),
            exists ? "已找到 UI 异常标记与堆栈" : "日志文件不存在"));

        checks.Add(new SmokeCheck(
            "unobserved-task-in-log",
            text.Contains(taskMarker, StringComparison.Ordinal),
            text.Contains(taskMarker, StringComparison.Ordinal)
                ? "已找到 Task 异常标记"
                : "日志里没有 " + taskMarker));

        checks.Add(new SmokeCheck(
            "chinese-not-mangled",
            text.Contains(chineseMarker, StringComparison.Ordinal),
            text.Contains(chineseMarker, StringComparison.Ordinal)
                ? "中文原样写入"
                : "中文丢失或被转义"));

        checks.Add(new SmokeCheck(
            "log-line-format",
            LineFormat.IsMatch(text),
            "行首形如 2026-10-01 23:59:59.123 [INF]"));

        checks.Add(new SmokeCheck(
            "log-no-bom",
            exists && !HasUtf8Bom(logFile),
            "日志文件无 BOM（记事本打开不乱码）"));

        checks.Add(new SmokeCheck(
            "ring-buffer",
            LogRingBuffer.Instance.Count > 0,
            "内存环形缓冲有 " + LogRingBuffer.Instance.Count + " 条"));

        var report = new SmokeReport(
            startedAt.ToString("O"),
            DateTimeOffset.Now.ToString("O"),
            stopwatch.ElapsedMilliseconds,
            paths.DataRoot,
            paths.LogsDir,
            null,
            checks);

        WriteReport(paths, report);
        return report;
    }

    private static async Task WaitForAsync(Func<bool> condition, int seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(100).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// 必须用 FileShare.ReadWrite 读日志：程序运行期间日志文件一直被 Serilog 持有，
    /// 默认的 FileShare.Read 会被系统拒绝访问（实测踩过）。
    /// 这同样适用于「复制最近 200 行日志」和诊断包导出。
    /// </summary>
    private static string ReadAllTextShared(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static bool HasUtf8Bom(string path)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var head = new byte[3];
            return stream.Read(head, 0, 3) == 3
                && head[0] == 0xEF
                && head[1] == 0xBB
                && head[2] == 0xBF;
        }
        catch (IOException)
        {
            // 读不了就不判定为"有 BOM"，避免把读失败说成编码问题
            return false;
        }
    }

    private static void WriteReport(AppPaths paths, SmokeReport report)
    {
        Directory.CreateDirectory(paths.TempDir);
        var path = Path.Combine(paths.TempDir, "logging-smoke.json");
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true,
            }),
            new UTF8Encoding(false));
    }
}