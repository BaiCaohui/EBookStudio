using System.Text;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Services.Editing;
using Xunit;

namespace EbookToolSuite.Services.Tests.Editing;

/// <summary>
/// T5-3 查找替换（2026-10-12 口径）：只替换文本节点、逐章校验全通过才写盘、正则可选、
/// 超时/命中上限兜底。走真实文件系统（本仓库测试约定：不 mock）。
/// </summary>
public sealed class ChapterFindReplaceServiceTests
{
    private const string Entry1 = "OEBPS/Text/ch1.xhtml";
    private const string Entry2 = "OEBPS/Text/ch2.xhtml";

    private static string Chapter(string body, string title = "书名")
        => "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n"
            + "<html xmlns=\"http://www.w3.org/1999/xhtml\">\r\n"
            + "<head><title>" + title + "</title></head>\r\n"
            + "<body>" + body + "</body>\r\n</html>";

    [Fact]
    public void 普通模式_只替换文本节点_标签与属性逐字节不变()
    {
        var root = NewRoot();

        try
        {
            var ws = new TempWorkspace(root);
            Write(root, Entry1, Chapter("<p class=\"x\">甲甲甲乙</p><p>第二段甲</p>"));
            var service = new ChapterFindReplaceService(new ChapterSourceService());

            var plan = service.Preview(ws, [Ref(Entry1)], new FindReplaceOptions("甲", "乙"));

            Assert.True(plan.Ok, plan.Error);
            Assert.Equal(4, plan.TotalMatches);
            Assert.Equal(1, plan.ChangedChapterCount);

            var outcome = service.Apply(ws, plan);

            Assert.True(outcome.Ok, outcome.Message);
            Assert.Equal(4, outcome.ReplacedCount);

            /* 逐字节：除了正文里的"甲"，其余一个字节都不能变（引号/缩进/声明都保持）。 */
            Assert.Equal(
                Chapter("<p class=\"x\">乙乙乙乙</p><p>第二段乙</p>"),
                Read(root, Entry1));
            Assert.Contains(Entry1, ws.Session.ChangedFiles);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void 搜标签名_命中为零_文件逐字节不变()
    {
        var root = NewRoot();

        try
        {
            var ws = new TempWorkspace(root);
            Write(root, Entry1, Chapter("<p class=\"x\">正文</p>"));
            var path = Path.Combine(root, Entry1.Replace('/', Path.DirectorySeparatorChar));
            var before = File.ReadAllBytes(path);
            var service = new ChapterFindReplaceService(new ChapterSourceService());

            var plan = service.Preview(ws, [Ref(Entry1)], new FindReplaceOptions("<p>", "X"));

            Assert.True(plan.Ok, plan.Error);
            Assert.Equal(0, plan.TotalMatches);
            Assert.Empty(plan.Changes);

            var outcome = service.Apply(ws, plan);

            Assert.True(outcome.Ok);
            Assert.Equal(0, outcome.ReplacedCount);
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Empty(ws.Session.ChangedFiles);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void 不改标题_标题里的词不会被替换()
    {
        var root = NewRoot();

        try
        {
            var ws = new TempWorkspace(root);
            Write(root, Entry1, Chapter("<p>甲</p>", title: "甲甲"));
            var service = new ChapterFindReplaceService(new ChapterSourceService());

            var plan = service.Preview(ws, [Ref(Entry1)], new FindReplaceOptions("甲", "乙"));

            Assert.True(plan.Ok, plan.Error);
            Assert.Equal(1, plan.TotalMatches);          // 只命中正文那一个
            Assert.Equal("第 1 章", plan.Preview[0].ChapterTitle);
            Assert.Equal("甲", plan.Preview[0].Match);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void 正则模式_捕获组可用()
    {
        var root = NewRoot();

        try
        {
            var ws = new TempWorkspace(root);
            Write(root, Entry1, Chapter("<p>a1 b2 c3</p>"));
            var service = new ChapterFindReplaceService(new ChapterSourceService());

            var plan = service.Preview(ws, [Ref(Entry1)], new FindReplaceOptions("([a-z])([0-9])", "$2$1", UseRegex: true));

            Assert.True(plan.Ok, plan.Error);
            Assert.Equal(3, plan.TotalMatches);

            var outcome = service.Apply(ws, plan);

            Assert.True(outcome.Ok, outcome.Message);
            Assert.Contains("1a 2b 3c", Read(root, Entry1), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void 正则模式_命名捕获组可用()
    {
        var root = NewRoot();

        try
        {
            var ws = new TempWorkspace(root);
            Write(root, Entry1, Chapter("<p>甲=1 乙=2</p>"));
            var service = new ChapterFindReplaceService(new ChapterSourceService());

            var plan = service.Preview(
                ws, [Ref(Entry1)], new FindReplaceOptions("(?<k>[甲乙])=(?<v>[0-9])", "${v}${k}", UseRegex: true));

            Assert.True(plan.Ok, plan.Error);

            var outcome = service.Apply(ws, plan);

            Assert.True(outcome.Ok, outcome.Message);
            Assert.Contains("1甲 2乙", Read(root, Entry1), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void 非法正则_不执行也不崩_文件不变()
    {
        var root = NewRoot();

        try
        {
            var ws = new TempWorkspace(root);
            Write(root, Entry1, Chapter("<p>正文</p>"));
            var path = Path.Combine(root, Entry1.Replace('/', Path.DirectorySeparatorChar));
            var before = File.ReadAllBytes(path);
            var service = new ChapterFindReplaceService(new ChapterSourceService());

            var plan = service.Preview(ws, [Ref(Entry1)], new FindReplaceOptions("[a-", "x", UseRegex: true));

            Assert.False(plan.Ok);
            Assert.Contains("正则", plan.Error!, StringComparison.Ordinal);
            Assert.Empty(plan.Changes);
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void 命中超上限_中止且不写盘()
    {
        var root = NewRoot();

        try
        {
            var ws = new TempWorkspace(root);
            Write(root, Entry1, Chapter("<p>甲甲甲甲甲</p>"));
            var path = Path.Combine(root, Entry1.Replace('/', Path.DirectorySeparatorChar));
            var before = File.ReadAllBytes(path);
            var service = new ChapterFindReplaceService(new ChapterSourceService());

            var plan = service.Preview(ws, [Ref(Entry1)], new FindReplaceOptions("甲", "乙") { MaxMatches = 2 });

            Assert.False(plan.Ok);
            Assert.Contains("上限", plan.Error!, StringComparison.Ordinal);

            var outcome = service.Apply(ws, plan);

            Assert.False(outcome.Ok);
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Empty(ws.Session.ChangedFiles);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void 正则超时_中止且不写盘()
    {
        var root = NewRoot();

        try
        {
            var ws = new TempWorkspace(root);
            Write(root, Entry1, Chapter("<p>" + new string('a', 4000) + "!</p>"));
            var path = Path.Combine(root, Entry1.Replace('/', Path.DirectorySeparatorChar));
            var before = File.ReadAllBytes(path);
            var service = new ChapterFindReplaceService(new ChapterSourceService());

            var plan = service.Preview(
                ws,
                [Ref(Entry1)],
                new FindReplaceOptions("(a+)+$", "x", UseRegex: true) { PerChapterTimeout = TimeSpan.FromMilliseconds(50) });

            Assert.False(plan.Ok);
            Assert.Contains("超时", plan.Error!, StringComparison.Ordinal);
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Empty(ws.Session.ChangedFiles);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void 预览只列前二十处_处数与命中数一致()
    {
        var root = NewRoot();

        try
        {
            var ws = new TempWorkspace(root);
            Write(root, Entry1, Chapter("<p>" + new string('甲', 25) + "</p>"));
            var service = new ChapterFindReplaceService(new ChapterSourceService());

            var plan = service.Preview(ws, [Ref(Entry1)], new FindReplaceOptions("甲", "乙"));

            Assert.True(plan.Ok, plan.Error);
            Assert.Equal(25, plan.TotalMatches);
            Assert.Equal(20, plan.Preview.Count);
            Assert.Equal("甲", plan.Preview[0].Match);
            Assert.True(plan.Preview[0].Before.Length <= 20);
            Assert.True(plan.Preview[0].After.Length <= 20);

            /* 第 21 处起不再列，但照样会替换。 */
            var outcome = service.Apply(ws, plan);

            Assert.Equal(25, outcome.ReplacedCount);
            Assert.DoesNotContain("甲", Read(root, Entry1), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void 全书替换_多章一次写完_并逐章记changedFiles()
    {
        var root = NewRoot();

        try
        {
            var ws = new TempWorkspace(root);
            Write(root, Entry1, Chapter("<p>甲一</p>"));
            Write(root, Entry2, Chapter("<p>甲二甲</p>"));
            var service = new ChapterFindReplaceService(new ChapterSourceService());

            var plan = service.Preview(ws, [Ref(Entry1, 0), Ref(Entry2, 1)], new FindReplaceOptions("甲", "乙"));

            Assert.True(plan.Ok, plan.Error);
            Assert.Equal(3, plan.TotalMatches);
            Assert.Equal(2, plan.ChangedChapterCount);

            var outcome = service.Apply(ws, plan);

            Assert.True(outcome.Ok, outcome.Message);
            Assert.Equal(3, outcome.ReplacedCount);
            Assert.Equal(2, outcome.ChangedChapters);
            Assert.Contains("乙一", Read(root, Entry1), StringComparison.Ordinal);
            Assert.Contains("乙二乙", Read(root, Entry2), StringComparison.Ordinal);
            Assert.Contains(Entry1, ws.Session.ChangedFiles);
            Assert.Contains(Entry2, ws.Session.ChangedFiles);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void 替换引入非法字符_与号_逐章校验失败_一个字都不写()
    {
        var root = NewRoot();

        try
        {
            var ws = new TempWorkspace(root);
            Write(root, Entry1, Chapter("<p>甲一</p>"));
            Write(root, Entry2, Chapter("<p>甲二甲</p>"));
            var path1 = Path.Combine(root, Entry1.Replace('/', Path.DirectorySeparatorChar));
            var path2 = Path.Combine(root, Entry2.Replace('/', Path.DirectorySeparatorChar));
            var before1 = File.ReadAllBytes(path1);
            var before2 = File.ReadAllBytes(path2);

            var service = new ChapterFindReplaceService(new ChapterSourceService());

            /* 替换串里写裸的 & ：计划原文"例如把 & 写进文本 -> 逐章校验失败 -> 一个字都不写"。 */
            var plan = service.Preview(
                ws, [Ref(Entry1, 0), Ref(Entry2, 1)], new FindReplaceOptions("甲", "甲 & 乙"));

            Assert.True(plan.Ok, plan.Error);
            Assert.Equal(3, plan.TotalMatches);

            var outcome = service.Apply(ws, plan);

            Assert.False(outcome.Ok);
            Assert.Contains("一个字都没写", outcome.Message, StringComparison.Ordinal);
            Assert.Contains("校验不过", outcome.Message, StringComparison.Ordinal);
            Assert.Equal(before1, File.ReadAllBytes(path1));
            Assert.Equal(before2, File.ReadAllBytes(path2));
            Assert.Empty(ws.Session.ChangedFiles);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void 实体字符按文本解码后匹配_找不到裸源码里的实体写法()
    {
        var root = NewRoot();

        try
        {
            var ws = new TempWorkspace(root);
            Write(root, Entry1, Chapter("<p>A&amp;B</p>"));
            var service = new ChapterFindReplaceService(new ChapterSourceService());

            /* 文本里的 & 是一个字符；把它换成中点后文件仍然良构。 */
            var plan = service.Preview(ws, [Ref(Entry1)], new FindReplaceOptions("&", ""));

            Assert.True(plan.Ok, plan.Error);
            Assert.Equal(1, plan.TotalMatches);

            var outcome = service.Apply(ws, plan);

            Assert.True(outcome.Ok, outcome.Message);
            Assert.Contains("<p>AB</p>", Read(root, Entry1), StringComparison.Ordinal);
            Assert.True(new ChapterSourceService().Check(Read(root, Entry1)).Ok);

            /* 源码里的 "&amp;" 不是文本，按文本查 "&amp;" 应当一处都找不到。 */
            var plan2 = service.Preview(ws, [Ref(Entry1)], new FindReplaceOptions("&amp;", "X"));
            Assert.True(plan2.Ok, plan2.Error);
            Assert.Equal(0, plan2.TotalMatches);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void 只替换第N处_其余不动()
    {
        var root = NewRoot();

        try
        {
            var ws = new TempWorkspace(root);
            Write(root, Entry1, Chapter("<p>甲甲甲</p>"));
            var service = new ChapterFindReplaceService(new ChapterSourceService());

            var plan = service.Preview(
                ws, [Ref(Entry1)], new FindReplaceOptions("甲", "乙") { OnlyMatchOrdinal = 1 });

            Assert.True(plan.Ok, plan.Error);
            Assert.Equal(1, plan.TotalMatches);

            var outcome = service.Apply(ws, plan);

            Assert.True(outcome.Ok, outcome.Message);
            Assert.Equal(1, outcome.ReplacedCount);
            Assert.Contains("<p>甲乙甲</p>", Read(root, Entry1), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void 某章校验失败_一个字都不写_原文可回滚()
    {
        var root = NewRoot();

        try
        {
            var ws = new TempWorkspace(root);
            Write(root, Entry1, Chapter("<p>甲一</p>"));
            Write(root, Entry2, Chapter("<p>甲二甲</p>"));
            var path1 = Path.Combine(root, Entry1.Replace('/', Path.DirectorySeparatorChar));
            var path2 = Path.Combine(root, Entry2.Replace('/', Path.DirectorySeparatorChar));
            var before1 = File.ReadAllBytes(path1);
            var before2 = File.ReadAllBytes(path2);

            var fake = new FailingCheckSource();
            var service = new ChapterFindReplaceService(fake);
            var plan = service.Preview(ws, [Ref(Entry1, 0), Ref(Entry2, 1)], new FindReplaceOptions("甲", "乙"));

            Assert.True(plan.Ok, plan.Error);

            var outcome = service.Apply(ws, plan);

            Assert.False(outcome.Ok);
            Assert.Contains("一个字都没写", outcome.Message, StringComparison.Ordinal);
            Assert.Equal(0, fake.SaveCount);
            Assert.Equal(before1, File.ReadAllBytes(path1));
            Assert.Equal(before2, File.ReadAllBytes(path2));
            Assert.Empty(ws.Session.ChangedFiles);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ---------- 辅助 ----------

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "ets-fr-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        return root;
    }

    private static FindReplaceChapter Ref(string entry, int index = 0) => new(index, entry, "第 " + (index + 1) + " 章");

    private static void Write(string root, string entry, string text)
    {
        var path = Path.Combine(root, entry.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string Read(string root, string entry)
        => File.ReadAllText(Path.Combine(root, entry.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>临时工作区：一个目录 + 一份会话（changedFiles 记在这里）。</summary>
    private sealed class TempWorkspace(string root) : IEpubWorkspace
    {
        public string SessionId => Session.SessionId;

        public string WorkDir { get; } = root;

        public WorkspaceSession Session { get; } = new()
        {
            SessionId = "t" + Guid.NewGuid().ToString("N")[..6],
            BookId = Guid.NewGuid(),
            FormatId = Guid.NewGuid(),
            SourcePath = Path.Combine(root, "book.epub"),
            OpenedAt = DateTimeOffset.Now,
        };

        public Task DiscardAsync(CancellationToken ct = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// 只用于"校验失败 -> 一个字都不写"这条：正文里出现"乙"就让 Check 失败。
    /// </summary>
    private sealed class FailingCheckSource : IChapterSourceService
    {
        private readonly ChapterSourceService _inner = new();

        public int SaveCount { get; private set; }

        public string ReadSource(IEpubWorkspace workspace, string entry) => _inner.ReadSource(workspace, entry);

        public ChapterSourceCheck Check(string source)
            => source.Contains('乙')
                ? new ChapterSourceCheck(false, 1, 1, "injected", "测试注入的校验失败")
                : _inner.Check(source);

        public ChapterSourceCheck Save(IEpubWorkspace workspace, string entry, string source)
        {
            SaveCount++;
            return _inner.Save(workspace, entry, source);
        }

        public ChapterVisualSaveResult SaveVisual(
            IEpubWorkspace workspace, string entry, string bodyInnerXml, bool applyFixes = false)
            => _inner.SaveVisual(workspace, entry, bodyInnerXml, applyFixes);
    }
}
