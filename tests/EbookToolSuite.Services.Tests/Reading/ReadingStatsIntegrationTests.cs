using System.Diagnostics;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Infrastructure.Config;
using EbookToolSuite.Infrastructure.Data;
using EbookToolSuite.Infrastructure.Data.Repositories;
using EbookToolSuite.Services.Reading;
using EbookToolSuite.Services.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace EbookToolSuite.Services.Tests.Reading;

/// <summary>
/// T7-3B 的集成判据（真实 SQLite + 真实仓储 + 真实聚合）：
/// 1. 1000 本书 + 一年约 5 万条会话时，统计查询首屏 ≤ 300 ms；
/// 2. 删书后会话随级联消失，汇总数字跟着变。
/// 纯口径的正确性在 ReadingStatsAggregatorTests 里，这里只验"接到真库上还成立"。
/// </summary>
public sealed class ReadingStatsIntegrationTests
{
    /// <summary>执行计划 T7-3B 判据：统计页首屏 ≤ 300 ms。</summary>
    private const int FirstPaintBudgetMs = 300;

    private readonly ITestOutputHelper _output;

    public ReadingStatsIntegrationTests(ITestOutputHelper output) => _output = output;

    private static ReadingStatsService NewService(ImportSandbox sandbox) => new(
        new ReadingStatsRepository(sandbox.Factory),
        SystemClock.Instance,
        NullLogger<ReadingStatsService>.Instance);

    [Fact]
    public async Task 统计查询_1000本书5万会话_首屏不超过300毫秒()
    {
        await using var sandbox = new ImportSandbox();
        await sandbox.InitializeAsync();

        await SeedLargeLibraryAsync(sandbox);

        var service = NewService(sandbox);

        // 先跑一次把 JIT / 连接池预热掉，量的是"暖"查询；页面每次切范围走的也是这条路径。
        _ = await service.GetAsync(ReadingStatsRange.All);

        var watch = Stopwatch.StartNew();
        var snapshot = await service.GetAsync(ReadingStatsRange.All);
        watch.Stop();

        _output.WriteLine(
            $"1000 本书 / 50000 条会话：GetAsync(All) = {watch.ElapsedMilliseconds} ms，"
            + $"总时长 {snapshot.Totals.Duration.TotalHours:F0} 小时");

        Assert.Equal(1000, snapshot.TotalBooks);
        Assert.True(snapshot.HasSessions);
        Assert.Equal(50000, await sandbox.Factory.WithSharedReadAsync(
            async conn => await conn.ScalarAsync<int>("SELECT COUNT(1) FROM reading_sessions;")));
        // 每条会话 2 分钟，跨日拆分不改变总时长
        var expectedHours = 120_000d * 50_000 / 3_600_000;
        Assert.Equal(expectedHours, snapshot.Totals.Duration.TotalHours, 0.01);

        Assert.True(
            watch.ElapsedMilliseconds <= FirstPaintBudgetMs,
            $"统计首屏 {watch.ElapsedMilliseconds} ms，超过 {FirstPaintBudgetMs} ms 门槛");
    }

    [Fact]
    public async Task 删除一本书_会话级联消失_汇总数字跟着变()
    {
        await using var sandbox = new ImportSandbox();
        await sandbox.InitializeAsync();

        var now = DateTimeOffset.Now;
        var day = now.AddHours(-1);

        var (bookA, bookB) = (NewBook("留着的书"), NewBook("要删的书"));
        await sandbox.Repository.AddAsync(bookA);
        await sandbox.Repository.AddAsync(bookB);

        var repository = new ReadingStatsRepository(sandbox.Factory);
        await repository.InsertSessionAsync(
            new ReadingSessionWrite(bookA.Id, day, day.AddMinutes(10), 3, 900));
        await repository.InsertSessionAsync(
            new ReadingSessionWrite(bookB.Id, day, day.AddMinutes(20), 5, 1500));

        var service = NewService(sandbox);

        var before = await service.GetAsync(ReadingStatsRange.All);
        Assert.Equal(2, (await repository.ListSessionsAsync()).Count);
        Assert.Equal(TimeSpan.FromMinutes(30), before.Totals.Duration);

        // 走真实的删书入口（BookDeletionService 内部最后就是 DELETE FROM books）
        await sandbox.Repository.DeleteAsync(bookB.Id);

        var after = await service.GetAsync(ReadingStatsRange.All);
        var remaining = await repository.ListSessionsAsync();

        Assert.Single(remaining);
        Assert.Equal(bookA.Id, remaining[0].BookId);
        Assert.Equal(TimeSpan.FromMinutes(10), after.Totals.Duration);
    }

    // ---------- 造数据 ----------

    /// <summary>
    /// 用一条递归 CTE 批量铺数据（逐条 INSERT 五万次会把测试拖成分钟级）：
    /// 1000 本书（GUID 用固定格式的数字串，Guid.Parse 能读），
    /// 50000 条会话均匀分布在过去 365 天，每条 2 分钟。
    /// </summary>
    private static async Task SeedLargeLibraryAsync(ImportSandbox sandbox)
    {
        var now = DateTimeOffset.UtcNow;
        var baseMs = now.AddDays(-365).ToUnixTimeMilliseconds();
        var stepMs = (long)(TimeSpan.FromDays(365).TotalMilliseconds / 50_000);

        await using var conn = await sandbox.Factory.OpenAsync();

        await conn.ExecuteAsync(
            """
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 1000)
            INSERT INTO books (id, title, rating, reading_status, damaged, created_at, updated_at)
            SELECT '00000000-0000-0000-0000-' || printf('%012d', i),
                   '统计书 ' || i, 0, (i % 4), 0, 1700000000000, 1700000000000
            FROM n;
            """);

        await conn.ExecuteAsync(
            """
            WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i + 1 FROM n WHERE i < 49999)
            INSERT INTO reading_sessions (book_id, started_at, ended_at, pages_read, chars_read)
            SELECT '00000000-0000-0000-0000-' || printf('%012d', (i % 1000) + 1),
                   @base + i * @step, @base + i * @step + 120000, 2, 800
            FROM n;
            """,
            new[] { SqliteExtensions.P("@base", baseMs), SqliteExtensions.P("@step", stepMs) });
    }

    private static Book NewBook(string title) => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        CreatedAt = DateTimeOffset.Now,
        UpdatedAt = DateTimeOffset.Now,
        Status = ReadingStatus.Reading,
    };
}
