using EbookToolSuite.Core.Enums;

namespace EbookToolSuite.Core.Models;

/// <summary>一次转换请求（T1-4 是最小实现：单本、同步等待、无队列）。</summary>
public sealed record ConversionRequest
{
    public required Guid BookId { get; init; }

    public required BookFormatKind TargetFormat { get; init; }

    /// <summary>null = 用该输出格式的默认模板（Config.conversion.defaultTemplates）。</summary>
    public string? TemplateId { get; init; }

    /// <summary>源文件格式。null = 自动挑该书库里第一个能转的格式。</summary>
    public BookFormatKind? SourceFormat { get; init; }

    /// <summary>输出目录。null = 该书的 Library 目录（转换产物与原件同目录）。</summary>
    public string? OutputDirectory { get; init; }

    /// <summary>界面「高级参数」框解析出来的临时覆盖，优先级高于模板。</summary>
    public IReadOnlyList<ConversionArgument> Overrides { get; init; } = Array.Empty<ConversionArgument>();

    /// <summary>null = 用 Config.conversion.timeoutMinutes。</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>
    /// 产物是否登记进 book_formats（默认 true，与既有行为一致）。
    /// T6-1 的 MOBI/AZW3 -> EPUB「惰性缓存」必须设成 false：那是阅读缓存的中间产物，
    /// 不是这本书新增的一个格式；登记进去会让书库凭空多出一个并不存在的 EPUB。
    /// </summary>
    public bool RegisterFormat { get; init; } = true;

    /// <summary>
    /// 队列优先级，数字越大越先跑（默认 0 = 批量转换）。T6-1 判据 5：
    /// 用户点开一本书触发的转换必须插到队列前面，不能排在 20 条批量转换后面。
    /// </summary>
    public int Priority { get; init; }
}
