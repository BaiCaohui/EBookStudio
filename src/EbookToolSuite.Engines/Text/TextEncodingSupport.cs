using System.Text;
using System.Text.RegularExpressions;

namespace EbookToolSuite.Engines.Text;

/// <summary>
/// 中文编码（GBK / GB18030）在 .NET 里要先注册代码页提供程序才能用。
/// </summary>
/// <remarks>
/// 【必须在任何 GBK/GB18030 解码之前执行】执行计划 T4-5 原话是"放在 Program.Main 的第一行最安全"，
/// 但本工程是 WPF（Main 是生成的，不在我们手里），所以改成：
/// 静态构造函数 + <see cref="EnsureRegistered"/>，任何走本类的调用都会被兜住。
/// 实测：.NET 10 共享框架自带 System.Text.Encoding.CodePages.dll，不需要额外 NuGet 包。
///
/// 【2026-10-20 扩表】用户要求编码下拉要有常见的一大组（UTF-8-BOM / UTF-16 / UTF-32 /
/// GB2312 / Big5 / Big5-HKSCS / ShiftJIS / EUC-JP / EUC-KR / ISO-2022-JP / ISO-2022-KR /
/// ISO-8859-1…16 / KOI8-R / KOI8-U / Windows-1250…1258 / IBM866 / IBM850 / CESU-8）。
/// 做法是把"名字 → 代码页"列表写死在这里，再用运行时逐个试：**试不出来的名字不进下拉框**
/// （避免选了才报错）。CESU-8 在 .NET 里没有代码页，按 UTF-8 别名处理（BMP 字符两者一致）。
/// </remarks>
public static class TextEncodingSupport
{
    public const int GbkCodePage = 936;
    public const int Gb18030CodePage = 54936;

    public const string Utf8Name = "UTF-8";
    public const string Utf8BomName = "UTF-8-BOM";
    public const string Utf16LeName = "UTF-16LE";
    public const string Utf16BeName = "UTF-16BE";
    public const string Utf32LeName = "UTF-32LE";
    public const string Utf32BeName = "UTF-32BE";
    public const string GbkName = "GBK";
    public const string Gb2312Name = "GB2312";
    public const string Gb18030Name = "GB18030";

    private static int _registered;
    private static IReadOnlyList<string>? _names;

    /// <summary>注册代码页提供程序；重复调用只生效一次。</summary>
    public static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 0)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
    }

    /// <summary>界面上给用户选的编码名单（运行时真能用的那些，顺序即下拉框顺序）。</summary>
    public static IReadOnlyList<string> SupportedNames
    {
        get
        {
            EnsureRegistered();

            _names ??= Declared
                .Where(entry => Create(entry.CodePage) is not null)
                .Select(entry => entry.Name)
                .ToArray();

            return _names;
        }
    }

    /// <summary>按名字取编码；名字不认识返回 null（调用方据此退回自动识别）。</summary>
    public static Encoding? Resolve(string? name)
    {
        EnsureRegistered();

        var canonical = Normalize(name);

        if (canonical.Length == 0)
        {
            return null;
        }

        /* UTF-8-BOM 与 UTF-8 共用代码页 65001，必须按名字给"会吐 BOM"的那个实例，
           否则用户在编辑器里选了 UTF-8-BOM、保存回盘却不带 BOM。 */
        if (string.Equals(canonical, Utf8BomName, StringComparison.Ordinal))
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        }

        foreach (var entry in Declared)
        {
            if (string.Equals(entry.Name, canonical, StringComparison.Ordinal))
            {
                return Create(entry.CodePage);
            }
        }

        return null;
    }

    /// <summary>把各种常见写法归一成下拉框里的标准名（认不出来给空串）。</summary>
    public static string Normalize(string? name)
    {
        var text = (name ?? string.Empty).Trim().Replace("_", "-").ToUpperInvariant();

        if (text.Length == 0)
        {
            return string.Empty;
        }

        switch (text)
        {
            case "UTF8":
            case "UTF-8":
                return Utf8Name;
            case "UTF8-BOM":
            case "UTF-8-BOM":
            case "UTF-8BOM":
            case "UTF8BOM":
                return Utf8BomName;
            case "UTF16":
            case "UTF-16":
            case "UNICODE":
            case "UTF-16LE":
                return Utf16LeName;
            case "UTF-16BE":
                return Utf16BeName;
            case "UTF32":
            case "UTF-32":
            case "UTF-32LE":
                return Utf32LeName;
            case "UTF-32BE":
                return Utf32BeName;
            case "GB2312":
            case "GB-2312":
                return Gb2312Name;
            case "GBK":
            case "936":
            case "CP936":
                return GbkName;
            case "GB18030":
            case "54936":
            case "CP54936":
                return Gb18030Name;
            case "BIG5":
            case "950":
            case "CP950":
                return "Big5";
            case "BIG5-HKSCS":
            case "BIG5HKSCS":
            case "HKSCS":
                return "Big5-HKSCS";
            case "SHIFTJIS":
            case "SHIFT-JIS":
            case "SJIS":
            case "932":
                return "ShiftJIS";
            case "EUCJP":
            case "EUC-JP":
                return "EUC-JP";
            case "EUCKR":
            case "EUC-KR":
                return "EUC-KR";
            case "ISO2022JP":
            case "ISO-2022-JP":
                return "ISO-2022-JP";
            case "ISO2022KR":
            case "ISO-2022-KR":
                return "ISO-2022-KR";
            case "KOI8R":
            case "KOI8-R":
                return "KOI8-R";
            case "KOI8U":
            case "KOI8-U":
                return "KOI8-U";
            case "IBM866":
            case "CP866":
            case "866":
                return "IBM866";
            case "IBM850":
            case "CP850":
            case "850":
                return "IBM850";
            case "CESU8":
            case "CESU-8":
                return "CESU-8";
        }

        var latin = Regex.Match(text, @"^ISO-?8859-?([0-9]{1,2})$");

        if (latin.Success)
        {
            return "ISO-8859-" + latin.Groups[1].Value;
        }

        var windows = Regex.Match(text, @"^(?:WINDOWS|CP|MS)-?([0-9]{4})$");

        if (windows.Success)
        {
            return "Windows-" + windows.Groups[1].Value;
        }

        return string.Empty;
    }

    /// <summary>
    /// 名字 → 代码页。**顺序就是下拉框里的顺序**（用户 2026-10-20 按他给的三张图定死）：
    /// UTF-8 起头，然后 UTF-8-BOM / UTF-16 / UTF-32 / 中文 / 日韩 / IBM / ISO-2022 /
    /// ISO-8859 / KOI8 / ShiftJIS / Windows-125x。
    /// UTF-8-BOM 与 CESU-8 的代码页都是 65001，差异在 <see cref="Create"/> 里处理。
    /// </summary>
    private static readonly (string Name, int CodePage)[] Declared = BuildDeclared();

    private static (string Name, int CodePage)[] BuildDeclared()
    {
        var list = new List<(string, int)>
        {
            (Utf8Name, 65001),
            (Utf8BomName, 65001),
            (Utf16BeName, 1201),
            (Utf16LeName, 1200),
            (Utf32BeName, 12001),
            (Utf32LeName, 12000),
            (Gb2312Name, GbkCodePage),
            (Gb18030Name, Gb18030CodePage),
            (GbkName, GbkCodePage),
            ("Big5", 950),
            ("Big5-HKSCS", 950),
            ("CESU-8", 65001),
            ("EUC-JP", 51932),
            ("EUC-KR", 51949),
            ("IBM866", 866),
            ("IBM850", 850),
            ("ISO-2022-JP", 50220),
            ("ISO-2022-KR", 50225),
        };

        // 用户图里的 ISO-8859 只到 1…10 和 13/14/15（11/12/16 不出现，就不放）
        foreach (var i in new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 13, 14, 15 })
        {
            list.Add(("ISO-8859-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), 28590 + i));
        }

        list.Add(("KOI8-R", 20866));
        list.Add(("KOI8-U", 21866));
        list.Add(("ShiftJIS", 932));

        for (var cp = 1250; cp <= 1258; cp++)
        {
            list.Add(("Windows-" + cp.ToString(System.Globalization.CultureInfo.InvariantCulture), cp));
        }

        return list.ToArray();
    }

    /// <summary>按代码页造编码实例；造不出来（运行时没有这个代码页）返回 null。</summary>
    private static Encoding? Create(int codePage)
    {
        try
        {
            return codePage switch
            {
                // 下拉框里的 "UTF-8" 是不带 BOM 的那个；"UTF-8-BOM" 用会吐 BOM 的实例
                65001 => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                1200 => new UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: false),
                1201 => new UnicodeEncoding(bigEndian: true, byteOrderMark: true, throwOnInvalidBytes: false),
                12000 => new UTF32Encoding(bigEndian: false, byteOrderMark: true, throwOnInvalidCharacters: false),
                12001 => new UTF32Encoding(bigEndian: true, byteOrderMark: true, throwOnInvalidCharacters: false),
                _ => Encoding.GetEncoding(codePage),
            };
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}
