using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Engines.Calibre;

/// <summary>一次 Calibre 转换的全部输入。不含"最终输出路径"：落在哪里由上层决定。</summary>
public sealed record CalibreConversionSpec
{
    public required string ExePath { get; init; }

    public required string InputPath { get; init; }

    /// <summary>
    /// 临时输出路径。**必须与最终文件同扩展名**：
    /// Calibre 依据输出文件的扩展名推断目标格式，叫 out.mobi.part 会直接失败（本机实测）。
    /// </summary>
    public required string TempOutputPath { get; init; }

    /// <summary>独立临时工作目录，保证不与输入同目录（Calibre 可能原地改文件）。</summary>
    public required string RunDirectory { get; init; }

    public required TimeSpan Timeout { get; init; }

    /// <summary>已经过白名单校验的选项（不含位置参数 input/output）。</summary>
    public IReadOnlyList<ConversionArgument> Options { get; init; } = Array.Empty<ConversionArgument>();

    public IReadOnlyDictionary<string, string?> Environment { get; init; }
        = new Dictionary<string, string?>();
}

/// <summary>转换产物（还在临时目录里，由上层决定改名到哪）。</summary>
public sealed record CalibreConversionOutcome(
    string TempOutputPath,
    long SizeBytes,
    TimeSpan Duration,
    IReadOnlyList<string> Arguments);
