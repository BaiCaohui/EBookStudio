using EbookToolSuite.Core.Common;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Infrastructure.Data.Repositories;

using EbookToolSuite.Infrastructure.Data;

namespace EbookToolSuite.Infrastructure.Tests.Data;

/// <summary>
/// operation_log（T3-6）。真 SQLite、真 SQL，覆盖迁移 0005 建出来的表与仓储的读写。
/// </summary>
public sealed class OperationLogRepositoryTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);

    [Fact]
    public async Task 首次初始化_operation_log_表在()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();

        var tables = await sb.QueryStringsAsync(
            "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;");

        Assert.Contains("operation_log", tables);
        Assert.Equal(SchemaMigrator.TargetVersion, await sb.Database.GetSchemaVersionAsync());
    }

    [Fact]
    public async Task 写入后能读回来_字段一个不差()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();

        var repository = new OperationLogRepository(sb.Factory);
        var bookId = Guid.NewGuid();
        var formatId = Guid.NewGuid();

        var id = await repository.AddAsync(new OperationLogEntry(
            OperationNames.Save,
            "保存《测试书》",
            bookId,
            formatId,
            "2026-10-08T13-00-00_ab12",
            "{\"outcome\":\"Pass\"}",
            T0));

        Assert.True(id > 0);

        var rows = await repository.ListRecentAsync();
        var entry = Assert.Single(rows);

        Assert.Equal(id, entry.Id);
        Assert.Equal(OperationNames.Save, entry.Operation);
        Assert.Equal("保存《测试书》", entry.Summary);
        Assert.Equal(bookId, entry.BookId);
        Assert.Equal(formatId, entry.FormatId);
        Assert.Equal("2026-10-08T13-00-00_ab12", entry.RestorePointId);
        Assert.Equal("{\"outcome\":\"Pass\"}", entry.Detail);
        Assert.Equal(T0, entry.At);
    }

    [Fact]
    public async Task 最近列表按时间倒序_并按数量截断()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();

        var repository = new OperationLogRepository(sb.Factory);
        for (var i = 0; i < 5; i++)
        {
            await repository.AddAsync(new OperationLogEntry(
                OperationNames.Save,
                "第 " + i + " 条",
                At: T0.AddMinutes(i)));
        }

        var recent = await repository.ListRecentAsync(3);

        Assert.Equal(3, recent.Count);
        Assert.Equal("第 4 条", recent[0].Summary);
        Assert.Equal("第 2 条", recent[2].Summary);
        Assert.Equal(5, await repository.CountAsync());
    }

    [Fact]
    public async Task 按书筛选_只返回这本书的()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();

        var repository = new OperationLogRepository(sb.Factory);
        var wanted = Guid.NewGuid();
        var other = Guid.NewGuid();

        await repository.AddAsync(new OperationLogEntry(OperationNames.Save, "要的 1", wanted, At: T0));
        await repository.AddAsync(new OperationLogEntry(OperationNames.SaveForced, "不要的", other, At: T0.AddMinutes(1)));
        await repository.AddAsync(new OperationLogEntry(OperationNames.Restore, "要的 2", wanted, At: T0.AddMinutes(2)));

        var rows = await repository.ListForBookAsync(wanted);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, entry => Assert.Equal(wanted, entry.BookId));
        Assert.Equal("要的 2", rows[0].Summary);
    }

    [Fact]
    public async Task 没有书号的日志也能写能读()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();

        var repository = new OperationLogRepository(sb.Factory);
        await repository.AddAsync(new OperationLogEntry(OperationNames.Prune, "清理了 2 个还原点", At: T0));

        var entry = Assert.Single(await repository.ListRecentAsync());
        Assert.Null(entry.BookId);
        Assert.Null(entry.FormatId);
        Assert.Null(entry.RestorePointId);
    }
}
