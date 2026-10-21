using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace EbookToolSuite.Engines.Calibre;

/// <summary>
/// Calibre 单次转换的执行者：拼命令行、跑进程、判定结果。
/// 产物落在调用方给的临时目录里；改名到正式区、登记书库都不归它管。
///
/// 退出码约定（实测，见 calibre-args-baseline 第 4 节）：
///   0 = 成功；1 = 运行期错误（缺文件/缺 DLL）；2 = 参数错误。
/// 所以 2 要报成"参数不被支持"而不是笼统的"转换失败"。
/// </summary>
public sealed class CalibreConverter
{
    private const int ErrorTailLines = 30;

    private readonly IProcessHost _processHost;
    private readonly ILogger<CalibreConverter>? _log;

    public CalibreConverter(IProcessHost processHost, ILogger<CalibreConverter>? log = null)
    {
        _processHost = processHost;
        _log = log;
    }

    public async Task<CalibreConversionOutcome> ConvertAsync(
        CalibreConversionSpec spec,
        IProgress<ProcessOutputLine>? output = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var arguments = new List<string>(spec.Options.Count * 2 + 2)
        {
            spec.InputPath,
            spec.TempOutputPath,
        };
        arguments.AddRange(CalibreArgumentBuilder.ToCommandLineOptions(spec.Options));

        var processSpec = new ProcessSpec
        {
            FileName = spec.ExePath,
            Arguments = arguments,
            WorkingDirectory = spec.RunDirectory,
            Environment = spec.Environment,
            Timeout = spec.Timeout,
        };

        var result = await _processHost.RunAsync(processSpec, output, ct).ConfigureAwait(false);
        return Judge(spec, arguments, result);
    }

    private CalibreConversionOutcome Judge(
        CalibreConversionSpec spec, IReadOnlyList<string> arguments, ProcessResult result)
    {
        var combined = result.StdOut + "\n" + result.StdErr;
        var file = new FileInfo(spec.TempOutputPath);

        if (result.ExitCode == 0)
        {
            if (!file.Exists || file.Length == 0)
            {
                throw new AppException(
                    ErrorCode.ConversionNoOutput,
                    "转换进程报告成功，但没有产出文件或产物是空的。请在活动中心查看日志。",
                    technical: $"exit=0 but output missing/empty: {spec.TempOutputPath}",
                    context: new Dictionary<string, object?>
                    {
                        ["input"] = spec.InputPath,
                        ["output"] = spec.TempOutputPath,
                        ["stdoutTail"] = Tail(result.StdOut),
                        ["stderrTail"] = Tail(result.StdErr),
                    });
            }

            _log?.LogInformation(
                "转换成功 {Input} -> {Output}（{Size} 字节，{Seconds:F1} 秒）",
                Path.GetFileName(spec.InputPath),
                Path.GetFileName(spec.TempOutputPath),
                file.Length,
                result.Duration.TotalSeconds);

            return new CalibreConversionOutcome(
                spec.TempOutputPath, file.Length, result.Duration, arguments);
        }

        throw BuildFailure(spec, result, combined);
    }

    /// <summary>把退出码与输出内容翻译成带动作的错误码。</summary>
    private AppException BuildFailure(
        CalibreConversionSpec spec, ProcessResult result, string combined)
    {
        var context = new Dictionary<string, object?>
        {
            ["exitCode"] = result.ExitCode,
            ["input"] = spec.InputPath,
            ["output"] = spec.TempOutputPath,
            ["stdoutTail"] = Tail(result.StdOut),
            ["stderrTail"] = Tail(result.StdErr),
        };

        _log?.LogError(
            "转换失败 exit={ExitCode} input={Input}\n  stdout={StdOut}\n  stderr={StdErr}",
            result.ExitCode,
            spec.InputPath,
            Tail(result.StdOut),
            Tail(result.StdErr));

        // 输出格式没有对应插件（例如有人想转 CBZ）。
        if (combined.Contains("No plugin to handle output format", StringComparison.OrdinalIgnoreCase))
        {
            return new AppException(
                ErrorCode.ConversionUnsupported,
                "Calibre 没有能写这个目标格式的插件。请换一个输出格式。",
                technical: "No plugin to handle output format",
                context: context);
        }

        // 退出码 2 = 参数错误（实测约定）。
        if (result.ExitCode == 2)
        {
            return new AppException(
                ErrorCode.ConversionArgsRejected,
                "Calibre 拒绝了这次的转换参数。请检查高级参数，或恢复默认模板。",
                technical: "exit=2 (argument error)",
                context: context);
        }

        return new AppException(
            ErrorCode.ConversionFailed,
            $"转换失败（退出码 {result.ExitCode}）。可在活动中心查看日志尾部。",
            technical: $"exit={result.ExitCode}; stderr={Tail(result.StdErr)}",
            context: context);
    }

    /// <summary>只保留末尾若干行，避免把整份输出塞进异常与日志。</summary>
    private static string Tail(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length <= ErrorTailLines
            ? text.Trim()
            : string.Join('\n', lines.Skip(lines.Length - ErrorTailLines)).Trim();
    }
}
