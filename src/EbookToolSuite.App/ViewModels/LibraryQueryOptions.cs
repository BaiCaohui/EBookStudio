using System.Windows.Input;
using EbookToolSuite.Core.Enums;

namespace EbookToolSuite.App.ViewModels;

/// <summary>格式筛选下拉的一项。Kind 为 null 表示"全部格式"。</summary>
public sealed record FormatOption(BookFormatKind? Kind, string Label)
{
    public static readonly FormatOption All = new(null, "全部格式");

    public static IReadOnlyList<FormatOption> AllOptions { get; } = new[]
    {
        All,
        new FormatOption(BookFormatKind.Epub, "EPUB"),
        new FormatOption(BookFormatKind.Mobi, "MOBI"),
        new FormatOption(BookFormatKind.Azw3, "AZW3"),
        new FormatOption(BookFormatKind.Pdf, "PDF"),
        new FormatOption(BookFormatKind.Txt, "TXT"),
        new FormatOption(BookFormatKind.Cbz, "CBZ"),
        new FormatOption(BookFormatKind.Fb2, "FB2"),
    };
}

/// <summary>阅读状态筛选下拉的一项。Status 为 null 表示"全部状态"。</summary>
public sealed record StatusOption(ReadingStatus? Status, string Label)
{
    public static readonly StatusOption All = new(null, "全部状态");

    public static IReadOnlyList<StatusOption> AllOptions { get; } = new[]
    {
        All,
        new StatusOption(ReadingStatus.Unread, "未读"),
        new StatusOption(ReadingStatus.Reading, "在读"),
        new StatusOption(ReadingStatus.Finished, "已读完"),
        new StatusOption(ReadingStatus.Abandoned, "弃读"),
    };
}

/// <summary>排序下拉的一项。方向和字段绑在一起，用户只选一项就够。</summary>
public sealed record SortOption(BookSort Sort, string Label, bool Descending)
{
    public static IReadOnlyList<SortOption> AllOptions { get; } = new[]
    {
        new SortOption(BookSort.Title, "标题 A-Z", false),
        new SortOption(BookSort.Title, "标题 Z-A", true),
        new SortOption(BookSort.Author, "作者 A-Z", false),
        new SortOption(BookSort.AddedAt, "最近加入", true),
        new SortOption(BookSort.LastReadAt, "最近阅读", true),
        new SortOption(BookSort.FileSize, "文件大小", true),
    };

    public static SortOption Default => AllOptions[0];
}

/// <summary>已选条件区里的一枚 chip。点它就把这个条件去掉。</summary>
public sealed record FilterChip(string Label, ICommand Remove);

/// <summary>虚拟化网格的一行：横向摆几张卡片。ListBox 直接吃的就是它。</summary>
public sealed class BookRowViewModel
{
    public BookRowViewModel(IReadOnlyList<BookCardViewModel> cards) => Cards = cards;

    public IReadOnlyList<BookCardViewModel> Cards { get; }
}
