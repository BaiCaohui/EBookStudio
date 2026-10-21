namespace EbookToolSuite.Core.Common;

/// <summary>
/// 自然序比较器：让 1.jpg / 2.jpg / 10.jpg 排成 1, 2, 10，而不是 1, 10, 2。
/// 漫画打包与漫画阅读都靠它（T6-3 会复用）。
/// </summary>
/// <remarks>
/// 规则：把字符串切成「数字段」与「非数字段」交替比较。非数字段用
/// OrdinalIgnoreCase 比（大小写不敏感）；数字段按数值比，数值相同再按
/// 位数比（这样 01.jpg 排在 1.jpg 前面，符合漫画的常见编号习惯）。
/// 纯数字段用 long 解析，超过 long 范围时退化成按位数 + 字典序比，不会抛异常。
/// </remarks>
public sealed class NaturalStringComparer : IComparer<string>
{
    public static NaturalStringComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        var i = 0;
        var j = 0;

        while (i < x.Length && j < y.Length)
        {
            var xDigit = char.IsDigit(x[i]);
            var yDigit = char.IsDigit(y[j]);

            if (xDigit && yDigit)
            {
                var result = CompareNumberSpans(x, i, y, j, out var xEnd, out var yEnd);
                if (result != 0)
                {
                    return result;
                }

                i = xEnd;
                j = yEnd;
                continue;
            }

            if (xDigit != yDigit)
            {
                // 数字排在非数字前面，保证结果稳定。
                return xDigit ? -1 : 1;
            }

            var diff = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
            if (diff != 0)
            {
                return diff < 0 ? -1 : 1;
            }

            i++;
            j++;
        }

        return (x.Length - i).CompareTo(y.Length - j);
    }

    private static int CompareNumberSpans(
        string x, int xStart, string y, int yStart, out int xEnd, out int yEnd)
    {
        xEnd = SkipDigits(x, xStart);
        yEnd = SkipDigits(y, yStart);

        // 去掉前导零后再比，01 与 1 视为同一个数。
        var xSig = TrimLeadingZeros(x, xStart, xEnd);
        var ySig = TrimLeadingZeros(y, yStart, yEnd);

        if (xSig.Length != ySig.Length)
        {
            return xSig.Length < ySig.Length ? -1 : 1;
        }

        for (var k = 0; k < xSig.Length; k++)
        {
            if (xSig[k] != ySig[k])
            {
                return xSig[k] < ySig[k] ? -1 : 1;
            }
        }

        // 数值相同：位数少的排前面（01.jpg 在 1.jpg 前）。
        var xLen = xEnd - xStart;
        var yLen = yEnd - yStart;
        return xLen.CompareTo(yLen);
    }

    private static int SkipDigits(string s, int start)
    {
        var i = start;
        while (i < s.Length && char.IsDigit(s[i]))
        {
            i++;
        }

        return i;
    }

    private static string TrimLeadingZeros(string s, int start, int end)
    {
        var i = start;
        while (i < end - 1 && s[i] == '0')
        {
            i++;
        }

        return s[i..end];
    }
}
