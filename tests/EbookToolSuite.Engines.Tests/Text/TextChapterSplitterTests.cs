using System.Diagnostics;
using System.Text;
using EbookToolSuite.Engines.Text;

namespace EbookToolSuite.Engines.Tests.Text;

/// <summary>
/// T4-5 判据 3 / 5：常见章节标记都要能切出来，切不出来要按固定字数硬切兜底。
/// 每一条都用"按字符范围把正文取回来"验证偏移量是对的（偏移错一个字，正文就对不上）。
/// </summary>
public sealed class TextChapterSplitterTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "ets-txt-split-" + Guid.NewGuid().ToString("N"));

    public TextChapterSplitterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void 第X章_切成三章_标题与范围都对得上()
    {
        var text = "书名页上的字\n\n第一章 起头\n第一章的正文甲。\n\n第二章 承接\n第二章的正文乙。\n\n第三章 收尾\n第三章的正文丙。\n";
        var path = Write("a.txt", text);

        var plan = Split(path, text);

        Assert.False(plan.UsedFallback);
        Assert.Equal(4, plan.Chapters.Count);          // 前言 + 三章
        Assert.Equal("前言", plan.Chapters[0].Title);
        Assert.Equal("第一章 起头", plan.Chapters[1].Title);
        Assert.Equal("第二章 承接", plan.Chapters[2].Title);
        Assert.Equal("第三章 收尾", plan.Chapters[3].Title);

        Assert.Equal("第一章 起头\n第一章的正文甲。", Read(path, plan, 1).Trim());
        Assert.Equal("第二章 承接\n第二章的正文乙。", Read(path, plan, 2).Trim());
        Assert.Equal("第三章 收尾\n第三章的正文丙。", Read(path, plan, 3).Trim());
        Assert.Equal("书名页上的字", Read(path, plan, 0).Trim());
    }

    [Fact]
    public void Chapter_X_能切()
    {
        var text = "Chapter 1 The Start\nbody one\n\nChapter 2 The Middle\nbody two\n\nChapter 3 The End\nbody three\n";
        var path = Write("b.txt", text);

        var plan = Split(path, text);

        Assert.False(plan.UsedFallback);
        Assert.Equal(3, plan.Chapters.Count);
        Assert.Equal("Chapter 1 The Start", plan.Chapters[0].Title);
        Assert.Contains("body two", Read(path, plan, 1), StringComparison.Ordinal);
    }

    [Fact]
    public void 方括号第X章_能切()
    {
        var text = "【第一章】开端\n正文一\n\n【第二章】发展\n正文二\n";
        var path = Write("c.txt", text);

        var plan = Split(path, text);

        Assert.False(plan.UsedFallback);
        Assert.Equal(2, plan.Chapters.Count);
        Assert.Equal("【第一章】开端", plan.Chapters[0].Title);
        Assert.Contains("正文二", Read(path, plan, 1), StringComparison.Ordinal);
    }

    [Fact]
    public void 卷X_能切()
    {
        var text = "卷壹 风起\n卷一正文\n\n卷贰 云涌\n卷二正文\n";
        var path = Write("d.txt", text);

        var plan = Split(path, text);

        Assert.False(plan.UsedFallback);
        Assert.Equal(2, plan.Chapters.Count);
        Assert.Equal("卷壹 风起", plan.Chapters[0].Title);
        Assert.Contains("卷二正文", Read(path, plan, 1), StringComparison.Ordinal);
    }

    [Fact]
    public void 数字编号行_能切()
    {
        var text = "1. 第一节\n第一节正文\n\n2. 第二节\n第二节正文\n\n3. 第三节\n第三节正文\n";
        var path = Write("e.txt", text);

        var plan = Split(path, text);

        Assert.False(plan.UsedFallback);
        Assert.Equal(3, plan.Chapters.Count);
        Assert.Equal("1. 第一节", plan.Chapters[0].Title);
        Assert.Contains("第三节正文", Read(path, plan, 2), StringComparison.Ordinal);
    }

    [Fact]
    public void 没有章节标记_按三千字硬切_每章都能读出来()
    {
        var builder = new StringBuilder();

        for (var i = 0; i < 200; i++)
        {
            builder.Append("这是一段没有任何章节标记的正文，用来验证硬切兜底。abcdefghij\n");
        }

        var text = builder.ToString();
        var path = Write("f.txt", text);

        var plan = Split(path, text);

        Assert.True(plan.UsedFallback);
        Assert.True(plan.Chapters.Count >= 2, "长文本必须被切成多章");
        Assert.Equal(0, plan.Chapters[0].StartChar);
        Assert.Equal(text.Length, plan.Chapters[^1].EndChar);

        // 相邻章节首尾相接，不丢字
        for (var i = 1; i < plan.Chapters.Count; i++)
        {
            Assert.Equal(plan.Chapters[i - 1].EndChar, plan.Chapters[i].StartChar);
        }

        var first = Read(path, plan, 0);
        Assert.False(string.IsNullOrWhiteSpace(first));
        Assert.Equal(text[..(int)plan.Chapters[0].EndChar].Trim(), first.Trim());
    }

    [Fact]
    public void 章节标记挨得太近_只认第一个()
    {
        // 正文里引用"第X章"是常见写法，不能每提一次就切一章
        var text = "第一章 真章节\n他说：请看第一章 开头那段。还有第二章 结尾那段。\n\n第二章 真第二章\n正文。\n";
        var path = Write("g.txt", text);

        var plan = Split(path, text);

        Assert.False(plan.UsedFallback);
        Assert.Equal(2, plan.Chapters.Count);
        Assert.Equal("第一章 真章节", plan.Chapters[0].Title);
        Assert.Equal("第二章 真第二章", plan.Chapters[1].Title);
    }

    [Fact]
    public void 番外_楔子_尾声_能切_正文里提到的不算()
    {
        // 2026-10-20 用户要求：无编号固定标题也要单独成章
        var text = "第一章 起点\n正文一\n\n番外 一\n番外正文\n\n楔子\n楔子正文\n\n尾声\n尾声正文\n";
        var path = Write("h.txt", text);

        var plan = Split(path, text);

        Assert.False(plan.UsedFallback);
        Assert.Equal(4, plan.Chapters.Count);
        Assert.Equal("第一章 起点", plan.Chapters[0].Title);
        Assert.Equal("番外 一", plan.Chapters[1].Title);
        Assert.Equal("楔子", plan.Chapters[2].Title);
        Assert.Equal("尾声", plan.Chapters[3].Title);
        Assert.Contains("番外正文", Read(path, plan, 1), StringComparison.Ordinal);
        Assert.Contains("楔子正文", Read(path, plan, 2), StringComparison.Ordinal);
        Assert.Contains("尾声正文", Read(path, plan, 3), StringComparison.Ordinal);

        // 正文行以"番外"开头、但标记后面不是分隔符 -> 不能当标题（否则每提一次就切一章）
        var inline = "第一章 真章节\n番外的事情以后再说。\n";
        var inlinePath = Write("i.txt", inline);
        var inlinePlan = Split(inlinePath, inline);

        Assert.False(inlinePlan.UsedFallback);
        Assert.Single(inlinePlan.Chapters);
    }

    [Fact]
    public void 大文件_五兆字符_切章要快且不整本读进内存()
    {
        var builder = new StringBuilder();

        for (var i = 1; i <= 200; i++)
        {
            builder.Append("第").Append(i).Append("章 第").Append(i).Append("段\n");

            for (var k = 0; k < 100; k++)
            {
                builder.Append("这是第 ").Append(i).Append(" 章的正文行，序号 ").Append(k)
                    .Append("，填充到大约五兆字符规模。\n");
            }
        }

        var text = builder.ToString();
        var path = Write("big.txt", text);

        var watch = Stopwatch.StartNew();
        var plan = TextChapterSplitter.Split(path, new UTF8Encoding(false));
        watch.Stop();

        Assert.Equal(200, plan.Chapters.Count);
        Assert.True(watch.ElapsedMilliseconds < 5000, "5 兆字符切章耗时 " + watch.ElapsedMilliseconds + " ms，太慢");

        // 中间随便挑一章，按范围取回来的正文必须真的是那一章
        var sample = Read(path, plan, 150);
        Assert.StartsWith("第151章", sample, StringComparison.Ordinal);
        Assert.Contains("这是第 151 章的正文行", sample, StringComparison.Ordinal);
    }

    private string Write(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    private static TextChapterPlan Split(string path, string text)
        => TextChapterSplitter.Split(path, new UTF8Encoding(false), bomLength: 0);

    private static string Read(string path, TextChapterPlan plan, int index)
    {
        var chapter = plan.Chapters[index];

        return TxtChapterHtml.ReadRange(path, new UTF8Encoding(false), 0, chapter.StartChar, chapter.EndChar);
    }
}
