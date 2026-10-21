namespace EbookToolSuite.Core.Models;

/// <summary>
/// 一本书的阅读缓存清单（Cache/Reader/{hash}/reader.json）。
/// 前端 reader.js 靠它拿到章节列表；宿主靠它做跳转与校验。
/// </summary>
public sealed class ReaderManifest
{
    /// <summary>清单结构版本。结构变了就升版，旧缓存视为无效重建。</summary>
    public int SchemaVersion { get; set; } = 1;

    public string BookId { get; set; } = "";

    /// <summary>来源格式：EPUB / TXT。</summary>
    public string Format { get; set; } = "EPUB";

    public string Title { get; set; } = "";
    public string? Author { get; set; }
    public string? Language { get; set; }

    /// <summary>包内基准目录（相对缓存根），如 "content/OEBPS/"。</summary>
    public string BasePath { get; set; } = "";

    /// <summary>源文件绝对路径与长度，仅用于诊断与"源变了要重建"判断。</summary>
    public string SourcePath { get; set; } = "";
    public long SourceSize { get; set; }

    /// <summary>书的全局样式表（已解压后的包内路径）。章节自己也会带 link，这里只是汇总。</summary>
    public List<string> Styles { get; set; } = new();

    public List<ReaderChapter> Spine { get; set; } = new();

    /// <summary>
    /// 目录（T4-4）：EPUB2 取自 NCX、EPUB3 取自 nav，由引擎解析好。
    /// 原书没有目录或目录坏了时，这里是"按 spine 平铺"的降级目录（见 <see cref="TocFallback"/>）。
    /// </summary>
    public List<TocEntry> Toc { get; set; } = new();

    /// <summary>目录是不是降级出来的（原书没有目录 / 目录解析失败）。界面据此给用户一句提示。</summary>
    public bool TocFallback { get; set; }

    /// <summary>
    /// TXT 专用（T4-5）：这本书用的文本编码名（UTF-8 / UTF-16LE / UTF-16BE / GBK / GB18030）。
    /// 非空就表示这是 TXT 书，章节内容要按 <see cref="ReaderChapter.TextStart"/> 从源文件里按需取。
    /// 用户手动指定过编码时，这里记的就是用户选的那个。
    /// </summary>
    public string? TextEncoding { get; set; }

    /// <summary>编码是自动识别出来的且没把握（判据 2：界面要提示"编码可能不准，请选择"）。</summary>
    public bool TextEncodingConfident { get; set; } = true;

    /// <summary>文件开头的 BOM 字节数，取正文时要跳过。</summary>
    public int TextBomLength { get; set; }

    /// <summary>没找到章节标记、按固定字数硬切的（判据 5）。</summary>
    public bool TextFallbackSplit { get; set; }
}

/// <summary>清单里的一章。</summary>
public sealed class ReaderChapter
{
    /// <summary>spine 序号，从 0 开始。</summary>
    public int Index { get; set; }

    /// <summary>相对缓存根的路径，如 "chapters/0001.html"。物化后才存在。</summary>
    public string Href { get; set; } = "";

    /// <summary>包内原始路径，如 "OEBPS/Text/chap01.xhtml"。链接跳转靠它对上号。</summary>
    public string SourceHref { get; set; } = "";

    /// <summary>目录里的标题，取不到时为 null（UI 回落成"第 N 章"）。</summary>
    public string? Title { get; set; }

    /// <summary>TXT 专用：这一章在源文件里的起始字符下标（含）。</summary>
    public long TextStart { get; set; }

    /// <summary>TXT 专用：这一章在源文件里的结束字符下标（不含）。</summary>
    public long TextEnd { get; set; }
}

/// <summary>一次阅读会话。缓存准备好的产物。</summary>
public sealed class ReaderSession
{
    public required Guid BookId { get; init; }

    /// <summary>要映射给 WebView2 虚拟主机的目录：http://ebook.local/ 指向这里。</summary>
    public required string CacheRoot { get; init; }

    public required ReaderManifest Manifest { get; init; }

    public required Enums.BookFormatKind SourceFormat { get; init; }

    public required string SourcePath { get; init; }

    /// <summary>true 表示这次没做准备工作，直接命中了已有缓存。</summary>
    public bool FromCache { get; init; }

    /// <summary>
    /// PDF 专用（T6-2）：第二个虚拟主机名（ebook-file.local）要映射的目录，即书库文件根。
    /// 非 PDF 书为 null。见设计文档 §7.4。
    /// </summary>
    public string? FileRoot { get; init; }

    /// <summary>
    /// PDF 专用（T6-2）：外壳把正文区 iframe 指到这个地址（第二个虚拟主机上）。
    /// 非 PDF 书为 null。
    /// </summary>
    public string? PdfUrl { get; init; }
}