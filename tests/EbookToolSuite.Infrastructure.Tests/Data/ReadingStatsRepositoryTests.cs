using EbookToolSuite.Core.Common;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Infrastructure.Tests.Data;

/// <summary>
/// T7-3B 阅读会话仓储。验证的是数据库事实：会话能落盘并原样读回、
/// 心跳能改收尾时间与累计量、删书级联带走会话，以及统计页要的三条只读查询。
/// 真实 SQLite 文件（<see cref="DataSandbox"/>），不 mock。
/// </summary>
public sealed class ReadingStatsRepositoryTests
{
    private static readonly Guid BookA = Guid.Parse("aaaaaaaa-1111-2222-3333-444444444444");

    private static readonly Guid BookB = Guid.Parse("bbbbbbbb-1111-2222-3333-444444444444");

    private static readonly Guid BookC = Guid.Parse("cccccccc-1111-2222-3333-444444444444");

    /// <summary>固定时间基点，避免测试依赖当前时钟（Unix 毫秒，与库口径一致）。</summary>
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);

    /// <summary>三本书：A 在读、B 未读、C 已读完，统计分布与"最近在读"都用到。</summary>
    private static async Task SeedBooksAsync(DataSandbox sb)
    {
        await sb.RawExecuteAsync(
            "INSERT INTO books (id, title, rating, reading_status, damaged, created_at, updated_at) VALUES "
            + $"('{BookA}', '统计书 A', 0, 1, 0, 1, 1),"
            + $"('{BookB}', '统计书 B', 0, 0, 0, 1, 1),"
            + $"('{BookC}', '统计书 C', 5, 2, 0, 1, 1);");
    }

    [Fact]
    public async Task 插入会话_返回自增id且字段往返一致()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();
        await SeedBooksAsync(sb);

        var repo = sb.StatsRepository;

        var firstId = await repo.InsertSessionAsync(
            new ReadingSessionWrite(BookA, T0, T0.AddMinutes(30), 12, 3456));
        var secondId = await repo.InsertSessionAsync(
            new ReadingSessionWrite(BookA, T0.AddHours(1), T0.AddHours(1).AddMinutes(5), 1, 100));

        Assert.True(firstId > 0);
        Assert.True(secondId > firstId, "AUTOINCREMENT 必须严格递增");

        var sessions = await repo.ListSessionsAsync();

        Assert.Equal(2, sessions.Count);

        // 按 started_at 升序：先开始的在前
        var first = sessions[0];
        Assert.Equal(firstId, first.Id);
        Assert.Equal(BookA, first.BookId);
        Assert.Equal(T0, first.StartedAt);
        Assert.Equal(T0.AddMinutes(30), first.EndedAt);
        Assert.Equal(12, first.PagesRead);
        Assert.Equal(3456, first.CharsRead);
        // 时长 = ended_at - started_at（口径见 docs/DECISIONS.md T7-3B）
        Assert.Equal(TimeSpan.FromMinutes(30), first.Duration);

        Assert.Equal(secondId, sessions[1].Id);
    }

    [Fact]
    public async Task 更新会话_改收尾时间与累计量_不存在id返回false()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();
        await SeedBooksAsync(sb);

        var repo = sb.StatsRepository;
        var id = await repo.InsertSessionAsync(
            new ReadingSessionWrite(BookA, T0, T0.AddMinutes(1), 0, 0));

        // 心跳形态：ended_at 推到"最后一次阅读事件"，两个累计量一起改
        Assert.True(await repo.UpdateSessionAsync(id, T0.AddMinutes(45), 20, 8888));

        var saved = (await repo.ListSessionsAsync()).Single();
        Assert.Equal(T0.AddMinutes(45), saved.EndedAt);
        Assert.Equal(20, saved.PagesRead);
        Assert.Equal(8888L, saved.CharsRead);
        Assert.Equal(TimeSpan.FromMinutes(45), saved.Duration);

        // 会话不存在（比如书被删、级联带走了）：静默 false，不抛
        Assert.False(await repo.UpdateSessionAsync(id + 1, T0.AddMinutes(50), 1, 1));
    }

    [Fact]
    public async Task 删书后它的会话会跟着级联消失()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();
        await SeedBooksAsync(sb);

        var repo = sb.StatsRepository;

        await repo.InsertSessionAsync(new ReadingSessionWrite(BookA, T0, T0.AddMinutes(10), 2, 200));
        await repo.InsertSessionAsync(new ReadingSessionWrite(BookA, T0.AddHours(1), T0.AddHours(2), 5, 500));
        await repo.InsertSessionAsync(new ReadingSessionWrite(BookB, T0, T0.AddMinutes(7), 1, 70));

        Assert.True(await sb.Repository.DeleteAsync(BookA));

        var left = await repo.ListSessionsAsync();
        Assert.Single(left);
        Assert.Equal(BookB, left[0].BookId);
    }

    [Fact]
    public async Task 书库阅读状态_没进过阅读器的书最后阅读时间为空()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();
        await SeedBooksAsync(sb);

        var lastRead = T0.AddHours(2);
        await sb.RawExecuteAsync(
            "INSERT INTO reading_progress (book_id, spine_index, percent, updated_at) "
            + $"VALUES ('{BookA}', 1, 0.4, {UnixTime.ToMillis(lastRead)});");

        var states = (await sb.StatsRepository.ListBookReadingStatesAsync())
            .ToDictionary(s => s.BookId);

        // 没进过阅读器的书也必须出现（LEFT JOIN，不是只统计读过的）
        Assert.Equal(3, states.Count);
        Assert.Equal(ReadingStatus.Reading, states[BookA].Status);
        Assert.Equal(lastRead, states[BookA].LastReadAt);
        Assert.Equal(ReadingStatus.Unread, states[BookB].Status);
        Assert.Null(states[BookB].LastReadAt);
        Assert.Equal(ReadingStatus.Finished, states[BookC].Status);
        Assert.Null(states[BookC].LastReadAt);
    }

    [Fact]
    public async Task 格式分布按文件份数统计_一本多格式各算一次()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();
        await SeedBooksAsync(sb);

        await sb.RawExecuteAsync(
            "INSERT INTO book_formats (id, book_id, format, file_path, file_size, missing, added_at) VALUES "
            + $"('{Guid.NewGuid()}', '{BookA}', 'EPUB', 'a.epub', 10, 0, 1),"
            + $"('{Guid.NewGuid()}', '{BookA}', 'PDF', 'a.pdf', 20, 0, 1),"
            + $"('{Guid.NewGuid()}', '{BookB}', 'EPUB', 'b.epub', 30, 0, 1),"
            + $"('{Guid.NewGuid()}', '{BookB}', 'AZW', 'b.azw', 40, 0, 1);");

        var counts = (await sb.StatsRepository.ListFormatCountsAsync())
            .ToDictionary(c => c.Format, c => c.Count);

        Assert.Equal(3, counts.Count);
        Assert.Equal(2, counts[BookFormatKind.Epub]);   // A 与 B 各一份
        Assert.Equal(1, counts[BookFormatKind.Pdf]);
        Assert.Equal(1, counts[BookFormatKind.Mobi]);   // AZW 是 MOBI 的别名，归并成同一枚举
    }

    [Fact]
    public async Task 最近在读按最后阅读时间倒序_最多返回指定本数()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();
        await SeedBooksAsync(sb);

        var readAtA = T0.AddHours(3);
        var readAtB = T0.AddHours(1);
        var readAtC = T0.AddHours(2);
        await sb.RawExecuteAsync(
            "INSERT INTO reading_progress (book_id, spine_index, percent, updated_at) VALUES "
            + $"('{BookA}', 1, 0.4, {UnixTime.ToMillis(readAtA)}),"
            + $"('{BookB}', 2, 0.2, {UnixTime.ToMillis(readAtB)}),"
            + $"('{BookC}', 3, 0.6, {UnixTime.ToMillis(readAtC)});");

        var repo = sb.StatsRepository;

        var top2 = await repo.ListRecentBooksAsync(take: 2);
        Assert.Equal(2, top2.Count);
        Assert.Equal(BookA, top2[0].BookId);
        Assert.Equal("统计书 A", top2[0].Title);
        Assert.Equal(0.4, top2[0].Percent);
        Assert.Equal(readAtA, top2[0].LastReadAt);
        Assert.Equal(BookC, top2[1].BookId);

        var all = await repo.ListRecentBooksAsync();
        Assert.Equal(new[] { BookA, BookC, BookB }, all.Select(b => b.BookId));

        // LIMIT 负数在 SQLite 里是"不限"，take<=0 必须在仓储里就挡掉
        Assert.Empty(await repo.ListRecentBooksAsync(take: 0));
        Assert.Empty(await repo.ListRecentBooksAsync(take: -1));
    }

    [Fact]
    public async Task 总本数统计全部书()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();

        Assert.Equal(0, await sb.StatsRepository.CountBooksAsync());

        await SeedBooksAsync(sb);

        Assert.Equal(3, await sb.StatsRepository.CountBooksAsync());
    }

    [Fact]
    public async Task 没有会话时_会话列表为空()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();
        await SeedBooksAsync(sb);

        Assert.Empty(await sb.StatsRepository.ListSessionsAsync());
        Assert.Empty(await sb.StatsRepository.ListFormatCountsAsync());
        Assert.Empty(await sb.StatsRepository.ListRecentBooksAsync());
    }
}
