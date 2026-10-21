using EbookToolSuite.Core;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Infrastructure.Config;
using EbookToolSuite.Services.Editing;
using EbookToolSuite.Services.Scanning;
using Microsoft.Extensions.Logging.Abstractions;

namespace EbookToolSuite.Services.Tests.Scanning;

/// <summary>
/// T5-5 资源引用扫描的验收测试，对应执行计划 5 条完成判据：
/// 删图 -> 悬空引用且能定位章节与行 / 加无用图 -> 孤儿资源 / CSS url() 按 CSS 所在目录解析 /
/// 500 章 &lt;= 5 秒 / data: URI 不误报。走真实文件系统与真实 EPUB，不 mock。
/// </summary>
public sealed class ResourceScanServiceTests : IDisposable
{
    private const string ChapterEntry = "OEBPS/ch1.xhtml";

    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly EpubWorkspaceFactory _factory;

    public ResourceScanServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ets-rs-" + Guid.NewGuid().ToString("N"));
        var appRoot = Path.Combine(_root, "app");
        var userLocal = Path.Combine(_root, "localappdata", "EbookToolSuite");
        Directory.CreateDirectory(appRoot);
        Directory.CreateDirectory(userLocal);

        _paths = AppPaths.Create(new AppPathsOptions
        {
            AppRoot = appRoot,
            UserLocalRoot = userLocal,
            Fallback = DataDirFallback.Throw,
        });
        _paths.EnsureCreated();

        _factory = new EpubWorkspaceFactory(
            _paths, SystemClock.Instance, NullLogger<EpubWorkspaceFactory>.Instance);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // 清理失败不影响结论
        }
    }

    // ---------- 判据 1：删掉被引用的图片 -> 悬空 + 能定位到章节和行 ----------

    [Fact]
    public async Task 删掉被引用的图片_报悬空引用并能定位到章节与行()
    {
        await using var workspace = await OpenAsync(new EpubBuilder { ChapterCount = 2, CoverBytes = EpubBuilder.MakeJpeg() });

        /* 自写多行 XHTML：EpubBuilder 给的是单行，往后面插元素会落到 </html> 外面（非良构 -> 按设计跳过）。 */
        WriteText(workspace, ChapterEntry, ChapterWithImg("Images/missing-pic.jpg"));

        var report = await new ResourceScanService().ScanAsync(workspace);
        var hits = report.Dangling
            .Where(issue => issue.Entry.EndsWith("Images/missing-pic.jpg", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(
            hits.Count == 1,
            "悬空 " + report.Dangling.Count + " 条；扫了 " + report.ScannedFiles + " 个文件、"
                + report.ReferenceCount + " 处引用；明细：" + Detail(report));

        Assert.Equal(ChapterEntry, hits[0].SourceEntry);
        Assert.Equal(6, hits[0].Line);   // ChapterWithImg 里 <img> 固定在第 6 行（数出来的）
    }

    // ---------- 判据 2：加一个没人引用的图片 -> 孤儿 ----------

    [Fact]
    public async Task 加一个没人引用的图片_报孤儿资源()
    {
        await using var workspace = await OpenAsync(new EpubBuilder { ChapterCount = 1, CoverBytes = EpubBuilder.MakeJpeg() });

        const string ImageEntry = "OEBPS/Images/unused-pic.jpg";
        WriteBytes(workspace, ImageEntry, EpubBuilder.MakeJpeg());
        AddManifestItem(workspace, "unused-pic", "Images/unused-pic.jpg", "image/jpeg");

        var report = await new ResourceScanService().ScanAsync(workspace);
        var orphan = report.Orphans.FirstOrDefault(issue => issue.Entry.Equals(ImageEntry, StringComparison.OrdinalIgnoreCase));

        Assert.True(orphan is not null, "孤儿 " + report.Orphans.Count + " 个：" + Detail(report));
        Assert.Equal(ResourceIssueKinds.Orphan, orphan!.Kind);
        Assert.True(orphan.SizeBytes > 0, "孤儿要带大小（设计文档 6.1.7 输出要求）");
    }

    // ---------- 判据 3：CSS url() 的相对路径按 CSS 所在目录解析 ----------

    [Fact]
    public async Task CSS里的url相对路径_按CSS所在目录解析()
    {
        await using var workspace = await OpenAsync(new EpubBuilder { ChapterCount = 1, CoverBytes = EpubBuilder.MakeJpeg() });

        WriteBytes(workspace, "OEBPS/Images/pic.jpg", EpubBuilder.MakeJpeg());
        WriteText(
            workspace,
            "OEBPS/Styles/extra.css",
            "@import url(\"../Styles/missing.css\");\nbody { background: url('../Images/pic.jpg'); }");
        AddManifestItem(workspace, "extra-css", "Styles/extra.css", "text/css");
        AddManifestItem(workspace, "pic", "Images/pic.jpg", "image/jpeg");

        var report = await new ResourceScanService().ScanAsync(workspace);

        Assert.True(report.ReferenceCount > 0, "应当扫到引用：" + Detail(report));

        // 存在的那个不能报悬空（说明 ../Images/pic.jpg 是相对 Styles/ 解析的）
        Assert.DoesNotContain(
            report.Dangling,
            issue => issue.Entry.EndsWith("Images/pic.jpg", StringComparison.OrdinalIgnoreCase));

        // 不存在的那个要报，且包内路径与行号正确
        var hits = report.Dangling
            .Where(issue => issue.SourceEntry is not null && issue.SourceEntry.EndsWith("Styles/extra.css", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(hits.Count == 1, "CSS 悬空 " + hits.Count + " 条：" + Detail(report));
        Assert.Equal("OEBPS/Styles/missing.css", hits[0].Entry);
        Assert.Equal(1, hits[0].Line);
    }

    // ---------- 判据 4：500 章 <= 5 秒 ----------

    [Fact]
    public async Task 扫描500章的书_不超过5秒()
    {
        await using var workspace = await OpenAsync(new EpubBuilder { ChapterCount = 500, CoverBytes = EpubBuilder.MakeJpeg() });

        var report = await new ResourceScanService().ScanAsync(workspace);

        Assert.True(report.ScannedFiles >= 500, "实际扫了 " + report.ScannedFiles + " 个文件");
        Assert.True(report.ReferenceCount > 0, "应当扫到引用");
        Assert.True(report.ElapsedMs <= 5000, "500 章实测 " + report.ElapsedMs + " ms（判据 <= 5000 ms）");
    }

    // ---------- 判据 5：data: URI 不误报 ----------

    [Fact]
    public async Task data_URI_的引用不误报()
    {
        await using var workspace = await OpenAsync(new EpubBuilder { ChapterCount = 1, CoverBytes = EpubBuilder.MakeJpeg() });

        WriteText(workspace, ChapterEntry, ChapterWithImg("data:image/png;base64,iVBORw0KGgo="));

        var report = await new ResourceScanService().ScanAsync(workspace);

        Assert.True(report.ReferenceCount > 0, "应当扫到引用（否则这条是空跑）：" + Detail(report));
        Assert.DoesNotContain(report.Dangling, issue => issue.Entry.Contains("data:", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(report.Dangling, issue => string.Equals(issue.SourceEntry, ChapterEntry, StringComparison.OrdinalIgnoreCase));
    }

    // ---------- 动作 1：移除悬空引用 ----------

    [Fact]
    public async Task 移除悬空引用_只动命中那一行_其它一字不差()
    {
        await using var workspace = await OpenAsync(new EpubBuilder { ChapterCount = 1, CoverBytes = EpubBuilder.MakeJpeg() });
        WriteText(workspace, ChapterEntry, ChapterWithImg("Images/missing-pic.jpg"));

        var before = File.ReadAllText(WorkspacePath(workspace, ChapterEntry));
        var service = new ResourceScanService(new ChapterSourceService());
        var report = await service.ScanAsync(workspace);
        var issue = report.Dangling.First(item => item.Entry.EndsWith("missing-pic.jpg", StringComparison.OrdinalIgnoreCase));

        var result = await service.RemoveReferenceAsync(workspace, issue);

        Assert.True(result.Ok, result.Message);

        var after = File.ReadAllText(WorkspacePath(workspace, ChapterEntry));

        // 只摘掉引用属性，其它字符一个不差（文本级替换，不重排文件）
        Assert.Equal(before.Replace(" src=\"Images/missing-pic.jpg\"", string.Empty, StringComparison.Ordinal), after);
        Assert.DoesNotContain("missing-pic.jpg", after, StringComparison.OrdinalIgnoreCase);

        var again = await service.ScanAsync(workspace);
        Assert.DoesNotContain(
            again.Dangling,
            item => item.Entry.EndsWith("missing-pic.jpg", StringComparison.OrdinalIgnoreCase));
    }

    // ---------- 动作 2：删除孤儿（安全约束） ----------

    [Fact]
    public async Task 删除孤儿_白名单外拒绝_白名单内移入deleted并摘掉manifest()
    {
        await using var workspace = await OpenAsync(new EpubBuilder { ChapterCount = 1, CoverBytes = EpubBuilder.MakeJpeg() });
        WriteBytes(workspace, "OEBPS/Images/unused-pic.jpg", EpubBuilder.MakeJpeg());
        AddManifestItem(workspace, "unused-pic", "Images/unused-pic.jpg", "image/jpeg");

        var service = new ResourceScanService(new ChapterSourceService());
        var report = await service.ScanAsync(workspace);
        var orphan = report.Orphans.First(item => item.Entry.EndsWith("unused-pic.jpg", StringComparison.OrdinalIgnoreCase));
        var imageFull = WorkspacePath(workspace, "OEBPS/Images/unused-pic.jpg");

        // 安全约束：白名单目录以外的（这里拿 OPF 冒充）必须拒绝
        var denied = await service.DeleteOrphanAsync(
            workspace, new ResourceIssue(ResourceIssueKinds.Orphan, "OEBPS/content.opf"));

        Assert.False(denied.Ok);
        Assert.True(File.Exists(WorkspacePath(workspace, workspace.Session.OpfPath!)));

        var result = await service.DeleteOrphanAsync(workspace, orphan);

        Assert.True(result.Ok, result.Message);
        Assert.False(File.Exists(imageFull));

        var deleted = Directory.GetFiles(Path.Combine(workspace.WorkDir, ".deleted"));
        Assert.Contains(deleted, path => Path.GetFileName(path).EndsWith("unused-pic.jpg", StringComparison.OrdinalIgnoreCase));

        // manifest 条目也要摘掉
        var opf = File.ReadAllText(WorkspacePath(workspace, workspace.Session.OpfPath!));
        Assert.DoesNotContain("unused-pic", opf, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- 工具 ----------

    /// <summary>第 6 行是 &lt;img src="..."&gt;：行号固定，测试里直接数，不靠估计。</summary>
    private static string ChapterWithImg(string source)
        => "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"
            + "<html xmlns=\"http://www.w3.org/1999/xhtml\">\n"
            + "  <head><title>测试章</title></head>\n"
            + "  <body>\n"
            + "    <p>正文。</p>\n"
            + "    <p><img src=\"" + source + "\" alt=\"x\" /></p>\n"
            + "  </body>\n"
            + "</html>\n";

    private static string Detail(ResourceScanReport report)
        => report.Summary + " | 悬空：" + string.Join(
            " ; ",
            report.Dangling.Select(issue => issue.SourceEntry + "@" + issue.Line + " -> " + issue.Entry));

    private async Task<IEpubWorkspace> OpenAsync(EpubBuilder builder)
    {
        var source = Path.Combine(_root, "source-" + Guid.NewGuid().ToString("N") + ".epub");
        builder.WriteTo(source);
        return await _factory.OpenAsync(new WorkspaceOpenRequest { EpubPath = source });
    }

    private static string WorkspacePath(IEpubWorkspace workspace, string entry)
        => Path.Combine(workspace.WorkDir, entry.Replace('/', Path.DirectorySeparatorChar));

    private static void WriteText(IEpubWorkspace workspace, string entry, string text)
    {
        var full = WorkspacePath(workspace, entry);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private static void WriteBytes(IEpubWorkspace workspace, string entry, byte[] bytes)
    {
        var full = WorkspacePath(workspace, entry);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
    }

    /// <summary>往 OPF 的 manifest 里插一个条目（模拟"包里多了一个没人引用的文件"）。</summary>
    private static void AddManifestItem(IEpubWorkspace workspace, string id, string hrefRelativeToOpf, string mediaType)
    {
        var opfFull = WorkspacePath(workspace, workspace.Session.OpfPath!);
        var text = File.ReadAllText(opfFull);
        var item = "<item id=\"" + id + "\" href=\"" + hrefRelativeToOpf + "\" media-type=\"" + mediaType + "\" />";
        const string Marker = "</manifest>";

        Assert.Contains(Marker, text, StringComparison.OrdinalIgnoreCase);
        text = text.Replace(Marker, "  " + item + Environment.NewLine + "  " + Marker, StringComparison.OrdinalIgnoreCase);
        File.WriteAllText(opfFull, text);
    }
}