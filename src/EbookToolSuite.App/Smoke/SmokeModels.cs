namespace EbookToolSuite.App.Smoke;

/// <summary>单项检查结果。</summary>
public sealed record SmokeCheck(string Name, bool Ok, string Detail);

/// <summary>冒烟测试的完整报告，写到磁盘供脚本判定。</summary>
public sealed record SmokeReport(
    string StartedAt,
    string FinishedAt,
    long DurationMs,
    string DataRoot,
    string WebView2DataDir,
    string? WebView2Version,
    IReadOnlyList<SmokeCheck> Checks)
{
    public bool Passed => Checks.All(c => c.Ok);
}