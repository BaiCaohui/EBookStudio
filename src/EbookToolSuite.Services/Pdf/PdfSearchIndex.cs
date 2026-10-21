using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Services.Pdf;

/// <summary>
/// 阅读器内搜索的文本索引（T6-2B）：把一本书每页的文本读进内存，按"页号 + 字符下标"给出命中。
/// </summary>
/// <remarks>
/// <para>
/// 【为什么不是 FTS5】全库全文检索是 T6-4 的事（设计文档把它排在 T6-2B 之后），
/// 这里只回答"当前这本书里有没有这几个字"。一本书几十万字，一次性读进内存就够，
/// 不值得为它单独建一套索引结构。
/// </para>
/// <para>
/// 【为什么不落盘】页文本是从 PDF 现读出来的派生数据，落盘就多一份要跟着清理的缓存；
/// 代价只有用户第一次按 Ctrl+F 的那一下，实测数字见 build\t62b-acceptance-report.md。
/// </para>
/// <para>
/// 【线程纪律】页文本只在后台线程读（pdfium 是同步原生调用），缓存表用锁护住，
/// 锁里不 await——读同一本书时最多重复读一次，不会死锁。
/// </para>
/// </remarks>
public sealed class PdfSearchIndex
{
    /// <summary>
    /// 一次搜索最多回多少条：命中上千条时外壳画不动、用户也翻不到，到顶就截断。
    /// </summary>
    public const int MaxHits = 500;

    private readonly IPdfDocumentService _pdf;
    private readonly Dictionary<string, IReadOnlyList<string>> _pages = new(StringComparer.OrdinalIgnoreCase);

    public PdfSearchIndex(IPdfDocumentService pdf)
    {
        _pdf = pdf ?? throw new ArgumentNullException(nameof(pdf));
    }

    /// <summary>抓这本书的每页文本（已经抓过就直接返回缓存）。索引即页号。</summary>
    public async Task<IReadOnlyList<string>> LoadAsync(
        string path, string? password, CancellationToken ct = default)
    {
        lock (_pages)
        {
            if (_pages.TryGetValue(path, out var cached))
            {
                return cached;
            }
        }

        var pages = await Task.Run(() => _pdf.ReadAllText(path, password), ct).ConfigureAwait(false);

        lock (_pages)
        {
            _pages[path] = pages;
        }

        return pages;
    }

    /// <summary>找 <paramref name="query"/> 在这本书里的所有命中（不区分大小写、不重叠、有上限）。</summary>
    public async Task<IReadOnlyList<PdfSearchHit>> SearchAsync(
        string path, string query, string? password, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(query))
        {
            return Array.Empty<PdfSearchHit>();
        }

        var pages = await LoadAsync(path, password, ct).ConfigureAwait(false);
        var hits = new List<PdfSearchHit>();

        for (var page = 0; page < pages.Count && hits.Count < MaxHits; page++)
        {
            var text = pages[page];

            if (text.Length == 0)
            {
                continue;
            }

            var from = 0;

            while (hits.Count < MaxHits)
            {
                var at = text.IndexOf(query, from, StringComparison.OrdinalIgnoreCase);

                if (at < 0)
                {
                    break;
                }

                hits.Add(new PdfSearchHit(page, at, query.Length));

                // 不重叠：命中串之后继续找，避免 "aaa" 在 "aaaaa" 里产出重叠结果
                from = at + query.Length;
            }
        }

        return hits;
    }
}
