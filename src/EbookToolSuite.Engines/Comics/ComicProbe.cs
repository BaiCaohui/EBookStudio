using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Engines.Comics;

/// <summary>
/// CBZ/CBR 的只读探测（T6-3）：只回答「是不是漫画包、多少页、加没加密」。
/// </summary>
/// <remarks>
/// 与 <c>PdfProbe</c> 同一约定：永不抛异常，所有失败都折进 <see cref="ComicProbeResult"/>。
/// 这样导入管线不必为「用户随便塞了个 .cbz」写 try/catch，也不会因一张坏图拖垮整批导入。
/// </remarks>
public static class ComicProbe
{
    public static ComicProbeResult Inspect(string path)
    {
        var size = TryGetFileSize(path);

        /* 先按文件头判「是不是压缩包」，再看 SharpCompress 能不能打开。
           顺序不能反：ArchiveFactory 对普通文本也会抛异常，「不是压缩包」和
           「是压缩包但坏了」对用户来说是两种完全不同的提示。 */
        if (!HasArchiveSignature(path))
        {
            return new ComicProbeResult { FileSize = size };
        }

        try
        {
            using var archive = ComicArchive.Open(path);

            /* 元数据是「锦上添花」：没有 ComicInfo.xml、文件损坏、解析失败都只是 null，
               不影响页数/加密等既有字段，也不改变「是不是漫画包」的结论。 */
            ComicMetadata? metadata = null;

            if (archive.TryReadTextEntry(ComicInfoXml.EntryName, out var comicInfo)
                && ComicInfoXml.TryParse(comicInfo, out var parsed))
            {
                metadata = parsed;
            }

            return new ComicProbeResult
            {
                LooksLikeComic = true,
                PageCount = archive.PageCount,
                IsRar = archive.IsRar,
                IsEncrypted = archive.IsEncrypted,
                FileSize = size,
                Metadata = metadata,
            };
        }
        catch (AppException ex) when (ex.Code == ErrorCode.UnsupportedFormat)
        {
            // 是压缩包，但里面没有图片：与「无法识别的电子书格式」同类
            return new ComicProbeResult { FileSize = size };
        }
        catch (AppException ex)
        {
            /* 能识别的压缩包但打不开（加密 / 固实 / 损坏）：
               仍报「像漫画包」，把原因带上，让导入给出明确提示而不是静默跳过。 */
            return new ComicProbeResult
            {
                LooksLikeComic = true,
                IsRar = HasRarSignature(path),
                FileSize = size,
                Error = ex.UserMessage,
            };
        }
        catch (Exception)
        {
            return new ComicProbeResult
            {
                LooksLikeComic = true,
                FileSize = size,
                Error = "读取这个漫画压缩包时发生意外错误，文件可能已损坏。",
            };
        }
    }

    /// <summary>ZIP 或 RAR 的文件头。只看前 8 字节，不读内容。</summary>
    public static bool HasArchiveSignature(string path)
    {
        var head = ReadHead(path, 8);

        if (head is null)
        {
            return false;
        }

        // ZIP：PK\x03\x04（正常）/ PK\x05\x06（空归档）/ PK\x07\x08（分卷）
        if (head.Length >= 4
            && head[0] == 0x50 && head[1] == 0x4B
            && head[2] is 0x03 or 0x05 or 0x07)
        {
            return true;
        }

        return HasRarSignature(head);
    }

    private static bool HasRarSignature(string path)
    {
        var head = ReadHead(path, 8);
        return head is not null && HasRarSignature(head);
    }

    /// <summary>RAR4 <c>Rar!\x1A\x07\x00</c> 与 RAR5 <c>Rar!\x1A\x07\x01\x00</c>。</summary>
    private static bool HasRarSignature(byte[] head)
        => head.Length >= 7
            && head[0] == 0x52 && head[1] == 0x61 && head[2] == 0x72 && head[3] == 0x21
            && head[4] == 0x1A && head[5] == 0x07
            && head[6] is 0x00 or 0x01;

    private static long TryGetFileSize(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static byte[]? ReadHead(string path, int count)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

            var length = (int)Math.Min(count, Math.Min(stream.Length, count));
            var buffer = new byte[Math.Max(1, length)];
            var read = stream.Read(buffer, 0, buffer.Length);

            return read == buffer.Length ? buffer : buffer[..read];
        }
        catch (Exception)
        {
            return null;
        }
    }
}
