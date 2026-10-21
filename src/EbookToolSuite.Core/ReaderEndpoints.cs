using System.Text;

namespace EbookToolSuite.Core;

/// <summary>
/// 阅读器前端的地址约定。宿主（App/Reader 程序集）与内容准备（Services）
/// 都要用，所以放在 Core 做唯一来源，禁止各处再写字面量。
/// </summary>
/// <remarks>
/// 设计文档 7.4 那张表里写的是 ets.reader，与 T0-6 实测通过的 ebook.local 冲突。
/// 因为 ebook.local 已经在 ReaderWebHost 里实现并有冒烟覆盖，保持它不变。
/// </remarks>
public static class ReaderEndpoints
{
    /// <summary>WebView2 虚拟主机名。</summary>
    public const string VirtualHostName = "ebook.local";

    /// <summary>虚拟主机根地址，映射到sample的缓存目录。</summary>
    public const string VirtualHostRootUrl = "http://" + VirtualHostName + "/";

    /// <summary>
    /// PDF 原始文件的虚拟主机名（T6-2）。映射到书库文件根（Data/Library），
    /// 因为 PDF 是固定版式、按字节流给渲染器读的，不做解包拷贝。
    /// </summary>
    /// <remarks>
    /// 另一个主机名就多一条解析超时：它**必须**同时出现在 WebEnvironmentProvider
    /// 的 --host-resolver-rules MAP 列表里，漏了每次导航白付约 2 秒（T1-5 实测）。
    /// </remarks>
    public const string FileHostName = "ebook-file.local";

    /// <summary>PDF 原始文件虚拟主机根地址。</summary>
    public const string FileHostRootUrl = "http://" + FileHostName + "/";

    /// <summary>
    /// 页位图的虚拟主机名（T6-2B）。它**不做目录映射**：每一个请求都由阅读器窗口的
    /// <c>WebResourceRequested</c> 用内存里的 JPEG 字节应答（自绘引擎的每一页都是我
    /// 们自己渲染的，磁盘上没有对应的文件可映射）。
    /// </summary>
    /// <remarks>
    /// 同样必须出现在 WebEnvironmentProvider 的 --host-resolver-rules MAP 列表里：
    /// 万一响应器没挂上，请求会退化成一次真实的域名解析并卡在超时上（见那里的注释）。
    /// </remarks>
    public const string PdfPageHostName = "ebook-pdf.local";

    /// <summary>页位图地址前缀，后面直接拼 token（如 http://ebook-pdf.local/p/&lt;token&gt;）。</summary>
    public const string PdfPageHostUrl = "http://" + PdfPageHostName + "/p/";

    /// <summary>给一张已经渲染好的页位图生成地址。token 由调用方保证只含 ASCII 字母数字与连字符。</summary>
    public static string PdfPageUrl(string token) => PdfPageHostUrl + token;

    /// <summary>
    /// 从请求地址里取回页位图的 token；不是页位图地址（或格式不对）返回 null，
    /// 让调用方把请求交回给 WebView2 的默认处理。查询串与锚点会被丢掉。
    /// </summary>
    public static string? PdfPageTokenOf(string? uri)
    {
        if (string.IsNullOrEmpty(uri) ||
            !uri.StartsWith(PdfPageHostUrl, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = uri[PdfPageHostUrl.Length..];
        var cut = token.IndexOfAny(new[] { '?', '#', '/' });

        if (cut >= 0)
        {
            token = token[..cut];
        }

        return token.Length == 0 ? null : token;
    }

    /// <summary>解压出来的 EPUB 内容所在目录（缓存根之下）。</summary>
    public const string ContentDirectoryName = "content";

    /// <summary>规范化后的章节所在目录（缓存根之下）。</summary>
    public const string ChaptersDirectoryName = "chapters";

    /// <summary>清单文件名。</summary>
    public const string ManifestFileName = "reader.json";

    /// <summary>包内资源对外暴露的 URL 前缀，末尾带斜杠。</summary>
    public const string ContentUrlPrefix = VirtualHostRootUrl + ContentDirectoryName + "/";

    /// <summary>
    /// MOBI / AZW3 的转换缓存键：快速指纹 + 源文件 mtime 的 ticks + 大小（T6-1 判据 3）。
    /// 源文件被替换后 mtime/size 变了，键就变了，旧产物自然作废。
    /// </summary>
    /// <remarks>
    /// 阅读服务（算缓存目录）与转换缓存服务（判断产物在不在）必须用同一套算法，
    /// 所以规则只放在这里一份，两边都调它。
    /// </remarks>
    public static string ConvertedCacheKey(string quickFingerprint, DateTime lastWriteTimeUtc, long length)
        => quickFingerprint + "-" + lastWriteTimeUtc.Ticks + "-" + length;

    /// <summary>
    /// 一本书在 Data/Cache/Reader 下的目录名。缓存键要能直接当目录名用，
    /// 所以只留十六进制与连字符（小写），净化后为空则退化成 unknown。
    /// </summary>
    /// <remarks>
    /// ReaderService 建缓存、BookDeletionService 删缓存都必须走这里。
    /// 两处各写一份规则迟早会漂移，然后就会留下永远删不掉的缓存目录。
    /// </remarks>
    public static string CacheFolderName(string? quickFingerprint)
    {
        if (string.IsNullOrWhiteSpace(quickFingerprint))
        {
            return "unknown";
        }

        var builder = new StringBuilder(quickFingerprint.Length);

        foreach (var ch in quickFingerprint)
        {
            if (char.IsAsciiHexDigit(ch) || ch == '-')
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.Length == 0 ? "unknown" : builder.ToString();
    }
}