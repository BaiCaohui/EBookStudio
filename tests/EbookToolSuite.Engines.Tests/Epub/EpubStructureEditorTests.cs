using System.IO.Compression;
using System.Xml.Linq;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Engines.Epub;
using EbookToolSuite.Engines.Text;

namespace EbookToolSuite.Engines.Tests.Epub;

/// <summary>
/// T5-1 章节结构编辑的单测：读树 / 重命名 / 移动 / 删除 / 新增 / 导入 / 合并。
/// </summary>
/// <remarks>
/// 全部在真实文件系统上跑（仓库惯例：不 mock），每个用例自己铺一个工作区、用完删掉。
/// "保存出来的 EPUB 能不能过 EpubCheck"由 EpubStructureIntegrationTests 用真工具兜底。
/// </remarks>
public sealed class EpubStructureEditorTests
{
    // ================= 读章节树（判据 1） =================

    [Fact]
    public void 读章节树_EPUB3_标题来自nav_嵌套层级也读得出来()
    {
        using var fixture = new StructureSandbox(StructureFixture.Create(epub3: true, chapters: 3, nestedToc: true));

        var chapters = fixture.Editor().ReadChapters();

        Assert.Equal(3, chapters.Count);
        Assert.Equal(new[] { "c1", "c2", "c3" }, chapters.Select(c => c.Id));
        Assert.Equal(StructureFixture.ChapterHref(1), chapters[0].Href);
        Assert.Equal("第 1 章", chapters[0].Title);
        Assert.True(chapters[0].TitleFromToc);
        Assert.Equal(1, chapters[0].Level);
        Assert.Equal(2, chapters[1].Level);   // 嵌套在第一章下面的子项
        Assert.All(chapters, c => Assert.True(c.Exists));
    }

    [Fact]
    public void 读章节树_EPUB2_标题来自toc_ncx()
    {
        using var fixture = new StructureSandbox(StructureFixture.Create(epub3: false, chapters: 3));

        var chapters = fixture.Editor(epub3: false).ReadChapters();

        Assert.Equal(3, chapters.Count);
        Assert.Equal("第 2 章", chapters[1].Title);
        Assert.True(chapters[1].TitleFromToc);
        Assert.Equal(1, chapters[1].Level);
    }

    [Fact]
    public void 读章节树_目录里缺的章_标题退回文件名()
    {
        // 真实电子书很常见：3 章正文，目录只列了前 2 章。
        using var fixture = new StructureSandbox(
            StructureFixture.Create(epub3: true, chapters: 3, tocChapters: 2));

        var chapters = fixture.Editor().ReadChapters();

        Assert.Equal(3, chapters.Count);
        Assert.True(chapters[0].TitleFromToc);
        Assert.False(chapters[2].TitleFromToc);
        Assert.Equal("chapter-0003", chapters[2].Title);   // 退回文件名（不带扩展名）
    }

    [Fact]
    public void 读章节树_目录里多出的条目_不会凭空多出章节()
    {
        // 目录里列了 4 条，spine 只有 3 章：spine 是权威，多出来的条目不该变成章节。
        using var fixture = new StructureSandbox(
            StructureFixture.Create(epub3: true, chapters: 3, tocChapters: 4));

        var chapters = fixture.Editor().ReadChapters();

        Assert.Equal(3, chapters.Count);
    }

    // ================= 重命名（判据 2） =================

    [Fact]
    public void 重命名_EPUB3_只改nav里的标题_文件名与OPF一个字节都不动()
    {
        using var fixture = new StructureSandbox(StructureFixture.Create(epub3: true, chapters: 3));
        var opfBefore = fixture.Read(StructureFixture.OpfEntry);
        var chapterBefore = fixture.Read(StructureFixture.ChapterHref(1));

        var result = fixture.Editor().Rename(0, "改名后的第一章");

        Assert.Equal("rename", result.Operation);
        Assert.Contains(StructureFixture.TocEntry(true), result.ChangedFiles);

        var nav = fixture.Read(StructureFixture.TocEntry(true));
        Assert.Contains("改名后的第一章", nav, StringComparison.Ordinal);
        Assert.DoesNotContain("第 1 章", nav, StringComparison.Ordinal);

        // 关键实现点 3：只改标题，不动文件名；OPF 更不该动。
        Assert.Equal(opfBefore, fixture.Read(StructureFixture.OpfEntry));
        Assert.Equal(chapterBefore, fixture.Read(StructureFixture.ChapterHref(1)));
        Assert.True(fixture.Exists(StructureFixture.ChapterHref(1)));
    }

    [Fact]
    public void 重命名_EPUB2_改的是ncx里的text()
    {
        using var fixture = new StructureSandbox(StructureFixture.Create(epub3: false, chapters: 3));

        fixture.Editor(epub3: false).Rename(1, "改过的第二章");

        var ncx = fixture.Read(StructureFixture.TocEntry(false));
        Assert.Contains("改过的第二章", ncx, StringComparison.Ordinal);
        Assert.DoesNotContain("第 2 章", ncx, StringComparison.Ordinal);
    }

    [Fact]
    public void 重命名_目录里没有的章_会补一条目录项()
    {
        // 第 3 章不在目录里：改名后目录里要能看到它（否则改了也白改）。
        using var fixture = new StructureSandbox(
            StructureFixture.Create(epub3: true, chapters: 3, tocChapters: 2));

        fixture.Editor().Rename(2, "补进来的第三章");

        var nav = fixture.Read(StructureFixture.TocEntry(true));
        Assert.Contains("补进来的第三章", nav, StringComparison.Ordinal);
        Assert.Contains("chapter-0003.xhtml", nav, StringComparison.Ordinal);
    }

    [Fact]
    public void 重命名_没有目录文件_不动文件并把原因说清楚()
    {
        using var fixture = new StructureSandbox(
            StructureFixture.Create(epub3: true, chapters: 3, withToc: false));
        var opfBefore = fixture.Read(StructureFixture.OpfEntry);

        var result = fixture.Editor().Rename(0, "改不了");

        Assert.Empty(result.ChangedFiles);
        Assert.Contains("没有目录文件", result.Message, StringComparison.Ordinal);
        Assert.Equal(opfBefore, fixture.Read(StructureFixture.OpfEntry));
    }

    [Fact]
    public void 重命名_空标题_被拒()
    {
        using var fixture = new StructureSandbox(StructureFixture.Create(epub3: true, chapters: 3));

        var exception = Assert.Throws<AppException>(() => fixture.Editor().Rename(0, "   "));

        Assert.Equal(ErrorCode.ValidationFailed, exception.Code);
    }

    // ================= 移动（判据 4） =================

    [Fact]
    public void 移动_EPUB3_spine与目录顶层顺序一起变()
    {
        using var fixture = new StructureSandbox(StructureFixture.Create(epub3: true, chapters: 3));

        fixture.Editor().Move(0, 2);

        var opf = fixture.Read(StructureFixture.OpfEntry);
        Assert.Equal(new[] { "c2", "c3", "c1" }, SpineIds(opf));

        var nav = fixture.Read(StructureFixture.TocEntry(true));
        var order = XDocument.Parse(nav).Descendants()
            .Where(e => e.Name.LocalName == "a")
            .Select(e => (string?)e.Attribute("href"))
            .ToList();
        Assert.Equal(
            new[] { "Text/chapter-0002.xhtml", "Text/chapter-0003.xhtml", "Text/chapter-0001.xhtml" },
            order);
    }

    [Fact]
    public void 移动_EPUB2_playOrder跟着重排()
    {
        using var fixture = new StructureSandbox(StructureFixture.Create(epub3: false, chapters: 3));

        fixture.Editor(epub3: false).Move(2, 0);

        var ncx = fixture.Read(StructureFixture.TocEntry(false));
        var document = XDocument.Parse(ncx);
        var points = document.Descendants().Where(e => e.Name.LocalName == "navPoint").ToList();

        Assert.Equal(new[] { "1", "2", "3" },
            points.Select(p => (string?)p.Attribute("playOrder")));
        Assert.Contains("chapter-0003.xhtml",
            (string?)points[0].Descendants().First(e => e.Name.LocalName == "content").Attribute("src"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void 移动_序号越界_抛验证失败()
    {
        using var fixture = new StructureSandbox(StructureFixture.Create(epub3: true, chapters: 3));

        var exception = Assert.Throws<AppException>(() => fixture.Editor().Move(0, 5));

        Assert.Equal(ErrorCode.ValidationFailed, exception.Code);
    }

    // ================= 删除（判据 3） =================

    [Fact]
    public void 删除_EPUB3_spine_manifest_目录_文件四件套同步_文件进暂存区()
    {
        using var fixture = new StructureSandbox(StructureFixture.Create(epub3: true, chapters: 3));

        var result = fixture.Editor().Delete(1);

        Assert.Equal("delete", result.Operation);

        var opf = fixture.Read(StructureFixture.OpfEntry);
        Assert.Equal(new[] { "c1", "c3" }, SpineIds(opf));
        Assert.DoesNotContain("id=\"c2\"", opf, StringComparison.Ordinal);

        var nav = fixture.Read(StructureFixture.TocEntry(true));
        Assert.DoesNotContain("chapter-0002.xhtml", nav, StringComparison.Ordinal);

        Assert.False(fixture.Exists(StructureFixture.ChapterHref(2)));
        Assert.True(fixture.Exists(".deleted/" + StructureFixture.ChapterHref(2)));   // 保存前还能捞回来
        Assert.True(fixture.Exists(StructureFixture.ChapterHref(1)));
        Assert.True(fixture.Exists(StructureFixture.ChapterHref(3)));
    }

    [Fact]
    public void 删除_最后一章_被拒()
    {
        using var fixture = new StructureSandbox(StructureFixture.Create(epub3: true, chapters: 1));

        var exception = Assert.Throws<AppException>(() => fixture.Editor().Delete(0));

        Assert.Equal(ErrorCode.EpubInvalid, exception.Code);
        Assert.True(fixture.Exists(StructureFixture.ChapterHref(1)));
    }

    // ================= 新增（判据 5） =================

    [Fact]
    public void 新增空章节_EPUB3_文件良构_manifest与spine同步_目录里也有()
    {
        using var fixture = new StructureSandbox(StructureFixture.Create(epub3: true, chapters: 3));

        var result = fixture.Editor().AddEmpty(1, "插进来的新章");

        Assert.True(fixture.Exists(result.Href!));
        var content = fixture.Read(result.Href!);
        Assert.Contains("插进来的新章", content, StringComparison.Ordinal);
        Assert.NotNull(XDocument.Parse(content));                          // 良构
        Assert.Contains("<meta charset=\"utf-8\" />", content, StringComparison.Ordinal);

        var opf = fixture.Read(StructureFixture.OpfEntry);
        var spine = SpineIds(opf);
        Assert.Equal(4, spine.Count);
        Assert.Equal("c1", spine[0]);
        Assert.Equal("c2", spine[2]);                                      // 插在第 2 位

        var added = XDocument.Parse(opf).Descendants()
            .Single(e => e.Name.LocalName == "item" && (string?)e.Attribute("id") == spine[1]);
        Assert.Equal("application/xhtml+xml", (string?)added.Attribute("media-type"));
        Assert.Null(added.Attribute("properties"));                        // 正文条目不该带 properties

        Assert.Contains("插进来的新章", fixture.Read(StructureFixture.TocEntry(true)), StringComparison.Ordinal);
    }

    [Fact]
    public void 新增空章节_EPUB2_head里用http_equiv而不是meta_charset()
    {
        using var fixture = new StructureSandbox(StructureFixture.Create(epub3: false, chapters: 2));

        var result = fixture.Editor(epub3: false).AddEmpty(0, "新的第一章");

        var content = fixture.Read(result.Href!);
        Assert.Contains("http-equiv=\"Content-Type\"", content, StringComparison.Ordinal);
        Assert.DoesNotContain("<meta charset", content, StringComparison.Ordinal);   // XHTML 1.1 没有这个写法
        Assert.Contains("XHTML 1.1//EN", content, StringComparison.Ordinal);         // EPUB2 的 DOCTYPE
        var opf = fixture.Read(StructureFixture.OpfEntry);
        var spine = SpineIds(opf);
        Assert.Equal(3, spine.Count);
        Assert.Equal("c1", spine[1]);      // 插在最前面，原来的第一章退到第 2 位

        var added = XDocument.Parse(opf).Descendants()
            .Single(e => e.Name.LocalName == "item" && (string?)e.Attribute("id") == spine[0]);
        Assert.Equal("application/xhtml+xml", (string?)added.Attribute("media-type"));
        Assert.Null(added.Attribute("properties"));   // EPUB2 更不能有 properties（那是 EPUB3 的写法）
    }

    // ================= 导入（判据 5 的兄弟：从文件导入章节） =================

    [Fact]
    public void 导入txt_GBK编码的中文正文不乱码()
    {
        using var fixture = new StructureSandbox(StructureFixture.Create(epub3: false, chapters: 2));
        var source = Path.Combine(fixture.WorkDir, "gbk.txt");
        var gb18030 = TextEncodingSupport.Resolve("GB18030")!;
        File.WriteAllBytes(source, gb18030.GetBytes("这是一段中文正文。\r\n第二段中文。"));

        var result = fixture.Editor(epub3: false).Import(1, source, "导入的中文章");

        var content = fixture.Read(result.Href!);
        Assert.Contains("这是一段中文正文。", content, StringComparison.Ordinal);
        Assert.Contains("第二段中文。", content, StringComparison.Ordinal);
        Assert.DoesNotContain("\uFFFD", content, StringComparison.Ordinal);   // 没有替换符 = 没乱码
        Assert.NotNull(XDocument.Parse(content));
    }

    [Fact]
    public void 导入xhtml_只搬body内容_不重复html与body标签()
    {
        using var fixture = new StructureSandbox(StructureFixture.Create(epub3: true, chapters: 2));
        var source = Path.Combine(fixture.WorkDir, "imported.xhtml");
        File.WriteAllText(source,
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<html xmlns=\"http://www.w3.org/1999/xhtml\">\n<body><h2>导入的标题</h2><p>导入的正文</p></body>\n</html>\n",
            new System.Text.UTF8Encoding(false));

        var result = fixture.Editor().Import(2, source, "导入章");

        var content = fixture.Read(result.Href!);
        Assert.Contains("导入的标题", content, StringComparison.Ordinal);
        Assert.Contains("导入的正文", content, StringComparison.Ordinal);
        Assert.Equal(1, CountOf(content, "<html"));
        Assert.Equal(1, CountOf(content, "<body"));
    }

    [Fact]
    public void 导入_坏掉的xhtml_被拒_工作区不变()
    {
        using var fixture = new StructureSandbox(StructureFixture.Create(epub3: true, chapters: 2));
        var source = Path.Combine(fixture.WorkDir, "broken.xhtml");
        File.WriteAllText(source, "<html><body><p>没闭合", new System.Text.UTF8Encoding(false));
        var opfBefore = fixture.Read(StructureFixture.OpfEntry);

        var exception = Assert.Throws<AppException>(() => fixture.Editor().Import(1, source, "坏文件"));

        Assert.Equal(ErrorCode.EpubInvalid, exception.Code);
        Assert.Equal(opfBefore, fixture.Read(StructureFixture.OpfEntry));
        Assert.Equal(2, Directory.GetFiles(fixture.PathOf("OEBPS/Text")).Length);   // 工作区里还是原来那两章
    }

    // ================= 合并（判据 6） =================

    [Fact]
    public void 合并_内容拼接_只有一个html与body_后一章被删并进暂存区()
    {
        using var fixture = new StructureSandbox(StructureFixture.Create(epub3: true, chapters: 3));

        var result = fixture.Editor().Merge(0);

        var merged = fixture.Read(StructureFixture.ChapterHref(1));
        Assert.Contains("正文 1", merged, StringComparison.Ordinal);
        Assert.Contains("正文 2", merged, StringComparison.Ordinal);     // 第二章的内容进来了
        Assert.Equal(1, CountOf(merged, "<html"));
        Assert.Equal(1, CountOf(merged, "<body"));
        Assert.NotNull(XDocument.Parse(merged));

        Assert.Equal("merge", result.Operation);
        Assert.Equal(new[] { "c1", "c3" }, SpineIds(fixture.Read(StructureFixture.OpfEntry)));
        Assert.False(fixture.Exists(StructureFixture.ChapterHref(2)));
        Assert.True(fixture.Exists(".deleted/" + StructureFixture.ChapterHref(2)));
    }

    [Fact]
    public void 合并_最后一章_被拒()
    {
        using var fixture = new StructureSandbox(StructureFixture.Create(epub3: true, chapters: 2));

        var exception = Assert.Throws<AppException>(() => fixture.Editor().Merge(1));

        Assert.Equal(ErrorCode.ValidationFailed, exception.Code);
    }

    // ================= 目录层级（升级 / 降级） =================

    [Fact]
    public void 目录降级_EPUB3_条目挪进前一个兄弟的子列表()
    {
        using var fixture = new StructureSandbox(StructureFixture.Create(epub3: true, chapters: 3));

        fixture.Editor().Demote(1);

        var nav = fixture.Read(StructureFixture.TocEntry(true));
        var items = XDocument.Parse(nav).Descendants().Where(e => e.Name.LocalName == "li").ToList();

        // 条目一个都没少，变的是嵌套：第二章现在挂在第一章底下
        Assert.Equal(3, items.Count);
        // 注意别用 li.ToString().Contains(...)：那会命中"把第二章包在里面的父 li"（嵌套后父项也含这个 href）
        var second = items.First(li =>
            (string?)li.Elements().FirstOrDefault(e => e.Name.LocalName == "a")?.Attribute("href")
            == "Text/chapter-0002.xhtml");
        var parentItem = second.Ancestors().FirstOrDefault(e => e.Name.LocalName == "li");

        Assert.True(parentItem is not null, "降级后的目录内容：" + nav);
        Assert.Contains("chapter-0001.xhtml", parentItem!.ToString(), StringComparison.Ordinal);

        var chapters = fixture.Editor().ReadChapters();
        Assert.Equal(1, chapters[0].Level);
        Assert.Equal(2, chapters[1].Level);                 // 章节树读回来的层级也跟着深了一级
    }

    [Fact]
    public void 目录升级_EPUB3_从父项里提出来()
    {
        using var fixture = new StructureSandbox(
            StructureFixture.Create(epub3: true, chapters: 3, nestedToc: true));

        Assert.Equal(2, fixture.Editor().ReadChapters()[1].Level);   // 夹具里第二章嵌在第一章下

        fixture.Editor().Promote(1);

        var chapters = fixture.Editor().ReadChapters();
        Assert.All(chapters, chapter => Assert.Equal(1, chapter.Level));

        var nav = fixture.Read(StructureFixture.TocEntry(true));
        Assert.Equal(1, CountOf(nav, "<ol"));               // 只剩顶层那一个列表
    }

    [Fact]
    public void 目录降级_EPUB2_变成父navPoint的子项且playOrder重排()
    {
        using var fixture = new StructureSandbox(StructureFixture.Create(epub3: false, chapters: 3));

        fixture.Editor(epub3: false).Demote(2);

        var document = XDocument.Parse(fixture.Read(StructureFixture.TocEntry(false)));
        var top = document.Descendants()
            .First(e => e.Name.LocalName == "navMap")
            .Elements()
            .Where(e => e.Name.LocalName == "navPoint")
            .ToList();

        Assert.Equal(2, top.Count);
        Assert.Single(top[1].Elements(), e => e.Name.LocalName == "navPoint");

        // 子 navPoint 必须排在 <content> 之后，否则 NCX 结构不合法
        var order = top[1].Elements().Select(e => e.Name.LocalName).ToList();
        Assert.True(order.IndexOf("content") < order.IndexOf("navPoint"));

        Assert.Equal(
            new[] { "1", "2", "3" },
            document.Descendants()
                .Where(e => e.Name.LocalName == "navPoint")
                .Select(e => (string?)e.Attribute("playOrder")));
    }

    [Fact]
    public void 目录升级_EPUB2_提出来之后父项还在()
    {
        using var fixture = new StructureSandbox(StructureFixture.Create(epub3: false, chapters: 3));

        var editor = fixture.Editor(epub3: false);
        editor.Demote(2);
        Assert.Equal(2, editor.ReadChapters()[2].Level);

        editor.Promote(2);

        Assert.Equal(3, editor.ReadChapters().Count(chapter => chapter.Level == 1));
    }

    [Fact]
    public void 目录层级_第一项不能降级_顶层不能升级_不在目录里的章被拒()
    {
        using var outOfBounds = new StructureSandbox(StructureFixture.Create(epub3: true, chapters: 3));
        var editor = outOfBounds.Editor();

        Assert.Equal(ErrorCode.ValidationFailed, Assert.Throws<AppException>(() => editor.Demote(0)).Code);
        Assert.Equal(ErrorCode.ValidationFailed, Assert.Throws<AppException>(() => editor.Promote(0)).Code);

        using var missing = new StructureSandbox(
            StructureFixture.Create(epub3: true, chapters: 3, tocChapters: 2));
        var exception = Assert.Throws<AppException>(() => missing.Editor().Demote(2));

        Assert.Equal(ErrorCode.ValidationFailed, exception.Code);
        Assert.Contains("不在目录里", exception.UserMessage, StringComparison.Ordinal);
    }

    // ================= 拆分章节 =================

    [Fact]
    public void 拆分章节_前半留在原文件_后半写成新章并紧跟其后()
    {
        using var fixture = new StructureSandbox(StructureFixture.Create(epub3: true, chapters: 2));
        var editor = fixture.Editor();
        var before = editor.ReadChapters();

        var points = editor.ReadSplitPoints(0);
        Assert.Single(points);                       // 夹具每章两块：<h1> 与 <p>

        var result = editor.Split(0, points[0].SplitAt);

        var after = editor.ReadChapters();
        Assert.Equal(before.Count + 1, after.Count);
        Assert.Equal(before[0].Href, after[0].Href);      // 原章还在原位
        Assert.Equal(result.Href, after[1].Href);         // 新章紧跟其后
        Assert.True(fixture.Exists(result.Href!));

        var head = fixture.Read(before[0].Href);
        Assert.Contains("第 1 章", head, StringComparison.Ordinal);
        Assert.DoesNotContain("正文 1", head, StringComparison.Ordinal);   // 后半段已经被搬走

        var tail = fixture.Read(result.Href!);
        Assert.Contains("第 1 章（续）", tail, StringComparison.Ordinal);
        Assert.Contains("正文 1", tail, StringComparison.Ordinal);
        Assert.NotNull(XDocument.Parse(head));            // 两半都是良构 XHTML
        Assert.NotNull(XDocument.Parse(tail));

        var opf = fixture.Read(StructureFixture.OpfEntry);
        Assert.Equal(3, SpineIds(opf).Count);             // spine 也跟着长了一章
        Assert.Contains("第 1 章（续）", fixture.Read(StructureFixture.TocEntry(true)), StringComparison.Ordinal);
    }

    [Fact]
    public void 删除章节_同时清掉OPF的guide引用()
    {
        // 真实书里 guide 会有指向章节的 reference（如"封面页"）；删章节时必须一起清，
        // 否则 OPF 留悬空引用（取证实例：Sample Book F 的
        // <reference href="Text/opencover.html" type="cover page"/>）
        using var fixture = new StructureSandbox(StructureFixture.Create(epub3: true, chapters: 2));
        var editor = fixture.Editor();
        var chapters = editor.ReadChapters();
        var opfPath = Path.Combine(fixture.WorkDir, StructureFixture.OpfEntry);
        var href = NavDocumentBuilder.RelativeHref(StructureFixture.OpfEntry, chapters[1].Href);
        var opf = File.ReadAllText(opfPath);

        opf = opf.Replace(
            "</package>",
            "<guide><reference type=\"cover page\" title=\"Cover\" href=\"" + href + "\"/></guide></package>",
            StringComparison.Ordinal);
        File.WriteAllText(opfPath, opf);

        Assert.Contains(href, File.ReadAllText(opfPath), StringComparison.Ordinal);

        editor.Delete(1);

        var after = File.ReadAllText(opfPath);

        Assert.DoesNotContain("cover page", after, StringComparison.Ordinal);
        Assert.DoesNotContain(href, after, StringComparison.Ordinal);
    }

    [Fact]
    public void 拆分章节_单块正文与越界拆分点都被拒()
    {
        using var fixture = new StructureSandbox(StructureFixture.Create(epub3: true, chapters: 1));
        var editor = fixture.Editor();

        // 夹具的章有 <h1> + <p> 两块，能拆；拆分点只有 1
        Assert.Equal(ErrorCode.ValidationFailed, Assert.Throws<AppException>(() => editor.Split(0, 0)).Code);
        Assert.Equal(ErrorCode.ValidationFailed, Assert.Throws<AppException>(() => editor.Split(0, 5)).Code);
    }

    // ================= 暂存区不打进 EPUB =================

    [Fact]
    public async Task 打包_暂存区与脚手架文件都不会进EPUB()
    {
        using var fixture = new StructureSandbox(StructureFixture.Create(epub3: true, chapters: 3));
        fixture.Editor().Delete(1);
        File.WriteAllText(fixture.PathOf(".session.json"), "{}");
        Directory.CreateDirectory(fixture.PathOf(".snapshots"));
        File.WriteAllText(fixture.PathOf(".snapshots/s1.json"), "{}");

        var output = Path.Combine(Path.GetTempPath(), "ets-structure-" + Guid.NewGuid().ToString("N") + ".epub");

        try
        {
            var result = await new EpubPackager().PackageAsync(fixture.WorkDir, output);

            Assert.True(File.Exists(output));
            Assert.True(result.EntryCount > 0);

            using var archive = ZipFile.OpenRead(output);
            var names = archive.Entries.Select(e => e.FullName).ToList();

            Assert.DoesNotContain(names, n => n.StartsWith(".deleted", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(names, n => n.StartsWith(".snapshots", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(names, n => n.Contains("chapter-0002.xhtml", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("mimetype", names);
        }
        finally
        {
            if (File.Exists(output))
            {
                File.Delete(output);
            }
        }
    }

    // ================= 小工具 =================

    private static IReadOnlyList<string> SpineIds(string opfXml)
        => XDocument.Parse(opfXml).Descendants()
            .Where(e => e.Name.LocalName == "itemref")
            .Select(e => (string?)e.Attribute("idref") ?? string.Empty)
            .ToList();

    private static int CountOf(string text, string needle)
    {
        var count = 0;
        var index = text.IndexOf(needle, StringComparison.Ordinal);

        while (index >= 0)
        {
            count++;
            index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
