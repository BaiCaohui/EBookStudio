using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace EbookToolSuite.Engines.Calibre;

/// <summary>
/// Calibre 参数白名单。
///
/// 为什么是运行时采集而不是内置静态表：Calibre 的可用选项随版本变，
/// 内置表一旦过期就会误报（把合法参数拦下）或漏报（把非法参数放过去）。
/// 采集方式与 docs/calibre-args-baseline.txt 完全一致：
///   ebook-convert.exe in.&lt;ext&gt; out.&lt;ext&gt; --help
/// 输出里 --option=VALUE 表示必须给值，--option 表示开关。
/// 结果按 (输入, 输出) 组合缓存到 Data/Cache/calibre-options.json。
///
/// 探测失败（工具缺失 / 退出码非 0）时退化为内置兜底清单：
/// 只允许内置模板用到的那些选项，界面上必须明示这一点。
/// </summary>
public sealed partial class CalibreArgsCatalog
{
    /// <summary>--help 里选项行的写法：两到八个空格缩进，可选短选项在前，然后 --name[=VALUE]。</summary>
    [GeneratedRegex(
        @"^\s{2,8}(?:-[A-Za-z0-9],\s+)?--([a-z0-9][a-z0-9-]*)(?:=([A-Za-z0-9_\-\[\]]+))?(?:\s{2,}\S.*)?\s*$",
        RegexOptions.Multiline)]
    private static partial Regex OptionLineRegex();

    private readonly IProcessHost _processHost;
    private readonly string _exePath;
    private readonly string _cacheFile;
    private readonly string _workingDirectory;
    private readonly IReadOnlyDictionary<string, string?> _environment;
    private readonly ILogger<CalibreArgsCatalog>? _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IReadOnlyList<CalibreOption> _fallbackOptions;

    private Dictionary<string, CalibreOptions>? _memory;

    public CalibreArgsCatalog(
        IProcessHost processHost,
        string exePath,
        string cacheFile,
        IReadOnlyDictionary<string, string?> environment,
        string workingDirectory,
        ILogger<CalibreArgsCatalog>? log = null,
        IReadOnlyList<CalibreOption>? fallbackOptions = null)
    {
        _processHost = processHost;
        _exePath = exePath;
        _cacheFile = cacheFile;
        _environment = environment;
        _workingDirectory = workingDirectory;
        _log = log;
        _fallbackOptions = fallbackOptions ?? ConvertFallbackOptions;
    }

    /// <summary>取某个转换链路的可用选项。同一组合在一个进程里只探测一次。</summary>
    public async Task<CalibreOptions> GetAsync(
        string inputExtension, string outputExtension, CancellationToken ct = default)
    {
        var key = MakeKey(inputExtension, outputExtension);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _memory ??= ReadCacheFile();
            if (_memory.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var probed = await ProbeAsync(inputExtension, outputExtension, ct)
                .ConfigureAwait(false);

            if (probed is not null)
            {
                _memory[key] = probed;
                WriteCacheFile(_memory);
                return probed;
            }

            var fallback = BuildFallback(inputExtension, outputExtension);
            _log?.LogWarning(
                "Calibre 参数探测失败，{Key} 退化为内置兜底清单（{Count} 项）",
                key, fallback.Options.Count);
            return fallback;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string MakeKey(string inputExtension, string outputExtension)
        => Normalize(inputExtension) + "->" + Normalize(outputExtension);

    private static string Normalize(string extension)
        => extension.TrimStart('.').ToLowerInvariant();

    /// <summary>跑一次 --help 采集选项。任何失败都返回 null，让调用方退化为兜底清单。</summary>
    private async Task<CalibreOptions?> ProbeAsync(
        string inputExtension, string outputExtension, CancellationToken ct)
    {
        var inExt = Normalize(inputExtension);
        var outExt = Normalize(outputExtension);

        var spec = new ProcessSpec
        {
            FileName = _exePath,
            Arguments = new[] { "in." + inExt, "out." + outExt, "--help" },
            WorkingDirectory = _workingDirectory,
            Environment = _environment,
            Timeout = TimeSpan.FromSeconds(60),
        };

        try
        {
            var result = await _processHost.RunAsync(spec, null, ct).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                _log?.LogWarning("采集 Calibre 参数失败：exit={Exit}", result.ExitCode);
                return null;
            }

            // 不同版本可能把帮助写到 stdout 或 stderr，取内容多的那一份。
            var text = result.StdOut.Length >= result.StdErr.Length
                ? result.StdOut
                : result.StdErr;

            var options = Parse(text);
            if (options.Count < 10)
            {
                _log?.LogWarning("Calibre 参数解析结果只有 {Count} 项，判定为失败", options.Count);
                return null;
            }

            return new CalibreOptions(inExt, outExt, options, isFallback: false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "采集 Calibre 参数时出错");
            return null;
        }
    }

    /// <summary>解析 --help 文本。内部可见：单元测试直接喂样本。</summary>
    internal static IReadOnlyList<CalibreOption> Parse(string helpText)
    {
        var byName = new Dictionary<string, CalibreOption>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in helpText.Split('\n'))
        {
            var match = OptionLineRegex().Match(raw.TrimEnd('\r'));
            if (!match.Success)
            {
                continue;
            }

            // 统一带上 -- 前缀：白名单的键与命令行上写的完全一致，避免"存的是 output-profile、查的是 --output-profile"。
            var name = "--" + match.Groups[1].Value;
            byName[name] = new CalibreOption(name, match.Groups[2].Success);
        }

        return byName.Values.OrderBy(o => o.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// 探测失败时的兜底清单：只含内置模板用到的选项。
    /// 宁可少放行，也不能因为拿不到白名单就把任意参数送进 Calibre。
    /// </summary>
    private static readonly CalibreOption[] ConvertFallbackOptions =
    {
        new("--output-profile", true),
        new("--epub-version", true),
        new("--base-font-size", true),
        new("--line-height", true),
        new("--change-justification", true),
        new("--extra-css", true),
        new("--paper-size", true),
        new("--pdf-page-margin-left", true),
        new("--pdf-page-margin-right", true),
        new("--pdf-page-margin-top", true),
        new("--pdf-page-margin-bottom", true),
        new("--txt-output-encoding", true),
        new("--enable-heuristics", false),
        new("--mobi-file-type", true),
        new("--personal-doc", true),
        new("--input-encoding", true),
        new("--language", true),
        new("--title", true),
        new("--authors", true),
    };

    /// <summary>
    /// ebook-polish 的兜底清单。它与 ebook-convert 的选项集不同：
    /// 用 convert 那份会把 --embed-fonts 误判成"不支持"，却放行 --output-profile。
    /// 实测 ebook-polish --help 输出 14 个选项，全部是开关型（没有一个需要值）。
    /// </summary>
    public static readonly CalibreOption[] PolishFallbackOptions =
    {
        new("--embed-fonts", false),
        new("--subset-fonts", false),
        new("--jacket", false),
        new("--remove-jacket", false),
        new("--smarten-punctuation", false),
        new("--remove-unused-css", false),
        new("--compress-images", false),
        new("--add-soft-hyphens", false),
        new("--remove-soft-hyphens", false),
        new("--upgrade-book", false),
        new("--download-external-resources", false),
        new("--verbose", false),
    };

    private CalibreOptions BuildFallback(string inputExtension, string outputExtension)
        => new(Normalize(inputExtension), Normalize(outputExtension), _fallbackOptions, true);

    private Dictionary<string, CalibreOptions> ReadCacheFile()
    {
        var result = new Dictionary<string, CalibreOptions>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(_cacheFile))
            {
                return result;
            }

            var json = File.ReadAllText(_cacheFile);
            var stored = JsonSerializer.Deserialize<Dictionary<string, List<CalibreOption>>>(
                json, JsonOptions);
            if (stored is null)
            {
                return result;
            }

            foreach (var pair in stored)
            {
                var arrow = pair.Key.IndexOf("->", StringComparison.Ordinal);
                if (arrow <= 0)
                {
                    continue;
                }

                result[pair.Key] = new CalibreOptions(
                    pair.Key[..arrow], pair.Key[(arrow + 2)..], pair.Value, false);
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "读 Calibre 参数缓存失败，当作空缓存");
        }

        return result;
    }

    private void WriteCacheFile(Dictionary<string, CalibreOptions> cache)
    {
        try
        {
            var stored = new Dictionary<string, List<CalibreOption>>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in cache)
            {
                // 兜底清单不落盘：下次仍然重新尝试探测。
                if (pair.Value.IsFallback)
                {
                    continue;
                }

                stored[pair.Key] = pair.Value.Options.ToList();
            }

            var directory = Path.GetDirectoryName(_cacheFile);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = _cacheFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(stored, JsonOptions), Utf8NoBom);
            File.Move(temp, _cacheFile, overwrite: true);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "写 Calibre 参数缓存失败（不影响转换）");
        }
    }

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };
}
