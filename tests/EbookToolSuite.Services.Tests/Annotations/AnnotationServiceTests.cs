using System.Text;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Infrastructure.Config;
using EbookToolSuite.Infrastructure.Data;
using EbookToolSuite.Infrastructure.Data.Repositories;
using EbookToolSuite.Services.Annotations;
using Microsoft.Extensions.Logging.Abstractions;

namespace EbookToolSuite.Services.Tests.Annotations;

/// <summary>
/// T6-5 导出的验收对象是**文件内容**（计划书判据 6），所以这里不 mock：
/// 真 SQLite + 真 BookRepository + 真文件系统，最后读回 .md 逐段断言。
/// </summary>
public sealed class AnnotationServiceTests
{
    private static readonly Guid BookA = Guid.Parse("aaaaaaaa-1111-2222-3333-444444444444");

    private static readonly Guid BookB = Guid.Parse("bbbbbbbb-1111-2222-3333-444444444444");

    private sealed class Sandbox : IAsyncDisposable
    {
        public Sandbox()
        {
            Root = Path.Combine(Path.GetTempPath(), "ets-anno-" + Guid.NewGuid().ToString("N"));
            var appRoot = Path.Combine(Root, "app");
            var userLocal = Path.Combine(Root, "localappdata", "EbookToolSuite");
            Directory.CreateDirectory(appRoot);
            Directory.CreateDirectory(userLocal);

            Paths = AppPaths.Create(new AppPathsOptions
            {
                AppRoot = appRoot,
                UserLocalRoot = userLocal,
                Fallback = DataDirFallback.Throw,
            });
            Paths.EnsureCreated();

            Factory = new SqliteConnectionFactory(Paths);
            Database = new Database(
                Factory,
                new SchemaMigrator(NullLogger<SchemaMigrator>.Instance),
                NullLogger<Database>.Instance);

            Books = new BookRepository(Factory);
            Repo = new AnnotationRepository(Factory);
            Service = new AnnotationService(Repo, Books, Paths, NullLogger<AnnotationService>.Instance);
        }

        public string Root { get; }

        public AppPaths Paths { get; }

        public SqliteConnectionFactory Factory { get; }

        public Database Database { get; }

        public BookRepository Books { get; }

        public AnnotationRepository Repo { get; }

        public AnnotationService Service { get; }

        public async ValueTask DisposeAsync()
        {
            await Factory.DisposeAsync();

            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static async Task AddBooksAsync(Sandbox sb)
    {
        await using var conn = await sb.Factory.OpenAsync();

        await conn.ExecuteAsync(
            "INSERT INTO books (id, title, rating, reading_status, damaged, created_at, updated_at) VALUES "
            + $"('{BookA}', '导出测试书 A', 0, 0, 0, 1, 1), ('{BookB}', '导出测试书 B', 0, 0, 0, 1, 1);");
    }

    [Fact]
    public async Task 空库导出会说明没有条目_且文件是UTF8无BOM()
    {
        await using var sb = new Sandbox();
        await sb.Database.InitializeAsync();

        var path = await sb.Service.ExportMarkdownAsync(null);

        Assert.True(File.Exists(path));

        var bytes = await File.ReadAllBytesAsync(path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);

        var text = Encoding.UTF8.GetString(bytes);
        Assert.Contains("没有任何书签或笔记", text);
    }

    [Fact]
    public async Task 导出内容包含书名_书签标签_引文_批注内容与时间()
    {
        await using var sb = new Sandbox();
        await sb.Database.InitializeAsync();
        await AddBooksAsync(sb);

        var stamp = new DateTimeOffset(2026, 10, 18, 20, 30, 0, TimeSpan.FromHours(8));

        await sb.Repo.AddBookmarkAsync(new Bookmark
        {
            BookId = BookA,
            SpineIndex = 2,
            Label = "第 3 章 · 江湖夜雨",
            Location = new ReaderLocator { ChapterIndex = 2, PercentInChapter = 0.4 },
            CreatedAt = stamp,
        });

        await sb.Repo.AddNoteAsync(new Note
        {
            BookId = BookA,
            SpineIndex = 2,
            Type = NoteType.Annotation,
            Quote = "这是一段被选中的原文。",
            Content = "这里写了一点想法。",
            CreatedAt = stamp,
        });

        var path = await sb.Service.ExportMarkdownAsync(BookA);
        var text = await File.ReadAllTextAsync(path);

        Assert.Contains("# 书签与笔记", text);
        Assert.Contains("## 《导出测试书 A》", text);
        Assert.Contains("### 书签", text);
        Assert.Contains("第 3 章 · 江湖夜雨", text);
        Assert.Contains("### 笔记", text);
        Assert.Contains("> 这是一段被选中的原文。", text);
        Assert.Contains("这里写了一点想法。", text);
        // 时间按本地时区显示（测试机在 +08:00，与创建时间一致）
        Assert.Contains("2026-10-18 20:30", text);
    }

    [Fact]
    public async Task 高亮没有用户输入时不会留空的内容段()
    {
        await using var sb = new Sandbox();
        await sb.Database.InitializeAsync();
        await AddBooksAsync(sb);

        await sb.Repo.AddNoteAsync(new Note
        {
            BookId = BookA,
            SpineIndex = 0,
            Type = NoteType.Highlight,
            Quote = "只是高亮的一段话",
        });

        var path = await sb.Service.ExportMarkdownAsync(BookA);
        var text = await File.ReadAllTextAsync(path);

        Assert.Contains("高亮", text);
        Assert.Contains("> 只是高亮的一段话", text);
        // 引文之后直接是下一条目（没有多余的空内容）
        Assert.DoesNotContain("高亮\n\n\n", text);
    }

    [Fact]
    public async Task 只导出sample时不含别的书()
    {
        await using var sb = new Sandbox();
        await sb.Database.InitializeAsync();
        await AddBooksAsync(sb);

        await sb.Repo.AddBookmarkAsync(new Bookmark { BookId = BookA, SpineIndex = 0, Label = "A 的书签" });
        await sb.Repo.AddBookmarkAsync(new Bookmark { BookId = BookB, SpineIndex = 0, Label = "B 的书签" });

        var path = await sb.Service.ExportMarkdownAsync(BookA);
        var text = await File.ReadAllTextAsync(path);

        Assert.Contains("A 的书签", text);
        Assert.DoesNotContain("B 的书签", text);
        Assert.DoesNotContain("导出测试书 B", text);
    }

    [Fact]
    public async Task 全库导出会把所有书分组列出()
    {
        await using var sb = new Sandbox();
        await sb.Database.InitializeAsync();
        await AddBooksAsync(sb);

        await sb.Repo.AddBookmarkAsync(new Bookmark { BookId = BookA, SpineIndex = 0, Label = "A 的书签" });
        await sb.Repo.AddNoteAsync(new Note { BookId = BookB, PageIndex = 3, Quote = "B 的引文" });
        // 没给 label 的页码书签：导出时按"第 N 页"兜底（N 是 0 基 +1）
        await sb.Repo.AddBookmarkAsync(new Bookmark { BookId = BookB, PageIndex = 3 });

        var path = await sb.Service.ExportMarkdownAsync(null);
        var text = await File.ReadAllTextAsync(path);

        Assert.Contains("## 《导出测试书 A》", text);
        Assert.Contains("## 《导出测试书 B》", text);
        Assert.Contains("> B 的引文", text);
        Assert.Contains("第 4 页", text);
    }
}
