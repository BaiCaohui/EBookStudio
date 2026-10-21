using EbookToolSuite.Core.Enums;

namespace EbookToolSuite.Core.Models;

/// <summary>
/// 「这本书能用什么格式读」的唯一判定（用户 2026-10-21）。
/// </summary>
/// <remarks>
/// 为什么要收成一处：同一套判断现在有三处要用——
/// 1. 阅读服务打开这本书时挑源文件（EPUB 优先的默认链，或用户选的当前阅读格式）；
/// 2. 书库详情页决定哪一行给「切换此格式」（读不了的不给）；
/// 3. 卡片角标与详情格式行：MOBI / AZW3 是**转成 EPUB 来读**的，
///    所以"读到的形态"是 EPUB，要在角标里额外显示 EPUB、并把当前阅读格式说成 EPUB。
///
/// 把这些规则散在三个地方写，迟早会出现"角标说能读、按钮却不给点"这种自相矛盾。
/// </remarks>
public static class ReaderFormatSupport
{
    /// <summary>
    /// 默认优先级链（T4-5 / T6-1 / T6-2 / T6-3 定下的）：
    /// EPUB -&gt; TXT -&gt; MOBI -&gt; AZW3 -&gt; PDF -&gt; CBZ。
    /// </summary>
    /// <remarks>
    /// EPUB 排第一是有意的：它带着最全的排版信息；PDF 与漫画排在最后，
    /// 只在这本书"没有文字类格式"时才上场。用户 2026-10-21 允许在详情页改这个默认。
    /// </remarks>
    public static readonly IReadOnlyList<BookFormatKind> PreferenceOrder = new[]
    {
        BookFormatKind.Epub,
        BookFormatKind.Txt,
        BookFormatKind.Mobi,
        BookFormatKind.Azw3,
        BookFormatKind.Pdf,
        BookFormatKind.Cbz,
    };

    /// <summary>
    /// MOBI / AZW3 不能直接读，要先转成 EPUB 缓存再读（T6-1 的惰性转换）。
    /// </summary>
    public static bool RequiresEpubConversion(BookFormatKind kind)
        => kind is BookFormatKind.Mobi or BookFormatKind.Azw3;

    /// <summary>
    /// 这个格式能不能读：直接读（EPUB / TXT / PDF / CBZ），或经 EPUB 转换后读（MOBI / AZW3）。
    /// FB2 与 Unknown 没有阅读实现，返回 false——详情页因此不给「切换此格式」按钮。
    /// </summary>
    public static bool CanRead(BookFormatKind kind)
        => kind is BookFormatKind.Epub
            or BookFormatKind.Txt
            or BookFormatKind.Pdf
            or BookFormatKind.Cbz
            || RequiresEpubConversion(kind);

    /// <summary>
    /// 用户实际"读到的形态"：MOBI / AZW3 读到的是转出来的 EPUB，其余就是格式本身。
    /// 角标与「当前阅读格式」都按这个说话（用户 2026-10-21 指定）。
    /// </summary>
    public static BookFormatKind ReadingForm(BookFormatKind kind)
        => RequiresEpubConversion(kind) ? BookFormatKind.Epub : kind;

    /// <summary>格式记录现在能不能用：没标缺失，而且磁盘上真的在。</summary>
    /// <remarks>
    /// 会碰一次 File.Exists。调用方都是"每本书只有一两个格式"的界面路径，
    /// 这点 IO 可以接受（BuildMetadata 本来也是这么做的）。
    /// </remarks>
    public static bool IsUsable(BookFormat? format)
        => format is not null && !format.Missing && File.Exists(format.FilePath);

    /// <summary>
    /// 挑这次要读的格式文件：用户选过（且还能用）就用它，否则按默认优先级链。
    /// 一个可读的都没有时返回 null，错误文案由调用方给。
    /// </summary>
    public static BookFormat? Resolve(
        IEnumerable<BookFormat> formats, BookFormatKind? preferred = null)
    {
        ArgumentNullException.ThrowIfNull(formats);

        var list = formats as IReadOnlyList<BookFormat> ?? formats.ToList();

        if (preferred is { } wanted)
        {
            var chosen = list.FirstOrDefault(f => f.Format == wanted && IsUsable(f));

            if (chosen is not null)
            {
                return chosen;
            }
        }

        foreach (var kind in PreferenceOrder)
        {
            var hit = list.FirstOrDefault(f => f.Format == kind && IsUsable(f));

            if (hit is not null)
            {
                return hit;
            }
        }

        return null;
    }
}
