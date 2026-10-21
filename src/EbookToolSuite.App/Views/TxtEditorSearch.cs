using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace EbookToolSuite.App.Views;

/// <summary>
/// 全文编辑器里的一处查找命中（字符下标 + 长度）。
/// 用 <c>IndexOf</c> 扫出来的区间互不重叠，所以渲染时按顺序往行上涂即可。
/// </summary>
internal readonly record struct TxtSearchMatch(int Start, int Length)
{
    internal int End => Start + Length;
}

/// <summary>
/// 把查找结果涂到 AvalonEdit 的文本行上（用户 2026-10-20 要求）：
/// **所有命中黄底、当前定位的那一处红底**。
///
/// 只影响渲染，不改文档、不改全局主题；字号/自动换行由编辑器自己重排。
/// 命中列表一变（查找框改字、替换、编辑正文）就 <see cref="SetMatches"/>，
/// 再由窗口调 <c>Editor.TextArea.TextView.Redraw()</c> 让 AvalonEdit 重新生成视觉行。
/// </summary>
internal sealed class TxtSearchColorizer : DocumentColorizingTransformer
{
    /// <summary>普通命中：黄色背景（#FFF2C94C 左右）。</summary>
    internal static readonly Brush MatchBackground = CreateFrozen(0xFF, 0xF2, 0xC9, 0x4C);

    /// <summary>当前定位的那一处：红色背景（#FFE05A5A 左右）。</summary>
    internal static readonly Brush CurrentBackground = CreateFrozen(0xFF, 0xE0, 0x5A, 0x5A);

    /// <summary>
    /// 命中文字用深色前景：黄底/红底在浅色主题和深色主题下都能看清。
    /// 只是局部文本属性，不动主题画刷。
    /// </summary>
    internal static readonly Brush HighlightForeground = CreateFrozen(0xFF, 0x1A, 0x1A, 0x1A);

    private IReadOnlyList<TxtSearchMatch> _matches = Array.Empty<TxtSearchMatch>();
    private int _currentIndex = -1;

    /// <summary>当前命中数（冒烟断言"黄色命中数"用）。</summary>
    internal int MatchCount => _matches.Count;

    /// <summary>当前红色高亮在命中列表里的下标（没有命中时 -1）。</summary>
    internal int CurrentIndex => _currentIndex;

    /// <summary>命中列表或当前项变了：先换列表，再让 TextView 重画。</summary>
    internal void SetMatches(IReadOnlyList<TxtSearchMatch> matches, int currentIndex)
    {
        _matches = matches;
        _currentIndex = currentIndex;
    }

    /// <summary>只挪红色高亮位置时用（命中列表没变，不用重建）。</summary>
    internal void SetCurrentIndex(int currentIndex) => _currentIndex = currentIndex;

    /// <summary>
    /// 某个字符下标会画成什么背景色（冒烟断言"普通命中=黄、当前命中=红、非命中=无"用）。
    /// 与 <see cref="ColorizeLine"/> 的判定保持一致。
    /// </summary>
    internal Brush? HighlightBrushAt(int offset)
    {
        for (var i = 0; i < _matches.Count; i++)
        {
            if (offset >= _matches[i].Start && offset < _matches[i].End)
            {
                return i == _currentIndex ? CurrentBackground : MatchBackground;
            }
        }

        return null;
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        if (_matches.Count == 0)
        {
            return;
        }

        var lineStart = line.Offset;
        var lineEnd = line.EndOffset;

        for (var i = 0; i < _matches.Count; i++)
        {
            var match = _matches[i];

            // 命中按 Start 升序：过了本行的就不用再看了；完全在本行前面的往下跳。
            if (match.Start >= lineEnd)
            {
                break;
            }

            if (match.End <= lineStart)
            {
                continue;
            }

            var start = Math.Max(match.Start, lineStart);
            var end = Math.Min(match.End, lineEnd);

            if (end <= start)
            {
                continue;
            }

            var background = i == _currentIndex ? CurrentBackground : MatchBackground;

            ChangeLinePart(start, end, element =>
            {
                element.TextRunProperties.SetBackgroundBrush(background);
                element.TextRunProperties.SetForegroundBrush(HighlightForeground);
            });
        }
    }

    private static Brush CreateFrozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
