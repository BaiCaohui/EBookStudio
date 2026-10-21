namespace EbookToolSuite.Core.Enums;

/// <summary>转换模板由哪个引擎执行（设计文档 6.3.5 的 6 个内置模板分属三条路径）。</summary>
public enum ConversionEngine
{
    /// <summary>ebook-convert：换格式的主力路径。</summary>
    Calibre = 0,

    /// <summary>ebook-polish：不换格式，只优化原文件（产物覆盖源文件）。</summary>
    Polish = 1,

    /// <summary>自研打包：图片文件夹 -> CBZ（Calibre 没有 CBZ 输出插件，实测）。</summary>
    CbzPacker = 2,
}

/// <summary>引擎自带的规则。放在 Core 是为了让仓储层也能用（它只引用 Core）。</summary>
public static class ConversionEngineRules
{
    /// <summary>
    /// polish 能吃哪些源格式。实测 ebook-polish 的 --help 原文：
    /// 「润色仅适用于 AZW3 或 EPUB 或 KEPUB 格式的文件」。
    /// 本项目不处理 KEPUB，所以只有这两种。
    /// </summary>
    public static IReadOnlyList<BookFormatKind> ApplicableFormats(ConversionEngine engine)
        => engine == ConversionEngine.Polish
            ? new[] { BookFormatKind.Epub, BookFormatKind.Azw3 }
            : Array.Empty<BookFormatKind>();
}
