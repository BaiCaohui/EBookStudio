namespace EbookToolSuite.Core.Interfaces;

/// <summary>哈希工具。实现放 Infrastructure/Files/HashUtil.cs。</summary>
public interface IHashUtil
{
    /// <summary>
    /// 快速指纹 = SHA256(文件头 64KB + 文件长度字节 + 文件尾 64KB)，
    /// 返回大写十六进制字符串。
    /// </summary>
    /// <remarks>
    /// 为什么不用全文件 SHA256：一本 200MB 的 PDF/CBZ 全量哈希要十几秒，
    /// 批量导入时不可接受（设计文档 7.1.2）。
    /// </remarks>
    Task<string> QuickFingerprintAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// 全文件 SHA256（设计文档 5.2 的 full_hash），返回大写十六进制字符串。
    /// </summary>
    /// <remarks>
    /// 只在需要精确判重时调用，调用方负责把结果写回 book_formats.full_hash 避免重算。
    /// 成本与文件大小成正比（200MB 约十几秒），绝不能在导入路径上无条件调用。
    /// </remarks>
    Task<string> ComputeContentHashAsync(string path, CancellationToken ct = default);
}
