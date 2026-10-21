using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using EbookToolSuite.App.Settings;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace EbookToolSuite.App.Services;

/// <summary>
/// 设置页「缓存 / 备份 / 高级」三组背后的磁盘动作（T7-4）。
///
/// 三条纪律：
/// 1. 只清 <c>Data/Cache</c>、只备份数据库与配置，绝不碰书库正式区；
/// 2. 被占用的文件不硬删，记进 Failures 返回给界面（用户知道为什么没清干净）；
/// 3. 诊断包不含书库内容，配置里的路径做脱敏（设计文档 §12.5 / T7-4）。
/// </summary>
public sealed class AppMaintenanceService : IAppMaintenanceService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>诊断包最多带多少天的日志、单个日志最多多大：自用软件排错够用即可。</summary>
    private const int DiagnosticsLogDays = 3;

    private const long DiagnosticsMaxLogBytes = 10L * 1024 * 1024;

    private readonly IAppPaths _paths;
    private readonly IConfigStore _config;
    private readonly IToolLocator _tools;
    private readonly SqliteConnectionFactory _db;
    private readonly IClock _clock;
    private readonly ILogger<AppMaintenanceService> _log;

    public AppMaintenanceService(
        IAppPaths paths,
        IConfigStore config,
        IToolLocator tools,
        SqliteConnectionFactory db,
        IClock clock,
        ILogger<AppMaintenanceService> log)
    {
        _paths = paths;
        _config = config;
        _tools = tools;
        _db = db;
        _clock = clock;
        _log = log;
    }

    // ---------- 缓存 ----------

    public Task<CacheUsage> GetCacheUsageAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var usage = MeasureDirectory(_paths.CacheDir, ct);
        return Task.FromResult(usage);
    }

    public Task<CacheCleanResult> CleanCacheAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var freed = 0L;
        var deleted = 0;
        var failures = new List<string>();

        foreach (var file in EnumerateFilesSafe(_paths.CacheDir, ct))
        {
            ct.ThrowIfCancellationRequested();

            long size = 0;

            try
            {
                size = new FileInfo(file).Length;
                File.Delete(file);
                deleted++;
                freed += size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add(Path.GetFileName(file) + "：" + ex.Message);
                _log.LogDebug(ex, "缓存文件删不掉（可能正被阅读器占用）：{File}", file);
            }
        }

        // 目录结构不动：这里只删文件，Directory 本身留着（下次写入继续用）
        _log.LogInformation("清理缓存：删除 {Count} 个文件，释放 {Bytes} 字节，{Failed} 个没删掉",
            deleted, freed, failures.Count);

        return Task.FromResult(new CacheCleanResult(freed, deleted, failures));
    }

    public Task<long> EnforceCacheLimitAsync(CancellationToken ct = default)
    {
        var limitMb = AppSettingsKeys.NormalizeCacheLimitMb(
            AppSettingsKeys.GetInt(_config, AppSettingsKeys.CacheLimitMb, AppSettingsKeys.DefaultCacheLimitMb));
        var limitBytes = (long)limitMb * 1024 * 1024;

        var freed = 0L;
        var usage = MeasureDirectory(_paths.CacheDir, ct);

        if (usage.TotalBytes <= limitBytes)
        {
            return Task.FromResult(0L);
        }

        long over = usage.TotalBytes - limitBytes;

        var files = new List<FileInfo>();

        foreach (var file in EnumerateFilesSafe(_paths.CacheDir, ct))
        {
            try
            {
                files.Add(new FileInfo(file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogDebug(ex, "缓存超限清理时读不到文件信息：{File}", file);
            }
        }

        files.Sort((left, right) => left.LastWriteTimeUtc.CompareTo(right.LastWriteTimeUtc));

        foreach (var file in files)
        {
            if (freed >= over)
            {
                break;
            }

            ct.ThrowIfCancellationRequested();

            try
            {
                var size = file.Length;
                file.Delete();
                freed += size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogDebug(ex, "缓存超限清理时文件删不掉：{File}", file.FullName);
            }
        }

        if (freed > 0)
        {
            _log.LogInformation("缓存超过上限（{Limit} MB），已清理最旧文件释放 {Bytes} 字节", limitMb, freed);
        }

        return Task.FromResult(freed);
    }

    // ---------- 备份 ----------

    public async Task<ImmediateBackupResult> CreateImmediateBackupAsync(CancellationToken ct = default)
    {
        await CheckpointWalAsync(ct).ConfigureAwait(false);

        var stamp = _clock.Now.ToLocalTime().ToString("yyyyMMdd-HHmmss");
        var targetDir = Path.Combine(_paths.BackupDatabaseDir, "manual-" + stamp);
        Directory.CreateDirectory(targetDir);

        var copied = 0;
        var bytes = 0L;

        if (File.Exists(_paths.DbFile))
        {
            var destination = Path.Combine(targetDir, Path.GetFileName(_paths.DbFile));
            File.Copy(_paths.DbFile, destination, overwrite: true);
            copied++;
            bytes += new FileInfo(destination).Length;
        }

        if (File.Exists(_paths.ConfigFile))
        {
            var destination = Path.Combine(targetDir, Path.GetFileName(_paths.ConfigFile));
            File.Copy(_paths.ConfigFile, destination, overwrite: true);
            copied++;
            bytes += new FileInfo(destination).Length;
        }

        _log.LogInformation("立即备份完成：{Dir}（{Count} 个文件，{Bytes} 字节）", targetDir, copied, bytes);

        return new ImmediateBackupResult(targetDir, bytes, copied);
    }

    // ---------- 诊断导出 ----------

    public async Task<string> ExportDiagnosticsAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(_paths.ExportsDir);

        var stamp = _clock.Now.ToLocalTime().ToString("yyyyMMdd-HHmmss");
        var zipPath = Path.Combine(_paths.ExportsDir, "Diagnostics-" + stamp + ".zip");

        using var stream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);

        AddTextEntry(zip, "config.json", BuildSanitizedConfig());
        AddTextEntry(zip, "tools/health.json", await BuildToolHealthAsync(ct).ConfigureAwait(false));
        AddTextEntry(zip, "db/schema.json", await BuildDatabaseSchemaAsync(ct).ConfigureAwait(false));
        AddTextEntry(zip, "system/info.json", BuildSystemInfo());

        AddRecentLogs(zip, ct);
        ct.ThrowIfCancellationRequested();

        _log.LogInformation("诊断信息已导出：{Path}", zipPath);

        return zipPath;
    }

    /// <summary>Config.json 脱敏：去掉窗口位置与路径，其余原样（设置值本身对排错有用）。</summary>
    private string BuildSanitizedConfig()
    {
        var copy = new JsonObject
        {
            ["note"] = "这是脱敏后的配置：窗口位置与路径已隐藏，其余设置原样保留。",
        };

        foreach (var pair in EnumerateRootNodes())
        {
            if (pair.Key is "ui" or "paths")
            {
                var masked = MaskSensitiveSection(pair.Key, pair.Value);
                copy[pair.Key] = masked;
            }
            else
            {
                copy[pair.Key] = pair.Value?.DeepClone();
            }
        }

        return copy.ToJsonString(JsonOptions);
    }

    private IEnumerable<KeyValuePair<string, JsonNode?>> EnumerateRootNodes()
    {
        if (_config.Get(string.Empty) is not JsonObject root)
        {
            yield break;
        }

        foreach (var pair in root)
        {
            yield return pair;
        }
    }

    private static JsonNode? MaskSensitiveSection(string name, JsonNode? node)
    {
        if (node is not JsonObject obj)
        {
            return node?.DeepClone();
        }

        var masked = new JsonObject();

        foreach (var pair in obj)
        {
            if (name == "ui" && pair.Key == "windowStates")
            {
                masked["windowStates"] = "<已隐藏>";
                continue;
            }

            masked[pair.Key] = pair.Value?.DeepClone();
        }

        if (name == "paths")
        {
            masked["dataDir"] = "<已隐藏>";
        }

        return masked;
    }

    private async Task<string> BuildToolHealthAsync(CancellationToken ct)
    {
        try
        {
            var report = await _tools.VerifyAllAsync(runProbes: true, ct).ConfigureAwait(false);

            var items = report.Items.Select(i => new
            {
                key = i.Key,
                name = i.DisplayName,
                required = i.Required,
                state = i.State.ToString(),
                version = i.Version,
                fixHint = i.FixHint,
            });

            return JsonSerializer.Serialize(new
            {
                blocking = report.HasBlockingIssue,
                items,
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "诊断导出：工具体检失败");

            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
        }
    }

    private async Task<string> BuildDatabaseSchemaAsync(CancellationToken ct)
    {
        var info = new JsonObject();

        try
        {
            await using var conn = await _db.OpenAsync(ct).ConfigureAwait(false);

            info["userVersion"] = await conn.ScalarAsync<long>("PRAGMA user_version;", ct: ct)
                .ConfigureAwait(false);

            var tables = new JsonArray();

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;";
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    tables.Add(reader.GetString(0));
                }
            }

            info["tables"] = tables;

            // 只统计"这本书有几行"这类元信息，不导出任何正文
            var counts = new JsonObject();

            foreach (var table in tables.Select(t => t!.GetValue<string>()))
            {
                try
                {
                    var count = await conn.ScalarAsync<long>($"SELECT COUNT(*) FROM \"{table}\";", ct: ct)
                        .ConfigureAwait(false);
                    counts[table] = count;
                }
                catch (SqliteException)
                {
                    // 视图/虚拟表可能查不了，忽略这一张，不影响其它
                }
            }

            info["rowCounts"] = counts;
        }
        catch (Exception ex)
        {
            info["error"] = ex.Message;
            _log.LogWarning(ex, "诊断导出：数据库统计失败");
        }

        return info.ToJsonString(JsonOptions);
    }

    private static string BuildSystemInfo()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";

        var webView2 = "unknown";

        try
        {
            webView2 = Microsoft.Web.WebView2.Core.CoreWebView2Environment
                .GetAvailableBrowserVersionString() ?? "not-installed";
        }
        catch (Exception)
        {
            webView2 = "not-available";
        }

        return JsonSerializer.Serialize(new
        {
            appVersion = version,
            os = RuntimeInformation.OSDescription,
            processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            framework = RuntimeInformation.FrameworkDescription,
            runtimeVersion = Environment.Version.ToString(),
            webView2,
            machine = Environment.MachineName,
            dataRoot = "<已隐藏>",
            utc = DateTimeOffset.UtcNow.ToString("O"),
        }, JsonOptions);
    }

    private void AddRecentLogs(ZipArchive zip, CancellationToken ct)
    {
        if (!Directory.Exists(_paths.LogsDir))
        {
            return;
        }

        var since = _clock.Now.AddDays(-DiagnosticsLogDays).UtcDateTime;
        var budget = DiagnosticsMaxLogBytes;

        foreach (var file in Directory.EnumerateFiles(_paths.LogsDir, "app-*.log")
                     .Select(f => new FileInfo(f))
                     .Where(f => f.LastWriteTimeUtc >= since)
                     .OrderByDescending(f => f.LastWriteTimeUtc))
        {
            ct.ThrowIfCancellationRequested();

            if (budget <= 0)
            {
                break;
            }

            try
            {
                var size = Math.Min(file.Length, budget);
                var entry = zip.CreateEntry("logs/" + file.Name, CompressionLevel.Fastest);

                using var entryStream = entry.Open();
                using var source = new FileStream(
                    file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

                var buffer = new byte[81920];
                long remaining = size;

                while (remaining > 0)
                {
                    ct.ThrowIfCancellationRequested();

                    var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));

                    if (read <= 0)
                    {
                        break;
                    }

                    entryStream.Write(buffer, 0, read);
                    remaining -= read;
                }

                budget -= size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogDebug(ex, "诊断导出：日志读不出来，跳过：{File}", file.FullName);
            }
        }
    }

    // ---------- 小工具 ----------

    private async Task CheckpointWalAsync(CancellationToken ct)
    {
        try
        {
            await using var conn = await _db.OpenAsync(ct).ConfigureAwait(false);
            await conn.ScalarAsync<string>("PRAGMA wal_checkpoint(TRUNCATE);", ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 备份前 checkpoint 失败只记 WRN：库还在原地，备份出来的副本可能少最后一小段
            _log.LogWarning(ex, "立即备份前 WAL checkpoint 失败，继续备份（可能少最后几条未落盘数据）");
        }
    }

    private static CacheUsage MeasureDirectory(string dir, CancellationToken ct)
    {
        var bytes = 0L;
        var count = 0;

        foreach (var file in EnumerateFilesSafe(dir, ct))
        {
            try
            {
                bytes += new FileInfo(file).Length;
                count++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 统计不是关键路径，读不到就跳过
            }
        }

        return new CacheUsage(bytes, count);
    }

    private static IEnumerable<string> EnumerateFilesSafe(string dir, CancellationToken ct)
    {
        if (!Directory.Exists(dir))
        {
            yield break;
        }

        IEnumerable<string> files;

        try
        {
            files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            yield return file;
        }
    }

    private static void AddTextEntry(ZipArchive zip, string entryName, string text)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);

        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, Utf8NoBom);
        writer.Write(text);
    }
}
