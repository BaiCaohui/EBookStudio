using System.IO;
using EbookToolSuite.Core;
using EbookToolSuite.Core.Interfaces;
using Microsoft.Web.WebView2.Core;

namespace EbookToolSuite.Reader.WebView;

/// <summary>
/// 提供 CoreWebView2Environment。差异（常青版 vs 固定版）封在这里，上层不感知。
/// </summary>
public interface IWebViewEnvironmentProvider
{
    /// <param name="browserExecutableFolder">null = 用系统常青版；否则指向固定版目录。</param>
    Task<CoreWebView2Environment> GetAsync(
        string? browserExecutableFolder = null,
        CancellationToken ct = default);
}

/// <summary>
/// 环境按 (browserExecutableFolder) 缓存：同一个 userDataFolder 不允许创建两个环境，
/// 否则 WebView2 会抛 "user data folder is already in use"。
/// </summary>
public sealed class WebViewEnvironmentProvider : IWebViewEnvironmentProvider
{
    private readonly IAppPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, CoreWebView2Environment> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public WebViewEnvironmentProvider(IAppPaths paths)
        => _paths = paths ?? throw new ArgumentNullException(nameof(paths));

    public async Task<CoreWebView2Environment> GetAsync(
        string? browserExecutableFolder = null,
        CancellationToken ct = default)
    {
        var key = browserExecutableFolder ?? "(evergreen)";

        await _gate.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            if (_cache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            // userDataFolder 必须指到数据目录内，否则 WebView2 会去写
            // %LOCALAPPDATA%，绿色化验收直接失败。
            Directory.CreateDirectory(_paths.WebView2DataDir);

            var options = new CoreWebView2EnvironmentOptions
            {
                // 参数串见 BuildBrowserArguments（抽出来是为了让 T6-2 的探针
                // 能构造"去掉 msPdfOOUI"的对照环境，而不必复制一份）。
                AdditionalBrowserArguments = BuildBrowserArguments(),
            };

            var env = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: browserExecutableFolder,
                userDataFolder: _paths.WebView2DataDir,
                options: options).ConfigureAwait(true);

            _cache[key] = env;
            return env;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 启动参数。抽成静态方法是为了让 T6-2 的探针能构造"去掉 msPdfOOUI"的对照环境
    /// （设计文档 §6.5.3 探针 1 的失败分支），而不必复制一份参数串——两份迟早漂移。
    /// </summary>
    /// <param name="disablePdfOoui">
    /// 是否关掉 Edge 内置 PDF 界面。正式运行永远是 true；探针传 false 做对照，
    /// 用来回答"PDF 渲染不出来到底是不是这条参数造成的"。
    /// </param>
    public static string BuildBrowserArguments(bool disablePdfOoui = true)
    {
        // --disable-features：关掉两个用不到的 Edge 内置浮层（PDF 工具栏与 Web 选择 UI）。
        //
        // --no-proxy-server：阅读器只加载本地内容，不走代理。
        //
        // --host-resolver-rules：实测关键项。2026-10-02 排障结论：
        //   同一个控件里导航 about:blank 只要 11ms，导航 http://ebook.local/reader.html
        //   却要 2035ms，冷热一样、每次都付；先怀疑代理自动发现(KPAD)，
        //   加 --no-proxy-server 后【毫无变化】，说明不是代理，而是这个自定义主机名
        //   仍然被送去走了一遍域名解析（解析不出去就卡在超时上）。
        //   把 ebook.local 直接映射到回环地址，解析立刻返回；
        //   内容仍由 SetVirtualHostNameToFolderMapping 提供，不经过网络。
        //
        //   T6-2 补充：PDF 走第二个主机名（ebook-file.local，见 ReaderEndpoints）。
        //   两个主机名**都要**在 MAP 列表里，规则之间用逗号分隔；漏一个，
        //   走它的每次导航都会退回上面那条 2 秒的解析超时。
        //
        //   T6-2B 补充：自绘引擎的页位图走第三个主机名（ebook-pdf.local），
        //   它的每一个请求都由阅读器窗口的 WebResourceRequested 用内存字节应答，
        //   正常路径根本不解析域名；这一条是"响应器没挂上"时的兜底，别让它退化成 2 秒超时。
        //
        // 引号用 (char)0x22 拼出来，不在源码里写反斜杠转义：
        // 工具链多次把转义序列弄坏，这样最不容易出错。
        var features = disablePdfOoui ? "msWebOOUI,msPdfOOUI" : "msWebOOUI";

        return "--disable-features=" + features + " --no-proxy-server " +
            "--host-resolver-rules=" + (char)0x22 +
            "MAP " + ReaderEndpoints.VirtualHostName + " 127.0.0.1," +
            "MAP " + ReaderEndpoints.FileHostName + " 127.0.0.1," +
            "MAP " + ReaderEndpoints.PdfPageHostName + " 127.0.0.1" +
            (char)0x22;
    }
}