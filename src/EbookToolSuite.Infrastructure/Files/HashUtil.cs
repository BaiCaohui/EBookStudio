using System.Security.Cryptography;
using EbookToolSuite.Core.Interfaces;

namespace EbookToolSuite.Infrastructure.Files;

/// <summary>
/// 快速指纹（设计文档 7.1.2）：SHA256(头 64KB + 长度 + 尾 64KB)，大写十六进制。
/// 一本 200MB 的书耗时约等于读 128KB，是毫秒级。
/// </summary>
public sealed class HashUtil : IHashUtil
{
    /// <summary>头尾各取多少字节。两处必须一致，否则历史指纹会失配。</summary>
    private const int WindowBytes = 64 * 1024;

    /// <summary>文件短于这个长度时只有头没有尾，避免头尾重叠。</summary>
    private const long TwoWindowThreshold = 2L * WindowBytes;

    public async Task<string> QuickFingerprintAsync(string path, CancellationToken ct = default)
    {
        await using var fs = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, useAsync: true);

        using var sha = SHA256.Create();
        var length = fs.Length;

        // 头（空文件时跳过；TransformBlock 不接受 count=0 加 null 输出缓冲的组合）
        var headLength = (int)Math.Min(WindowBytes, length);
        if (headLength > 0)
        {
            var head = new byte[headLength];
            await fs.ReadExactlyAsync(head, ct).ConfigureAwait(false);
            sha.TransformBlock(head, 0, head.Length, null, 0);
        }

        // 长度入哈希：没有它，"只有中间被改过的大文件"会碰撞
        var lengthBytes = BitConverter.GetBytes(length);
        sha.TransformBlock(lengthBytes, 0, lengthBytes.Length, null, 0);

        // 尾
        if (length > TwoWindowThreshold)
        {
            fs.Seek(-WindowBytes, SeekOrigin.End);
            var tail = new byte[WindowBytes];
            await fs.ReadExactlyAsync(tail, ct).ConfigureAwait(false);
            sha.TransformBlock(tail, 0, tail.Length, null, 0);
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }

    /// <summary>
    /// 全文件 SHA256（设计文档 5.2 的 full_hash），大写十六进制。
    /// </summary>
    /// <remarks>
    /// 成本与文件大小成正比：只给精确判重使用，不要在导入路径上无条件调用。
    /// </remarks>
    public async Task<string> ComputeContentHashAsync(string path, CancellationToken ct = default)
    {
        await using var fs = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 128 * 1024, useAsync: true);

        using var sha = SHA256.Create();
        var buffer = new byte[128 * 1024];

        int read;
        while ((read = await fs.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            sha.TransformBlock(buffer, 0, read, null, 0);
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }
}
