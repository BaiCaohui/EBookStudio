using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Engines.Processes;
using EbookToolSuite.Infrastructure.Config;

namespace EbookToolSuite.Engines.Tests;

/// <summary>
/// ToolLocator 的三条纪律：
///   1. 启动路径只查存在性，不 spawn 进程；
///   2. 探测结果有缓存，重复调用不重复 spawn；
///   3. 每个问题都有可执行的修复指引。
/// </summary>
public sealed class ToolLocatorTests : IDisposable
{
    private readonly string _sandbox;
    private readonly AppPaths _paths;
    private readonly FakeClock _clock = new();

    public ToolLocatorTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(),
                                "ets-tool@locator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);

        _paths = AppPaths.Create(new AppPathsOptions
        {
            AppRoot = Path.Combine(_sandbox, "app"),
            UserLocalRoot = Path.Combine(_sandbox, "local"),
        });
        _paths.EnsureCreated();
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    // ---------- 辅助 ----------

    private const string OneToolManifest = """
    {
      "schemaVersion": 1,
      "tools": [
        {
          "key": "calibre",
          "displayName": "Calibre",
          "version": "9.15.0",
          "required": true,
          "entryPoints": { "convert": "Calibre/ebook-convert.exe" },
          "probe": {
            "kind": "process",
            "file": "Calibre/ebook-convert.exe",
            "args": ["--version"],
            "expectExitCode": 0,
            "versionPattern": "calibre\\s+([\\d.]+)"
          }
        }
      ]
    }
    """;

    private void WriteManifest(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.ToolsManifestFile)!);
        File.WriteAllText(_paths.ToolsManifestFile, json);
    }

    private string CreateTool(string relative)
    {
        var full = Path.Combine(
            _paths.ToolsRoot,
            relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "stub");
        return full;
    }

    private ToolLocator NewLocator(FakeProcessHost host, params string[] disabled)
        => new(_paths, host, _clock, disabled);

    // ---------- 纪律 1：启动路径不 spawn ----------

    [Fact]
    public async Task VerifyAllAsync_WithoutProbes_NeverSpawnsProcess()
    {
        WriteManifest(OneToolManifest);
        CreateTool("Calibre/ebook-convert.exe");
        var host = new FakeProcessHost("calibre 9.15.0");

        var report = await NewLocator(host).VerifyAllAsync(runProbes: false);

        Assert.Equal(0, host.CallCount);
        Assert.Single(report.Items);
        Assert.Equal(ToolState.Ok, report.Items[0].State);
        Assert.False(report.HasBlockingIssue);
    }

    // ---------- 缺失与修复指引 ----------

    [Fact]
    public async Task VerifyAllAsync_ReportsMissingRequiredTool_WithFixHint()
    {
        WriteManifest(OneToolManifest);
        var host = new FakeProcessHost("calibre 9.15.0");

        var report = await NewLocator(host).VerifyAllAsync(runProbes: false);

        var item = Assert.Single(report.Items);
        Assert.Equal(ToolState.Missing, item.State);
        Assert.True(item.Required);
        Assert.True(report.HasBlockingIssue);
        Assert.Contains("Tools", item.FixHint);
        Assert.NotNull(item.Detail);
    }

    // ---------- 纪律 2：缓存 ----------

    [Fact]
    public async Task VerifyAllAsync_ProbesOnce_ThenServesFromCache()
    {
        WriteManifest(OneToolManifest);
        CreateTool("Calibre/ebook-convert.exe");
        var host = new FakeProcessHost("calibre 9.15.0");
        var locator = NewLocator(host);

        var first = await locator.VerifyAllAsync(runProbes: true);
        var second = await locator.VerifyAllAsync(runProbes: true);

        Assert.Equal(1, host.CallCount);
        Assert.Equal(ToolState.Ok, first.Items[0].State);
        Assert.Equal(ToolState.Ok, second.Items[0].State);
        Assert.Equal("9.15.0", second.Items[0].Version);
        Assert.True(File.Exists(Path.Combine(_paths.CacheDir, "tool-health.json")));
    }

    [Fact]
    public async Task VerifyAllAsync_AfterTtlExpires_ProbesAgain()
    {
        WriteManifest(OneToolManifest);
        CreateTool("Calibre/ebook-convert.exe");
        var host = new FakeProcessHost("calibre 9.15.0");
        var locator = NewLocator(host);

        await locator.VerifyAllAsync(runProbes: true);
        Assert.Equal(1, host.CallCount);

        _clock.Advance(ToolLocator.HealthCacheTtl + TimeSpan.FromMinutes(1));

        await locator.VerifyAllAsync(runProbes: true);
        Assert.Equal(2, host.CallCount);
    }

    [Fact]
    public async Task InvalidateHealthCache_ForcesReprobe()
    {
        WriteManifest(OneToolManifest);
        CreateTool("Calibre/ebook-convert.exe");
        var host = new FakeProcessHost("calibre 9.15.0");
        var locator = NewLocator(host);

        await locator.VerifyAllAsync(runProbes: true);
        locator.InvalidateHealthCache();
        await locator.VerifyAllAsync(runProbes: true);

        Assert.Equal(2, host.CallCount);
    }

    // ---------- 版本比对 ----------

    [Fact]
    public async Task VerifyAllAsync_ReportsVersionMismatch_WhenPinnedDiffers()
    {
        WriteManifest(OneToolManifest);
        CreateTool("Calibre/ebook-convert.exe");
        var host = new FakeProcessHost("ebook-convert.exe (calibre 9.99.0)");

        var report = await NewLocator(host).VerifyAllAsync(runProbes: true);

        var item = Assert.Single(report.Items);
        Assert.Equal(ToolState.VersionMismatch, item.State);
        Assert.Equal("9.99.0", item.Version);
        Assert.Contains("9.15.0", item.Detail);
        Assert.True(report.HasBlockingIssue);
    }

    [Fact]
    public async Task VerifyAllAsync_ReportsProbeFailed_WhenVersionNotExtractable()
    {
        WriteManifest(OneToolManifest);
        CreateTool("Calibre/ebook-convert.exe");
        var host = new FakeProcessHost("完全读不出版本");

        var report = await NewLocator(host).VerifyAllAsync(runProbes: true);

        Assert.Equal(ToolState.ProbeFailed, report.Items[0].State);
    }

    [Fact]
    public async Task VerifyAllAsync_ReportsProbeFailed_OnUnexpectedExitCode()
    {
        WriteManifest(OneToolManifest);
        CreateTool("Calibre/ebook-convert.exe");
        var host = new FakeProcessHost("calibre 9.15.0", exitCode: 2);

        var report = await NewLocator(host).VerifyAllAsync(runProbes: true);

        Assert.Equal(ToolState.ProbeFailed, report.Items[0].State);
        Assert.Contains("2", report.Items[0].Detail);
    }

    // ---------- 依赖与可选 ----------

    [Fact]
    public async Task VerifyAllAsync_SkipsProbe_WhenDependencyMissing()
    {
        WriteManifest("""
        {
          "schemaVersion": 1,
          "tools": [
            {
              "key": "jre",
              "displayName": "JRE",
              "required": false,
              "entryPoints": { "java": "JRE/bin/java.exe" },
              "probe": { "kind": "process", "file": "JRE/bin/java.exe", "args": ["-version"] }
            },
            {
              "key": "epubcheck",
              "displayName": "EpubCheck",
              "required": false,
              "entryPoints": { "jar": "EpubCheck/epubcheck.jar" },
              "probe": {
                "kind": "process",
                "file": "JRE/bin/java.exe",
                "args": ["-jar", "EpubCheck/epubcheck.jar", "--version"],
                "dependsOn": ["jre"]
              }
            }
          ]
        }
        """);
        CreateTool("EpubCheck/epubcheck.jar");
        var host = new FakeProcessHost("should not run");

        var report = await NewLocator(host).VerifyAllAsync(runProbes: true);

        Assert.Equal(0, host.CallCount);
        var epubcheck = report.Items.Single(i => i.Key == "epubcheck");
        Assert.Equal(ToolState.Missing, report.Items.Single(i => i.Key == "jre").State);
        Assert.Equal(ToolState.ProbeFailed, epubcheck.State);
        Assert.Contains("JRE", epubcheck.Detail);
    }

    [Fact]
    public async Task VerifyAllAsync_ReportsOptionalDisabled()
    {
        WriteManifest(OneToolManifest);
        CreateTool("Calibre/ebook-convert.exe");
        var host = new FakeProcessHost("calibre 9.15.0");

        WriteManifest("""
        {
          "schemaVersion": 1,
          "tools": [
            {
              "key": "calibre",
              "displayName": "Calibre",
              "version": "9.15.0",
              "required": false,
              "entryPoints": { "convert": "Calibre/ebook-convert.exe" }
            }
          ]
        }
        """);

        var report = await NewLocator(host, "calibre").VerifyAllAsync(runProbes: true);

        Assert.Equal(ToolState.OptionalDisabled, report.Items[0].State);
        Assert.False(report.HasBlockingIssue);
        Assert.Equal(0, host.CallCount);
    }

    // ---------- 清单本身 ----------

    [Fact]
    public void Constructor_Throws_WhenManifestMissing()
    {
        var host = new FakeProcessHost("x");

        var ex = Assert.Throws<AppException>(() => NewLocator(host));

        Assert.Equal(ErrorCode.MissingTool, ex.Code);
        Assert.Contains("manifest.json", ex.UserMessage);
    }

    // ---------- 环境变量注入 ----------

    [Fact]
    public void BuildEnvironment_ReplacesDataDirPlaceholder()
    {
        WriteManifest("""
        {
          "schemaVersion": 1,
          "tools": [
            {
              "key": "calibre",
              "displayName": "Calibre",
              "required": true,
              "entryPoints": { "convert": "Calibre/ebook-convert.exe" },
              "env": {
                "CALIBRE_CONFIG_DIRECTORY": "{DataDir}/CalibreConfig",
                "PYTHONIOENCODING": "utf-8"
              }
            }
          ]
        }
        """);
        var host = new FakeProcessHost("x");

        var env = NewLocator(host).BuildEnvironment("calibre");

        Assert.Equal(Path.Combine(_paths.DataRoot, "CalibreConfig"),
                     env["CALIBRE_CONFIG_DIRECTORY"]);
        Assert.Equal("utf-8", env["PYTHONIOENCODING"]);
    }
}