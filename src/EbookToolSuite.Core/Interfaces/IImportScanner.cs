using EbookToolSuite.Core.Abstractions;

namespace EbookToolSuite.Core.Interfaces;

/// <summary>
/// 把用户选的文件与文件夹展开成一串待导入的源文件路径（执行计划 T2-1）。
/// 纯路径操作：不读内容、不查库、不产生任何副作用。
/// </summary>
public interface IImportScanner
{
    /// <summary>
    /// 展开。支持混合选择（既有文件又有文件夹）。同名文件只收一次。
    /// 不抛异常表达业务问题：读不到的路径记进 SkippedUnreadable / MissingPaths。
    /// </summary>
    ImportScanResult Scan(IReadOnlyList<string> paths, bool recursive, CancellationToken ct = default);
}