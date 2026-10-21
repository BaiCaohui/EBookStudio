using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Core.Interfaces;

/// <summary>
/// MOBI / AZW3 的「惰性转 EPUB 缓存」（执行计划 T6-1）。
///
/// WebView2 读不了 MOBI/AZW3，而 Calibre 能把它们转成 EPUB，所以打开时按需转换、
/// 转出来的 EPUB 当缓存用，绝不登记成书库里的一个新格式。
/// 缓存键带源文件的指纹与 mtime/size，源文件被替换后自动失效（判据 3）。
/// </summary>
public interface IEpubCacheService
{
    /// <summary>
    /// 取sample源文件对应的缓存 EPUB 路径；不存在就排一次「高优先级、不入库」的
    /// MOBI/AZW3 -&gt; EPUB 转换并等它跑完（判据 1、5）。失败抛
    /// <see cref="EbookToolSuite.Core.Exceptions.AppException"/>（ErrorCode.ReaderOpenFailed）。
    /// </summary>
    /// <param name="cacheKey">由源文件指纹 + mtime + size 算出的缓存键（已 sanitize）。</param>
    Task<string> GetOrConvertAsync(
        Guid bookId,
        BookFormatKind sourceFormat,
        string sourcePath,
        string cacheKey,
        CancellationToken ct = default);

    /// <summary>缓存 EPUB 的落盘路径（不保证存在）。</summary>
    string GetArtifactPath(string cacheKey);

    /// <summary>
    /// 用户 2026-10-21：这本书这个 MOBI/AZW3 的转换产物现在在不在。
    /// 在就返回它的字节数，不在（或快速指纹还没算出来、文件已不在）返回 null。
    /// </summary>
    /// <remarks>
    /// 书库详情页用它在"还没点过阅读"时隐藏 EPUB 行、把底部小字换成
    /// "当前格式不支持阅读，点击阅读后自动转换为 EPUB 格式进行阅读"。
    /// 只做一次 FileInfo + File.Exists，不读文件内容，可以在界面路径上调。
    /// </remarks>
    long? GetConvertedArtifactSize(BookFormat format);

    /// <summary>标记sample的阅读缓存正在使用，缓存清理必须跳过它（判据 6）。</summary>
    void BeginRead(Guid bookId, string cacheRoot);

    /// <summary>阅读窗口关闭，释放这本书占用的缓存。</summary>
    void EndRead(Guid bookId);

    /// <summary>按 reader.cacheLimitMb 清理阅读缓存，返回释放的字节数；0/负数 = 不限制。</summary>
    Task<long> EnforceLimitAsync(CancellationToken ct = default);
}