namespace EbookToolSuite.Core.Models;

/// <summary>
/// PDF 阅读器内搜索的一条命中（T6-2B）：位置 = 页号 + 该页文本里的字符下标区间。
/// </summary>
/// <remarks>
/// 【为什么回"页号 + 下标"而不是文本片段】外壳已经拿到了每页的字符表
/// （<see cref="PdfCharBox"/>，与页文本同序），拿下标就能直接在文本层上把命中的那几个字符框
/// 高亮出来。回片段的话外壳还得再拿片段去页文本里做一次字符串匹配，多一次对不齐的机会
/// （同一句话在一页里出现多次时尤其容易错）。
/// </remarks>
/// <param name="PageIndex">命中所在页，0 基。</param>
/// <param name="Start">命中在该页文本里的起始字符下标。</param>
/// <param name="Length">命中长度，等于查询串长度。</param>
public sealed record PdfSearchHit(int PageIndex, int Start, int Length);
