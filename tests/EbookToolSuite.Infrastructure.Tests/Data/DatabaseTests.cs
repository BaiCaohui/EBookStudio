using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace EbookToolSuite.Infrastructure.Tests.Data;

public sealed class DatabaseTests
{
    private static readonly string[] ExpectedTables =
        ["import_batch", "books", "book_formats", "reading_progress", "duplicate_copy", "duplicate_ignore",
         "conversion_tasks", "conversion_templates", "operation_log", "reading_sessions"];

    [Fact]
    public async Task 首次初始化_建库并建齐核心表()
    {
        await using var sb = new DataSandbox();

        Assert.False(File.Exists(sb.DbFile), "建库前不应该有 Library.db");

        await sb.Database.InitializeAsync();

        Assert.True(File.Exists(sb.DbFile), "初始化后应该生成 Library.db");

        var tables = await sb.QueryStringsAsync(
            "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;");

        foreach (var t in ExpectedTables)
        {
            Assert.Contains(t, tables);
        }

        Assert.Equal(SchemaMigrator.TargetVersion, await sb.Database.GetSchemaVersionAsync());
    }

    [Fact]
    public async Task 初始化后_journal_mode_为_wal()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();

        var mode = await sb.RawScalarAsync<string>("PRAGMA journal_mode;");

        Assert.Equal("wal", mode);
    }

    [Fact]
    public async Task 再次初始化_版本不变且数据不丢()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();

        await sb.RawExecuteAsync(
            "INSERT INTO books (id, title, rating, reading_status, damaged, created_at, updated_at) "
            + "VALUES ('11111111-1111-1111-1111-111111111111', '旧书', 0, 0, 0, 1, 1);");

        await sb.Database.InitializeAsync();

        Assert.Equal(SchemaMigrator.TargetVersion, await sb.Database.GetSchemaVersionAsync());
        Assert.Equal(1, await sb.RawScalarAsync<int>("SELECT COUNT(1) FROM books;"));
    }

    [Fact]
    public async Task 从v9升级_建出会话表与索引且旧数据不丢()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();

        // 造一点"升级前就有"的书库数据：v9→v10 只许新增表，旧行一行都不许动
        await sb.RawExecuteAsync(
            "INSERT INTO books (id, title, rating, reading_status, damaged, created_at, updated_at) "
            + "VALUES ('55555555-5555-5555-5555-555555555555', 'v9 的书', 5, 2, 0, 1, 1);");
        await sb.RawExecuteAsync(
            "INSERT INTO reading_progress (book_id, spine_index, percent, updated_at) "
            + "VALUES ('55555555-5555-5555-5555-555555555555', 2, 0.5, 1700000000000);");

        // 模拟一个停在 v9 的旧库：0010 的表与索引都还没有，user_version = 9
        await sb.RawExecuteAsync("DROP INDEX IF EXISTS idx_reading_sessions_book;");
        await sb.RawExecuteAsync("DROP INDEX IF EXISTS idx_reading_sessions_started;");
        await sb.RawExecuteAsync("DROP TABLE IF EXISTS reading_sessions;");
        await sb.RawExecuteAsync("PRAGMA user_version = 9;");

        Assert.Equal(9, await sb.Database.GetSchemaVersionAsync());

        await sb.Database.InitializeAsync();

        // 版本推进到当前目标（不是只到"表存在"就算）：
        // 停在 v9 的库升级时会连 0010（会话表）与 0011（阅读格式旁表）一起跑。
        Assert.Equal(SchemaMigrator.TargetVersion, await sb.Database.GetSchemaVersionAsync());

        var tables = await sb.QueryStringsAsync(
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'reading_sessions';");
        Assert.Equal(new[] { "reading_sessions" }, tables);

        var formatTables = await sb.QueryStringsAsync(
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'book_reading_format';");
        Assert.Equal(new[] { "book_reading_format" }, formatTables);

        var indexes = await sb.QueryStringsAsync(
            "SELECT name FROM sqlite_master WHERE type = 'index' "
            + "AND name IN ('idx_reading_sessions_book','idx_reading_sessions_started') ORDER BY name;");
        Assert.Equal(new[] { "idx_reading_sessions_book", "idx_reading_sessions_started" }, indexes);

        // 旧数据原样还在
        Assert.Equal(1, await sb.RawScalarAsync<int>("SELECT COUNT(1) FROM books;"));
        Assert.Equal("v9 的书", await sb.RawScalarAsync<string>("SELECT title FROM books LIMIT 1;"));
        Assert.Equal(5, await sb.RawScalarAsync<int>("SELECT rating FROM books LIMIT 1;"));
        Assert.Equal(0.5, await sb.RawScalarAsync<double>("SELECT percent FROM reading_progress LIMIT 1;"));

        // 升级出来的表能直接写（自增 id、外键、列都齐）
        var sessionId = await sb.StatsRepository.InsertSessionAsync(new ReadingSessionWrite(
            Guid.Parse("55555555-5555-5555-5555-555555555555"),
            DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000),
            DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_060_000),
            3,
            120));

        Assert.True(sessionId > 0);
        Assert.Single(await sb.StatsRepository.ListSessionsAsync());
    }

    [Fact]
    public async Task user_version_归零后重建_表结构与数据都还在()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();

        await sb.RawExecuteAsync(
            "INSERT INTO books (id, title, rating, reading_status, damaged, created_at, updated_at) "
            + "VALUES ('22222222-2222-2222-2222-222222222222', '保留我', 5, 2, 0, 100, 100);");

        // 模拟「迁移记录丢了但表还在」的现场
        await sb.RawExecuteAsync("PRAGMA user_version = 0;");
        Assert.Equal(0, await sb.Database.GetSchemaVersionAsync());

        await sb.Database.InitializeAsync();

        Assert.Equal(SchemaMigrator.TargetVersion, await sb.Database.GetSchemaVersionAsync());
        Assert.Equal(1, await sb.RawScalarAsync<int>(
            "SELECT COUNT(1) FROM books WHERE title = '保留我';"));
        Assert.Equal(5, await sb.RawScalarAsync<int>(
            "SELECT rating FROM books WHERE id = '22222222-2222-2222-2222-222222222222';"));
    }

    [Fact]
    public async Task 数据库版本高于程序支持版本_抛AppException且不动数据()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();

        await sb.RawExecuteAsync("PRAGMA user_version = 99;");

        var ex = await Assert.ThrowsAsync<AppException>(
            () => sb.Database.InitializeAsync());

        Assert.Equal(ErrorCode.InternalError, ex.Code);
        Assert.Equal(99, await sb.Database.GetSchemaVersionAsync());
    }

    [Fact]
    public async Task 外键约束生效_引用了不存在的批次会被拒()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();

        // 批次不存在 -> 违反外键
        var ex = await Assert.ThrowsAsync<SqliteException>(() => sb.RawExecuteAsync(
            "INSERT INTO books (id, title, rating, reading_status, damaged, import_batch_id, created_at, updated_at) "
            + "VALUES ('33333333-3333-3333-3333-333333333333', '坏书', 0, 0, 0, 'no-such-batch', 1, 1);"));

        Assert.Contains("FOREIGN KEY", ex.Message, StringComparison.OrdinalIgnoreCase);

        // 批次为空 -> 允许（导入批次是可选归属）
        await sb.RawExecuteAsync(
            "INSERT INTO books (id, title, rating, reading_status, damaged, import_batch_id, created_at, updated_at) "
            + "VALUES ('44444444-4444-4444-4444-444444444444', '好书', 0, 0, 0, NULL, 1, 1);");

        Assert.Equal(1, await sb.RawScalarAsync<int>("SELECT COUNT(1) FROM books;"));
    }

    [Fact]
    public async Task 每条连接都开着外键开关()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();

        // 外键是连接级开关，漏设会让级联删除静默失效
        Assert.Equal(1, await sb.RawScalarAsync<int>("PRAGMA foreign_keys;"));
    }
}
