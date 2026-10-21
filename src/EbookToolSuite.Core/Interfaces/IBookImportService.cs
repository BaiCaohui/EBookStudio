using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Core.Interfaces;

/// <summary>
/// 导入入口。实现放 Services/Library/ImportPipeline.cs。
/// </summary>
/// <remarks>
/// 【为什么是窄接口】设计文档 3.3 的完整 ILibraryService（含 QueryAsync /
/// UpdateMetadataAsync 等）要等 T1-3 做网格时才有意义，这里先只暴露导入，
/// 避免一堆 NotImplemented 空壳。T1-3 会把本接口并入 ILibraryService。
/// </remarks>
public interface IBookImportService
{
    /// <summary>
    /// 导入一个文件。**不抛异常表达业务失败**：所有结果都在返回值里
    /// （InvalidEpub / Duplicate / Failed 等），只有取消会抛
    /// OperationCanceledException 之外的意外才可能外泄。
    /// </summary>
    Task<ImportResult> ImportAsync(
        string path,
        ImportOptions options,
        IProgress<ImportProgress>? progress = null,
        CancellationToken ct = default);
}