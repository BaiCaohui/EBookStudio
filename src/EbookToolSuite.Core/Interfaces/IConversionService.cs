using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Core.Interfaces;

/// <summary>
/// 格式转换。T1-4 只做最小实现：单本、同步等待、失败即抛。
/// 队列、并发上限、重试、崩溃恢复属于 T2-4，不在这里。
/// </summary>
public interface IConversionService
{
    /// <summary>
    /// 转换一本已有的书，成功后把产物登记进 book_formats。
    /// 取消抛 OperationCanceledException；失败抛 AppException（带错误码与动作）。
    /// </summary>
    Task<ConversionResult> ConvertAsync(
        ConversionRequest request,
        IProgress<ConversionProgress>? progress = null,
        CancellationToken ct = default,
        IProgress<ProcessOutputLine>? rawOutput = null);

    /// <summary>该输出格式可用的模板（含「不套模板」这一项由 UI 自行加）。</summary>
    IReadOnlyList<ConversionTemplate> GetTemplates(BookFormatKind targetFormat);

    /// <summary>
    /// 「不换格式，只优化」的模板（目前只有 polish-only）。
    /// 与目标格式无关，只取决于源格式：只有 EPUB / AZW3 的书才有（polish 的实测限制）。
    /// </summary>
    IReadOnlyList<ConversionTemplate> GetFormatKeepingTemplates(BookFormatKind sourceFormat);

    /// <summary>这个转换链路 Calibre 支持吗（只判断静态支持矩阵，不探测工具）。</summary>
    bool IsSupported(BookFormatKind source, BookFormatKind target);

    /// <summary>给定源格式，能转出哪些目标格式。</summary>
    IReadOnlyList<BookFormatKind> GetSupportedTargets(BookFormatKind source);
}
