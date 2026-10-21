using System.Diagnostics;
using System.Text;
using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace EbookToolSuite.Engines.Processes;

/// <summary>
/// 外部进程宿主。全应用唯一允许启动进程的地方。
///
/// 当前版本要点：
///   - 参数走 ArgumentList，绝不拼命令行字符串（中文/空格路径会被拼接破坏）；
///   - stdout/stderr 并发读取，避免管道缓冲区写满导致子进程卡死；
///   - 超时或取消时 Kill(entireProcessTree) 并等它真正退出，不留孤儿进程；
///   - 强制 UTF-8 解码，配合 Calibre 的 PYTHONIOENCODING=utf-8。
///
///   - Job Object（KILL_ON_JOB_CLOSE）：本进程被强杀时，子进程连同它 spawn 的孙进程一起死；
///   - 取消/超时先 TerminateAll 杀掉整个 Job，再用 Kill(entireProcessTree) 兜底，最后等它真退出。
/// </summary>
public sealed class ProcessHost : IProcessHost
{
    private const int LoggedOutputChars = 4000;

    private readonly ILogger<ProcessHost>? _log;

    /// <summary>
    /// 日志是可选的：引擎层只依赖日志抽象，测试里传 null 即可（不引 Serilog 本体）。
    /// </summary>
    public ProcessHost(ILogger<ProcessHost>? log = null) => _log = log;

    public async Task<ProcessResult> RunAsync(
        ProcessSpec spec,
        IProgress<ProcessOutputLine>? output = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var psi = new ProcessStartInfo
        {
            FileName = spec.FileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = spec.CaptureStdout,
            RedirectStandardError = spec.CaptureStderr,
        };

        if (spec.CaptureStdout)
        {
            psi.StandardOutputEncoding = Encoding.UTF8;
        }

        if (spec.CaptureStderr)
        {
            psi.StandardErrorEncoding = Encoding.UTF8;
        }

        if (!string.IsNullOrWhiteSpace(spec.WorkingDirectory))
        {
            psi.WorkingDirectory = spec.WorkingDirectory;
        }

        foreach (var arg in spec.Arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        foreach (var pair in spec.Environment)
        {
            if (pair.Value is null)
            {
                psi.Environment.Remove(pair.Key);
            }
            else
            {
                psi.Environment[pair.Key] = pair.Value;
            }
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var stopwatch = Stopwatch.StartNew();

        using var process = new Process { StartInfo = psi };
        using var job = TryCreateJob();

        if (spec.CaptureStdout)
        {
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null)
                {
                    return;
                }

                // T7-2 第 11 项：乱码字节解出来的 U+FFFD 在这里收成可读标记。
                var line = ToolOutputText.Sanitize(e.Data);
                stdout.AppendLine(line);
                output?.Report(new ProcessOutputLine(DateTimeOffset.Now, false, line));
            };
        }

        if (spec.CaptureStderr)
        {
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null)
                {
                    return;
                }

                var line = ToolOutputText.Sanitize(e.Data);
                stderr.AppendLine(line);
                output?.Report(new ProcessOutputLine(DateTimeOffset.Now, true, line));
            };
        }

        LogStart(spec, psi);

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new AppException(
                ErrorCode.MissingTool,
                $"无法启动外部程序：{spec.FileName}。请检查文件是否存在、是否有执行权限。",
                technical: $"Process.Start failed: {spec.FileName}. {ex.Message}",
                inner: ex,
                context: new Dictionary<string, object?>
                {
                    ["file"] = spec.FileName,
                    ["args"] = string.Join(' ', spec.Arguments),
                });
        }

        // 立刻纳入 Job Object。中间不能插别的代码：子进程一旦先 fork 出去就逃逸了。
        TryAssignToJob(job, process);

        if (spec.CaptureStdout)
        {
            process.BeginOutputReadLine();
        }

        if (spec.CaptureStderr)
        {
            process.BeginErrorReadLine();
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (spec.Timeout is { } timeout)
        {
            linked.CancelAfter(timeout);
        }

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 区分「用户取消」与「超时」：前者原样抛出，后者转成 ProcessTimeout。
            timedOut = !ct.IsCancellationRequested;

            // 双保险：先杀整个 Job（连 Calibre 再 spawn 出来的 Python 一起），
            // 再用 Kill(entireProcessTree) 兜底（Job 建不出来时它是唯一手段）。
            job?.TerminateAll();
            TryKillTree(process);

            // 必须等它真正退出，否则调用方以为结束了但进程还在。
            try
            {
                await process.WaitForExitAsync(CancellationToken.None)
                             .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 已经尽力了
            }
        }

        // WaitForExit() 无参重载会等异步读取的事件处理完，确保 stdout/stderr 不丢尾巴。
        if (spec.CaptureStdout || spec.CaptureStderr)
        {
            try
            {
                process.WaitForExit();
            }
            catch (Exception)
            {
                // 进程句柄可能已失效，忽略
            }
        }

        stopwatch.Stop();

        if (!timedOut && ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }

        if (timedOut)
        {
            throw new AppException(
                ErrorCode.ProcessTimeout,
                $"外部程序超过 {spec.Timeout} 未完成，已终止。",
                technical: $"Process timed out after {spec.Timeout}: {spec.FileName}",
                context: new Dictionary<string, object?>
                {
                    ["file"] = spec.FileName,
                    ["args"] = string.Join(' ', spec.Arguments),
                    ["timeout"] = spec.Timeout?.ToString(),
                    ["stderrTail"] = Tail(stderr.ToString()),
                });
        }

        var exitCode = process.ExitCode;

        _log?.LogInformation(
            "外部进程结束 exe={Exe} exit={ExitCode} 耗时={ElapsedMs}ms\n  stdout={StdOut}\n  stderr={StdErr}",
            Path.GetFileName(spec.FileName),
            exitCode,
            (long)stopwatch.Elapsed.TotalMilliseconds,
            Truncate(stdout.ToString(), LoggedOutputChars),
            Truncate(stderr.ToString(), LoggedOutputChars));

        return new ProcessResult(
            exitCode,
            stdout.ToString(),
            stderr.ToString(),
            stopwatch.Elapsed);
    }

    /// <summary>外部进程调用必须留痕：记 exe、完整参数、工作目录、注入的环境变量。</summary>
    private void LogStart(ProcessSpec spec, ProcessStartInfo psi)
    {
        if (_log is null)
        {
            return;
        }

        var env = spec.Environment.Count == 0
            ? "(无)"
            : string.Join(
                ", ",
                spec.Environment.Select(p => p.Key + "=" + MaskPaths(p.Value ?? "(删除)")));

        _log.LogInformation(
            "外部进程启动 exe={Exe}\n  args={Args}\n  cwd={Cwd}\n  env={Env}",
            MaskPaths(spec.FileName),
            "[" + string.Join(", ", spec.Arguments.Select(a => "\"" + MaskPaths(a) + "\"")) + "]",
            MaskPaths(psi.WorkingDirectory),
            env);
    }

    /// <summary>把用户目录替换成 %USERPROFILE%，日志里不出现真实用户名。</summary>
    private static string MaskPaths(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(profile)
            ? value
            : value.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
    }

    private static string Truncate(string text, int max)
        => text.Length <= max
            ? text
            : text[..max] + "(已截断，实际 " + text.Length + " 字符)";

    /// <summary>
    /// 建一个 Job Object。建不出来（非 Windows、被安全软件拦、句柄耗尽）不算致命：
    /// 返回 null，取消时还有 Kill(entireProcessTree) 兜底。
    /// </summary>
    private JobObject? TryCreateJob()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return new JobObject("ETS_" + Guid.NewGuid().ToString("N"));
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "创建 Job Object 失败，改用 Kill(entireProcessTree) 兜底");
            return null;
        }
    }

    /// <summary>纳入 Job 失败也只是少一层保险，不打断转换。</summary>
    private void TryAssignToJob(JobObject? job, Process process)
    {
        if (job is null)
        {
            return;
        }

        try
        {
            job.Assign(process);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "把进程纳入 Job Object 失败，改用 Kill(entireProcessTree) 兜底");
        }
    }
    private static void TryKillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // 进程可能刚好自己退出了，忽略
        }
    }

    /// <summary>只保留末尾若干行，避免把整份日志塞进异常上下文。</summary>
    private static string Tail(string text, int lines = 20)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var all = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return all.Length <= lines
            ? text
            : string.Join('\n', all.Skip(all.Length - lines));
    }
}
