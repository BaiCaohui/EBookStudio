using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Engines.Processes;
using EbookToolSuite.Infrastructure.Config;

namespace EbookToolSuite.Engines.Tests;

/// <summary>
/// 用仓库里真实的 Tools/manifest.json 与真实外部工具跑一遍体检。
///
/// 默认跳过（会 spawn 真进程，Calibre 探测要好几秒）。
/// 需要时这样跑：
///   $env:ETS_RUN_INTEGRATION='1'; dotnet test --filter RealTools
///
/// 它的价值：抓住 manifest.json 与代码之间的漂移，
/// 比如字段改名、版本号对不上、探测参数写错。
/// </summary>
public sealed class RealToolsIntegrationTests
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("ETS_RUN_INTEGRATION") == "1";

    private static string? FindRepoRoot()
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

        return null;
    }

    [Fact]
    public async Task RealTools_AllReportHealthy()
    {
        if (!Enabled)
        {
            return;
        }

        var root = FindRepoRoot();
        Assert.NotNull(root);

        var paths = AppPaths.Create(new AppPathsOptions
        {
            AppRoot = root!,
            UserLocalRoot = Path.Combine(
                Path.GetTempPath(), "ets-int-" + Guid.NewGuid().ToString("N")),
        });
        paths.EnsureCreated();

        var locator = new ToolLocator(paths, new ProcessHost(), SystemClock.Instance);
        var report = await locator.VerifyAllAsync(runProbes: true);

        // 判据：必需工具必须 Ok；可选工具只禁止"探测失败/版本不符"
        // （那说明 manifest 或环境有问题），单纯"没装"是允许的
        // 比如 pdfium 是 P2 工具，现在本来就没装。
        var problems = report.Items
            .Where(i => i.Required
                ? i.State != ToolState.Ok
                : i.State is ToolState.ProbeFailed or ToolState.VersionMismatch)
            .Select(i => i.Key + ": " + i.State + " - " + i.Detail)
            .ToList();

        Assert.True(problems.Count == 0, string.Join(" | ", problems));

        // 可选工具也要报告状态（哪怕是不健康），不能凭空消失
        Assert.Contains(report.Items, i => i.Key == "webview2");
        Assert.Contains(report.Items, i => i.Key == "calibre");
    }
}