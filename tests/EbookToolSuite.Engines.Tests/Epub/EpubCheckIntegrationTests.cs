using System.IO.Compression;
using System.Text;
using System.Text.Json;
using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Engines.Epub;
using EbookToolSuite.Engines.Processes;
using EbookToolSuite.Infrastructure.Config;

namespace EbookToolSuite.Engines.Tests.Epub;

/// <summary>
/// 真 EpubCheck 5.4.0 的集成测试（默认跳过，设 ETS_RUN_INTEGRATION=1 才跑）。
///
/// 用的是**真实样本**（TestCorpus\ 里的 28 本 EPUB）与**实测基线计数**这些数字不是猜的，
/// 是 2026-10-08 用真工具逐本跑出来的（28 本全表见 docs/DECISIONS.md 的 T3-5 条目）：
///    完全干净（0/0/0）：Sample Book E / Sample Book H / 
///    只有警告（0/0/1）：Sample Book D
///    有 error：Sample Book C = 0/6/0、Sample Book I = 0/4/0、Sample Book B = 1/202/0
///
/// 覆盖计划判据：1（干净书零错误）、3（只有警告的放行依据）、5（超时）、6（工具缺失）、
/// 7（报告与手工跑一致  用独立的一次 java 直跑 + JsonDocument 交叉核对）。
/// </summary>
public sealed class EpubCheckIntegrationTests : IDisposable
{
    private static bool Enabled => Environment.GetEnvironmentVariable("ETS_RUN_INTEGRATION") == "1";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ets-check-int-" + Guid.NewGuid().ToString("N"));

    private readonly ProcessHost _host = new(log: null);

    public EpubCheckIntegrationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception)
        {
            // 临时目录删不掉不影响结论
        }
    }

    // ---------- 判据 1：干净样本零错误 ----------

    [Fact]
    public async Task 真工具_完全干净的样本_零错误零警告()
    {
        if (!Enabled)
        {
            return;
        }

        var book = FindCorpus("Sample Book E");
        if (book is null)
        {
            return;   // 没样本就跳过（与既有集成测试同策略）
        }

        var report = await NewRunner().ValidateAsync(book);

        Assert.False(report.ParseFailed);
        Assert.Equal(0, report.FatalCount);
        Assert.Equal(0, report.ErrorCount);
    }

    // ---------- 判据 7：报告与手工跑一致 ----------

    [Fact]
    public async Task 真工具_Subject101_计数与手工直跑逐项一致()
    {
        if (!Enabled)
        {
            return;
        }

        var book = FindCorpus("Sample Book C");
        if (book is null)
        {
            return;
        }

        var report = await NewRunner().ValidateAsync(book);

        // 基线 1：repo 里那份真报告样本（原封不动存的 28061 字节 JSON）说 0/6/0
        Assert.False(report.ParseFailed);
        Assert.Equal(0, report.FatalCount);
        Assert.Equal(6, report.ErrorCount);
        Assert.Equal(0, report.WarningCount);
        Assert.Equal(6, report.Messages.Count);

        // 基线 2：独立再手工直跑一遍（绕开 runner 的解析），两边必须对上
        var manual = await ManualCheckAsync(book);
        Assert.Equal(manual.Fatal, report.FatalCount);
        Assert.Equal(manual.Error, report.ErrorCount);
        Assert.Equal(manual.Warning, report.WarningCount);
    }

    // ---------- 判据 3 的输入：只有警告的真实样本 ----------

    [Fact]
    public async Task 真工具_只有警告的样本_错误为零警告有一条()
    {
        if (!Enabled)
        {
            return;
        }

        var book = FindCorpus("Sample Book D");
        if (book is null)
        {
            return;
        }

        var report = await NewRunner().ValidateAsync(book);

        Assert.Equal(0, report.FatalCount);
        Assert.Equal(0, report.ErrorCount);
        Assert.Equal(1, report.WarningCount);
        Assert.False(report.HasBlockingErrors);
        Assert.True(report.HasWarnings);
    }

    // ---------- 判据 5：超时 ----------

    [Fact]
    public async Task 真工具_超时两秒_抛ProcessTimeout()
    {
        if (!Enabled)
        {
            return;
        }

        var book = FindCorpus("Sample Book C") ?? FindCorpus("Sample Book I");
        if (book is null)
        {
            return;
        }

        // 实测：58.5 KB 的书要 7.4 秒，2 秒必超时
        var ex = await Assert.ThrowsAsync<AppException>(
            () => NewRunner(timeoutSeconds: 2).ValidateAsync(book));

        Assert.Equal(ErrorCode.ProcessTimeout, ex.Code);
    }

    // ---------- 判据 6：把 Tools 里的 java 拿掉，保存要能跳过校验 ----------

    [Fact]
    public async Task 真工具_工具目录里没有java_抛JavaMissing()
    {
        if (!Enabled)
        {
            return;
        }

        // 模拟"把 Tools/EpubCheck 改名"：临时 AppRoot 下只放一份 manifest.json，不放任何二进制
        var appRoot = Path.Combine(_root, "no-tools-app");
        var tools = Path.Combine(appRoot, "Tools");
        Directory.CreateDirectory(tools);
        File.Copy(
            Path.Combine(RepoRoot(), "Tools", "manifest.json"),
            Path.Combine(tools, "manifest.json"),
            overwrite: true);

        var paths = AppPaths.Create(new AppPathsOptions
        {
            AppRoot = appRoot,
            UserLocalRoot = Path.Combine(_root, "no-tools-local"),
        });
        var locator = new ToolLocator(paths, _host, SystemClock.Instance);

        Assert.Null(locator.Java);
        Assert.Null(locator.EpubCheckJar);

        var epub = Path.Combine(_root, "any.epub");
        File.WriteAllBytes(epub, new byte[] { 0x50, 0x4B });

        var ex = await Assert.ThrowsAsync<AppException>(
            () => new EpubCheckRunner(locator, _host).ValidateAsync(epub));

        Assert.Equal(ErrorCode.JavaMissing, ex.Code);
    }

    // ---------- 判据 4：一键修复（补 manifest）真的有救 ----------

    [Fact]
    public async Task 真工具_被引用但未声明的图片_补manifest后变干净()
    {
        if (!Enabled)
        {
            return;
        }

        // 造一个"图在包里、内容引用它、manifest 没声明"的工作区（实测这条会被报 RSC-008）
        var source = Path.Combine(_root, "referenced-src.epub");
        EpubFixture.Write(
            source,
            EpubFixture.Epub3Opf,
            true,
            new[] { ("OEBPS/Images/ref.jpg", EpubFixture.MakeJpeg(8, 8)) });

        var workDir = Path.Combine(_root, "referenced-ws");
        Directory.CreateDirectory(workDir);
        ZipFile.ExtractToDirectory(source, workDir, overwriteFiles: true);

        var chapter = Path.Combine(workDir, "OEBPS", "ch1.xhtml");
        File.WriteAllText(
            chapter,
            File.ReadAllText(chapter, Encoding.UTF8).Replace(
                "</body>",
                "<p><img src=\"Images/ref.jpg\" alt=\"x\"/></p></body>",
                StringComparison.Ordinal),
            new UTF8Encoding(false));

        var opf = Path.Combine(workDir, "OEBPS", "content.opf");
        var packager = new EpubPackager();

        // 修之前：必须报错，而且报的就是"被引用却没声明"那一条
        var before = Path.Combine(_root, "before.epub");
        await packager.PackageAsync(workDir, before);
        var beforeReport = await NewRunner().ValidateAsync(before);

        Assert.True(beforeReport.ErrorCount > 0, "预期报错，实际 " + beforeReport.CountsText);
        Assert.Contains(
            beforeReport.Messages,
            message => message.Id is "RSC-008" or "OPF-003");

        // 一键修复
        var repair = OpfManifestRepair.Repair(opf, workDir);
        Assert.Equal(1, repair.AddedCount);
        Assert.Equal("Images/ref.jpg", repair.AddedHrefs[0]);

        // 修之后：必须干净
        var after = Path.Combine(_root, "after.epub");
        await packager.PackageAsync(workDir, after);
        var afterReport = await NewRunner().ValidateAsync(after);

        Assert.Equal(0, afterReport.FatalCount);
        Assert.Equal(0, afterReport.ErrorCount);
    }

    // ---------- 辅助 ----------

    private EpubCheckRunner NewRunner(int timeoutSeconds = EpubCheckRunner.DefaultTimeoutSeconds)
    {
        // 用仓库自己的 Tools：AppRoot 指仓库根，ToolsRoot 就正好是 <repo>\Tools
        var paths = AppPaths.Create(new AppPathsOptions
        {
            AppRoot = RepoRoot(),
            UserLocalRoot = Path.Combine(_root, "local"),
        });
        var locator = new ToolLocator(paths, _host, SystemClock.Instance);

        Assert.NotNull(locator.Java);
        Assert.NotNull(locator.EpubCheckJar);

        return new EpubCheckRunner(locator, _host, log: null, timeoutSeconds: timeoutSeconds);
    }

    /// <summary>绕开 runner：自己按 manifest 的模板直跑一次 java，再用 JsonDocument 独立读计数。</summary>
    private async Task<(int Fatal, int Error, int Warning)> ManualCheckAsync(string epubPath)
    {
        var paths = AppPaths.Create(new AppPathsOptions
        {
            AppRoot = RepoRoot(),
            UserLocalRoot = Path.Combine(_root, "local"),
        });
        var locator = new ToolLocator(paths, _host, SystemClock.Instance);
        var json = Path.Combine(_root, "manual-" + Guid.NewGuid().ToString("N") + ".json");

        var spec = new ProcessSpec
        {
            FileName = locator.Java!,
            Arguments = new[] { "-jar", locator.EpubCheckJar!, epubPath, "--json", json, "--quiet" },
            Environment = locator.BuildEnvironment("epubcheck"),
            Timeout = TimeSpan.FromSeconds(120),
        };

        var result = await _host.RunAsync(spec, null, CancellationToken.None);
        Assert.True(result.ExitCode <= 1, "手工直跑退出码异常：" + result.ExitCode + " " + result.StdErr);

        using var document = JsonDocument.Parse(File.ReadAllBytes(json));
        var checker = document.RootElement.GetProperty("checker");
        return (
            checker.GetProperty("nFatal").GetInt32(),
            checker.GetProperty("nError").GetInt32(),
            checker.GetProperty("nWarning").GetInt32());
    }

    /// <summary>在真实样本目录里按文件名片段找一本 EPUB（找不到就返回 null，测试跳过）。</summary>
    private static string? FindCorpus(string fragment)
    {
        var dir = Path.Combine(RepoRoot(), "TestCorpus");
        if (!Directory.Exists(dir))
        {
            return null;
        }

        return Directory
            .EnumerateFiles(dir, "*.epub", SearchOption.AllDirectories)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .FirstOrDefault(path => Path.GetFileName(path).Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Tools", "manifest.json")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("没找到仓库根：" + AppContext.BaseDirectory);
    }
}
