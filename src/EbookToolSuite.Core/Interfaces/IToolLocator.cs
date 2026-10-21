using EbookToolSuite.Core.Abstractions;

namespace EbookToolSuite.Core.Interfaces;

/// <summary>
/// 外部工具的位置与健康状态。路径来源是 Tools/manifest.json（只读）。
///
/// 三条实现纪律：
///   1. 启动时只查存在性，不跑版本探测（否则冷启动要 spawn 4 个进程）；
///   2. 版本探测结果要缓存（带过期时间），重复进页面不重复 spawn；
///   3. 每个工具都必须有"缺失时怎么办"的文案，不能只报红叉。
/// </summary>
public interface IToolLocator
{
    string AppRoot { get; }

    string ToolsRoot { get; }

    string DataRoot { get; }

    string CalibreConvert { get; }

    string CalibreMeta { get; }

    string CalibrePolish { get; }

    string CalibreCustomize { get; }

    /// <summary>为空表示未配置或缺失。EpubCheck 关闭时不要求。</summary>
    string? Java { get; }

    string? EpubCheckJar { get; }

    /// <summary>固定版 WebView2 目录。null = 用系统常青版。</summary>
    string? WebView2FixedFolder { get; }

    string? PdfiumDll { get; }

    /// <summary>
    /// 把某工具在 Tools/manifest.json 里声明的环境变量解析成可直接注入 ProcessSpec 的字典
    /// （{DataDir} / {ToolsRoot} 占位符在这里展开）。Calibre 的四个变量必须每次调用都带上，
    /// 否则它会去写用户的 %LOCALAPPDATA%（实测会污染系统目录，破坏"绿色便携"）。
    /// </summary>
    IReadOnlyDictionary<string, string?> BuildEnvironment(string toolKey);

    /// <summary>
    /// 体检全部工具。
    /// </summary>
    /// <param name="runProbes">
    /// false = 只查存在性（启动路径，快）；
    /// true = 额外跑版本探测（"工具健康"页面用），命中缓存则跳过 spawn。
    /// </param>
    /// <remarks>
    /// 设计文档 2.5 把本方法写成同步的 VerifyAll(bool)，但它要 spawn 进程，
    /// 同步等待会造成 UI 卡死甚至死锁。这里改为异步并保留同一语义。
    /// </remarks>
    Task<ToolHealthReport> VerifyAllAsync(bool runProbes, CancellationToken ct = default);
}