using System.Diagnostics;
using System.IO;
using System.Text.Json;
using EbookToolSuite.App.Views;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Infrastructure.Config;
using Microsoft.Extensions.DependencyInjection;

namespace EbookToolSuite.App.Smoke;

/// <summary>
/// T7-3B（阅读统计页）进程内冒烟的总入口。
///
/// 分工：
///   T73BDataSmoke   = 数据与采集（书库检索/排序持久化、会话采集端到端、阅读窗口接线）
///   T73BStatsSmoke  = 统计页 UI（空态、四卡、14 根趋势、构成、范围切换、最近在读、三套主题截图）
/// 这里只做公共的静态检查（schema 版本 / 契约可解析）与报告落盘，具体判据留在两个子文件里，
/// 免得一个文件继续膨胀。
/// </summary>
internal static class T73BSmokeTest
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static async Task<SmokeReport> RunAsync(
        IServiceProvider services,
        AppPaths paths,
        string workDirectory,
        CancellationToken ct = default)
    {
        var startedAt = DateTimeOffset.Now;
        var watch = Stopwatch.StartNew();
        var checks = new List<SmokeCheck>();

        Directory.CreateDirectory(workDirectory);

        // ---------- 公共静态检查 ----------

        checks.Add(await CheckSchemaAsync(services, ct).ConfigureAwait(true));
        checks.Add(CheckContracts(services));
        checks.Add(CheckReaderWiring());

        // ---------- 数据与采集 ----------

        checks.AddRange(await T73BDataSmoke
            .RunAsync(services, paths, workDirectory, ct)
            .ConfigureAwait(true));

        // ---------- 统计页 UI ----------

        checks.AddRange(await T73BStatsSmoke
            .RunAsync(services, paths, Path.Combine(workDirectory, "shots"), ct)
            .ConfigureAwait(true));

        watch.Stop();

        var report = new SmokeReport(
            startedAt.ToString("O"),
            DateTimeOffset.Now.ToString("O"),
            watch.ElapsedMilliseconds,
            paths.DataRoot,
            string.Empty,
            null,
            checks);

        var jsonPath = Path.Combine(workDirectory, "t73b-report.json");
        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(report, JsonOptions), ct)
            .ConfigureAwait(true);

        return report;
    }

    /// <summary>
    /// 迁移真的跑过了：user_version = 当前程序支持的版本，且 reading_sessions 表存在。
    /// 不再写死 10：schema 会继续加脚本，写死数字只会让这条检查每次都要改（2026-10-21 升到 11）。
    /// </summary>
    private static async Task<SmokeCheck> CheckSchemaAsync(IServiceProvider services, CancellationToken ct)
    {
        try
        {
            var database = services.GetRequiredService<IDatabase>();
            var version = await database.GetSchemaVersionAsync(ct).ConfigureAwait(true);

            var expected = Infrastructure.Data.SchemaMigrator.TargetVersion;

            var repository = services.GetRequiredService<IReadingStatsRepository>();
            var sessions = await repository.ListSessionsAsync(ct).ConfigureAwait(true);

            return new SmokeCheck(
                "t73b-schema-v10",
                version == expected,
                $"user_version={version}（期望 {expected}），reading_sessions 可读，现有 {sessions.Count} 条会话");
        }
        catch (Exception ex)
        {
            return new SmokeCheck("t73b-schema-v10", false, "查询 schema 失败：" + ex.Message);
        }
    }

    /// <summary>统计与采集所需的契约都要能从容器里解析出来。</summary>
    private static SmokeCheck CheckContracts(IServiceProvider services)
    {
        try
        {
            var stats = services.GetRequiredService<IReadingStatsService>();
            var sessions = services.GetRequiredService<IReadingSessionService>();

            return new SmokeCheck(
                "t73b-contracts",
                stats is not null && sessions is not null,
                "IReadingStatsService / IReadingSessionService / IReadingStatsRepository 均已注册");
        }
        catch (Exception ex)
        {
            return new SmokeCheck("t73b-contracts", false, "解析服务失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 阅读窗口与 ReadCommand 确实把采集器接上了（不真开 WebView2，只反射查字段与构造参数）。
    /// 真正的端到端写库在 T73BDataSmoke 里验。
    /// </summary>
    private static SmokeCheck CheckReaderWiring()
    {
        const System.Reflection.BindingFlags Flags =
            System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Public;

        var readerField = typeof(ReaderWindow).GetField("_stats", Flags);

        var commandCtor = typeof(EbookToolSuite.App.Commands.ReadCommand).GetConstructors().FirstOrDefault();

        var commandParam = commandCtor?.GetParameters()
            .Any(p => p.ParameterType == typeof(IReadingSessionService)) ?? false;

        var ok = readerField is not null && commandParam;

        return new SmokeCheck(
            "t73b-reader-wiring",
            ok,
            $"ReaderWindow._stats={(readerField is not null)}，ReadCommand 注入采集器={commandParam}");
    }
}
