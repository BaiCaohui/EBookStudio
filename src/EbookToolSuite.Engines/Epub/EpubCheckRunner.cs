using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using Microsoft.Extensions.Logging;

namespace EbookToolSuite.Engines.Epub;

/// <summary>
/// EpubCheck 5.4.0 的进程封装（T3-5）。
///
/// 调用模板来自 Tools/manifest.json 的 notes：
///   {java} -jar {jar} "{input}" --json "{out}" --quiet
/// 关键事实（实测，别再自己猜）：
///   - 退出码 <c>0</c> = 没有 error，<c>1</c> = 存在 error。**两个都是"正常跑完"**，不是进程失败；
///   - <c>--json</c> 自带输出文件参数，不能再叠 <c>-o</c>；
///   - 冷启动很慢：418 KB 的书约 7 秒、3.7 MB 的书约 15 秒，所以默认超时给 60 秒；
///   - 进程一律走 IProcessHost（全应用唯一允许 Process.Start 的地方），超时会杀整棵进程树。
/// </summary>
public sealed class EpubCheckRunner : IEpubChecker
{
    public const int DefaultTimeoutSeconds = 60;

    private readonly IToolLocator _tools;
    private readonly IProcessHost _host;
    private readonly ILogger<EpubCheckRunner>? _log;
    private readonly TimeSpan _timeout;

    /// <param name="timeoutSeconds">小于等于 0 时用默认值（测试里会传 2 秒来造超时）。</param>
    public EpubCheckRunner(
        IToolLocator tools,
        IProcessHost host,
        ILogger<EpubCheckRunner>? log = null,
        int timeoutSeconds = DefaultTimeoutSeconds)
    {
        _tools = tools;
        _host = host;
        _log = log;
        TimeoutSeconds = timeoutSeconds > 0 ? timeoutSeconds : DefaultTimeoutSeconds;
        _timeout = TimeSpan.FromSeconds(TimeoutSeconds);
    }

    public int TimeoutSeconds { get; }

    public async Task<CheckReport> ValidateAsync(string epubPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(epubPath))
        {
            throw new AppException(ErrorCode.InvalidPath, "要校验的文件路径为空。");
        }

        if (!File.Exists(epubPath))
        {
            throw new AppException(ErrorCode.FileNotFound, "要校验的文件不存在：" + epubPath, epubPath);
        }

        var java = _tools.Java;
        if (string.IsNullOrWhiteSpace(java) || !File.Exists(java))
        {
            throw new AppException(
                ErrorCode.JavaMissing,
                "没找到便携 JRE（Tools/JRE），无法用 EpubCheck 校验。",
                "IToolLocator.Java = " + (java ?? "(null)"));
        }

        var jar = _tools.EpubCheckJar;
        if (string.IsNullOrWhiteSpace(jar) || !File.Exists(jar))
        {
            throw new AppException(
                ErrorCode.MissingTool,
                "没找到 EpubCheck（Tools/EpubCheck/epubcheck.jar），无法校验。",
                "IToolLocator.EpubCheckJar = " + (jar ?? "(null)"));
        }

        // 报告写到临时目录，文件名带随机串：并发校验两本书时不会互相覆盖。
        var reportPath = Path.Combine(
            Path.GetTempPath(),
            "ets-epubcheck-" + Guid.NewGuid().ToString("N") + ".json");

        try
        {
            var spec = new ProcessSpec
            {
                FileName = java,
                Arguments = new[] { "-jar", jar, epubPath, "--json", reportPath, "--quiet" },
                Environment = _tools.BuildEnvironment("epubcheck"),
                Timeout = _timeout,
            };

            // 超时/缺工具都由 ProcessHost 抛 AppException（ProcessTimeout / MissingTool），原样往上抛。
            var result = await _host.RunAsync(spec, null, ct).ConfigureAwait(false);

            // 0 = 没有 error，1 = 有 error，都是正常结束；>= 2 才是工具自己崩了。
            if (result.ExitCode > 1)
            {
                throw new AppException(
                    ErrorCode.ProcessCrashed,
                    "EpubCheck 运行失败（退出码 " + result.ExitCode + "）。",
                    "exit=" + result.ExitCode + " stderrTail=" + Tail(result.StdErr));
            }

            if (!File.Exists(reportPath))
            {
                return CheckReport.Unparseable(
                    "EpubCheck 没有生成 JSON 报告（退出码 " + result.ExitCode + "）。");
            }

            var report = EpubCheckReportParser.ParseFile(reportPath);
            _log?.LogInformation(
                "EpubCheck 完成 {Counts} 耗时 {ElapsedMs}ms 文件={File}",
                report.CountsText,
                (long)result.Duration.TotalMilliseconds,
                Path.GetFileName(epubPath));

            return report with { Duration = result.Duration };
        }
        finally
        {
            TryDelete(reportPath);
        }
    }

    private static string Tail(string text)
    {
        var trimmed = (text ?? string.Empty).Trim();
        return trimmed.Length <= 400 ? trimmed : trimmed[^400..];
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
            // 临时文件删不掉不算错，系统迟早会清
        }
    }
}
