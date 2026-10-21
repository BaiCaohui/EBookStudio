using EbookToolSuite.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace EbookToolSuite.Engines.Calibre;

/// <summary>
/// ebook-polish 的参数白名单。
///
/// 为什么不直接复用 CalibreArgsCatalog：两者的命令行形态不同（
/// ebook-convert 是 in.ext out.ext，ebook-polish 是 [选项] 输入 [输出]），
/// 且**选项集完全不同**（polish 只有 14 个开关，没有 --output-profile 这类转换选项）。
/// 实测 ebook-polish --help 与 ebook-polish in.epub out.epub --help 输出逐字节相同，
/// 所以探测方式可以沿用；但兜底清单必须换成 CalibreArgsCatalog.PolishFallbackOptions。
///
/// polish 的选项与文件格式无关，固定用 epub 组合探一次即可。
/// </summary>
public sealed class CalibrePolishArgsCatalog
{
    private readonly CalibreArgsCatalog _inner;

    public CalibrePolishArgsCatalog(
        IProcessHost processHost,
        string exePath,
        string cacheFile,
        IReadOnlyDictionary<string, string?> environment,
        string workingDirectory,
        ILogger<CalibreArgsCatalog>? log = null)
    {
        _inner = new CalibreArgsCatalog(
            processHost,
            exePath,
            cacheFile,
            environment,
            workingDirectory,
            log,
            CalibreArgsCatalog.PolishFallbackOptions);
    }

    /// <summary>取 polish 的可用选项（与格式无关，固定探测一次）。</summary>
    public Task<CalibreOptions> GetAsync(CancellationToken ct = default)
        => _inner.GetAsync(".epub", ".epub", ct);
}
