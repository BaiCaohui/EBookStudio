using System.Globalization;

namespace EbookToolSuite.Core.Common;

/// <summary>
/// CBZ 包内条目名的顺序化规则。
/// </summary>
/// <remarks>
/// <para>
/// 【为什么需要它】CBZ 没有页序字段，阅读器按**包内文件名自然序**排页。
/// 用户手动拖拽/上下移调完序之后，必须让文件名本身能表达这个顺序，
/// 否则阅读器打开还是原来的顺序。做法：给整个列表重新编号成
/// <c>0001_原文件名.jpg</c>、<c>0002_原文件名.jpg</c>……（前缀为零填充序号 + 下划线）。
/// </para>
/// <para>
/// 【为什么不直接改成 0001.jpg】原始文件名是用户自己的信息，去掉就找不回来了；
/// 前缀方案既保证顺序，又保留原名。只有**手动调过序**才加前缀。
/// </para>
/// <para>
/// 【幂等】再次调序时会先剥掉旧前缀（<c>^\d{4,}_</c>）再重新编号，
/// 不会叠成 <c>0001_0002_xxx.jpg</c>。
/// </para>
/// </remarks>
public static class CbzEntryNames
{
    /// <summary>序号最少补到 4 位（0001）。超过 9999 张时自动加宽。</summary>
    public const int MinOrderWidth = 4;

    /// <summary>前缀与原名之间的分隔符。</summary>
    public const char OrderSeparator = '_';

    /// <summary>按总条数决定序号宽度。10000 张时就是 5 位，保证字典序 == 数值序。</summary>
    public static int OrderWidth(int count)
        => Math.Max(MinOrderWidth, Math.Max(1, count).ToString(CultureInfo.InvariantCulture).Length);

    /// <summary>这个条目名是不是已经带了我们生成的有序前缀。</summary>
    public static bool HasOrderPrefix(string fileName) => TryParseOrderPrefix(fileName, out _);

    /// <summary>剥掉有序前缀（没有就原样返回）。只会剥 <c>\d{4,}_</c> 这种形态。</summary>
    public static string StripOrderPrefix(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        var rest = fileName;

        while (TryParseOrderPrefix(rest, out _))
        {
            rest = rest[(rest.IndexOf(OrderSeparator) + 1)..];
        }

        return rest;
    }

    /// <summary>
    /// 给一个文件名加上第 <paramref name="order"/> 位的前缀（1 起）。
    /// 已经带前缀的会先剥掉，所以反复调用不会叠加。
    /// </summary>
    public static string WithOrderPrefix(int order, int count, string fileName)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(order, 1);
        ArgumentNullException.ThrowIfNull(fileName);

        var width = OrderWidth(count);
        var stem = StripOrderPrefix(fileName);

        return order.ToString("D" + width.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)
            + OrderSeparator + stem;
    }

    /// <summary>
    /// 按传入顺序整表编号：第 1 个得到 0001_…，第 2 个得到 0002_…，以此类推。
    /// 返回的列表与传入列表**同序**，可直接作为打包顺序。
    /// </summary>
    public static IReadOnlyList<string> ApplyOrder(IReadOnlyList<string> fileNames)
    {
        ArgumentNullException.ThrowIfNull(fileNames);

        var count = fileNames.Count;
        var result = new string[count];

        for (var i = 0; i < count; i++)
        {
            result[i] = WithOrderPrefix(i + 1, count, fileNames[i]);
        }

        return result;
    }

    /// <summary>
    /// 校验一个条目名能不能进包。合法返回 null，不合法返回给用户看的中文原因。
    /// </summary>
    /// <remarks>
    /// 中文、空格、括号都允许；只拦掉 Windows/ZIP 双重解压路径会出问题的字符，
    /// 以及会破坏「文件名即顺序」的文件名分隔符（/ 和 \）。
    /// </remarks>
    public static string? InvalidEntryNameReason(string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName))
        {
            return "文件名不能为空。";
        }

        if (entryName is "." or "..")
        {
            return "文件名不能是「.」或「..」。";
        }

        var invalid = Path.GetInvalidFileNameChars();

        foreach (var ch in entryName)
        {
            if (ch is '/' or '\\')
            {
                return "文件名不能包含斜杠（/ 或 \\）。";
            }

            if (Array.IndexOf(invalid, ch) >= 0)
            {
                return "文件名不能包含这些字符：\\ / : * ? \" < > |";
            }
        }

        if (entryName.Length > 200)
        {
            return "文件名太长了（最多 200 个字符）。";
        }

        return null;
    }

    private static bool TryParseOrderPrefix(string fileName, out int order)
    {
        order = 0;

        if (string.IsNullOrEmpty(fileName) || fileName.Length < MinOrderWidth + 1)
        {
            return false;
        }

        var separator = fileName.IndexOf(OrderSeparator);
        if (separator < MinOrderWidth)
        {
            return false;
        }

        for (var i = 0; i < separator; i++)
        {
            if (!char.IsAsciiDigit(fileName[i]))
            {
                return false;
            }
        }

        return int.TryParse(
            fileName.AsSpan(0, separator),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out order);
    }
}
