using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using EbookToolSuite.Engines.Text;

namespace EbookToolSuite.Engines.Tests.Calibre;

/// <summary>
/// T4-5 判据 6 的后半条：TXT 能转成 EPUB / MOBI（用仓库里的真 Calibre 跑）。
///
/// 默认跳过（要 spawn Calibre，一次十几秒）。需要时这样跑：
///   $env:ETS_RUN_INTEGRATION='1'; dotnet test --filter TxtConversion
///
/// 【已知缺口】转出来的 EPUB 的目录由 Calibre 决定，不是我们自己切章的结果：
/// 计划文件里 2026-10-01 的实测已经写清楚（TXT 输入插件没有 --chapter，日志里
/// "Auto generated TOC with 0 entries"）。要根治得用我们自己的切章结果先造 EPUB
/// （切章器与打包器都已就绪），这条留在 T4-7 之后补。
/// </summary>
public sealed class TxtConversionIntegrationTests : IDisposable
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("ETS_RUN_INTEGRATION") == "1";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ets-txt-conv-" + Guid.NewGuid().ToString("N"));

    public TxtConversionIntegrationTests() => Directory.CreateDirectory(_root);

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

    [Fact]
    public void 判据6_TXT能转成EPUB与MOBI()
    {
        if (!Enabled)
        {
            return;
        }

        var root = FindRepoRoot();
        Assert.NotNull(root);

        var converter = Path.Combine(root!, "Tools", "Calibre Portable", "Calibre", "ebook-convert.exe");
        Assert.True(File.Exists(converter), "找不到 Calibre：" + converter);

        // GBK 要先注册代码页提供程序才拿得到（生产的探测器会做，测试里得自己来）
        TextEncodingSupport.EnsureRegistered();

        var txt = Path.Combine(_root, "小说.txt");

        File.WriteAllBytes(txt, Encoding.GetEncoding(936).GetBytes(BuildNovel()));

        // 我们自己的切章器能看到几章（对照用：Calibre 的 TXT 输入插件看不到）
        var plan = TextChapterSplitter.Split(txt, Encoding.GetEncoding(936));

        Assert.True(plan.Chapters.Count >= 3, "切章器没切出章节：" + plan.Chapters.Count);

        var epub = Path.Combine(_root, "小说.epub");
        var mobi = Path.Combine(_root, "小说.mobi");

        var epubResult = RunCalibre(converter, txt, epub);

        Assert.Equal(0, epubResult.ExitCode);
        Assert.True(File.Exists(epub) && new FileInfo(epub).Length > 1024, "EPUB 没产出或太小");

        var mobiResult = RunCalibre(converter, txt, mobi);

        Assert.Equal(0, mobiResult.ExitCode);
        Assert.True(File.Exists(mobi) && new FileInfo(mobi).Length > 1024, "MOBI 没产出或太小");

        // EPUB 必须是合法包：第一条是 mimetype 且内容正确（判据 1 的老口径，顺手当自检）
        using var zip = ZipFile.OpenRead(epub);

        Assert.NotEmpty(zip.Entries);
        Assert.Equal("mimetype", zip.Entries[0].FullName);

        using (var stream = zip.Entries[0].Open())
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            Assert.Equal("application/epub+zip", reader.ReadToEnd().Trim());
        }
    }

    private static string BuildNovel()
    {
        var builder = new StringBuilder();

        for (var i = 1; i <= 4; i++)
        {
            builder.Append("第").Append(i).Append("章 第").Append(i).Append("段\n\n");

            for (var k = 0; k < 8; k++)
            {
                builder.Append("这是第").Append(i).Append("章的第").Append(k)
                    .Append("段正文，用来验证 TXT 转 EPUB / MOBI 能跑通。\n\n");
            }
        }

        return builder.ToString();
    }

    private static (int ExitCode, string StdErr) RunCalibre(string converter, string input, string output)
    {
        var startInfo = new ProcessStartInfo(converter, "\"" + input + "\" \"" + output + "\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        startInfo.Environment["CALIBRE_OVERRIDE_LANG"] = "en";

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();

        if (!process.WaitForExit(300_000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw new TimeoutException(converter + " 超时未返回");
        }

        return (process.ExitCode, stderr + stdout[^Math.Min(400, stdout.Length)..]);
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
