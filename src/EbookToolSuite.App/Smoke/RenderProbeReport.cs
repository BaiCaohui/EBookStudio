namespace EbookToolSuite.App.Smoke;

/// <summary>
/// 渲染探针报告：把一个文件直接丢给 WebView2 打开，看它到底能不能渲染。
/// T0-9 用 MOBI/AZW3 做输入来验证一条设计前提：这类格式不能靠 WebView2 直接读，
/// 必须先用 Calibre 转成 EPUB 缓存。
/// </summary>
public sealed record RenderProbeReport(
    string StartedAt,
    string FinishedAt,
    long DurationMs,
    string TargetFile,
    long TargetSizeBytes,
    string FileUrl,
    string? WebView2Version,
    bool NavigationCompleted,
    bool NavigationSuccess,
    string? NavigationError,
    string? ContentType,
    int BodyTextLength,
    string? BodyTextHead,
    bool DownloadTriggered,
    string? DownloadSuggestedName,
    bool DownloadCancelled,
    IReadOnlyList<string> Notes)
{
    /// <summary>
    /// 关键结论：WebView2 把它当成可渲染的文档打开了，而不是触发下载或给出空白页。
    /// 对一个 EPUB / HTML / PDF 文件，这里应该是 true；对 MOBI，应该是 false。
    /// </summary>
    public bool RenderedAsDocument =>
        NavigationSuccess && !DownloadTriggered && BodyTextLength > 0;
}