using EbookToolSuite.Core.Enums;

namespace EbookToolSuite.Core.Models;

/// <summary>一个转换参数。Value 为 null 表示开关型（命令行上不带值）。</summary>
/// <param name="Note">面向用户的一行中文说明：这个参数做什么、什么时候该开。</param>
public sealed record ConversionArgument(string Key, string? Value = null, string? Note = null)
{
    public override string ToString() => Value is null ? Key : Key + " " + Value;
}

/// <summary>内置或用户转换模板（设计文档 6.3.5）。参数必须全部出现在 Calibre 白名单里。</summary>
public sealed record ConversionTemplate
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>目标格式。null 表示「不换格式」（如 polish-only 只优化原文件）。</summary>
    public BookFormatKind? TargetFormat { get; init; }

    /// <summary>由哪个引擎执行。默认 ebook-convert。</summary>
    public ConversionEngine Engine { get; init; } = ConversionEngine.Calibre;

    /// <summary>不换格式的模板能作用在哪些源格式上（polish 实测只吃 EPUB / AZW3 / KEPUB）。</summary>
    public IReadOnlyList<BookFormatKind> ApplicableFormats { get; init; } = Array.Empty<BookFormatKind>();

    /// <summary>true = 不换格式，产物覆盖源文件本身（polish-only）。</summary>
    public bool KeepsFormat => Engine == ConversionEngine.Polish;

    /// <summary>面向用户的一句话说明：这个模板做什么、什么时候该用。</summary>
    public string? Note { get; init; }

    public bool IsBuiltIn { get; init; } = true;

    /// <summary>主用 / 常用 / 备选。仅用于 UI 排序，不参与逻辑。</summary>
    public string Priority { get; init; } = "常用";

    public IReadOnlyList<ConversionArgument> Arguments { get; init; } = Array.Empty<ConversionArgument>();

    /// <summary>
    /// 这个模板能不能作用在给定的源格式上。换格式的模板不限源格式。
    /// ApplicableFormats 为空时回落到引擎自带的规则（用户模板从库里读出来时
    /// 不单独存这一列，见 ConversionTemplateRepository）。
    /// </summary>
    public bool AppliesTo(BookFormatKind sourceFormat)
    {
        if (!KeepsFormat)
        {
            return true;
        }

        var formats = ApplicableFormats.Count > 0
            ? ApplicableFormats
            : ConversionEngineRules.ApplicableFormats(Engine);

        return formats.Contains(sourceFormat);
    }
}
