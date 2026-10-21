namespace EbookToolSuite.Core.Enums;

/// <summary>
/// 书籍文件格式。存库用 <see cref="BookFormatKinds.ToStorageString"/>（大写字串），
/// 不存数字，便于直接读库排障（设计文档 5.2）。
/// </summary>
public enum BookFormatKind
{
    Unknown = 0,
    Epub = 1,
    Mobi = 2,   // .mobi 与 .azw 都归此
    Azw3 = 3,
    Pdf = 4,
    Txt = 5,
    Cbz = 6,    // .cbz 与 .cbr 都归此
    Fb2 = 7,
}

public static class BookFormatKinds
{
    public static string ToStorageString(this BookFormatKind kind) => kind switch
    {
        BookFormatKind.Epub => "EPUB",
        BookFormatKind.Mobi => "MOBI",
        BookFormatKind.Azw3 => "AZW3",
        BookFormatKind.Pdf => "PDF",
        BookFormatKind.Txt => "TXT",
        BookFormatKind.Cbz => "CBZ",
        BookFormatKind.Fb2 => "FB2",
        _ => "UNKNOWN",
    };

    public static BookFormatKind FromStorageString(string? s) => s?.ToUpperInvariant() switch
    {
        "EPUB" => BookFormatKind.Epub,
        "MOBI" or "AZW" => BookFormatKind.Mobi,
        "AZW3" => BookFormatKind.Azw3,
        "PDF" => BookFormatKind.Pdf,
        "TXT" => BookFormatKind.Txt,
        "CBZ" or "CBR" => BookFormatKind.Cbz,
        "FB2" => BookFormatKind.Fb2,
        _ => BookFormatKind.Unknown,
    };

    /// <summary>按扩展名推断。覆盖导入白名单的 9 个扩展名。</summary>
    public static BookFormatKind FromExtension(string path)
        => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".epub" => BookFormatKind.Epub,
            ".mobi" or ".azw" => BookFormatKind.Mobi,
            ".azw3" => BookFormatKind.Azw3,
            ".pdf" => BookFormatKind.Pdf,
            ".txt" => BookFormatKind.Txt,
            ".cbz" or ".cbr" => BookFormatKind.Cbz,
            ".fb2" => BookFormatKind.Fb2,
            _ => BookFormatKind.Unknown,
        };

    /// <summary>
    /// 输出文件的扩展名（含点）。Calibre 依据输出文件的扩展名推断目标格式，
    /// 所以临时文件也必须带正确扩展名，不能写成 out.mobi.part（实测必失败）。
    /// </summary>
    public static string ToExtension(this BookFormatKind kind) => kind switch
    {
        BookFormatKind.Epub => ".epub",
        BookFormatKind.Mobi => ".mobi",
        BookFormatKind.Azw3 => ".azw3",
        BookFormatKind.Pdf => ".pdf",
        BookFormatKind.Txt => ".txt",
        BookFormatKind.Cbz => ".cbz",
        BookFormatKind.Fb2 => ".fb2",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未知格式没有输出扩展名"),
    };
}
