using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EbookToolSuite.Engines.Epub;

namespace EbookToolSuite.Engines.Tests.Epub;

/// <summary>
/// T3-2 判据 1 与判据 5 的真工具验证：
/// 改完元数据 -> 换回 EPUB -> 让真 EpubCheck 判"零 error"、让真 Calibre 读出改后的书名。
///
/// 默认跳过（要 spawn JRE 与 Calibre，一次好几秒）。需要时这样跑：
///   $env:ETS_RUN_INTEGRATION='1'; dotnet test --filter MetadataIntegration
///
/// 为什么值得单独一组：判据 5 的"EpubCheck fatal=0 error=0"不是单测能替代的 
/// 自己写的 OPF 好不好使，只有真 EpubCheck 说了算。
/// </summary>
public sealed class MetadataIntegrationTests : IDisposable
{
    private const string NewTitle = "改过的书名（第二版）";

    private static bool Enabled =>
        Environment.GetEnvironmentVariable("ETS_RUN_INTEGRATION") == "1";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ets-meta-int-" + Guid.NewGuid().ToString("N"));

    public MetadataIntegrationTests() => Directory.CreateDirectory(_root);

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

    // ================= 判据 5：EpubCheck 零 error =================

    [Fact]
    public void 判据5_基线_未经编辑的样本本身就是零error()
    {
        if (!Enabled)
        {
            return;
        }

        var epub3 = EpubFixture.Write(Path.Combine(_root, "base3.epub"), EpubFixture.Epub3Opf, epub3: true);
        var epub2 = EpubFixture.Write(Path.Combine(_root, "base2.epub"), EpubFixture.Epub2Opf, epub3: false);

        AssertClean(epub3, "EPUB3 基线样本");
        AssertClean(epub2, "EPUB2 基线样本");
    }

    [Fact]
    public void 判据5_EPUB3改过元数据后EpubCheck零error()
    {
        if (!Enabled)
        {
            return;
        }

        var source = EpubFixture.Write(Path.Combine(_root, "src3.epub"), EpubFixture.Epub3Opf, epub3: true);

        var workOpf = Path.Combine(_root, "c.opf");
        File.WriteAllText(workOpf, EpubFixture.Epub3Opf, new UTF8Encoding(false));

        var target = new OpfMetadataEditor(workOpf);
        target.SetTitle(NewTitle);
        target.SetAuthors(new[] { new EbookToolSuite.Core.Models.EpubContributor("曹雪芹", "aut") });
        target.SetPublisher("人民文学出版社");
        target.SetSubjects(new[] { "古典", "小说" });
        target.SetSeries("四大名著", 1);
        target.Save();

        var edited = EpubFixture.ReplaceOpf(
            source, File.ReadAllText(workOpf, Encoding.UTF8), Path.Combine(_root, "edited3.epub"));

        AssertClean(edited, "改过元数据的 EPUB3");

        // 顺带独立确认书名确实写进去了（EpubCheck 自己解析出来的）
        var publication = ReadPublication(edited);
        Assert.Equal(NewTitle, publication.GetProperty("title").GetString());
        Assert.Equal("曹雪芹", publication.GetProperty("creator")[0].GetString());
    }

    [Fact]
    public void 判据5_EPUB2改过元数据后EpubCheck零error()
    {
        if (!Enabled)
        {
            return;
        }

        var source = EpubFixture.Write(Path.Combine(_root, "src2.epub"), EpubFixture.Epub2Opf, epub3: false);
        var workOpf = Path.Combine(_root, "c2.opf");
        File.WriteAllText(workOpf, EpubFixture.Epub2Opf, new UTF8Encoding(false));

        var editor = new OpfMetadataEditor(workOpf);
        editor.SetTitle(NewTitle);
        editor.SetAuthors(new[] { new EbookToolSuite.Core.Models.EpubContributor("Sample Author", "aut") });
        editor.SetPublisher("民主与建设出版社");
        editor.SetPublicationDate(new DateOnly(2016, 5, 31));
        editor.Save();

        var edited = EpubFixture.ReplaceOpf(
            source, File.ReadAllText(workOpf, Encoding.UTF8), Path.Combine(_root, "edited2.epub"));

        AssertClean(edited, "改过元数据的 EPUB2");

        var publication = ReadPublication(edited);
        Assert.Equal(NewTitle, publication.GetProperty("title").GetString());
        Assert.Equal("Sample Author", publication.GetProperty("creator")[0].GetString());
    }

    // ================= 判据 6：真实样本的"没重排整个 OPF" =================

    [Fact]
    public void 判据6_真实样本零改动往返的每处改动都必须是可预期的()
    {
        if (!Enabled)
        {
            return;
        }

        var root = FindRepoRoot();
        Assert.NotNull(root);

        var corpus = Path.Combine(root!, "TestCorpus");
        if (!Directory.Exists(corpus))
        {
            return;   // 样本不在（比如换台机器）就跳过，不算失败
        }

        var problems = new List<string>();
        var checkedCount = 0;

        foreach (var epub in Directory.EnumerateFiles(corpus, "*.epub").OrderBy(x => x))
        {
            checkedCount++;
            var original = ReadOpfText(epub).TrimStart('\uFEFF');
            var work = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".opf");
            File.WriteAllText(work, original, new UTF8Encoding(false));

            new OpfMetadataEditor(work).Save();
            var once = File.ReadAllText(work, Encoding.UTF8);

            // 再存一次必须完全一样：说明归一化是幂等的，不会来回抖
            new OpfMetadataEditor(work).Save();
            if (File.ReadAllText(work, Encoding.UTF8) != once)
            {
                problems.Add(Path.GetFileName(epub) + "：连续保存两次结果不同");
            }

            foreach (var (oldLine, newLine, number) in ChangedLinePairs(original, once))
            {
                if (!IsExpectedChange(oldLine, newLine))
                {
                    problems.Add($"{Path.GetFileName(epub)} 第 {number} 行出现非预期改动："
                        + $"[{Clip(oldLine)}] -> [{Clip(newLine)}]");
                }
            }
        }

        Assert.True(checkedCount > 0, "测试样本目录里一本 EPUB 都没找到：" + corpus);
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// 判据 6 的不变量：零改动往返只允许四类差异。
    /// 1) xmlns:opf 声明归位（不归位的话整份 OPF 的元素会被 LINQ 改写成 &lt;opf:package&gt;）；
    /// 2) dcterms:modified 按规则 5 刷新；
    /// 3) 去掉空白后相同  自闭合写法 &lt;x/&gt; 与 &lt;x /&gt; 的差别；
    /// 4) 只差 OPF 元素上的 opf: 前缀（原文件自己用了 &lt;opf:metadata&gt; 这种写法时的归一）。
    /// 除这四类以外的任何改动都算"重排了 OPF"，直接判失败。
    /// </summary>
    private static bool IsExpectedChange(string oldLine, string newLine)
    {
        if (oldLine.Contains("xmlns:opf", StringComparison.Ordinal)
            || newLine.Contains("xmlns:opf", StringComparison.Ordinal))
        {
            return true;
        }

        if (oldLine.Contains("dcterms:modified", StringComparison.Ordinal)
            || newLine.Contains("dcterms:modified", StringComparison.Ordinal))
        {
            return true;
        }

        return Normalize(oldLine) == Normalize(newLine);
    }

    private static string Normalize(string line)
        => Regex.Replace(line, "\\s+", string.Empty).Replace("opf:", string.Empty);

    private static string Clip(string line)
        => line.Length > 90 ? line[..90] + "..." : line;

    private static IEnumerable<(string Old, string New, int Number)> ChangedLinePairs(
        string before, string after)
    {
        var a = before.Replace("\r\n", "\n").Split('\n');
        var b = after.Replace("\r\n", "\n").Split('\n');
        var count = Math.Max(a.Length, b.Length);

        for (var i = 0; i < count; i++)
        {
            var oldLine = i < a.Length ? a[i] : "<无>";
            var newLine = i < b.Length ? b[i] : "<无>";
            if (oldLine != newLine)
            {
                yield return (oldLine, newLine, i + 1);
            }
        }
    }

    private static string ReadOpfText(string epubPath)
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(epubPath);
        var entry = zip.Entries.First(e =>
            e.FullName.EndsWith(".opf", StringComparison.OrdinalIgnoreCase));
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    // ================= 判据 1：Calibre 能读出改后的书名 =================

    [Fact]
    public void 判据1_Calibre能读出改后的书名()
    {
        if (!Enabled)
        {
            return;
        }

        var root = FindRepoRoot();
        Assert.NotNull(root);
        var calibre = Path.Combine(root!, "Tools", "Calibre Portable", "Calibre", "ebook-meta.exe");
        Assert.True(File.Exists(calibre), "找不到 Calibre：" + calibre);

        var source = EpubFixture.Write(Path.Combine(_root, "src.epub"), EpubFixture.Epub3Opf, epub3: true);
        var workOpf = Path.Combine(_root, "c.opf");
        File.WriteAllText(workOpf, EpubFixture.Epub3Opf, new UTF8Encoding(false));

        var editor = new OpfMetadataEditor(workOpf);
        editor.SetTitle(NewTitle);
        editor.Save();

        var edited = EpubFixture.ReplaceOpf(
            source, File.ReadAllText(workOpf, Encoding.UTF8), Path.Combine(_root, "edited.epub"));

        var (exitCode, stdout, stderr) = RunProcess(calibre, "\"" + edited + "\"");
        Assert.True(exitCode == 0, "ebook-meta 退出码 " + exitCode + "：" + stderr);

        var titleLine = stdout
            .Split('\n')
            .FirstOrDefault(l => l.TrimStart().StartsWith("Title", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(titleLine);
        Assert.Contains(NewTitle, titleLine!);
    }

    // ================= 工具 =================

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

    private static JsonElement ReadPublication(string epubPath)
    {
        var root = FindRepoRoot()!;
        var java = Path.Combine(root, "Tools", "JRE", "bin", "java.exe");
        var jar = Path.Combine(root, "Tools", "EpubCheck", "epubcheck.jar");
        var jsonPath = Path.Combine(Path.GetTempPath(), "ets-epubcheck-pub-" + Guid.NewGuid().ToString("N") + ".json");

        try
        {
            RunProcess(java, "-jar \"" + jar + "\" \"" + epubPath + "\" --json \"" + jsonPath + "\" --quiet");
            using var document = JsonDocument.Parse(File.ReadAllText(jsonPath, Encoding.UTF8));
            return document.RootElement.GetProperty("publication").Clone();
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

        // Calibre 的输出语言固定成英文，免得输出被本地化后断言失配（设计文档 S6）
        startInfo.Environment["CALIBRE_OVERRIDE_LANG"] = "en";

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


