namespace EbookToolSuite.Core.Abstractions;

/// <summary>
/// 一次外部进程调用的完整描述。
/// 参数一律走 <see cref="Arguments"/>（对应 ProcessStartInfo.ArgumentList），
/// 不要自己拼命令行字符串：中文路径、空格、引号都会在拼接时被破坏。
/// </summary>
public sealed record ProcessSpec
{
    public required string FileName { get; init; }

    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();

    public string? WorkingDirectory { get; init; }

    /// <summary>要注入的环境变量。值为 null 表示删除该变量。</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; init; }
        = new Dictionary<string, string?>();

    /// <summary>超时后终止整棵进程树。null = 不设超时（调用方自己控制 CancellationToken）。</summary>
    public TimeSpan? Timeout { get; init; }

    public bool CaptureStdout { get; init; } = true;

    public bool CaptureStderr { get; init; } = true;
}

/// <summary>进程结束后的汇总。</summary>
public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, TimeSpan Duration)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>实时输出的一行，供进度解析与日志用。</summary>
public sealed record ProcessOutputLine(DateTimeOffset At, bool IsError, string Text);