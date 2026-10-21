using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Services.Conversion;

/// <summary>
/// 内置转换模板（设计文档 6.3.5，2026-10-04 T2-5 补齐为 6 个）。
///
/// 五条纪律：
///   1. 每个参数都要能在 docs/calibre-args-baseline.txt 里找到（否则运行时会被白名单拦下）；
///   2. 每个模板、每个参数都要有一行中文说明它做什么、什么时候该用；
///   3. 只放"确实需要"的参数，能不加就不加（少一个参数少一类意外）；
///   4. 内置模板以代码为唯一来源，【不落 conversion_templates 表】；
///      用户"另存为"的模板才落库（is_builtin=0）。这样内置参数随程序升级，不必回填迁移。
///   5. 6 个模板分属三条执行路径，见 ConversionEngine：Calibre / Polish / CbzPacker。
///
/// 目标设备（2026-10-01 决定）：安卓手机 / 安卓平板 / 电脑屏幕，没有 Kindle。
/// 所以 EPUB 是主输出，MOBI 只是备选（部分安卓阅读器能读）。
/// </summary>
public static class ConversionTemplates
{
    public const string MobileReadable = "mobile-readable";
    public const string A4Print = "a4-print";
    public const string PlainUtf8 = "plain-utf8";
    public const string ComicZip = "comic-zip";
    public const string KindleBest = "kindle-best";
    public const string PolishOnly = "polish-only";

    /// <summary>
    /// --extra-css 的取值由运行时替换成随程序分发的 eye-care.css 的绝对路径。
    /// Calibre 拿到相对路径会找不到文件，所以模板里只能放占位符。
    /// </summary>
    public const string ExtraCssPlaceholder = "{EyeCareCss}";

    /// <summary>随程序分发的护眼样式文件名（放在 Assets/Conversion/ 下）。</summary>
    public const string EyeCareCssFileName = "eye-care.css";

    /// <summary>6 个内置模板。顺序就是界面上建议的展示顺序。</summary>
    public static IReadOnlyList<ConversionTemplate> All { get; } = new[]
    {
        new ConversionTemplate
        {
            Id = MobileReadable,
            DisplayName = "手机 / 平板易读 (EPUB)",
            TargetFormat = BookFormatKind.Epub,
            Priority = "主用",
            Note = "主用。正文 18pt、行高 1.6、两端不强制对齐，再叠加一层护眼样式："
                + "手机上不费眼，字号可以在阅读器里再调。",
            Arguments = new[]
            {
                new ConversionArgument("--epub-version", "3",
                    "输出 EPUB 3；安卓主流阅读器都认。"),
                new ConversionArgument("--base-font-size", "18",
                    "正文基准字号 18pt，手机上不费眼；阅读器里仍可再调。"),
                new ConversionArgument("--line-height", "160",
                    "行高 1.6 倍，长段落更好读。"),
                new ConversionArgument("--change-justification", "left",
                    "两端对齐改成左对齐：中英混排时两端对齐会出现难看的空隙。"),
                new ConversionArgument("--extra-css", ExtraCssPlaceholder,
                    "叠加护眼样式（段间距、图片居中、标题留白）。"),
            },
        },
        new ConversionTemplate
        {
            Id = A4Print,
            DisplayName = "A4 打印 / 电脑阅读 (PDF)",
            TargetFormat = BookFormatKind.Pdf,
            Priority = "常用",
            Note = "常用。固定版式，电脑屏上看或打印用；字不能重排，手机上看会偏小。",
            Arguments = new[]
            {
                new ConversionArgument("--paper-size", "a4",
                    "按 A4 纸排版。"),
                new ConversionArgument("--pdf-page-margin-left", "20",
                    "左边距 20pt。"),
                new ConversionArgument("--pdf-page-margin-right", "20",
                    "右边距 20pt。"),
                new ConversionArgument("--pdf-page-margin-top", "20",
                    "上边距 20pt。"),
                new ConversionArgument("--pdf-page-margin-bottom", "20",
                    "下边距 20pt；四边各 20pt 是打印不裁字的常用值。"),
            },
        },
        new ConversionTemplate
        {
            Id = PlainUtf8,
            DisplayName = "纯文本归档 (TXT)",
            TargetFormat = BookFormatKind.Txt,
            Priority = "常用",
            Note = "常用。只留文字，丢掉全部排版与图片；适合做全文搜索的样本或极简阅读。",
            Arguments = new[]
            {
                new ConversionArgument("--txt-output-encoding", "utf-8",
                    "输出 UTF-8，别的阅读器打开不会乱码。"),
                new ConversionArgument("--enable-heuristics", null,
                    "开启发式处理：能更好地识别章节与段落，正文更干净。"),
            },
        },
        new ConversionTemplate
        {
            Id = ComicZip,
            DisplayName = "漫画打包 (CBZ)",
            Engine = ConversionEngine.CbzPacker,
            TargetFormat = BookFormatKind.Cbz,
            Priority = "常用",
            Note = "常用。把【图片文件夹】按自然序（第2张排在第10张前面）打成 CBZ。"
                + "CBZ 本质就是 zip 改名，安卓漫画阅读器与电脑都能读；"
                + "Calibre 没有 CBZ 输出插件（已实测），所以这条路径不走 Calibre。",
            Arguments = Array.Empty<ConversionArgument>(),
        },
        new ConversionTemplate
        {
            Id = KindleBest,
            DisplayName = "MOBI（备选）",
            TargetFormat = BookFormatKind.Mobi,
            Priority = "备选",
            Note = "备选。本机没有 Kindle 设备；只有部分安卓阅读器能读 MOBI，优先选 EPUB。",
            Arguments = new[]
            {
                new ConversionArgument("--output-profile", "kindle_pw3",
                    "按 6 寸 300ppi 设备优化版式。本机无 Kindle，纯备选档位。"),
                new ConversionArgument("--mobi-file-type", "both",
                    "MOBI 输出同时生成 old 与 new 两种类型，兼容性最好。"),
            },
        },
        new ConversionTemplate
        {
            Id = PolishOnly,
            DisplayName = "仅净化，不换格式",
            Engine = ConversionEngine.Polish,
            TargetFormat = null,
            ApplicableFormats = new[] { BookFormatKind.Epub, BookFormatKind.Azw3 },
            Priority = "常用",
            Note = "常用。不改格式，只优化原文件：嵌字体、删无用 CSS、无损压图。"
                + "实测 ebook-polish 只吃 EPUB / AZW3 / KEPUB，所以对 MOBI / PDF / TXT 不提供此模板。"
                + "产物会覆盖书库里的那个文件，原文件先备份一份。",
            Arguments = new[]
            {
                new ConversionArgument("--embed-fonts", null,
                    "把用到的字体嵌进文件，阅读器不缺字体。"),
                new ConversionArgument("--subset-fonts", null,
                    "字体子集化：只保留用到的字形，能省很多体积。"),
                new ConversionArgument("--remove-unused-css", null,
                    "删掉没用到的 CSS 规则。"),
                new ConversionArgument("--compress-images", null,
                    "无损压缩图片（没有质量参数，它本身就是无损的）。"),
            },
        },
    };

    public static ConversionTemplate? Find(string? templateId)
        => templateId is null
            ? null
            : All.FirstOrDefault(t => string.Equals(t.Id, templateId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 换格式的模板（走 ebook-convert），按目标格式过滤。
    /// comic-zip 不在其中：它由 CbzPacker 执行，输入是图片文件夹而不是书。
    /// </summary>
    public static IReadOnlyList<ConversionTemplate> ForTarget(BookFormatKind target)
        => All.Where(t => !t.KeepsFormat
            && t.Engine == ConversionEngine.Calibre
            && t.TargetFormat == target).ToList();

    /// <summary>不换格式的模板，按源格式过滤（polish 只吃 EPUB/AZW3）。UI 上单独一组。</summary>
    public static IReadOnlyList<ConversionTemplate> FormatKeepingFor(BookFormatKind sourceFormat)
        => All.Where(t => t.KeepsFormat && t.AppliesTo(sourceFormat)).ToList();

    /// <summary>漫画打包模板：CbzPacker 路径的唯一入口。</summary>
    public static ConversionTemplate ComicZipTemplate => All.First(t => t.Id == ComicZip);
}
