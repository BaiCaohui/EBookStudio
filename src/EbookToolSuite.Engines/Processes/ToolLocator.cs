using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.IO;

namespace EbookToolSuite.Engines.Processes;

/// <summary>
/// 读 Tools/manifest.json，解析出各外部工具的绝对路径，并做健康检查。
///
/// 三条纪律的落点：
///   1. VerifyAllAsync(runProbes: false) 只做 File.Exists，不 spawn 进程；
///   2. VerifyAllAsync(runProbes: true) 的结果写 Data/Cache/tool-health.json，24 小时内复用；
///   3. 每个工具的 FixHint 都有具体路径与获取途径，不是一句"工具缺失"。
/// </summary>
public sealed class ToolLocator : IToolLocator
{
    public const string ManifestFileName = "manifest.json";

    /// <summary>版本探测结果的缓存有效期。</summary>
    public static TimeSpan HealthCacheTtl { get; } = TimeSpan.FromHours(24);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IAppPaths _appPaths;
    private readonly IProcessHost _processHost;
    private readonly IClock _clock;
    private readonly ToolManifest _manifest;
    private readonly Dictionary<string, ToolManifestEntry> _byKey;
    private readonly HashSet<string> _disabledOptional;
    private readonly string _healthCacheFile;

    public ToolLocator(
        IAppPaths appPaths,
        IProcessHost processHost,
        IClock clock,
        IEnumerable<string>? disabledOptionalTools = null)
    {
        _appPaths = appPaths ?? throw new ArgumentNullException(nameof(appPaths));
        _processHost = processHost ?? throw new ArgumentNullException(nameof(processHost));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));

        _disabledOptional = new HashSet<string>(
            disabledOptionalTools ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

        _manifest = LoadManifest(appPaths.ToolsManifestFile);
        _byKey = new Dictionary<string, ToolManifestEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in _manifest.Tools)
        {
            _byKey[tool.Key] = tool;
        }

        _healthCacheFile = Path.Combine(appPaths.CacheDir, "tool-health.json");
    }

    // ---------- IToolLocator：路径 ----------

    public string AppRoot => _appPaths.AppRoot;

    public string ToolsRoot => _appPaths.ToolsRoot;

    public string DataRoot => _appPaths.DataRoot;

    public string CalibreConvert => Required("calibre", "convert");

    public string CalibreMeta => Required("calibre", "meta");

    public string CalibrePolish => Required("calibre", "polish");

    public string CalibreCustomize => Required("calibre", "customize");

    public string? Java => Existing("jre", "java");

    public string? EpubCheckJar => Existing("epubcheck", "jar");

    public string? WebView2FixedFolder
    {
        get
        {
            var dir = Path.Combine(ToolsRoot, "EBWebView");
            return Directory.Exists(dir) ? dir : null;
        }
    }

    public string? PdfiumDll => Existing("pdfium", "dll");

    /// <summary>把某工具声明的环境变量解析成可直接注入 ProcessSpec 的字典。</summary>
    /// <remarks>
    /// {DataDir} 占位符在这里替换。Calibre 的四个变量必须每次调用都带上，
    /// 否则它会去写用户的 %LOCALAPPDATA%（实测会污染系统目录）。
    /// </remarks>
    public IReadOnlyDictionary<string, string?> BuildEnvironment(string toolKey)
    {
        if (!_byKey.TryGetValue(toolKey, out var tool) || tool.Env.Count == 0)
        {
            return new Dictionary<string, string?>();
        }

        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in tool.Env)
        {
            var isPath = pair.Value.Contains("{DataDir}", StringComparison.OrdinalIgnoreCase)
                      || pair.Value.Contains("{ToolsRoot}", StringComparison.OrdinalIgnoreCase);

            var value = pair.Value
                .Replace("{DataDir}", DataRoot, StringComparison.OrdinalIgnoreCase)
                .Replace("{ToolsRoot}", ToolsRoot, StringComparison.OrdinalIgnoreCase);

            // 清单里写的是 '{DataDir}/CalibreConfig'（正斜杠），
            // 这里统一成平台分隔符，避免传出去的是混合分隔符的路径。
            if (isPath)
            {
                value = value.Replace('/', Path.DirectorySeparatorChar);
            }

            result[pair.Key] = value;
        }

        return result;
    }

    // ---------- IToolLocator：健康检查 ----------

    public async Task<ToolHealthReport> VerifyAllAsync(
        bool runProbes,
        CancellationToken ct = default)
    {
        var cache = runProbes ? ReadHealthCache() : null;
        var items = new List<ToolHealthItem>();
        var cacheChanged = false;

        foreach (var tool in _manifest.Tools)
        {
            ct.ThrowIfCancellationRequested();

            if (!tool.Required && _disabledOptional.Contains(tool.Key))
            {
                items.Add(new ToolHealthItem(
                    tool.Key, tool.DisplayName, false, ToolState.OptionalDisabled,
                    null, "已在设置中关闭，不参与健康检查", null));
                continue;
            }

            var missing = FindMissingFile(tool);
            if (missing is not null)
            {
                items.Add(new ToolHealthItem(
                    tool.Key, tool.DisplayName, tool.Required, ToolState.Missing,
                    null, "缺少文件：" + missing, BuildFixHint(tool, missing)));
                continue;
            }

            if (!runProbes)
            {
                // 启动路径：只查存在性，绝不 spawn 进程。
                items.Add(new ToolHealthItem(
                    tool.Key, tool.DisplayName, tool.Required, ToolState.Ok,
                    tool.Version, null, null));
                continue;
            }

            if (cache is not null
                && cache.Tools.TryGetValue(tool.Key, out var cached)
                && _clock.Now - cached.ProbedAt < HealthCacheTtl)
            {
                items.Add(new ToolHealthItem(
                    tool.Key, tool.DisplayName, tool.Required, cached.State,
                    cached.Version, cached.Detail, BuildFixHint(tool, null)));
                continue;
            }

            var probed = await ProbeAsync(tool, ct).ConfigureAwait(false);
            items.Add(probed);

            cache ??= new HealthCache();
            cache.Tools[tool.Key] = new HealthCacheEntry
            {
                ProbedAt = _clock.Now,
                State = probed.State,
                Version = probed.Version,
                Detail = probed.Detail,
            };
            cacheChanged = true;
        }

        if (runProbes && cacheChanged && cache is not null)
        {
            WriteHealthCache(cache);
        }

        return new ToolHealthReport(items);
    }

    /// <summary>删除缓存，供 UI 的「重新检测」按钮用。</summary>
    public void InvalidateHealthCache()
    {
        try
        {
            if (File.Exists(_healthCacheFile))
            {
                File.Delete(_healthCacheFile);
            }
        }
        catch (Exception)
        {
            // 删不掉就算了，下次 probe 会覆盖
        }
    }

    // ---------- 内部：路径解析 ----------

    private string Required(string toolKey, string entryName)
    {
        // 声明的路径即使文件不存在也要返回，方便 UI 显示"期望它在哪"。
        if (_byKey.TryGetValue(toolKey, out var tool)
            && tool.EntryPoints.TryGetValue(entryName, out var relative))
        {
            return ToAbsolute(relative);
        }

        return Path.Combine(ToolsRoot, toolKey, entryName);
    }

    private string? Existing(string toolKey, string entryName)
    {
        if (!_byKey.TryGetValue(toolKey, out var tool)
            || !tool.EntryPoints.TryGetValue(entryName, out var relative))
        {
            return null;
        }

        var full = ToAbsolute(relative);
        return File.Exists(full) ? full : null;
    }

    private string ToAbsolute(string toolsRelative)
    {
        var normalized = toolsRelative
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);

        return Path.IsPathRooted(normalized)
            ? Path.GetFullPath(normalized)
            : Path.GetFullPath(Path.Combine(ToolsRoot, normalized));
    }

    private string? FindMissingFile(ToolManifestEntry tool)
    {
        // 有的工具（如 webview2）没有文件入口，EntryPoints 是空的。
        if (tool.EntryPoints.Count == 0)
        {
            return null;
        }

        foreach (var relative in tool.EntryPoints.Values)
        {
            var full = ToAbsolute(relative);
            if (!File.Exists(full) && !Directory.Exists(full))
            {
                return full;
            }
        }

        return null;
    }

    // ---------- 内部：探测 ----------

    private async Task<ToolHealthItem> ProbeAsync(
        ToolManifestEntry tool,
        CancellationToken ct)
    {
        var probe = tool.Probe;
        if (probe is null)
        {
            return new ToolHealthItem(
                tool.Key, tool.DisplayName, tool.Required, ToolState.Ok,
                tool.Version, "未配置探测方式，只验证了文件存在", null);
        }

        foreach (var dependency in probe.DependsOn)
        {
            if (!_byKey.TryGetValue(dependency, out var dep))
            {
                continue;
            }

            if (FindMissingFile(dep) is not null)
            {
                return new ToolHealthItem(
                    tool.Key, tool.DisplayName, tool.Required, ToolState.ProbeFailed,
                    null, "依赖的工具缺失：" + dep.DisplayName,
                    "请先修复 " + dep.DisplayName + "。");
            }
        }

        switch (probe.Kind.ToLowerInvariant())
        {
            case "fileexists":
                return OkWithVersion(tool);

            case "webview2":
                return ProbeWebView2(tool);

            case "process":
            default:
                return await ProbeProcessAsync(tool, probe, ct).ConfigureAwait(false);
        }
    }

    private ToolHealthItem OkWithVersion(ToolManifestEntry tool)
    {
        var version = string.IsNullOrWhiteSpace(tool.Version) ? "未知" : tool.Version;
        return new ToolHealthItem(
            tool.Key, tool.DisplayName, tool.Required, ToolState.Ok,
            version, null, null);
    }

    private ToolHealthItem ProbeWebView2(ToolManifestEntry tool)
    {
        var fixedFolder = WebView2FixedFolder;
        if (fixedFolder is not null)
        {
            return new ToolHealthItem(
                tool.Key, tool.DisplayName, true, ToolState.Ok,
                "fixed", "固定版：" + fixedFolder, null);
        }

        var version = DetectEvergreenWebView2Version();
        if (version is null)
        {
            return new ToolHealthItem(
                tool.Key, tool.DisplayName, true, ToolState.Missing,
                null, "未检测到系统 WebView2 常青版运行时",
                "安装 WebView2 Evergreen Runtime，或把固定版解压到 Tools/EBWebView/。");
        }

        return new ToolHealthItem(
            tool.Key, tool.DisplayName, true, ToolState.Ok,
            version, "常青版 " + version, null);
    }

    private static string? DetectEvergreenWebView2Version()
    {
        // Engines 是 net10.0（非 Windows 专用 TFM），分析器要求显式守卫。
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        const string key = @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients"
                         + @"\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
        try
        {
            using var reg = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key);
            return reg?.GetValue("pv") as string;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<ToolHealthItem> ProbeProcessAsync(
        ToolManifestEntry tool,
        ToolProbeSpec probe,
        CancellationToken ct)
    {
        var file = probe.File is null ? null : ToAbsolute(probe.File);
        if (file is null || !File.Exists(file))
        {
            return new ToolHealthItem(
                tool.Key, tool.DisplayName, tool.Required, ToolState.ProbeFailed,
                null, "探测用的可执行文件不存在：" + (file ?? "(未声明)"),
                BuildFixHint(tool, file));
        }

        // 工作目录固定为 ToolsRoot：manifest 里的探测参数可能写相对路径
        // （例如 java -jar EpubCheck/epubcheck.jar），不设 cwd 就会找不到文件。
        // 参数里的 {ToolsRoot} / {DataDir} 占位符在这里展开。
        var args = probe.Args
            .Select(a => a
                .Replace("{ToolsRoot}", ToolsRoot, StringComparison.OrdinalIgnoreCase)
                .Replace("{DataDir}", DataRoot, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var spec = new ProcessSpec
        {
            FileName = file,
            Arguments = args,
            WorkingDirectory = ToolsRoot,
            Environment = BuildEnvironment(tool.Key),
            Timeout = TimeSpan.FromSeconds(30),
        };

        ProcessResult result;
        try
        {
            result = await _processHost.RunAsync(spec, null, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AppException ex)
        {
            return new ToolHealthItem(
                tool.Key, tool.DisplayName, tool.Required, ToolState.ProbeFailed,
                null, "探测失败：" + ex.UserMessage, BuildFixHint(tool, file));
        }

        if (result.ExitCode != probe.ExpectExitCode)
        {
            return new ToolHealthItem(
                tool.Key, tool.DisplayName, tool.Required, ToolState.ProbeFailed,
                null,
                $"退出码 {result.ExitCode}，期望 {probe.ExpectExitCode}",
                BuildFixHint(tool, file));
        }

        var output = result.StdOut + "\n" + result.StdErr;
        var pinned = string.IsNullOrWhiteSpace(tool.Version) ? null : tool.Version;
        var pattern = probe.VersionPattern;

        if (string.IsNullOrWhiteSpace(pattern))
        {
            return new ToolHealthItem(
                tool.Key, tool.DisplayName, tool.Required, ToolState.Ok,
                pinned, "探测通过（清单未配置版本抽取正则）", null);
        }

        var match = Regex.Match(output, pattern);
        if (!match.Success || match.Groups.Count < 2)
        {
            return new ToolHealthItem(
                tool.Key, tool.DisplayName, tool.Required, ToolState.ProbeFailed,
                null,
                "读不出版本号（正则未匹配：" + pattern + "）",
                BuildFixHint(tool, file));
        }

        var detected = match.Groups[1].Value.Trim();

        if (pinned is not null
            && !string.Equals(pinned, detected, StringComparison.OrdinalIgnoreCase))
        {
            return new ToolHealthItem(
                tool.Key, tool.DisplayName, tool.Required, ToolState.VersionMismatch,
                detected,
                "实测版本 " + detected + "，清单钉住的是 " + pinned,
                "版本不一致可能导致行为变化：请更新 Tools/manifest.json 里的 version，"
                + "或换回钉住的版本。");
        }

        return new ToolHealthItem(
            tool.Key, tool.DisplayName, tool.Required, ToolState.Ok,
            detected, null, null);
    }
    private string? BuildFixHint(ToolManifestEntry tool, string? missingPath)
    {
        var expected = Path.Combine(ToolsRoot, tool.Key);
        var hint = tool.Key.ToLowerInvariant() switch
        {
            "calibre" => "把 Calibre 便携版解压到 Tools/Calibre Portable/（确认存在 "
                       + "Tools/Calibre Portable/Calibre/ebook-convert.exe），或改用全量发布包（已内置 Calibre）。",
            "jre" => "下载 Temurin JRE 17 的 zip（不是安装包），解压到 Tools/JRE/，"
                   + "确认存在 Tools/JRE/bin/java.exe。",
            "epubcheck" => "下载 EpubCheck 的 zip，把它解压到 Tools/EpubCheck/，"
                         + "注意 epubcheck.jar 与 lib/ 必须一起放。",
            "pdfium" => "P2 才需要，可暂时不管。",
            "webview2" => "装系统常青版运行时，或把固定版解压到 Tools/EBWebView/。",
            _ => "请参考文档第 2 章的「工具准备」。",
        };

        return missingPath is null ? hint : hint + "（期望位置：" + expected + "）";
    }

    // ---------- 内部：清单与缓存 ----------

    private static ToolManifest LoadManifest(string manifestPath)
    {
        if (!File.Exists(manifestPath))
        {
            throw new AppException(
                ErrorCode.MissingTool,
                "工具清单缺失：" + manifestPath + "。请重新解压发布包，"
                + "或参考文档第 2 章重建 Tools/manifest.json。",
                technical: "Tool manifest not found: " + manifestPath,
                context: new Dictionary<string, object?> { ["path"] = manifestPath });
        }

        try
        {
            var json = File.ReadAllText(manifestPath);
            var manifest = JsonSerializer.Deserialize<ToolManifest>(json, JsonOptions);
            if (manifest is null || manifest.Tools.Count == 0)
            {
                throw new AppException(
                    ErrorCode.MissingTool,
                    "工具清单是空的或格式不对：" + manifestPath,
                    technical: "Tool manifest parsed but empty: " + manifestPath);
            }

            return manifest;
        }
        catch (AppException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new AppException(
                ErrorCode.MissingTool,
                "工具清单解析失败：" + manifestPath + "。文件可能被改坏了。",
                technical: "Tool manifest parse error: " + ex.Message,
                inner: ex,
                context: new Dictionary<string, object?> { ["path"] = manifestPath });
        }
    }

    private HealthCache? ReadHealthCache()
    {
        try
        {
            if (!File.Exists(_healthCacheFile))
            {
                return null;
            }

            var json = File.ReadAllText(_healthCacheFile);
            return JsonSerializer.Deserialize<HealthCache>(json, JsonOptions);
        }
        catch (Exception)
        {
            // 缓存坏了不是错误，当成没有缓存即可。
            return null;
        }
    }

    private void WriteHealthCache(HealthCache cache)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_healthCacheFile)!);
            AtomicFile.WriteAllText(
                _healthCacheFile,
                JsonSerializer.Serialize(cache, JsonOptions),
                new UTF8Encoding(false));
        }
        catch (Exception)
        {
            // 缓存写不了不影响正确性，只是下次还要重新探测。
        }
    }

    private sealed class HealthCache
    {
        public Dictionary<string, HealthCacheEntry> Tools { get; set; }
            = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class HealthCacheEntry
    {
        public DateTimeOffset ProbedAt { get; set; }

        public ToolState State { get; set; }

        public string? Version { get; set; }

        public string? Detail { get; set; }
    }
}