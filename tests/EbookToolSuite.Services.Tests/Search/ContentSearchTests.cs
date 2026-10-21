using System.Text;
using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Infrastructure.Data.Repositories;
using EbookToolSuite.Services.Pdf;
using EbookToolSuite.Services.Search;
using EbookToolSuite.Services.Tests.Pdf;
using Microsoft.Extensions.Logging.Abstractions;

namespace EbookToolSuite.Services.Tests.Search;

/// <summary>
/// T6-4 端到端（真 SQLite + 真 EPUB/TXT/PDF 抽取）：导入 → 后台索引 → 中文/英文/短词搜索 → 续建。
/// </summary>
public sealed class ContentSearchTests : IAsyncLifetime
{
    private const string ChapterText = "这是一本讲电子书与阅读器的书，hello world 也在这里。";

    private ImportSandbox _sandbox = null!;
    private ContentRepository _repo = null!;
    private ContentSearchService _search = null!;
    private ContentTextExtractor _extractor = null!;
    private ContentIndexer _indexer = null!;
    private Guid _bookId;

    public async Task InitializeAsync()
    {
        _sandbox = new ImportSandbox();
        await _sandbox.InitializeAsync();

        _repo = new ContentRepository(_sandbox.Factory);
        _search = new ContentSearchService(_repo);
        _extractor = new ContentTextExtractor(null, NullLogger<ContentTextExtractor>.Instance);
        _indexer = new ContentIndexer(
            _repo, _sandbox.Repository, _extractor, NullLogger<ContentIndexer>.Instance);

        var path = _sandbox.WriteEpub(new EpubBuilder
        {
            Title = "全文搜索测试书",
            ChapterCount = 2,
            ChapterText = ChapterText,
        }, "search.epub");

        var result = await _sandbox.Pipeline.ImportAsync(path, new ImportOptions());
        Assert.Equal(ImportOutcome.Imported, result.Outcome);
        _bookId = result.BookId!.Value;
    }

    public async Task DisposeAsync()
    {
        await _indexer.StopAsync();
        await _sandbox.DisposeAsync();
    }

    [Fact]
    public async Task 启动索引器会捡起缺索引的书_建完状态为Done()
    {
        await _indexer.StartAsync();

        Assert.True(await _indexer.WaitForIdleAsync(TimeSpan.FromSeconds(30)),
            "索引队列在 30 秒内没走空");

        var state = await _repo.GetStateAsync(_bookId);

        Assert.NotNull(state);
        Assert.Equal(ContentIndexStatus.Done, state!.Status);
        Assert.True(state.ChunkCount > 0, "切片数应大于 0，实际 " + state.ChunkCount);
    }

    [Fact]
    public async Task 三字中文词能命中并带回书名与章号()
    {
        await IndexAsync();

        var result = await _search.SearchAsync("电子书");

        Assert.False(result.UsedShortQueryFallback);
        Assert.True(result.TotalMatches >= 2, "两章都该命中，实际 " + result.TotalMatches);
        Assert.All(result.Hits, h => Assert.Equal(_bookId, h.BookId));
        Assert.All(result.Hits, h => Assert.Equal("全文搜索测试书", h.BookTitle));
        Assert.Contains(result.Hits, h => h.SpineIndex == 0);
        Assert.Contains(result.Hits, h => h.SpineIndex == 1);
    }

    [Fact]
    public async Task 英文单词能命中()
    {
        await IndexAsync();

        var result = await _search.SearchAsync("hello");

        Assert.True(result.TotalMatches >= 2, "实际 " + result.TotalMatches);
    }

    [Fact]
    public async Task 两字中文词走LIKE兜底也能命中()
    {
        await IndexAsync();

        // 正文里确实有两字的词（"电子"），trigram 对 2 字查询产不出 token，必须靠兜底
        var result = await _search.SearchAsync("电子");

        Assert.True(result.UsedShortQueryFallback, "两字查询必须走兜底");
        Assert.True(result.TotalMatches >= 2, "实际 " + result.TotalMatches);
    }

    [Fact]
    public async Task 搜不到的词返回零_不抛异常()
    {
        await IndexAsync();

        var result = await _search.SearchAsync("量子纠缠弦论");

        Assert.Equal(0, result.TotalMatches);
        Assert.Empty(result.Hits);
    }

    [Fact]
    public async Task 中断后续建_状态为Indexing的书会被重新索引()
    {
        await IndexAsync();

        // 模拟"索引到一半被杀进程"：切片没了、状态停在 Indexing
        await _repo.DeleteBookChunksAsync(_bookId);
        await _repo.SaveStateAsync(new ContentIndexState
        {
            BookId = _bookId,
            Status = ContentIndexStatus.Indexing,
        });

        var rested = new ContentIndexer(
            _repo, _sandbox.Repository, _extractor, NullLogger<ContentIndexer>.Instance);

        try
        {
            await rested.StartAsync();
            Assert.True(await rested.WaitForIdleAsync(TimeSpan.FromSeconds(30)), "续建没走完");
        }
        finally
        {
            await rested.StopAsync();
        }

        var state = await _repo.GetStateAsync(_bookId);
        Assert.Equal(ContentIndexStatus.Done, state!.Status);

        var result = await _search.SearchAsync("电子书");
        Assert.True(result.TotalMatches >= 2, "续建后应能搜到，实际 " + result.TotalMatches);
    }

    [Fact]
    public async Task 索引体积记录在案()
    {
        await IndexAsync();

        var volume = await _repo.MeasureVolumeAsync();

        Assert.True(volume.ChunkCount > 0);
        Assert.True(volume.BodyBytes > 0);
    }

    [Fact]
    public async Task TXT也能索引_章序与阅读器切章一致()
    {
        var txtPath = Path.Combine(_sandbox.SourceDir, "search.txt");
        await File.WriteAllTextAsync(
            txtPath,
            "第1章 开端\n电子书正文甲。\n第2章 继续\nhello world 乙。\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var imported = await _sandbox.Pipeline.ImportAsync(txtPath, new ImportOptions());
        Assert.Equal(ImportOutcome.Imported, imported.Outcome);

        var book = await _sandbox.Repository.GetByIdAsync(imported.BookId!.Value);
        Assert.NotNull(book);

        var chunks = _extractor.Extract(book!, CancellationToken.None);

        Assert.NotEmpty(chunks);
        Assert.Contains(chunks, c => c.SpineIndex == 0 && c.Body.Contains("电子书"));
        Assert.Contains(chunks, c => c.SpineIndex == 1 && c.Body.Contains("hello world"));
        Assert.All(chunks, c => Assert.Equal(imported.BookId!.Value, c.BookId));
    }

    [Fact]
    public async Task PDF正文能被索引_全书搜索能命中_这是T6_2B转交的判据4()
    {
        var pdf = new PdfDocumentService(new PdfiumLibrary());
        var source = PdfTestCorpus.CorpusPath(PdfTestCorpus.TextBook);

        var imported = await _sandbox.Pipeline.ImportAsync(source, new ImportOptions());
        Assert.Equal(ImportOutcome.Imported, imported.Outcome);

        var book = await _sandbox.Repository.GetByIdAsync(imported.BookId!.Value);
        Assert.NotNull(book);

        var extractor = new ContentTextExtractor(pdf, NullLogger<ContentTextExtractor>.Instance);
        var chunks = extractor.Extract(book!, CancellationToken.None);

        Assert.NotEmpty(chunks);
        Assert.All(chunks, c => Assert.Equal(imported.BookId!.Value, c.BookId));
        Assert.Contains(chunks, c => c.ChapterTitle is not null && c.ChapterTitle.StartsWith("第 "));

        // 从已抽出的 PDF 正文里取一段连续的（无空白的）真中文，再拿去搜：
        // 证明命中的确实是从 PDF 文本层索引出来的内容，而不是元数据或占位文本。
        var body = chunks[0].Body;
        var runStart = -1;
        var runLength = 0;

        for (var i = 0; i < body.Length; i++)
        {
            if (char.IsWhiteSpace(body[i]))
            {
                runStart = -1;
                runLength = 0;
                continue;
            }

            if (runStart < 0)
            {
                runStart = i;
            }

            runLength++;

            if (runLength >= 6)
            {
                break;
            }
        }

        Assert.True(runStart >= 0 && runLength >= 6, "PDF 第一片里找不到 6 个连续字符");

        var phrase = body.Substring(runStart, 6);

        await _repo.ReplaceBookChunksAsync(imported.BookId!.Value, chunks);
        await _repo.SaveStateAsync(new ContentIndexState
        {
            BookId = imported.BookId.Value,
            Status = ContentIndexStatus.Done,
            ChunkCount = chunks.Count,
        });

        var result = await _search.SearchAsync(phrase, imported.BookId);

        Assert.True(result.TotalMatches > 0, "PDF 正文里取出的「" + phrase + "」搜不到");
    }

    private async Task IndexAsync()
    {
        await _indexer.StartAsync();
        Assert.True(await _indexer.WaitForIdleAsync(TimeSpan.FromSeconds(30)), "索引没走完");
    }
}
