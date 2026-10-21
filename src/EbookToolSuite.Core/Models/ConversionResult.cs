using EbookToolSuite.Core.Enums;

namespace EbookToolSuite.Core.Models;

/// <summary>转换成功后的产物描述。</summary>
public sealed record ConversionResult(
    string OutputPath,
    long SizeBytes,
    TimeSpan Duration,
    BookFormatKind Format,
    string? TemplateId,
    string CommandLine)
{
    public double SizeMb => SizeBytes / 1024d / 1024d;
}
