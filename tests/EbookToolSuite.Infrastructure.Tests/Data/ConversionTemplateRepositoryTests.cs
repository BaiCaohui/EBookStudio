using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Infrastructure.Data;
using EbookToolSuite.Infrastructure.Data.Repositories;

namespace EbookToolSuite.Infrastructure.Tests.Data;

/// <summary>
/// conversion_templates 仓储的真库测试（执行计划 T2-5）。
/// 重点验的是映射：arguments_json 的往返、可空的 out_format、两列新加的
/// engine / applicable_formats（迁移 0005）。
/// </summary>
public sealed class ConversionTemplateRepositoryTests
{
    private static ConversionTemplate NewTemplate(
        string id = "user-abc",
        BookFormatKind? target = BookFormatKind.Mobi,
        ConversionEngine engine = ConversionEngine.Calibre) => new()
    {
        Id = id,
        DisplayName = "我的 MOBI 模板",
        TargetFormat = target,
        Engine = engine,
        ApplicableFormats = engine == ConversionEngine.Polish
            ? new[] { BookFormatKind.Epub }
            : Array.Empty<BookFormatKind>(),
        Note = "测试用",
        IsBuiltIn = false,
        Arguments = new[]
        {
            new ConversionArgument("--output-profile", "kindle_pw3", "档位"),
            new ConversionArgument("--embed-fonts", null, "开关型"),
        },
    };

    private static async Task<DataSandbox> NewSandboxAsync()
    {
        var sandbox = new DataSandbox();
        await sandbox.Database.InitializeAsync();
        return sandbox;
    }

    [Fact]
    public async Task 存一份再读回来_参数与引擎都不丢()
    {
        await using var sb = await NewSandboxAsync();
        var repo = new ConversionTemplateRepository(sb.Factory);

        await repo.UpsertAsync(NewTemplate());

        var loaded = await repo.GetAsync("user-abc");
        Assert.NotNull(loaded);
        Assert.Equal("我的 MOBI 模板", loaded.DisplayName);
        Assert.Equal(BookFormatKind.Mobi, loaded.TargetFormat);
        Assert.Equal(ConversionEngine.Calibre, loaded.Engine);
        Assert.False(loaded.IsBuiltIn);
        Assert.Equal(2, loaded.Arguments.Count);
        Assert.Equal("--output-profile", loaded.Arguments[0].Key);
        Assert.Equal("kindle_pw3", loaded.Arguments[0].Value);
        Assert.Equal("档位", loaded.Arguments[0].Note);
        Assert.Null(loaded.Arguments[1].Value);
    }

    [Fact]
    public async Task polish模板_没有目标格式但引擎与适用格式都在()
    {
        await using var sb = await NewSandboxAsync();
        var repo = new ConversionTemplateRepository(sb.Factory);

        await repo.UpsertAsync(NewTemplate(
            id: "user-polish", target: null, engine: ConversionEngine.Polish));

        var loaded = await repo.GetAsync("user-polish");
        Assert.NotNull(loaded);
        Assert.Null(loaded.TargetFormat);
        Assert.True(loaded.KeepsFormat);
        Assert.Equal(
            ConversionEngineRules.ApplicableFormats(ConversionEngine.Polish),
            loaded.ApplicableFormats);
    }

    [Fact]
    public async Task 同id再存一次是覆盖不是新增()
    {
        await using var sb = await NewSandboxAsync();
        var repo = new ConversionTemplateRepository(sb.Factory);

        await repo.UpsertAsync(NewTemplate());
        var changed = NewTemplate() with { DisplayName = "改过名字" };
        await repo.UpsertAsync(changed);

        var all = await repo.ListAsync();
        Assert.Single(all);
        Assert.Equal("改过名字", all[0].DisplayName);
    }

    [Fact]
    public async Task 删除返回是否真的删掉了()
    {
        await using var sb = await NewSandboxAsync();
        var repo = new ConversionTemplateRepository(sb.Factory);

        await repo.UpsertAsync(NewTemplate());

        Assert.True(await repo.DeleteAsync("user-abc"));
        Assert.False(await repo.DeleteAsync("user-abc"));
        Assert.Null(await repo.GetAsync("user-abc"));
        Assert.Empty(await repo.ListAsync());
    }

    [Fact]
    public async Task arguments_json被改坏时当成没有参数_不抛()
    {
        await using var sb = await NewSandboxAsync();
        var repo = new ConversionTemplateRepository(sb.Factory);

        await repo.UpsertAsync(NewTemplate());
        await using (var conn = await sb.Factory.OpenAsync())
        {
            await conn.ExecuteAsync(
                "UPDATE conversion_templates SET arguments_json = '{不是数组' WHERE id = @id;",
                new[] { new Microsoft.Data.Sqlite.SqliteParameter("@id", "user-abc") });
        }

        var loaded = await repo.GetAsync("user-abc");
        Assert.NotNull(loaded);
        Assert.Empty(loaded.Arguments);
    }
}
