using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Infrastructure.Data.Repositories;

namespace EbookToolSuite.Infrastructure.Tests.Data;

/// <summary>
/// T6-4 正文索引仓储。这里验证的是**数据库事实**（FTS5 + trigram 的真实行为），
/// 不是我们对它的想象：
/// <list type="bullet">
/// <item>三字中文与英文单词能 MATCH 到（trigram 窗口 ≥3）；</item>
/// <item>两字中文 MATCH 命中不了 —— 这就是服务层必须用 LIKE 兜底的原因；</item>
/// <item>触发器能把 content_chunk 的增删改同步进外部内容表。</item>
/// </list>
/// </summary>
public sealed class ContentRepositoryTests
{
    private static readonly Guid BookId = Guid.Parse("aaaaaaaa-1111-2222-3333-444444444444");

    private static ContentRepository Repo(DataSandbox sb) => new(sb.Factory);

    private static async Task SeedBookAsync(DataSandbox sb)
    {
        await sb.RawExecuteAsync(
            "INSERT INTO books (id, title, rating, reading_status, damaged, created_at, updated_at) "
            + $"VALUES ('{BookId}', '全文搜索测试书', 0, 0, 0, 1, 1);");
    }

    private static ContentChunk Chunk(int spine, string body) => new()
    {
        BookId = BookId,
        SpineIndex = spine,
        ChapterTitle = "第 " + (spine + 1) + " 章",
        ChapterHref = "ch" + (spine + 1) + ".xhtml",
        Body = body,
    };

    [Fact]
    public async Task 迁移建出正文搜索的三张表与触发器()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();

        var objects = await sb.QueryStringsAsync(
            "SELECT name FROM sqlite_master WHERE type IN ('table','trigger') "
            + "AND (name LIKE 'content_%' OR name LIKE 'content_chunk_%') ORDER BY name;");

        Assert.Contains("content_chunk", objects);
        Assert.Contains("content_fts", objects);
        Assert.Contains("content_index_state", objects);
        Assert.Contains("content_chunk_ai", objects);
        Assert.Contains("content_chunk_ad", objects);

        // FTS5 真的是虚拟表
        var sql = await sb.QueryStringsAsync(
            "SELECT sql FROM sqlite_master WHERE name = 'content_fts';");

        Assert.Contains("VIRTUAL TABLE", sql[0]);
        Assert.Contains("trigram", sql[0]);
    }

    [Fact]
    public async Task 三字中文与英文单词_FTS路径能命中()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();
        await SeedBookAsync(sb);

        var repo = Repo(sb);

        await repo.ReplaceBookChunksAsync(BookId, new[]
        {
            Chunk(0, "这是一本讲电子书与阅读器的书，hello world 也在这里。"),
            Chunk(1, "第二章只有别的内容。"),
        });

        var chinese = await repo.SearchAsync(new ContentSearchQuery
        {
            Terms = new[] { "电子书" },
            UseLikeFallback = false,
        });

        Assert.Equal(1, chinese.TotalMatches);
        Assert.Equal(0, chinese.Hits[0].SpineIndex);
        Assert.Equal("全文搜索测试书", chinese.Hits[0].BookTitle);

        var english = await repo.SearchAsync(new ContentSearchQuery
        {
            Terms = new[] { "hello" },
            UseLikeFallback = false,
        });

        Assert.Equal(1, english.TotalMatches);
    }

    [Fact]
    public async Task 两字中文_FTS命中不了_这是LIKE兜底存在的理由()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();
        await SeedBookAsync(sb);

        var repo = Repo(sb);

        await repo.ReplaceBookChunksAsync(BookId, new[]
        {
            Chunk(0, "这是一段测试正文。"),
        });

        // 强制走 FTS：证明 trigram 对 2 字符查询产不出 token
        var fts = await repo.SearchAsync(new ContentSearchQuery
        {
            Terms = new[] { "测试" },
            UseLikeFallback = false,
        });

        Assert.Equal(0, fts.TotalMatches);

        // 走 LIKE 兜底：同一条查询必须命中
        var like = await repo.SearchAsync(new ContentSearchQuery
        {
            Terms = new[] { "测试" },
            UseLikeFallback = true,
        });

        Assert.Equal(1, like.TotalMatches);
        Assert.True(like.UsedShortQueryFallback);
    }

    [Fact]
    public async Task 词间是AND_多个词必须同时命中同一片()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();
        await SeedBookAsync(sb);

        var repo = Repo(sb);

        await repo.ReplaceBookChunksAsync(BookId, new[]
        {
            Chunk(0, "电子书与阅读器一起出现。"),
            Chunk(1, "这一片只有阅读器，没有前者。"),
        });

        var both = await repo.SearchAsync(new ContentSearchQuery
        {
            Terms = new[] { "电子书", "阅读器" },
            UseLikeFallback = false,
        });

        Assert.Equal(1, both.TotalMatches);
        Assert.Equal(0, both.Hits[0].SpineIndex);
    }

    [Fact]
    public async Task 只在这本书里搜_其它书不出现()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();
        await SeedBookAsync(sb);

        var other = Guid.Parse("bbbbbbbb-1111-2222-3333-444444444444");
        await sb.RawExecuteAsync(
            "INSERT INTO books (id, title, rating, reading_status, damaged, created_at, updated_at) "
            + $"VALUES ('{other}', '另一本书', 0, 0, 0, 1, 1);");

        var repo = Repo(sb);

        await repo.ReplaceBookChunksAsync(BookId, new[] { Chunk(0, "电子书在这里。") });
        await repo.ReplaceBookChunksAsync(other, new[]
        {
            new ContentChunk
            {
                BookId = other,
                SpineIndex = 0,
                ChapterTitle = "别的章",
                ChapterHref = "x.xhtml",
                Body = "电子书也在另一本里。",
            },
        });

        var scoped = await repo.SearchAsync(new ContentSearchQuery
        {
            Terms = new[] { "电子书" },
            BookId = BookId,
            UseLikeFallback = false,
        });

        Assert.Equal(1, scoped.TotalMatches);
        Assert.All(scoped.Hits, h => Assert.Equal(BookId, h.BookId));
    }

    [Fact]
    public async Task 删掉切片后_触发器把FTS里的也删干净()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();
        await SeedBookAsync(sb);

        var repo = Repo(sb);

        await repo.ReplaceBookChunksAsync(BookId, new[] { Chunk(0, "电子书在这里。") });

        var before = await repo.SearchAsync(new ContentSearchQuery
        {
            Terms = new[] { "电子书" },
            UseLikeFallback = false,
        });

        Assert.Equal(1, before.TotalMatches);

        await repo.DeleteBookChunksAsync(BookId);

        var after = await repo.SearchAsync(new ContentSearchQuery
        {
            Terms = new[] { "电子书" },
            UseLikeFallback = false,
        });

        Assert.Equal(0, after.TotalMatches);
    }

    [Fact]
    public async Task 索引状态可以落库并读回_需要重建的书能被列出来()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();
        await SeedBookAsync(sb);

        var repo = Repo(sb);

        // 没有状态行 = 需要索引
        var missing = await repo.ListBookIdsNeedingIndexAsync();
        Assert.Contains(BookId, missing);

        await repo.SaveStateAsync(new ContentIndexState
        {
            BookId = BookId,
            Status = ContentIndexStatus.Done,
            ChunkCount = 3,
        });

        Assert.DoesNotContain(BookId, await repo.ListBookIdsNeedingIndexAsync());

        var state = await repo.GetStateAsync(BookId);
        Assert.NotNull(state);
        Assert.Equal(ContentIndexStatus.Done, state!.Status);
        Assert.Equal(3, state.ChunkCount);

        // 中断留下的 Indexing 也算待建
        await repo.SaveStateAsync(new ContentIndexState
        {
            BookId = BookId,
            Status = ContentIndexStatus.Indexing,
        });

        Assert.Contains(BookId, await repo.ListBookIdsNeedingIndexAsync());
    }

    [Fact]
    public async Task 索引体积可量_用于判据8的磁盘预算()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();
        await SeedBookAsync(sb);

        var repo = Repo(sb);

        await repo.ReplaceBookChunksAsync(BookId, new[]
        {
            Chunk(0, "电子书在这里，这一片有一百来个字，用来量体积。"),
            Chunk(1, "第二片也有字。"),
        });

        var volume = await repo.MeasureVolumeAsync();

        Assert.Equal(2, volume.ChunkCount);
        Assert.True(volume.BodyBytes > 0);
        // FTS 体积靠 dbstat（可选编译项），拿不到时是 0；这里不断言它一定 > 0，
        // 但总量必须至少等于正文体积
        Assert.True(volume.TotalBytes >= volume.BodyBytes);
    }
}
