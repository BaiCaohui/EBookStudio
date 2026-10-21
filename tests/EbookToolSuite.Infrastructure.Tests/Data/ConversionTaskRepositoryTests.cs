using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Infrastructure.Data;
using EbookToolSuite.Infrastructure.Data.Repositories;
using Microsoft.Data.Sqlite;

namespace EbookToolSuite.Infrastructure.Tests.Data;

/// <summary>
/// conversion_tasks 仓储的真库测试（执行计划 T2-4）。
/// 这一层的价值就在 SQL 本身（列名、列序、状态过滤），所以不 mock，建真库真表跑。
/// </summary>
public sealed class ConversionTaskRepositoryTests
{
    private static readonly Guid BookId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static async Task AddBookAsync(DataSandbox sb)
    {
        await using var conn = await sb.Factory.OpenAsync();
        await conn.ExecuteAsync(
            "INSERT INTO books (id, title, rating, reading_status, damaged, created_at, updated_at) "
            + "VALUES (@id, @title, 0, 0, 0, 1, 1);",
            new[]
            {
                new SqliteParameter("@id", BookId.ToString()),
                new SqliteParameter("@title", "测试书"),
            });
    }

    private static ConversionTask NewTask(
        ConversionTaskStatus status = ConversionTaskStatus.Queued,
        string inputPath = @"C:\books\a.epub",
        DateTimeOffset? createdAt = null) => new()
    {
        Id = Guid.NewGuid(),
        BookId = BookId,
        InputPath = inputPath,
        OutputPath = @"C:\out\a.mobi",
        OutFormat = BookFormatKind.Mobi,
        TemplateId = "kindle-best",
        ArgumentsJson = "[]",
        Status = status,
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task 增删查改_字段整行往返()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();
        await AddBookAsync(sb);

        var repo = new ConversionTaskRepository(sb.Factory);
        var task = NewTask();
        await repo.AddAsync(task);

        var loaded = await repo.GetByIdAsync(task.Id);
        Assert.NotNull(loaded);
        Assert.Equal(task.Id, loaded!.Id);
        Assert.Equal(BookId, loaded.BookId);
        Assert.Equal(@"C:\books\a.epub", loaded.InputPath);
        Assert.Equal(@"C:\out\a.mobi", loaded.OutputPath);
        Assert.Equal(BookFormatKind.Mobi, loaded.OutFormat);
        Assert.Equal("kindle-best", loaded.TemplateId);
        Assert.Equal("[]", loaded.ArgumentsJson);
        Assert.Equal(ConversionTaskStatus.Queued, loaded.Status);
        Assert.Equal("测试书", loaded.BookTitle);
        Assert.Equal(BookFormatKind.Epub, loaded.SourceFormat);
        Assert.Null(loaded.Progress);
        Assert.Equal(0, loaded.RetryCount);
        Assert.Null(loaded.StartedAt);
        Assert.Null(loaded.FinishedAt);

        // 状态、进度、错误、重试计数一起改，整行写回
        loaded.Status = ConversionTaskStatus.Failed;
        loaded.Progress = 0.42;
        loaded.ProgressKind = ConversionTaskProgressKind.Percent;
        loaded.ErrorCode = "ETS-CONV-0203";
        loaded.ErrorMessage = "转换失败";
        loaded.RetryCount = 2;
        loaded.StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        loaded.FinishedAt = DateTimeOffset.UtcNow;
        await repo.UpdateAsync(loaded);

        var again = await repo.GetByIdAsync(task.Id);
        Assert.NotNull(again);
        Assert.Equal(ConversionTaskStatus.Failed, again!.Status);
        Assert.Equal(0.42, again.Progress!.Value, 3);
        Assert.Equal(ConversionTaskProgressKind.Percent, again.ProgressKind);
        Assert.Equal("ETS-CONV-0203", again.ErrorCode);
        Assert.Equal("转换失败", again.ErrorMessage);
        Assert.Equal(2, again.RetryCount);
        Assert.NotNull(again.StartedAt);
        Assert.NotNull(again.FinishedAt);

        await repo.DeleteAsync(task.Id);
        Assert.Null(await repo.GetByIdAsync(task.Id));
    }

    [Fact]
    public async Task 未终态列表_只含三种未终态_并按创建时间升序()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();
        await AddBookAsync(sb);
        var repo = new ConversionTaskRepository(sb.Factory);

        var t0 = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var waiting = NewTask(ConversionTaskStatus.WaitingRetry, createdAt: t0);
        var queued = NewTask(ConversionTaskStatus.Queued, createdAt: t0.AddMinutes(1));
        var running = NewTask(ConversionTaskStatus.Running, createdAt: t0.AddMinutes(2));
        var done = NewTask(ConversionTaskStatus.Completed, createdAt: t0.AddMinutes(3));
        var canceled = NewTask(ConversionTaskStatus.Canceled, createdAt: t0.AddMinutes(4));
        var failed = NewTask(ConversionTaskStatus.Failed, createdAt: t0.AddMinutes(5));

        // 故意乱序插入，验的是查询里的 ORDER BY 而不是插入顺序
        foreach (var t in new[] { running, done, waiting, failed, canceled, queued })
        {
            await repo.AddAsync(t);
        }

        var active = await repo.ListActiveAsync();
        Assert.Equal(3, active.Count);
        Assert.Equal(new[] { waiting.Id, queued.Id, running.Id }, active.Select(t => t.Id).ToArray());
    }

    [Fact]
    public async Task 最近列表_按创建时间降序_并遵守条数上限()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();
        await AddBookAsync(sb);
        var repo = new ConversionTaskRepository(sb.Factory);

        var t0 = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var oldest = NewTask(createdAt: t0);
        var middle = NewTask(createdAt: t0.AddMinutes(1));
        var newest = NewTask(createdAt: t0.AddMinutes(2));

        foreach (var t in new[] { middle, oldest, newest })
        {
            await repo.AddAsync(t);
        }

        var recent = await repo.ListRecentAsync(2);
        Assert.Equal(2, recent.Count);
        Assert.Equal(newest.Id, recent[0].Id);
        Assert.Equal(middle.Id, recent[1].Id);
    }

    [Fact]
    public async Task 崩溃恢复_运行中与等待重试回排队并清进度_且幂等()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();
        await AddBookAsync(sb);
        var repo = new ConversionTaskRepository(sb.Factory);

        var running = NewTask(ConversionTaskStatus.Running);
        running.Progress = 0.67;
        running.ProgressKind = ConversionTaskProgressKind.Percent;
        running.StartedAt = DateTimeOffset.UtcNow;
        var waiting = NewTask(ConversionTaskStatus.WaitingRetry);
        waiting.RetryCount = 1;
        var done = NewTask(ConversionTaskStatus.Completed);
        var failed = NewTask(ConversionTaskStatus.Failed);
        var queued = NewTask(ConversionTaskStatus.Queued);

        foreach (var t in new[] { running, waiting, done, failed, queued })
        {
            await repo.AddAsync(t);
        }

        Assert.Equal(2, await repo.ResetInterruptedAsync());

        foreach (var id in new[] { running.Id, waiting.Id })
        {
            var t = await repo.GetByIdAsync(id);
            Assert.NotNull(t);
            Assert.Equal(ConversionTaskStatus.Queued, t!.Status);
            Assert.Null(t.Progress);
            Assert.Equal(ConversionTaskProgressKind.Unknown, t.ProgressKind);
            Assert.Null(t.StartedAt);
        }

        // 重试计数故意保留：用户要看得见"这本已经试过 1 次"
        Assert.Equal(1, (await repo.GetByIdAsync(waiting.Id))!.RetryCount);

        // 终态的两条不能被碰
        Assert.Equal(ConversionTaskStatus.Completed, (await repo.GetByIdAsync(done.Id))!.Status);
        Assert.Equal(ConversionTaskStatus.Failed, (await repo.GetByIdAsync(failed.Id))!.Status);

        // 幂等：第二次调没有匹配行
        Assert.Equal(0, await repo.ResetInterruptedAsync());
    }

    [Fact]
    public async Task 清除已完成_只删终态_活跃任务保留()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();
        await AddBookAsync(sb);
        var repo = new ConversionTaskRepository(sb.Factory);

        var queued = NewTask(ConversionTaskStatus.Queued);
        var running = NewTask(ConversionTaskStatus.Running);
        var done = NewTask(ConversionTaskStatus.Completed);
        var failed = NewTask(ConversionTaskStatus.Failed);
        var canceled = NewTask(ConversionTaskStatus.Canceled);

        foreach (var t in new[] { queued, running, done, failed, canceled })
        {
            await repo.AddAsync(t);
        }

        Assert.Equal(3, await repo.DeleteFinishedAsync());
        Assert.Null(await repo.GetByIdAsync(done.Id));
        Assert.Null(await repo.GetByIdAsync(failed.Id));
        Assert.Null(await repo.GetByIdAsync(canceled.Id));
        Assert.NotNull(await repo.GetByIdAsync(queued.Id));
        Assert.NotNull(await repo.GetByIdAsync(running.Id));
    }

    [Fact]
    public async Task 书被删掉_任务还在_书名回落成null()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();
        await AddBookAsync(sb);
        var repo = new ConversionTaskRepository(sb.Factory);

        var task = NewTask();
        await repo.AddAsync(task);
        Assert.Equal("测试书", (await repo.GetByIdAsync(task.Id))!.BookTitle);

        await using (var conn = await sb.Factory.OpenAsync())
        {
            await conn.ExecuteAsync(
                "DELETE FROM books WHERE id = @id;",
                new[] { new SqliteParameter("@id", BookId.ToString()) });
        }

        var after = await repo.GetByIdAsync(task.Id);
        Assert.NotNull(after);
        Assert.Null(after!.BookId);
        Assert.Null(after.BookTitle);
    }
}
