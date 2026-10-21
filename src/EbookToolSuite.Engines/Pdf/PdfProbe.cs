using System.Text;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;

namespace EbookToolSuite.Engines.Pdf;

/// <summary>
/// PDF 导入时的只读体检（T6-2 的前置：PDF 得先能进书库）。
///
/// 只做三件事，**不解析页数、不渲染**——页数与首页封面交给 pdfium（设计文档 §6.5.4）：
///   1. 头 1 KB 里有没有 <c>%PDF-</c>：只认这一个签名，别的当"无法识别的电子书格式"；
///   2. 有没有 <c>/Encrypt</c>：加密**不拒收**，阶段一交给 Chromium 自己弹密码框；
///   3. 尾部有没有 <c>%%EOF</c>：明显截断**也不拒收**，打开失败走失败面板 + 外部打开兜底。
///
/// 后两条是"允许导入，但如实记账"的口径（设计文档 §6.5.4、决策日志 2026-10-13）：
/// 不因为"可能损坏"就拒收，否则用户手上唯一那份文件连进都进不来。
/// </summary>
/// <remarks>
/// 为什么加密只扫首尾两段：<c>/Encrypt</c> 只会出现在 trailer 字典（文件尾）里，
/// 或者线性化 PDF 放在文件头部的第一段 xref 里。中间那几十上百 MB 是页内容流，
/// 全扫一遍既慢（100 MB 起步）又容易被压缩流里的偶然字节串骗到。
/// </remarks>
public static class PdfProbe
{
    /// <summary>头部采样长度：Adobe 规范要求 <c>%PDF-</c> 出现在前 1024 字节内。</summary>
    private const int HeaderWindow = 1024;

    /// <summary>尾部采样长度：<c>%%EOF</c> 与 trailer 里的 <c>/Encrypt</c> 都在这里。</summary>
    private const int TailWindow = 64 * 1024;

    /// <summary>头部加密采样长度：线性化 PDF 把第一段 xref 放在文件最前面。</summary>
    private const int HeadEncryptWindow = 1024 * 1024;

    private static readonly byte[] PdfSignature = Encoding.ASCII.GetBytes("%PDF-");
    private static readonly byte[] EofSignature = Encoding.ASCII.GetBytes("%%EOF");
    private static readonly byte[] EncryptKey = Encoding.ASCII.GetBytes("/Encrypt");

    /// <summary>
    /// 体检一个 PDF。文件读不到抛 <see cref="ErrorCode.FileNotFound"/> /
    /// <see cref="ErrorCode.CorruptFile"/>；**"不是 PDF"不抛异常**，
    /// 而是回 <see cref="PdfProbeResult.LooksLikePdf"/> = false，由调用方归到"不支持的格式"。
    /// </summary>
    public static PdfProbeResult Inspect(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new AppException(ErrorCode.FileNotFound, "文件不存在或已被移动。", technical: path);
        }

        long length;

        try
        {
            length = new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AppException(ErrorCode.CorruptFile, "读不到这个文件。", path, ex);
        }

        if (length == 0)
        {
            // 空文件连签名都不可能存在，直接归到"不是 PDF"
            return new PdfProbeResult(false, false, true, 0);
        }

        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 64 * 1024);

            if (IndexOf(ReadWindow(stream, 0, HeaderWindow), PdfSignature) < 0)
            {
                return new PdfProbeResult(false, false, false, length);
            }

            var tail = ReadWindow(stream, Math.Max(0, length - TailWindow), TailWindow);
            var hasEof = IndexOf(tail, EofSignature) >= 0;

            var encrypted = ContainsEncryptKey(tail)
                || ContainsEncryptKey(ReadWindow(stream, 0, HeadEncryptWindow));

            return new PdfProbeResult(true, encrypted, !hasEof, length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AppException(ErrorCode.CorruptFile, "读这个文件时出错。", path, ex);
        }
    }

    /// <summary>从 <paramref name="offset"/> 起最多读 <paramref name="maxBytes"/> 个字节；短读按实际长度返回。</summary>
    private static byte[] ReadWindow(FileStream stream, long offset, int maxBytes)
    {
        var available = stream.Length - offset;
        var count = (int)Math.Min(maxBytes, Math.Max(0, available));
        var buffer = new byte[count];

        stream.Seek(offset, SeekOrigin.Begin);

        var read = 0;

        while (read < count)
        {
            var got = stream.Read(buffer, read, count - read);

            if (got <= 0)
            {
                break;
            }

            read += got;
        }

        return read == count ? buffer : buffer[..read];
    }

    /// <summary>
    /// 在这段窗口里找 <c>/Encrypt</c>，并且要求它后面跟的是 <c>&lt;&lt;</c>（直接字典）
    /// 或数字（间接引用）。加这道判断是为了挡掉压缩内容流里偶然出现的同名字节串。
    /// </summary>
    private static bool ContainsEncryptKey(ReadOnlySpan<byte> window)
    {
        var from = 0;

        while (from < window.Length)
        {
            var at = IndexOf(window[from..], EncryptKey);

            if (at < 0)
            {
                return false;
            }

            var cursor = from + at + EncryptKey.Length;

            while (cursor < window.Length && IsPdfWhitespace(window[cursor]))
            {
                cursor++;
            }

            if (cursor < window.Length
                && (window[cursor] == (byte)'<'
                    || (window[cursor] >= (byte)'0' && window[cursor] <= (byte)'9')))
            {
                return true;
            }

            from += at + 1;
        }

        return false;
    }

    /// <summary>PDF 规范里的空白字符：NUL、HT、LF、FF、CR、SP。</summary>
    private static bool IsPdfWhitespace(byte value)
        => value is 0x00 or 0x09 or 0x0A or 0x0C or 0x0D or 0x20;

    private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
        => haystack.Length < needle.Length ? -1 : haystack.IndexOf(needle);
}

/// <summary>PDF 体检结果。</summary>
/// <param name="LooksLikePdf">头 1 KB 里有 <c>%PDF-</c>。false = 当"无法识别的电子书格式"处理。</param>
/// <param name="IsEncrypted">发现 <c>/Encrypt</c>（加密，或者只是权限受限）。仍然允许导入。</param>
/// <param name="IsTruncated">尾部没有 <c>%%EOF</c>，疑似被截断。仍然允许导入。</param>
/// <param name="FileSize">文件字节数。</param>
public sealed record PdfProbeResult(
    bool LooksLikePdf,
    bool IsEncrypted,
    bool IsTruncated,
    long FileSize);
