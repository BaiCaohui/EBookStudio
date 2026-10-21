using System.IO;
using EbookToolSuite.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace EbookToolSuite.Reader.WebView;

/// <summary>
/// 把 WPF 的 WebView2 控件初始化成"应用内的浏览器"：
/// 设安全开关、把本地目录映射成 http 虚拟主机、接好双向消息。
///
/// 为什么不直接用 file://：file:// 下有 CORS、localStorage、module
/// 等一堆限制，而且相对路径解析行为与真实 Web 不一致。虚拟主机映射
/// 让本地文件拥有 http 语义，前端代码可以按正常 Web 的方式写。
/// </summary>
public sealed class ReaderWebHost
{
    /// <summary>虚拟主机名。前端用 http://ebook.local/... 访问本地资源。</summary>
    public const string VirtualHostName = ReaderEndpoints.VirtualHostName;

    /// <summary>PDF 源文件主机名。前端用 http://ebook-file.local/... 访问书库里的原始 PDF。</summary>
    public const string FileHostName = ReaderEndpoints.FileHostName;

    private readonly IWebViewEnvironmentProvider _environments;

    public ReaderWebHost(IWebViewEnvironmentProvider environments)
        => _environments = environments
            ?? throw new ArgumentNullException(nameof(environments));

    /// <summary>CoreWebView2 就绪后触发，用于挂接消息处理。</summary>
    public event EventHandler<CoreWebView2>? Ready;

    public CoreWebView2? Core { get; private set; }

    /// <summary>
    /// 初始化控件。<paramref name="contentRoot"/> 会被映射到
    /// http://ebook.local/，随后 Navigate 时用该前缀。
    /// </summary>
    public async Task InitializeAsync(
        WebView2 webView,
        string contentRoot,
        string? browserExecutableFolder = null,
        CancellationToken ct = default,
        string? libraryRoot = null)
    {
        ArgumentNullException.ThrowIfNull(webView);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRoot);

        var environment = await _environments
            .GetAsync(browserExecutableFolder, ct)
            .ConfigureAwait(true);

        await webView.EnsureCoreWebView2Async(environment).ConfigureAwait(true);

        var core = webView.CoreWebView2;
        ApplySecuritySettings(core.Settings);

        Directory.CreateDirectory(contentRoot);
        core.SetVirtualHostNameToFolderMapping(
            VirtualHostName,
            contentRoot,
            CoreWebView2HostResourceAccessKind.DenyCors);

        /* T6-2：PDF 的源文件不在解包目录里（它是固定版式、按字节流给渲染器读的，
           不解包也不拷贝），所以走第二个主机名指向书库文件根（设计文档 §7.4 / §8.9.1）。
           PDF 的地址形如 http://ebook-file.local/{bookId}/{文件名}.pdf。

           注意它必须与 VirtualHostName 一起出现在 --host-resolver-rules 的 MAP 列表里
           （WebEnvironmentProvider.BuildBrowserArguments），漏一个每次导航白付约 2 秒。 */
        if (!string.IsNullOrWhiteSpace(libraryRoot))
        {
            Directory.CreateDirectory(libraryRoot);
            core.SetVirtualHostNameToFolderMapping(
                FileHostName,
                libraryRoot,
                CoreWebView2HostResourceAccessKind.DenyCors);
        }

        Core = core;
        Ready?.Invoke(this, core);
    }

    /// <summary>
    /// 把 http://ebook.local/ 重新指向另一个目录（会话的阅读缓存根换目录时必须跟着换）。
    /// TXT 编辑器保存后书的 quick_fingerprint 变 → 缓存目录按新指纹换名；不重指的话
    /// WebView 还在按老目录取文件，重载/翻章全是 404（Chromium 会渲染"它可能已被移动、编辑或删除"）。
    /// </summary>
    public void RemapContentHost(string contentRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRoot);

        var core = Core ?? throw new InvalidOperationException("CoreWebView2 未就绪");

        Directory.CreateDirectory(contentRoot);
        core.SetVirtualHostNameToFolderMapping(
            VirtualHostName,
            contentRoot,
            CoreWebView2HostResourceAccessKind.DenyCors);
    }

    /// <summary>把相对路径转成虚拟主机 URL。</summary>
    public static string ToVirtualUrl(string relativePath)
        => "http://" + VirtualHostName + "/" + relativePath.TrimStart('/');

    /// <summary>把书库文件根之下的相对路径转成 PDF 文件主机 URL（T6-2）。</summary>
    public static string ToFileUrl(string relativePath)
        => "http://" + FileHostName + "/" + relativePath.TrimStart('/').Replace('\\', '/');

    /// <summary>
    /// 安全设置。默认按"正式运行"配置：不开放 DevTools、不允许宿主对象。
    /// 调试期把 devTools 传 true 即可（由 DEBUG 编译符号或命令行开关决定）。
    /// </summary>
    public static void ApplySecuritySettings(
        CoreWebView2Settings settings,
        bool devTools = false)
    {
        ArgumentNullException.ThrowIfNull(settings);

        settings.AreDevToolsEnabled = devTools;
        settings.IsStatusBarEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.IsScriptEnabled = true;
        settings.IsWebMessageEnabled = true;

        // 只用 postMessage 通信，不允许注入宿主对象（更小的攻击面）
        settings.AreHostObjectsAllowed = false;

        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
    }
}