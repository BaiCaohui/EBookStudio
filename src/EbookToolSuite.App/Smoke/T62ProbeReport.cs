namespace EbookToolSuite.App.Smoke;

/// <summary>
/// T6-2 探针（设计文档 §6.5.3 九项）的一条结论。
/// </summary>
/// <param name="Id">探针编号 1..9。</param>
/// <param name="Question">要验的事（照抄设计文档）。</param>
/// <param name="Verdict">判定（短，可断言）。</param>
/// <param name="Branch">据此走的分支——也就是 T6-2 里这些按钮是真按钮还是置灰。</param>
/// <param name="Evidence">证据：数字、HTTP 状态、截图文件名。</param>
public sealed record T62ProbeFinding(
    string Id,
    string Question,
    string Verdict,
    string Branch,
    string Evidence);

/// <summary>一帧截图的像素指标。截图本身留在磁盘上供人眼复核。</summary>
/// <param name="InkRatio">整幅里"被画深"的像素占比（亮度 &lt; 200）。白屏≈0。</param>
/// <param name="TopBandInkRatio">顶部 8% 区域的墨迹占比——内置工具栏若存在，会落在这里。</param>
public sealed record T62ProbeShot(
    string Name,
    string Path,
    int Width,
    int Height,
    double InkRatio,
    double TopBandInkRatio);

/// <summary>本机取到的 PDF 样本。设计文档写的 pdf-text.pdf / pdf-100mb.pdf 并不存在，
/// 实际用的是用户放进 TestCorpus/ 的三个，报告里如实记录档位。</summary>
public sealed record T62ProbePdf(string Name, long Bytes, string Role, string Sha256Prefix);

/// <summary>WebView2 网络层的一条响应——用来判"有没有真的取到 PDF""是不是 206（支持 Range）"。</summary>
public sealed record T62ProbeNet(
    string Url,
    int Status,
    string MimeType,
    string? AcceptRanges,
    string? ContentRange,
    long EncodedBytes);

/// <summary>
/// 探针报告。落到 &lt;Data&gt;\Temp\t62-probe.json，再由 build\smoke-t62-probe.ps1
/// 汇总成 build\t62-probe-report.md。
/// </summary>
public sealed record T62ProbeReport(
    string StartedAt,
    string FinishedAt,
    long ElapsedMs,
    string BrowserVersion,
    string PdfDirectory,
    string ShellHost,
    string FileHost,
    string PdfAccessKind,
    bool HidBuiltInToolbarItems,
    List<T62ProbePdf> Files,
    List<T62ProbeShot> Shots,
    List<T62ProbeNet> Network,
    List<T62ProbeFinding> Findings,
    List<string> Notes)
{
    /// <summary>
    /// "通过"＝九项都跑出了结论。注意它**不代表九项都如我们所愿**：
    /// 探针的职责是把分支定下来（例如"#zoom 无效 → 缩放按钮阶段一置灰"也是合格结论）。
    /// </summary>
    public bool Passed => Findings.Count == 9;
}
