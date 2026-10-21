using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using EbookToolSuite.Engines.Epub;

namespace EbookToolSuite.Engines.Tests.Epub;

/// <summary>
/// T5-1 的真工具验收：章节结构改完 -> 重新打包 -> 让真 EpubCheck 判"零 error"，
/// 并确认阅读器（EpubPackageIndex）读到的目录标题/顺序真的变了。
///
/// 默认跳过（要 spawn JRE，一次一两秒到十几秒）。需要时这样跑：
///   $env:ETS_RUN_INTEGRATION='1'; dotnet test --filter EpubStructureIntegration
///
/// 为什么值得单独一组：判据 2/3/4/5 都要求"保存出来的 EPUB 还能被工具接受"，
/// 自己改的 manifest/spine/目录三件套对不对，只有真 EpubCheck 说了算。
/// </summary>
public sealed class EpubStructureIntegrationTests : IDisposable
{
    private const string NewTitle = "改过的第一章";

    private static bool Enabled =>
        Environment.GetEnvironmentVariable("ETS_RUN_INTEGRATION") == "1";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ets-structure-int-" + Guid.NewGuid().ToString("N"));

    public EpubStructureIntegrationTests() => Directory.CreateDirectory(_root);

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

    // ================= 判据 2：重命名 -> 保存 -> 阅读器目录里的标题变了 =================

    [Fact]
    public async Task 判据2_EPUB2_重命名后重打包_EpubCheck零error且目录标题变了()
    {
        if (!Enabled)
        {
            return;
        }

        using var fixture = new StructureSandbox(StructureFixture.Create(epub3: false, chapters: 3));
        fixture.Editor(epub3: false).Rename(0, NewTitle);

        var epub = await PackageAsync(fixture.WorkDir, "renamed2.epub");

        AssertClean(epub, "EPUB2 重命名后");

        var index = ReadIndex(epub);

        Assert.Equal(3, index.Spine.Count);
        var entry = index.Toc.Single(t => t.SpineIndex == 0);
        Assert.Equal(NewTitle, entry.Title);
        Assert.Equal(StructureFixture.ChapterHref(1), index.Spine[0].SourceHref);
    }

    [Fact]
    public async Task 判据2_EPUB3_重命名后重打包_EpubCheck零error且目录标题变了()
    {
        if (!Enabled)
        {
            return;
        }

        using var fixture = new StructureSandbox(StructureFixture.Create(epub3: true, chapters: 3));
        fixture.Editor().Rename(0, NewTitle);

        var epub = await PackageAsync(fixture.WorkDir, "renamed3.epub");

        AssertClean(epub, "EPUB3 重命名后");

        var index = ReadIndex(epub);
        Assert.Equal(NewTitle, index.Toc.Single(t => t.SpineIndex == 0).Title);
    }

    // ================= 判据 3：删除章节 -> 保存 -> EpubCheck 零 error =================

    [Fact]
    public async Task 判据3_EPUB3_删掉一章后重打包_EpubCheck零error()
    {
        if (!Enabled)
        {
            return;
        }

        using var fixture = new StructureSandbox(StructureFixture.Create(epub3: true, chapters: 4));
        fixture.Editor().Delete(1);

        var epub = await PackageAsync(fixture.WorkDir, "deleted3.epub");

        AssertClean(epub, "EPUB3 删章节后");

        var index = ReadIndex(epub);
        Assert.Equal(3, index.Spine.Count);
        Assert.DoesNotContain(index.Toc, t => t.SpineIndex < 0 && t.Title.Contains("第 2 章", StringComparison.Ordinal));
        Assert.DoesNotContain(index.Spine, s => s.SourceHref.Contains("chapter-0002", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 判据3_EPUB2_删掉一章后重打包_EpubCheck零error()
    {
        if (!Enabled)
        {
            return;
        }

        using var fixture = new StructureSandbox(StructureFixture.Create(epub3: false, chapters: 4));
        fixture.Editor(epub3: false).Delete(2);

        var epub = await PackageAsync(fixture.WorkDir, "deleted2.epub");

        AssertClean(epub, "EPUB2 删章节后");
        Assert.Equal(3, ReadIndex(epub).Spine.Count);
    }

    // ================= 判据 4：移动顺序 -> 打开后顺序变了 =================

    [Fact]
    public async Task 判据4_EPUB2_移动章节后重打包_EpubCheck零error且读取顺序变了()
    {
        if (!Enabled)
        {
            return;
        }

        using var fixture = new StructureSandbox(StructureFixture.Create(epub3: false, chapters: 3));
        fixture.Editor(epub3: false).Move(0, 2);

        var epub = await PackageAsync(fixture.WorkDir, "moved2.epub");

        AssertClean(epub, "EPUB2 移动章节后");

        var index = ReadIndex(epub);
        Assert.Equal(
            new[] { "chapter-0002", "chapter-0003", "chapter-0001" },
            index.Spine.Select(s => Path.GetFileNameWithoutExtension(s.SourceHref)));
    }

    // ================= 判据 5 与 6：新增空章节、合并 =================

    [Fact]
    public async Task 判据5_EPUB3_新增空章节后重打包_EpubCheck零error()
    {
        if (!Enabled)
        {
            return;
        }

        using var fixture = new StructureSandbox(StructureFixture.Create(epub3: true, chapters: 3));
        var added = fixture.Editor().AddEmpty(1, "插进来的新章");

        var epub = await PackageAsync(fixture.WorkDir, "added3.epub");

        AssertClean(epub, "EPUB3 新增空章节后");

        var index = ReadIndex(epub);
        Assert.Equal(4, index.Spine.Count);
        Assert.Equal(Path.GetFileName(added.Href!), Path.GetFileName(index.Spine[1].SourceHref));
    }

    [Fact]
    public async Task 判据5_EPUB2_新增空章节后重打包_EpubCheck零error()
    {
        if (!Enabled)
        {
            return;
        }

        using var fixture = new StructureSandbox(StructureFixture.Create(epub3: false, chapters: 3));
        fixture.Editor(epub3: false).AddEmpty(3, "追加的新章");

        var epub = await PackageAsync(fixture.WorkDir, "added2.epub");

        AssertClean(epub, "EPUB2 新增空章节后");
        Assert.Equal(4, ReadIndex(epub).Spine.Count);
    }

    [Fact]
    public async Task 判据6_EPUB3_合并两章后重打包_EpubCheck零error()
    {
        if (!Enabled)
        {
            return;
        }

        using var fixture = new StructureSandbox(StructureFixture.Create(epub3: true, chapters: 3));
        fixture.Editor().Merge(0);

        var epub = await PackageAsync(fixture.WorkDir, "merged3.epub");

        AssertClean(epub, "EPUB3 合并两章后");
        Assert.Equal(2, ReadIndex(epub).Spine.Count);
    }

    // ================= 目录层级（T5-1 的「多级缩进」那一条） =================

    [Fact]
    public async Task 目录层级_EPUB3_降级后重打包_EpubCheck零error()
    {
        if (!Enabled)
        {
            return;
        }

        using var fixture = new StructureSandbox(StructureFixture.Create(epub3: true, chapters: 4));
        var editor = fixture.Editor();
        editor.Demote(1);

        var epub = await PackageAsync(fixture.WorkDir, "level3.epub");

        AssertClean(epub, "EPUB3 目录降级后");

        var index = ReadIndex(epub);
        Assert.Equal(4, index.Spine.Count);
        // 顶层只该剩 3 条；被降级的第 2 章要挂在第 1 章的 Children 里、层级为 2
        Assert.Equal(3, index.Toc.Count);
        var children = index.Toc.SelectMany(entry => entry.Children).ToList();
        Assert.Single(children);
        Assert.Equal(2, children[0].Level);
        Assert.Equal(1, children[0].SpineIndex);
    }

    [Fact]
    public async Task 目录层级_EPUB2_降级再升级后重打包_EpubCheck零error()
    {
        if (!Enabled)
        {
            return;
        }

        using var fixture = new StructureSandbox(StructureFixture.Create(epub3: false, chapters: 4));
        var editor = fixture.Editor(epub3: false);
        editor.Demote(3);
        editor.Promote(3);

        var epub = await PackageAsync(fixture.WorkDir, "level2.epub");

        AssertClean(epub, "EPUB2 目录降级再升级后");

        var index = ReadIndex(epub);
        Assert.Equal(4, index.Spine.Count);
        Assert.All(index.Toc, entry => Assert.Equal(1, entry.Level));
    }

    // ================= 拆分章节 =================

    [Fact]
    public async Task 拆分章节_EPUB2_拆完重打包_EpubCheck零error()
    {
        if (!Enabled)
        {
            return;
        }

        using var fixture = new StructureSandbox(StructureFixture.Create(epub3: false, chapters: 3));
        var editor = fixture.Editor(epub3: false);

        var points = editor.ReadSplitPoints(1);
        Assert.NotEmpty(points);
        editor.Split(1, points[^1].SplitAt);

        var epub = await PackageAsync(fixture.WorkDir, "split2.epub");

        AssertClean(epub, "EPUB2 拆分章节后");
        Assert.Equal(4, ReadIndex(epub).Spine.Count);
    }

    [Fact]
    public async Task 拆分章节_EPUB3_拆完重打包_EpubCheck零error()
    {
        if (!Enabled)
        {
            return;
        }

        using var fixture = new StructureSandbox(StructureFixture.Create(epub3: true, chapters: 3));
        var editor = fixture.Editor();

        var points = editor.ReadSplitPoints(0);
        Assert.NotEmpty(points);
        editor.Split(0, points[^1].SplitAt);

        var epub = await PackageAsync(fixture.WorkDir, "split3.epub");

        AssertClean(epub, "EPUB3 拆分章节后");
        Assert.Equal(4, ReadIndex(epub).Spine.Count);
    }

    // ================= 章节源码编辑（T5-2 判据 2） =================

    [Fact]
    public async Task 判据2_源码改过正文后重打包_EpubCheck零error()
    {
        if (!Enabled)
        {
            return;
        }

        using var fixture = new StructureSandbox(StructureFixture.Create(epub3: true, chapters: 2));
        var editor = new ChapterSourceEditor();
        var chapters = fixture.Editor().ReadChapters();
        var href = chapters[0].Href;

        var source = editor.ReadSource(fixture.WorkDir, href);
        var edited = source.Replace("正文 1", "正文 1（源码视图改过）", StringComparison.Ordinal);

        Assert.NotEqual(source, edited);
        Assert.True(editor.Check(edited).Ok);              // 改完仍然是良构 XHTML

        editor.WriteSource(fixture.WorkDir, href, edited);

        var epub = await PackageAsync(fixture.WorkDir, "source.epub");

        AssertClean(epub, "源码视图改过正文后");

        // 改的内容真的进了包
        using var zip = System.IO.Compression.ZipFile.OpenRead(epub);
        var entry = zip.Entries.First(e => e.FullName.EndsWith("chapter-0001.xhtml", StringComparison.OrdinalIgnoreCase));
        using var reader = new StreamReader(entry.Open(), System.Text.Encoding.UTF8);
        Assert.Contains("源码视图改过", reader.ReadToEnd(), StringComparison.Ordinal);
    }

    // ================= 可视模式保存 + 自动修正（T5-2W） =================

    [Fact]
    public async Task 可视模式保存_自动修正后重打包_EpubCheck零error()
    {
        if (!Enabled)
        {
            return;
        }

        using var fixture = new StructureSandbox(StructureFixture.Create(epub3: false, chapters: 1));
        var editor = new ChapterSourceEditor();
        var fixer = new ChapterSourceFixer();
        var href = fixture.Editor(epub3: false).ReadChapters()[0].Href;
        var source = editor.ReadSource(fixture.WorkDir, href);

        // 模拟可视编辑器回传的 body：HTML 风格的 <br>、&nbsp;，还有没闭合的 div
        var assembled = ChapterVisualEdit.Assemble(source, "<div class=\"keeptext\"><p>甲<br>乙&nbsp;丙</p>");
        Assert.False(editor.Check(assembled).Ok);

        var (fixedText, fixes) = fixer.Fix(assembled);
        var afterFix = editor.Check(fixedText);

        Assert.NotEmpty(fixes);
        Assert.True(
            afterFix.Ok,
            "修正后仍不过：" + afterFix.Summary + " | 修正后（前 260 字）："
                + fixedText[..Math.Min(260, fixedText.Length)]);

        editor.WriteSource(fixture.WorkDir, href, fixedText);

        var epub = await PackageAsync(fixture.WorkDir, "visual.epub");

        AssertClean(epub, "可视模式保存（含自动修正）后");
    }

    // ================= 真实样本（干净的那几本） =================

    [Fact]
    public async Task 真实样本_干净的EPUB2_重命名与移动后重打包_EpubCheck仍然零error()
    {
        if (!Enabled)
        {
            return;
        }

        var source = FindCleanCorpusBook();
        if (source is null)
        {
            return;   // 样本不在（比如 CI 上只跑了仓库）就跳过
        }

        var workDir = Path.Combine(_root, "corpus");

        try
        {
            ZipFile.ExtractToDirectory(source, workDir);
        }
        catch (IOException)
        {
            return;   // 这本包里有不安全条目之类，换一本也无所谓：这里不是产品缺陷
        }

        var opfEntry = ReadOpfEntry(workDir);
        Assert.NotNull(opfEntry);

        var editor = new EpubStructureEditor(workDir, opfEntry!);
        var chapters = editor.ReadChapters();
        Assert.True(chapters.Count >= 2, "样本至少要有两章才能验移动");

        // 只做"零风险"的两种结构操作：重命名与移动（删章节可能撞上原书的内链，那是既有问题）
        editor.Rename(0, NewTitle);
        editor.Move(0, chapters.Count - 1);

        var epub = Path.Combine(_root, "corpus-edited.epub");
        await new EpubPackager().PackageAsync(workDir, epub);

        AssertClean(epub, "真实样本（" + Path.GetFileName(source) + "）重命名+移动后");

        var index = ReadIndex(epub);
        Assert.Equal(chapters.Count, index.Spine.Count);

        // 第 1 章既改了名又被移到了最后：目录里只该有一条新标题，而且它现在指向最后一章。
        var renamed = Assert.Single(index.Toc, entry => entry.Title == NewTitle);
        Assert.Equal(index.Spine.Count - 1, renamed.SpineIndex);
        Assert.Equal(
            Path.GetFileName(chapters[0].Href),
            Path.GetFileName(index.Spine[^1].SourceHref));
    }

    // ================= 工具 =================

    private async Task<string> PackageAsync(string workDir, string fileName)
    {
        var output = Path.Combine(_root, fileName);
        await new EpubPackager().PackageAsync(workDir, output);
        Assert.True(File.Exists(output));
        return output;
    }

    private static EpubPackageIndex ReadIndex(string epubPath)
    {
        var index = EpubPackageIndex.TryRead(epubPath, out var reason);

        if (index is null)
        {
            Assert.Fail("读不出 EPUB 结构：" + reason);
        }

        return index!;
    }

    private static string? ReadOpfEntry(string workDir)
    {
        var container = Path.Combine(workDir, "META-INF", "container.xml");
        if (!File.Exists(container))
        {
            return null;
        }

        return XDocument.Load(container).Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "rootfile")
            ?.Attribute("full-path")?.Value;
    }

    private static string? FindCleanCorpusBook()
    {
        var root = FindRepoRoot();
        if (root is null)
        {
            return null;
        }

        var corpus = Path.Combine(root, "TestCorpus");
        if (!Directory.Exists(corpus))
        {
            return null;
        }

        // T3-5 的样本基线里"干净（0 fatal 0 error）"的那几本之一
        return Directory.EnumerateFiles(corpus, "*.epub")
            .FirstOrDefault(f => Path.GetFileName(f).Contains("Big Book of Subject", StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertClean(string epubPath, string what)
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);

        var java = Path.Combine(root!, "Tools", "JRE", "bin", "java.exe");
        var jar = Path.Combine(root!, "Tools", "EpubCheck", "epubcheck.jar");
        Assert.True(File.Exists(java), "找不到 JRE：" + java);
        Assert.True(File.Exists(jar), "找不到 EpubCheck：" + jar);

        var jsonPath = Path.Combine(Path.GetTempPath(), "ets-epubcheck-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var (exitCode, _, stderr) = RunProcess(
                java, "-jar \"" + jar + "\" \"" + epubPath + "\" --json \"" + jsonPath + "\" --quiet");

            Assert.True(exitCode is 0 or 1, "EpubCheck 进程本身出问题，退出码 " + exitCode + "：" + stderr);
            Assert.True(File.Exists(jsonPath), "EpubCheck 没有产出 JSON：" + stderr);

            using var document = JsonDocument.Parse(File.ReadAllText(jsonPath, Encoding.UTF8));
            var checker = document.RootElement.GetProperty("checker");
            var fatal = checker.GetProperty("nFatal").GetInt32();
            var error = checker.GetProperty("nError").GetInt32();

            if (fatal != 0 || error != 0)
            {
                var details = document.RootElement.GetProperty("messages").EnumerateArray()
                    .Where(m => m.GetProperty("severity").GetString() is "FATAL" or "ERROR")
                    .Select(m => m.GetProperty("ID").GetString() + ": " + m.GetProperty("message").GetString());

                Assert.Fail(what + " 没通过 EpubCheck：fatal=" + fatal + " error=" + error
                    + Environment.NewLine + string.Join(Environment.NewLine, details));
            }
        }
        finally
        {
            if (File.Exists(jsonPath))
            {
                File.Delete(jsonPath);
            }
        }
    }

    private static (int ExitCode, string StdOut, string StdErr) RunProcess(string file, string arguments)
    {
        var startInfo = new ProcessStartInfo(file, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();

        if (!process.WaitForExit(180_000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw new TimeoutException(file + " 超时未返回");
        }

        return (process.ExitCode, stdout, stderr);
    }

    private static string? FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Tools", "manifest.json")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
