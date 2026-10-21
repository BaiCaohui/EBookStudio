using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Infrastructure.Data.Repositories;

namespace EbookToolSuite.Infrastructure.Tests.Data;

/// <summary>
/// T6-5 书签 / 笔记仓储。验证的是数据库事实：两张表能建出来、
/// 排序与级联删除按约定工作、锚点 JSON 能原样往返。
/// </summary>
public sealed class AnnotationRepositoryTests
{
    private static readonly Guid BookA = Guid.Parse("aaaaaaaa-1111-2222-3333-444444444444");

    private static readonly Guid BookB = Guid.Parse("bbbbbbbb-1111-2222-3333-444444444444");

    private static AnnotationRepository Repo(DataSandbox sb) => new(sb.Factory);

    private static async Task SeedBooksAsync(DataSandbox sb)
    {
        await sb.RawExecuteAsync(
            "INSERT INTO books (id, title, rating, reading_status, damaged, created_at, updated_at) VALUES "
            + $"('{BookA}', '书签测试书 A', 0, 0, 0, 1, 1), ('{BookB}', '书签测试书 B', 0, 0, 0, 1, 1);");
    }

    [Fact]
    public async Task 迁移建出书签与笔记的表和索引()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();

        var tables = await sb.QueryStringsAsync(
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name IN ('bookmarks','notes') ORDER BY name;");

        Assert.Equal(new[] { "bookmarks", "notes" }, tables);

        var indexes = await sb.QueryStringsAsync(
            "SELECT name FROM sqlite_master WHERE type = 'index' "
            + "AND name IN ('idx_bookmarks_book','idx_notes_book') ORDER BY name;");

        Assert.Equal(new[] { "idx_bookmarks_book", "idx_notes_book" }, indexes);
    }

    [Fact]
    public async Task 书签按加入时间倒序返回()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();
        await SeedBooksAsync(sb);

        var repo = Repo(sb);
        var old = new Bookmark
        {
            BookId = BookA,
            SpineIndex = 1,
            Label = "第 2 章",
            Location = new ReaderLocator { ChapterIndex = 1, PercentInChapter = 0.25 },
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
        };
        var fresh = new Bookmark
        {
            BookId = BookA,
            SpineIndex = 3,
            Label = "第 4 章",
            Location = new ReaderLocator { ChapterIndex = 3, PercentInChapter = 0.5 },
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await repo.AddBookmarkAsync(old);
        await repo.AddBookmarkAsync(fresh);

        var list = await repo.ListBookmarksAsync(BookA);

        Assert.Equal(2, list.Count);
        Assert.Equal(fresh.Id, list[0].Id);
        Assert.Equal("第 4 章", list[0].Label);
        Assert.Equal(3, list[0].SpineIndex);
        // 三级定位原样回来
        Assert.Equal(0.5, list[0].Location!.PercentInChapter);
    }

    [Fact]
    public async Task 笔记按加入时间正序返回_锚点JSON能往返()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();
        await SeedBooksAsync(sb);

        var repo = Repo(sb);
        var anchor = new NoteAnchor
        {
            StartPath = "body/0/p/2",
            StartOffset = 3,
            EndPath = "body/0/p/3",
            EndOffset = 7,
            PercentInChapter = 0.42,
        };

        await repo.AddNoteAsync(new Note
        {
            BookId = BookA,
            SpineIndex = 0,
            Anchor = anchor,
            Type = NoteType.Annotation,
            Quote = "被选中的原文",
            Content = "我的一点想法",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        });

        await repo.AddNoteAsync(new Note
        {
            BookId = BookA,
            SpineIndex = 2,
            Type = NoteType.Highlight,
            Quote = "另一段原文",
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var list = await repo.ListNotesAsync(BookA);

        Assert.Equal(2, list.Count);
        // 正序：先加的在前面，和阅读顺序一致
        Assert.Equal(NoteType.Annotation, list[0].Type);
        Assert.Equal("我的一点想法", list[0].Content);
        Assert.Equal("body/0/p/2", list[0].Anchor!.StartPath);
        Assert.Equal(3, list[0].Anchor!.StartOffset);
        Assert.Equal("body/0/p/3", list[0].Anchor!.EndPath);
        Assert.Equal(7, list[0].Anchor!.EndOffset);
        Assert.Equal(0.42, list[0].Anchor!.PercentInChapter);
        Assert.Equal(NoteType.Highlight, list[1].Type);
        Assert.Null(list[1].Content);
    }

    [Fact]
    public async Task 改笔记会刷新内容_样式与颜色可选更新()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();
        await SeedBooksAsync(sb);

        var repo = Repo(sb);
        var note = new Note
        {
            BookId = BookA,
            SpineIndex = 0,
            Type = NoteType.Highlight,
            Style = MarkStyle.Highlight,
            Quote = "先高亮",
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-1),
        };

        await repo.AddNoteAsync(note);

        // 只改内容：样式与颜色保持原样（传 null = 不动）
        Assert.True(await repo.UpdateNoteAsync(note.Id, "后来补的想法", null, null));

        var saved = (await repo.ListNotesAsync(BookA)).Single();

        Assert.Equal("后来补的想法", saved.Content);
        Assert.Equal(MarkStyle.Highlight, saved.Style);
        Assert.Equal(0, saved.Color);
        Assert.NotNull(saved.UpdatedAt);
        Assert.True(saved.UpdatedAt >= saved.CreatedAt);

        // 同时改样式与颜色（笔记面板里点颜色/样式后提交的形态）
        Assert.True(await repo.UpdateNoteAsync(note.Id, "后来补的想法", (int)MarkStyle.Wavy, 6));

        var restyled = (await repo.ListNotesAsync(BookA)).Single();

        Assert.Equal(MarkStyle.Wavy, restyled.Style);
        Assert.Equal(6, restyled.Color);
        Assert.Equal("后来补的想法", restyled.Content);
    }

    [Fact]
    public async Task 标记样式能往返_四种都存得住()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();
        await SeedBooksAsync(sb);

        var repo = Repo(sb);

        foreach (var style in new[] { MarkStyle.Highlight, MarkStyle.Underline, MarkStyle.Wavy, MarkStyle.Strikethrough })
        {
            await repo.AddNoteAsync(new Note
            {
                BookId = BookA,
                SpineIndex = 0,
                Type = NoteType.Highlight,
                Style = style,
                Quote = "样式 " + (int)style,
                // 显式递增：同一毫秒内加四条时 created_at 相同，排序会退化成按 Guid
                CreatedAt = DateTimeOffset.UtcNow.AddSeconds((int)style),
            });
        }

        var list = await repo.ListNotesAsync(BookA);

        Assert.Equal(4, list.Count);
        Assert.Equal(
            new[] { MarkStyle.Highlight, MarkStyle.Underline, MarkStyle.Wavy, MarkStyle.Strikethrough },
            list.Select(n => n.Style));
        // 样式在旁表里，删书时随 notes 级联一起走
        await sb.Repository.DeleteAsync(BookA);
        Assert.Empty(await repo.ListNotesAsync(BookA));
    }

    [Fact]
    public async Task 删除不存在的条目返回false()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();

        var repo = Repo(sb);

        Assert.False(await repo.DeleteBookmarkAsync(Guid.NewGuid()));
        Assert.False(await repo.DeleteNoteAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task 删书会级联删掉书签与笔记()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();
        await SeedBooksAsync(sb);

        var repo = Repo(sb);

        await repo.AddBookmarkAsync(new Bookmark { BookId = BookA, SpineIndex = 0, Label = "章首" });
        await repo.AddNoteAsync(new Note { BookId = BookA, SpineIndex = 0, Quote = "原文" });
        await repo.AddBookmarkAsync(new Bookmark { BookId = BookB, SpineIndex = 1, Label = "别人的书签" });

        await sb.Repository.DeleteAsync(BookA);

        Assert.Empty(await repo.ListBookmarksAsync(BookA));
        Assert.Empty(await repo.ListNotesAsync(BookA));
        // 别的书不受影响
        Assert.Single(await repo.ListBookmarksAsync(BookB));
    }

    [Fact]
    public async Task 全库导出入口能拿到多本书的条目()
    {
        await using var sb = new DataSandbox();
        await sb.Database.InitializeAsync();
        await SeedBooksAsync(sb);

        var repo = Repo(sb);

        await repo.AddBookmarkAsync(new Bookmark { BookId = BookA, PageIndex = 4, Label = "第 5 页" });
        await repo.AddBookmarkAsync(new Bookmark { BookId = BookB, SpineIndex = 2, Label = "第 3 章" });
        await repo.AddNoteAsync(new Note { BookId = BookB, PageIndex = 1, Quote = "B 书的引文" });

        var bookmarks = await repo.ListAllBookmarksAsync();
        var notes = await repo.ListAllNotesAsync();

        Assert.Equal(2, bookmarks.Count);
        Assert.Equal(2, bookmarks.Select(b => b.BookId).Distinct().Count());
        Assert.Single(notes);
        Assert.Equal(4, bookmarks.Single(b => b.BookId == BookA).PageIndex);
    }
}
