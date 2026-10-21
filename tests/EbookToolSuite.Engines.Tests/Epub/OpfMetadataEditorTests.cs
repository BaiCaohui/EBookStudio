using System.Text;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Engines.Epub;

namespace EbookToolSuite.Engines.Tests.Epub;

/// <summary>
/// OPF 元数据编辑器（执行计划 T3-2）。判据编号对应计划第 3105 行起的 7 条完成判据。
/// </summary>
public sealed class OpfMetadataEditorTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow =
        new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private static string FixedStamp => "2026-03-04T05:06:07Z";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ets-opf-" + Guid.NewGuid().ToString("N"));

    public OpfMetadataEditorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // ================= 造样本 =================

    /// <summary>
    /// EPUB2 样本。刻意写成"XmlWriter 自己的风格"（小写 utf-8 声明、自闭合带空格），
    /// 这样零改动保存应当逐字节相同  任何 diff 都是编辑器造成的，不是格式噪声。
    /// </summary>
    private static string Opf2(string metadataInner, bool opfNamespaceOnMetadata = false, bool compact = false)
    {
        var close = compact ? "\"/>" : "\" />";
        var closeRef = compact ? "/>" : " />";

        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n"
             + "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"2.0\" unique-identifier=\"bookid\">\r\n"
             + "  <metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\""
             + (opfNamespaceOnMetadata ? " xmlns:opf=\"http://www.idpf.org/2007/opf\"" : string.Empty)
             + ">\r\n"
             + metadataInner
             + "  </metadata>\r\n"
             + "  <manifest>\r\n"
             + "    <item id=\"ch1\" href=\"ch1.xhtml\" media-type=\"application/xhtml+xml" + close + "\r\n"
             + "  </manifest>\r\n"
             + "  <spine>\r\n"
             + "    <itemref idref=\"ch1\"" + closeRef + "\r\n"
             + "  </spine>\r\n"
             + "</package>\r\n";
    }

    private static string Opf3(string metadataInner)
        => "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n"
         + "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"bookid\">\r\n"
         + "  <metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\">\r\n"
         + metadataInner
         + "    <meta property=\"dcterms:modified\">" + FixedStamp + "</meta>\r\n"
         + "  </metadata>\r\n"
         + "  <manifest>\r\n"
         + "    <item id=\"ch1\" href=\"ch1.xhtml\" media-type=\"application/xhtml+xml\" />\r\n"
         + "  </manifest>\r\n"
         + "  <spine>\r\n"
         + "    <itemref idref=\"ch1\" />\r\n"
         + "  </spine>\r\n"
         + "</package>\r\n";

    private const string BasicInner =
        "    <dc:title>测试书</dc:title>\r\n"
        + "    <dc:creator>作者甲</dc:creator>\r\n"
        + "    <dc:language>zh-CN</dc:language>\r\n";

    private string WriteOpf(string content)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".opf");
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private static OpfMetadataEditor Open(string path) => new(path, new FixedClock());

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset Now => FixedNow;
    }

    // ================= 比对工具 =================

    private static string[] SplitLines(string text) => text.Replace("\r\n", "\n").Split('\n');

    /// <summary>逐行比对，返回 "行号: [旧] -> [新]"。行数不同也会报出来。</summary>
    private static List<string> ChangedLines(string before, string after)
    {
        var a = SplitLines(before);
        var b = SplitLines(after);
        var changed = new List<string>();
        var count = Math.Max(a.Length, b.Length);

        for (var i = 0; i < count; i++)
        {
            var oldLine = i < a.Length ? a[i] : "<无>";
            var newLine = i < b.Length ? b[i] : "<无>";
            if (oldLine != newLine)
            {
                changed.Add($"{i + 1}: [{oldLine}] -> [{newLine}]");
            }
        }

        return changed;
    }

    private static string Read(string path) => File.ReadAllText(path, Encoding.UTF8);

    // ================= 判据 6：只有预期改动的行变化 =================

    [Fact]
    public void 判据6_只改书名时只有书名那一行变化()
    {
        var original = Opf2(BasicInner);
        var path = WriteOpf(original);

        var editor = Open(path);
        editor.SetTitle("新书名");
        editor.Save();

        var changed = ChangedLines(original, Read(path));

        Assert.Single(changed);
        Assert.Contains("新书名", changed[0]);
    }

    [Fact]
    public void 判据6_零改动保存逐字节相同()
    {
        var original = Opf2(BasicInner);
        var path = WriteOpf(original);

        Open(path).Save();

        Assert.Equal(original, Read(path));
    }

    [Fact]
    public void 判据6_EPUB3零改动保存逐字节相同()
    {
        var original = Opf3(BasicInner);
        var path = WriteOpf(original);

        Open(path).Save();

        Assert.Equal(original, Read(path));
    }

    [Fact]
    public void 判据6_manifest与spine不会因为改元数据而重排()
    {
        var original = Opf2(BasicInner);
        var path = WriteOpf(original);

        var editor = Open(path);
        editor.SetTitle("新书名");
        editor.SetPublisher("新出版社");
        editor.Save();

        var after = Read(path);

        // 自闭合写法与缩进原样保留（清单行的内容一字不差）
        Assert.Contains("    <item id=\"ch1\" href=\"ch1.xhtml\" media-type=\"application/xhtml+xml\" />", after);
        Assert.Contains("    <itemref idref=\"ch1\" />", after);
        Assert.Contains("  <spine>", after);
        Assert.Contains("</package>", after);
    }

    [Fact]
    public void 原文件用紧凑自闭合写法时零改动保存仍然逐字节相同()
    {
        // 真实电子书里大量 <itemref/>（Sample Book I有 106 处、Sample Book A有 1722 处）。
        // System.Xml 一律输出 <itemref ... />，必须在写回后按原风格还原，
        // 否则"只改一行"会变成"改了上千行"，正是判据 6 要防的重排。
        var original = Opf2(BasicInner, compact: true);
        var path = WriteOpf(original);

        Open(path).Save();

        Assert.Equal(original, Read(path));
    }

    [Fact]
    public void 改书名时紧凑自闭合写法也保持不变()
    {
        var original = Opf2(BasicInner, compact: true);
        var path = WriteOpf(original);

        var editor = Open(path);
        editor.SetTitle("新书名");
        editor.Save();

        var changed = ChangedLines(original, Read(path));

        Assert.Single(changed);
        Assert.Contains("新书名", changed[0]);
        Assert.Contains("<itemref idref=\"ch1\"/>", Read(path));
    }

    [Fact]
    public void 属性值里出现斜杠大于号时不会被误当成自闭合标签()
    {
        var inner =
            "    <dc:title>测试书</dc:title>\r\n"
            + "    <meta name=\"probe\" content=\"a /> b\"/>\r\n";
        var original = Opf2(inner, compact: true);
        var path = WriteOpf(original);

        Open(path).Save();

        var after = Read(path);

        // 属性值语义原样：里面的 " />" 没有被压成 "/>"（System.Xml 可能把 > 转义成 &gt;，两种都算对）
        var saved = System.Xml.Linq.XDocument.Load(path);
        var probe = saved
            .Descendants(System.Xml.Linq.XName.Get("meta", "http://www.idpf.org/2007/opf"))
            .First(e => (string?)e.Attribute("name") == "probe");
        Assert.Equal("a /> b", (string?)probe.Attribute("content"));

        // 标签本身恢复成紧凑写法
        Assert.DoesNotContain("b\" />", after);
    }

    [Fact]
    public void 注释里的斜杠大于号不会被改写()
    {
        var inner =
            "    <!-- 正文里会写 <img /> 这种标签 -->\r\n"
            + "    <dc:title>测试书</dc:title>\r\n";
        var original = Opf2(inner, compact: true);
        var path = WriteOpf(original);

        Open(path).Save();

        Assert.Contains("<!-- 正文里会写 <img /> 这种标签 -->", Read(path));
    }

    // ================= 判据 2：EPUB2 的 opf:role / opf:file-as =================

    [Fact]
    public void 判据2_EPUB2改作者写入opf_role与opf_file_as并声明xmlns_opf()
    {
        var path = WriteOpf(Opf2(BasicInner));

        var editor = Open(path);
        editor.SetAuthors(new[] { new EpubContributor("John Doe", "aut") });
        editor.Save();

        var after = Read(path);

        Assert.False(editor.IsEpub3);
        Assert.Contains("xmlns:opf=\"http://www.idpf.org/2007/opf\"", after);
        Assert.Contains("opf:role=\"aut\"", after);
        Assert.Contains("opf:file-as=\"John Doe\"", after);
        Assert.Contains(">John Doe</dc:creator>", after);
    }

    [Fact]
    public void 判据2_作者可以有多个且顺序保持()
    {
        var path = WriteOpf(Opf2(BasicInner));

        var editor = Open(path);
        editor.SetAuthors(new[]
        {
            new EpubContributor("作者甲", "aut"),
            new EpubContributor("作者乙", "aut"),
        });
        editor.Save();

        var after = Read(path);
        var first = after.IndexOf("作者甲", StringComparison.Ordinal);
        var second = after.IndexOf("作者乙", StringComparison.Ordinal);

        Assert.True(first > 0 && second > first, "两位作者要按传入顺序写入");
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(after, "opf:role=").Count);
    }

    [Fact]
    public void 没有xmlns_opf的EPUB2写作者后_元素前缀不被改写()
    {
        var path = WriteOpf(Opf2(BasicInner));

        var editor = Open(path);
        editor.SetAuthors(new[] { new EpubContributor("作者甲", "aut") });
        editor.Save();

        var after = Read(path);

        // 声明补在根元素上，且是第一个属性  否则整份 OPF 会被写成 <opf:package>
        Assert.Contains("<package xmlns:opf=\"http://www.idpf.org/2007/opf\" xmlns=\"http://www.idpf.org/2007/opf\"",
            after);
        Assert.DoesNotContain("<opf:package", after);
        Assert.DoesNotContain("<opf:metadata", after);
        Assert.DoesNotContain("<opf:meta ", after);
        Assert.DoesNotContain("<opf:item", after);
    }

    [Fact]
    public void 真实书形态_xmlns_opf原本在metadata上_元素也不会被加前缀()
    {
        // 复刻 TestCorpus\Sample Book I 的形态：xmlns:opf 声明在 <metadata> 上、dc:date 用 opf:event
        var inner =
            "    <dc:title>Sample Book I</dc:title>\r\n"
            + "    <dc:creator opf:role=\"aut\">Sample Author</dc:creator>\r\n"
            + "    <dc:date opf:event=\"publication\">2016-05-31 16:00:00+00:00</dc:date>\r\n"
            + "    <meta name=\"cover\" content=\"cover_img\" />\r\n";
        var original = Opf2(inner, opfNamespaceOnMetadata: true);
        var path = WriteOpf(original);

        var editor = Open(path);
        editor.SetTitle("Sample Book I（新版）");
        editor.Save();

        var after = Read(path);

        Assert.DoesNotContain("<opf:metadata", after);
        Assert.DoesNotContain("<opf:meta ", after);
        Assert.DoesNotContain("<opf:package", after);

        // 声明被归位到根元素第一个属性，metadata 上不再重复
        Assert.Contains("<package xmlns:opf=", after);
        Assert.DoesNotContain("xmlns:opf", after.Split('\n')[2]);

        // 原有 opf:* 属性保留
        Assert.Contains("opf:role=\"aut\"", after);
        Assert.Contains("opf:event=\"publication\"", after);

        // 归位本身的改动只涉及 2 行 + 书名 1 行
        var changed = ChangedLines(original, after);
        Assert.Equal(3, changed.Count);
    }

    [Fact]
    public void 声明归位是幂等的_连续保存两次结果相同()
    {
        var original = Opf2(BasicInner, opfNamespaceOnMetadata: true);
        var path = WriteOpf(original);

        var first = Open(path);
        first.SetTitle("新书名");
        first.Save();
        var once = Read(path);

        var second = Open(path);
        second.SetTitle("新书名");
        second.Save();

        Assert.Equal(once, Read(path));
    }

    // ================= 判据 3：EPUB3 走 refines，不出现 opf:* =================

    [Fact]
    public void 判据3_EPUB3改作者写成refines且不出现任何opf前缀属性()
    {
        var path = WriteOpf(Opf3(BasicInner));

        var editor = Open(path);
        Assert.True(editor.IsEpub3);

        editor.SetAuthors(new[] { new EpubContributor("John Doe", "aut") });
        editor.Save();

        var after = Read(path);

        Assert.Contains("<dc:creator id=\"creator-", after);
        Assert.Contains("property=\"role\"", after);
        Assert.Contains("scheme=\"marc:relators\">aut</meta>", after);
        Assert.Contains("property=\"file-as\">John Doe</meta>", after);
        Assert.Contains("refines=\"#creator-", after);

        // 规则 4：一个 opf:* 属性都不许有（xmlns:opf 声明也不该出现）
        Assert.DoesNotContain("opf:role", after);
        Assert.DoesNotContain("opf:file-as", after);
        Assert.DoesNotContain("xmlns:opf", after);
        Assert.DoesNotContain("<opf:", after);
    }

    [Fact]
    public void 判据3_EPUB3原本带opf_event的dc_date_改日期后不再出现opf属性()
    {
        var inner =
            "    <dc:title>测试书</dc:title>\r\n"
            + "    <dc:date opf:event=\"publication\">2016-05-31</dc:date>\r\n";
        var original = Opf3(inner).Replace("<metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\">",
            "<metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:opf=\"http://www.idpf.org/2007/opf\">");
        var path = WriteOpf(original);

        var editor = Open(path);
        editor.SetPublicationDate(new DateOnly(2024, 1, 2));
        editor.Save();

        var after = Read(path);

        Assert.Contains("<dc:date>2024-01-02</dc:date>", after);
        Assert.DoesNotContain("opf:event", after);
    }

    [Fact]
    public void EPUB2改日期会带上opf_event()
    {
        var path = WriteOpf(Opf2(BasicInner));

        var editor = Open(path);
        editor.SetPublicationDate(new DateOnly(2024, 1, 2));
        editor.Save();

        var after = Read(path);

        Assert.Contains("<dc:date opf:event=\"publication\">2024-01-02</dc:date>", after);
    }

    // ================= 判据 4：dcterms:modified =================

    [Fact]
    public void 判据4_EPUB3保存后dcterms_modified恰好一个且为当前UTC()
    {
        var path = WriteOpf(Opf3(BasicInner));

        Open(path).Save();

        var after = Read(path);
        var count = System.Text.RegularExpressions.Regex.Matches(
            after, "property=\"dcterms:modified\"").Count;

        Assert.Equal(1, count);
        Assert.Contains(">" + FixedStamp + "</meta>", after);
    }

    [Fact]
    public void 判据4_EPUB3原本有多个dcterms_modified_保存后只剩一个()
    {
        var inner = BasicInner
            + "    <meta property=\"dcterms:modified\">2020-01-01T00:00:00Z</meta>\r\n"
            + "    <meta property=\"dcterms:modified\">2021-02-02T00:00:00Z</meta>\r\n";
        var path = WriteOpf(Opf3(inner));

        Open(path).Save();

        var after = Read(path);

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(
            after, "property=\"dcterms:modified\""));
        Assert.Contains(">" + FixedStamp + "</meta>", after);
        Assert.DoesNotContain("2020-01-01T00:00:00Z", after);
        Assert.DoesNotContain("2021-02-02T00:00:00Z", after);
    }

    [Fact]
    public void 判据4_EPUB3缺dcterms_modified_保存时自动补一个()
    {
        var original = Opf3(BasicInner)
            .Replace("    <meta property=\"dcterms:modified\">" + FixedStamp + "</meta>\r\n", string.Empty);
        var path = WriteOpf(original);

        Open(path).Save();

        var after = Read(path);

        Assert.Contains("property=\"dcterms:modified\">" + FixedStamp + "</meta>", after);
    }

    [Fact]
    public void EPUB2不会写出dcterms_modified()
    {
        var path = WriteOpf(Opf2(BasicInner));

        Open(path).Save();

        Assert.DoesNotContain("dcterms:modified", Read(path));
    }

    [Fact]
    public void EPUB3保存时刷新dcterms_modified为当前时间()
    {
        var path = WriteOpf(Opf3(BasicInner));
        var clock = new MutableClock(new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero));

        var editor = new OpfMetadataEditor(path, clock);
        editor.Save();

        Assert.Contains(">2026-03-04T05:06:07Z</meta>", Read(path));

        clock.Now = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var again = new OpfMetadataEditor(path, clock);
        again.Save();

        Assert.Contains(">2027-01-01T00:00:00Z</meta>", Read(path));
    }

    private sealed class MutableClock : IClock
    {
        public MutableClock(DateTimeOffset now) => Now = now;

        public DateTimeOffset Now { get; set; }
    }

    // ================= 判据 7：中文不乱码、无 BOM =================

    [Fact]
    public void 判据7_中文书名作者写回后不乱码且不带BOM()
    {
        var path = WriteOpf(Opf2(BasicInner));

        var editor = Open(path);
        editor.SetTitle("红楼梦（校注本）");
        editor.SetAuthors(new[] { new EpubContributor("曹雪芹", "aut") });
        editor.SetPublisher("人民文学出版社");
        editor.Save();

        var bytes = File.ReadAllBytes(path);

        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "OPF 必须是「无 BOM」的 UTF-8（判据 6 的硬要求）");

        var text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        Assert.Contains("红楼梦（校注本）", text);
        Assert.Contains("曹雪芹", text);
        Assert.Contains("人民文学出版社", text);
        Assert.DoesNotContain("\uFFFD", text);
    }

    [Fact]
    public void 判据7_保留原文件的XML声明写法()
    {
        var path = WriteOpf(Opf2(BasicInner));

        Open(path).Save();

        var firstLine = SplitLines(Read(path))[0];
        Assert.Equal("<?xml version=\"1.0\" encoding=\"utf-8\"?>", firstLine);
    }

    // ================= 读 =================

    [Fact]
    public void 读回来的字段与写入的一致()
    {
        var path = WriteOpf(Opf3(BasicInner));

        var editor = Open(path);
        editor.SetTitle("新书名");
        editor.SetSubtitle("副标题");
        editor.SetAuthors(new[]
        {
            new EpubContributor("作者甲", "aut"),
            new EpubContributor("作者乙", "aut"),
        });
        editor.SetContributors(new[] { new EpubContributor("译者丙", "trl") });
        editor.SetLanguage("zh-TW");
        editor.SetPublisher("出版社");
        editor.SetDescription("简介文本");
        editor.SetPublicationDate(new DateOnly(2024, 5, 6));
        editor.SetSubjects(new[] { "标签一", "标签二" });
        editor.SetSeries("系列名", 2.5);
        editor.SetRating(4);
        editor.SetIsbn("9787111123456");
        editor.SetCoverItemId("cover-img");
        editor.Save();

        var metadata = Open(path).Read();

        Assert.Equal("新书名", metadata.Title);
        Assert.Equal("副标题", metadata.Subtitle);
        Assert.Equal(2, metadata.Authors.Count);
        Assert.Equal("作者甲", metadata.Authors[0].Name);
        Assert.Equal("aut", metadata.Authors[0].Role);
        Assert.Equal("作者乙", metadata.Authors[1].Name);
        Assert.Single(metadata.Contributors);
        Assert.Equal("译者丙", metadata.Contributors[0].Name);
        Assert.Equal("trl", metadata.Contributors[0].Role);
        Assert.Equal("zh-TW", metadata.Language);
        Assert.Equal("出版社", metadata.Publisher);
        Assert.Equal("简介文本", metadata.Description);
        Assert.Equal(new DateOnly(2024, 5, 6), metadata.PublicationDate);
        Assert.Equal(new[] { "标签一", "标签二" }, metadata.Subjects);
        Assert.Equal("系列名", metadata.Series);
        Assert.Equal(2.5, metadata.SeriesIndex);
        Assert.Equal(4, metadata.Rating);          // 模型是 0-5 星
        Assert.Equal("9787111123456", metadata.Isbn);
        Assert.Equal("cover-img", metadata.CoverItemId);
        Assert.True(metadata.IsEpub3);
    }

    [Fact]
    public void 读_EPUB2的opf_role与file_as能读出来()
    {
        var inner =
            "    <dc:title>测试书</dc:title>\r\n"
            + "    <dc:creator opf:role=\"aut\" opf:file-as=\"Doe, John\">John Doe</dc:creator>\r\n"
            + "    <dc:language>en</dc:language>\r\n";
        var path = WriteOpf(Opf2(inner, opfNamespaceOnMetadata: true));

        var metadata = Open(path).Read();

        Assert.Equal("John Doe", metadata.Authors[0].Name);
        Assert.Equal("aut", metadata.Authors[0].Role);
        Assert.Equal("Doe, John", metadata.Authors[0].FileAs);
        Assert.False(metadata.IsEpub3);
    }

    [Fact]
    public void 读_EPUB3的副标题不会被当成主标题()
    {
        var inner =
            "    <dc:title>主标题</dc:title>\r\n"
            + "    <dc:title id=\"t2\">副标题</dc:title>\r\n"
            + "    <meta refines=\"#t2\" property=\"title-type\">subtitle</meta>\r\n"
            + "    <dc:language>zh</dc:language>\r\n";
        var path = WriteOpf(Opf3(inner));

        var metadata = Open(path).Read();

        Assert.Equal("主标题", metadata.Title);
        Assert.Equal("副标题", metadata.Subtitle);
    }

    [Fact]
    public void 读_评分按Calibre的0到10折成0到5星()
    {
        var inner = BasicInner + "    <meta name=\"calibre:rating\" content=\"8\"/>\r\n";
        var path = WriteOpf(Opf2(inner));

        Assert.Equal(4, Open(path).Read().Rating);
    }

    // ================= 系列 / 评分 / 标签 的写 =================

    [Fact]
    public void 系列与评分走Calibre的name与content形式()
    {
        var path = WriteOpf(Opf2(BasicInner));

        var editor = Open(path);
        editor.SetSeries("Sample Book J", 1.5);
        editor.SetRating(5);
        editor.Save();

        var after = Read(path);

        Assert.Contains("<meta name=\"calibre:series\" content=\"Sample Book J\" />", after);
        Assert.Contains("<meta name=\"calibre:series_index\" content=\"1.5\" />", after);
        Assert.Contains("<meta name=\"calibre:rating\" content=\"10\" />", after);
    }

    [Fact]
    public void 标签会被整体替换()
    {
        var inner = BasicInner + "    <dc:subject>旧标签</dc:subject>\r\n";
        var path = WriteOpf(Opf2(inner));

        var editor = Open(path);
        editor.SetSubjects(new[] { "新一", "新二" });
        editor.Save();

        var after = Read(path);

        Assert.DoesNotContain("旧标签", after);
        Assert.Contains("<dc:subject>新一</dc:subject>", after);
        Assert.Contains("<dc:subject>新二</dc:subject>", after);
    }

    [Fact]
    public void 传null会删掉对应元素()
    {
        var path = WriteOpf(Opf2(BasicInner));

        var editor = Open(path);
        editor.SetPublisher("临时出版社");
        editor.SetIsbn("9787111123456");
        editor.SetRating(3);
        editor.Save();

        Assert.Contains("临时出版社", Read(path));

        var second = Open(path);
        second.SetPublisher(null);
        second.SetIsbn(null);
        second.SetRating(0);
        second.SetTitle(null);
        second.Save();

        var after = Read(path);

        Assert.DoesNotContain("临时出版社", after);
        Assert.DoesNotContain("<dc:publisher", after);
        Assert.DoesNotContain("9787111123456", after);
        Assert.DoesNotContain("calibre:rating", after);
        Assert.DoesNotContain("<dc:title", after);
    }

    // ================= 损坏输入 =================

    [Fact]
    public void 缺metadata时抛EpubInvalid()
    {
        var path = WriteOpf(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n"
            + "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"2.0\">\r\n"
            + "  <manifest />\r\n"
            + "</package>\r\n");

        var ex = Assert.Throws<AppException>(() => new OpfMetadataEditor(path));

        Assert.Equal(ErrorCode.EpubInvalid, ex.Code);
    }

    [Fact]
    public void 不是XML时抛EpubInvalid()
    {
        var path = WriteOpf("这根本不是 XML");

        var ex = Assert.Throws<AppException>(() => new OpfMetadataEditor(path));

        Assert.Equal(ErrorCode.EpubInvalid, ex.Code);
    }

    [Fact]
    public void 缺version属性时按EPUB2处理()
    {
        var path = WriteOpf(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n"
            + "<package xmlns=\"http://www.idpf.org/2007/opf\">\r\n"
            + "  <metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\">\r\n"
            + "    <dc:title>测试书</dc:title>\r\n"
            + "  </metadata>\r\n"
            + "</package>\r\n");

        var editor = Open(path);

        Assert.False(editor.IsEpub3);
        Assert.Equal("2.0", editor.Version);
    }
}






