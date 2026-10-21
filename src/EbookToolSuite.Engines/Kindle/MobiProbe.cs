using System.Buffers.Binary;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;

namespace EbookToolSuite.Engines.Kindle;

/// <summary>
/// MOBI / AZW / AZW3 导入时的只读体检（T6-1 的前置：这些格式得先进得来书库）。
///
/// 只做两件事，别的都交给 Calibre：
/// 1. 确认它真的是 MOBI（PalmDB 头偏移 60 处是 "BOOKMOBI"），不是改了扩展名的别的东西；
/// 2. 判定有没有 DRM（设计文档 6.1.9：MOBI 头的 drm_offset/drm_count，或 EXTH 记录 201/202）。
///
/// 不做元数据与封面：那要跑外部工具，导入阶段保持"只读、快、离线可用"。
/// </summary>
public static class MobiProbe
{
    private const int PdbHeaderLength = 78;
    private const int MobiHeaderFlagOffset = 116;
    private const int MobiHeaderDrmOffset = 128;
    private const uint NoDrmOffset = 0xFFFFFFFFu;

    /// <summary>探测上限：MOBI 头与 EXTH 都在文件最前面，1 MB 足够。</summary>
    private const int MaxProbeBytes = 1024 * 1024;

    /// <summary>
    /// 结构不合法抛 <see cref="ErrorCode.EpubInvalid"/>；有 DRM 抛 <see cref="ErrorCode.DrmDetected"/>；
    /// 文件读不到抛 <see cref="ErrorCode.CorruptFile"/>。
    /// </summary>
    public static void Validate(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new AppException(ErrorCode.FileNotFound, "文件不存在或已被移动。", technical: path);
        }

        byte[] head;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var take = (int)Math.Min(stream.Length, MaxProbeBytes);
            head = new byte[take];
            stream.ReadExactly(head, 0, take);
        }
        catch (Exception ex)
        {
            throw new AppException(ErrorCode.CorruptFile, "无法读取这个文件。", technical: path, inner: ex);
        }

        // PalmDB 头 78 字节 + 至少一条记录信息 8 字节 + 2 字节结尾
        if (head.Length < PdbHeaderLength + 8 + 2 || !Matches(head, 60, "BOOKMOBI"))
        {
            throw new AppException(
                ErrorCode.EpubInvalid,
                "这个文件不是有效的 MOBI / AZW3。",
                technical: "pdb header mismatch: " + path);
        }

        var recordCount = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(76));
        if (recordCount == 0)
        {
            throw new AppException(ErrorCode.EpubInvalid, "这个 MOBI / AZW3 没有任何内容记录。", technical: path);
        }

        // 记录 0 = PalmDOC 头(16 字节) + MOBI 头
        var record0 = (long)BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(78));
        var mobiStart = record0 + 16;
        if (mobiStart <= 0 || mobiStart + 132 > head.Length || !Matches(head, (int)mobiStart, "MOBI"))
        {
            /* 老式 PalmDOC（type=TEXtREAd）没有 MOBI 头；它仍是合法的 MOBI 输入，
               Calibre 认它。这种情况没有 DRM 字段可查，放行。 */
            return;
        }

        var span = head.AsSpan((int)mobiStart);

        var drmOffset = BinaryPrimitives.ReadUInt32BigEndian(span[MobiHeaderDrmOffset..]);
        var drmCount = BinaryPrimitives.ReadUInt32BigEndian(span[(MobiHeaderDrmOffset + 4)..]);
        if (drmOffset != NoDrmOffset && drmCount > 0)
        {
            throw Drm(path, "drm_offset/drm_count");
        }

        var exthFlags = BinaryPrimitives.ReadUInt32BigEndian(span[MobiHeaderFlagOffset..]);
        if ((exthFlags & 0x40) == 0)
        {
            return;
        }

        var headerLength = BinaryPrimitives.ReadUInt32BigEndian(span[4..]);
        if (headerLength < 116 || headerLength > 4096)
        {
            headerLength = 232;
        }

        var exthStart = (int)(mobiStart + headerLength);
        if (exthStart + 12 > head.Length || !Matches(head, exthStart, "EXTH"))
        {
            return;
        }

        var exthLength = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(exthStart + 4));
        var exthCount = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(exthStart + 8));
        var cursor = exthStart + 12;
        var limit = (int)Math.Min(head.Length, exthStart + Math.Max(exthLength, 12u));

        for (var i = 0u; i < exthCount && cursor + 8 <= limit; i++)
        {
            var type = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(cursor));
            var size = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(cursor + 4));
            if (size < 8 || cursor + (long)size > limit)
            {
                break;
            }

            // 201 = DRM offset、202 = DRM count（设计文档 6.1.9）
            if (type is 201 or 202)
            {
                throw Drm(path, "exth " + type);
            }

            cursor += (int)size;
        }
    }

    private static AppException Drm(string path, string detail) => new(
        ErrorCode.DrmDetected,
        "这本书有 DRM 保护，无法导入或转换。",
        technical: "mobi drm (" + detail + "): " + path);

    private static bool Matches(byte[] data, int offset, string ascii)
    {
        if (offset < 0 || offset + ascii.Length > data.Length)
        {
            return false;
        }

        for (var i = 0; i < ascii.Length; i++)
        {
            if (data[offset + i] != (byte)ascii[i])
            {
                return false;
            }
        }

        return true;
    }
}