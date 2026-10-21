using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Core.Interfaces;

/// <summary>
/// EPUB 只读探测器。实现放 Engines/Epub/EpubProbe.cs。
/// 只读：不复制文件、不写任何东西、不修改源文件。
/// </summary>
public interface IEpubProbe
{
    /// <summary>
    /// 探测一本书。loadCover=false 时跳过封面读取（批量导入快很多）。
    /// </summary>
    /// <exception cref="Core.Exceptions.AppException">
    /// 所有失败都必须转成 ErrorCode.EpubInvalid（损坏/非 EPUB）
    /// 或 ErrorCode.DrmDetected（有 META-INF/encryption.xml），
    /// 绝不把底层异常漏给调用方：一个坏文件不能让导入队列整个崩掉。
    /// </exception>
    Task<EpubProbeResult> ProbeAsync(string epubPath, bool loadCover, CancellationToken ct = default);
}