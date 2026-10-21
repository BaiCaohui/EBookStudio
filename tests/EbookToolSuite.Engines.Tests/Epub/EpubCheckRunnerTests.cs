using System.Text;
using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Engines.Epub;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Engines.Processes;
using EbookToolSuite.Infrastructure.Config;

namespace EbookToolSuite.Engines.Tests.Epub;

/// <summary>
/// EpubCheck 进程封装（T3-5）。这一层不启动真 java：
///   - 工具位置用**真 ToolLocator**（temp 目录 + 手写 manifest + stub 文件），走的就是生产解析路径；
///   - 进程用既有的 <see cref="FakeProcessHost"/>，它既能返回预设结果，也能在"运行"时**真的把报告写到磁盘**，
///     所以参数模板、报告读取、临时文件清理这些真实行为都能测到。
/// 真 java + 真 EpubCheck 的验证在 EpubCheckIntegrationTests（ETS_RUN_INTEGRATION=1）。
/// </summary>
public sealed class EpubCheckRunnerTests : IDisposable
{
    private readonly string _sandbox;
    private readonly AppPaths _paths;
    private readonly FakeClock _clock = new();
    private readonly string _epub;

    /// <summary>只用来读工具路径（真 ToolLocator 的解析结果），不参与被测流程。</summary>
    private readonly IToolLocator _locator;

    public EpubCheckRunnerTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "ets-runner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);

        _paths = AppPaths.Create(new AppPathsOptions
        {
            AppRoot = Path.Combine(_sandbox, "app"),
            UserLocalRoot = Path.Combine(_sandbox, "local"),
        });
        _paths.EnsureCreated();

        Directory.CreateDirectory(Path.GetDirectoryName(_paths.ToolsManifestFile)!);
        File.WriteAllText(_paths.ToolsManifestFile, Manifest, new UTF8Encoding(false));

        CreateStub("JRE/bin/java.exe");
        CreateStub("EpubCheck/epubcheck.jar");

        _locator = new ToolLocator(_paths, new FakeProcessHost(string.Empty), _clock);

        _epub = Path.Combine(_sandbox, "book.epub");
        File.WriteAllBytes(_epub, new byte[] { 0x50, 0x4B, 0x03, 0x04 });
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_sandbox, recursive: true);
        }
        catch (Exception)
        {
            // 临时目录删不掉不影响测试结论
        }
    }

    // ---------- 参数模板与正常路径 ----------

    [Fact]
    public async Task 参数模板正确_把报告读出来_并清掉临时报告()
    {
        string? reportPath = null;
        var host = new FakeProcessHost(spec =>
        {
            reportPath = WriteReport(spec, ErrorJson);
            return new ProcessResult(1, string.Empty, string.Empty, TimeSpan.FromMilliseconds(5));
        });

        var report = await NewRunner(host).ValidateAsync(_epub);

        // 退出码 1 = 有 error，算"正常跑完"，不许当成进程失败
        Assert.Equal(6, report.ErrorCount);
        Assert.Equal(0, report.FatalCount);
        Assert.Equal(TimeSpan.FromMilliseconds(5), report.Duration);

        // 参数模板：{java} -jar {jar} "{input}" --json "{out}" --quiet
        var call = Assert.Single(host.Calls);
        Assert.Equal(_locator.Java, call.FileName);
        Assert.Equal(6, call.Arguments.Count);
        Assert.Equal("-jar", call.Arguments[0]);
        Assert.Equal(_locator.EpubCheckJar, call.Arguments[1]);
        Assert.Equal(_epub, call.Arguments[2]);
        Assert.Equal("--json", call.Arguments[3]);
        Assert.EndsWith(".json", call.Arguments[4], StringComparison.Ordinal);
        Assert.Equal("--quiet", call.Arguments[5]);
        Assert.Equal(TimeSpan.FromSeconds(EpubCheckRunner.DefaultTimeoutSeconds), call.Timeout);

        // 临时报告用完必须删掉，不能堆在 %TEMP%
        Assert.NotNull(reportPath);
        Assert.False(File.Exists(reportPath));
    }

    [Fact]
    public async Task 退出码0_也算正常跑完_返回零错误报告()
    {
        var host = new FakeProcessHost(spec =>
        {
            WriteReport(spec, CleanJson);
            return new ProcessResult(0, string.Empty, string.Empty, TimeSpan.FromMilliseconds(3));
        });

        var report = await NewRunner(host).ValidateAsync(_epub);

        Assert.False(report.ParseFailed);
        Assert.False(report.HasBlockingErrors);
        Assert.Empty(report.Messages);
    }

    [Fact]
    public async Task 自定义超时秒数_会带进进程规格()
    {
        var host = new FakeProcessHost(spec =>
        {
            WriteReport(spec, CleanJson);
            return new ProcessResult(0, string.Empty, string.Empty, TimeSpan.Zero);
        });

        await NewRunner(host, timeoutSeconds: 2).ValidateAsync(_epub);

        Assert.Equal(TimeSpan.FromSeconds(2), Assert.Single(host.Calls).Timeout);
    }

    // ---------- 兜底 ----------

    [Fact]
    public async Task 退出码大于1_当成工具崩溃()
    {
        var host = new FakeProcessHost("boom", exitCode: 2);

        var ex = await Assert.ThrowsAsync<AppException>(() => NewRunner(host).ValidateAsync(_epub));

        Assert.Equal(ErrorCode.ProcessCrashed, ex.Code);
    }

    [Fact]
    public async Task 工具没生成报告_返回解析失败而不是抛异常()
    {
        var host = new FakeProcessHost(string.Empty, exitCode: 1);

        var report = await NewRunner(host).ValidateAsync(_epub);

        Assert.True(report.ParseFailed);
        Assert.False(string.IsNullOrWhiteSpace(report.ParseFailureReason));
    }

    [Fact]
    public async Task 报告是坏JSON_返回解析失败而不是抛异常()
    {
        var host = new FakeProcessHost(spec =>
        {
            WriteReport(spec, "<<< 不是 JSON >>>");
            return new ProcessResult(1, string.Empty, string.Empty, TimeSpan.Zero);
        });

        var report = await NewRunner(host).ValidateAsync(_epub);

        Assert.True(report.ParseFailed);
    }

    [Fact]
    public async Task 超时_原样抛ProcessTimeout交给门禁兜底()
    {
        // ProcessHost 的真实行为：超时抛 AppException(ProcessTimeout)。这里用替身复现同一条契约。
        var host = new FakeProcessHost(_ => throw new AppException(
            ErrorCode.ProcessTimeout, "外部程序超过 00:00:02 未完成，已终止。"));

        var ex = await Assert.ThrowsAsync<AppException>(() => NewRunner(host, timeoutSeconds: 2).ValidateAsync(_epub));

        Assert.Equal(ErrorCode.ProcessTimeout, ex.Code);
    }

    [Fact]
    public async Task 用户取消_原样抛OperationCanceledException()
    {
        var host = new FakeProcessHost(_ => throw new OperationCanceledException());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => NewRunner(host).ValidateAsync(_epub, cts.Token));
    }

    // ---------- 输入与工具缺失 ----------

    [Fact]
    public async Task 文件不存在_抛FileNotFound()
    {
        var host = new FakeProcessHost(string.Empty);

        var ex = await Assert.ThrowsAsync<AppException>(
            () => NewRunner(host).ValidateAsync(Path.Combine(_sandbox, "没有这个文件.epub")));

        Assert.Equal(ErrorCode.FileNotFound, ex.Code);
        Assert.Equal(0, host.CallCount);
    }

    [Fact]
    public async Task 没有JRE_抛JavaMissing()
    {
        File.Delete(_locator.Java!);
        var host = new FakeProcessHost(string.Empty);

        var ex = await Assert.ThrowsAsync<AppException>(() => NewRunner(host).ValidateAsync(_epub));

        Assert.Equal(ErrorCode.JavaMissing, ex.Code);
        Assert.Equal(0, host.CallCount);
    }

    [Fact]
    public async Task 没有EpubCheck_抛MissingTool()
    {
        File.Delete(_locator.EpubCheckJar!);
        var host = new FakeProcessHost(string.Empty);

        var ex = await Assert.ThrowsAsync<AppException>(() => NewRunner(host).ValidateAsync(_epub));

        Assert.Equal(ErrorCode.MissingTool, ex.Code);
        Assert.Equal(0, host.CallCount);
    }

    // ---------- 辅助 ----------

    private EpubCheckRunner NewRunner(FakeProcessHost host, int timeoutSeconds = EpubCheckRunner.DefaultTimeoutSeconds)
        => new(new ToolLocator(_paths, host, _clock), host, log: null, timeoutSeconds: timeoutSeconds);

    private static string WriteReport(ProcessSpec spec, string json)
    {
        var arguments = spec.Arguments.ToList();
        var path = arguments[arguments.IndexOf("--json") + 1];
        File.WriteAllText(path, json, new UTF8Encoding(false));
        return path;
    }

    private void CreateStub(string relative)
    {
        var full = Path.Combine(_paths.ToolsRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "stub");
    }

    private const string Manifest = """
    {
      "schemaVersion": 1,
      "tools": [
        {
          "key": "jre",
          "displayName": "JRE",
          "version": "17.0.20.1",
          "required": false,
          "entryPoints": { "java": "JRE/bin/java.exe" },
          "probe": {
            "kind": "process",
            "file": "JRE/bin/java.exe",
            "args": [ "-version" ],
            "expectExitCode": 0
          }
        },
        {
          "key": "epubcheck",
          "displayName": "EpubCheck",
          "version": "5.4.0",
          "required": false,
          "entryPoints": { "jar": "EpubCheck/epubcheck.jar" },
          "probe": {
            "kind": "process",
            "file": "JRE/bin/java.exe",
            "args": [ "-jar", "{ToolsRoot}/EpubCheck/epubcheck.jar", "--version" ],
            "expectExitCode": 0,
            "dependsOn": [ "jre" ]
          }
        }
      ]
    }
    """;

    /// <summary>真报告结构（取自 EpubCheck 5.4.0 实测输出），6 条 RSC-005 error。</summary>
    private const string ErrorJson = """
    {
      "messages" : [ {
        "ID" : "RSC-005",
        "severity" : "ERROR",
        "message" : "Error while parsing file: attribute \"clear\" not allowed here",
        "additionalLocations" : 0,
        "locations" : [ {
          "path" : "text/part0000_split_001.html",
          "line" : 14,
          "column" : 53,
          "context" : null
        } ],
        "suggestion" : null
      }, {
        "ID" : "RSC-005",
        "severity" : "ERROR",
        "message" : "Error while parsing file: attribute \"link\" not allowed here",
        "locations" : [ { "path" : "text/part0000_split_000.html", "line" : 10, "column" : 65 } ]
      }, {
        "ID" : "RSC-005", "severity" : "ERROR", "message" : "3", "locations" : []
      }, {
        "ID" : "RSC-005", "severity" : "ERROR", "message" : "4", "locations" : []
      }, {
        "ID" : "RSC-005", "severity" : "ERROR", "message" : "5", "locations" : []
      }, {
        "ID" : "RSC-005", "severity" : "ERROR", "message" : "6", "locations" : []
      } ],
      "customMessageFileName" : null,
      "checker" : {
        "path" : "book.epub",
        "filename" : "book.epub",
        "checkerVersion" : "5.4.0",
        "checkDate" : "10-08-2026 13:09:38",
        "elapsedTime" : 7400,
        "nFatal" : 0,
        "nError" : 6,
        "nWarning" : 0,
        "nUsage" : 0
      }
    }
    """;

    private const string CleanJson = """
    {
      "messages" : [ ],
      "checker" : {
        "checkerVersion" : "5.4.0",
        "elapsedTime" : 5000,
        "nFatal" : 0,
        "nError" : 0,
        "nWarning" : 0,
        "nUsage" : 0
      }
    }
    """;
}
