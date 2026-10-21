namespace EbookToolSuite.Core.Models;

/// <summary>资源引用问题的两类（执行计划 T5-5）。</summary>
public static class ResourceIssueKinds
{
    /// <summary>悬空引用：引用了不存在的文件（UI 红色，可"移除该引用"/"定位到源文件"）。</summary>
    public const string Dangling = "dangling";

    /// <summary>孤儿资源：文件在 manifest 里但没人引用（UI 黄色，可"删除"，默认只提示）。</summary>
    public const string Orphan = "orphan";
}

/// <summary>
/// 一条资源引用问题。
/// <para>
/// 悬空引用：<paramref name="Entry"/> = 被引用但**不存在**的包内路径，
/// <paramref name="SourceEntry"/> / <paramref name="Line"/> = 哪个文件的第几行引用的（判据 1 要能定位）。
/// 孤儿资源：<paramref name="Entry"/> = 那个没人引用的文件（来源为空）。
/// </para>
/// </summary>
public sealed record ResourceIssue(
    string Kind,
    string Entry,
    string? SourceEntry = null,
    int? Line = null,
    string? Snippet = null,
    long? SizeBytes = null);

/// <summary>资源引用扫描结果。</summary>
public sealed record ResourceScanReport(
    IReadOnlyList<ResourceIssue> Dangling,
    IReadOnlyList<ResourceIssue> Orphans,
    int ScannedFiles,
    int ReferenceCount,
    double ElapsedMs)
{
    public bool HasIssues => Dangling.Count > 0 || Orphans.Count > 0;

    public string Summary =>
        "扫了 " + ScannedFiles + " 个文件、" + ReferenceCount + " 处引用：悬空 "
        + Dangling.Count + " 处、孤儿 " + Orphans.Count + " 个（" + ElapsedMs.ToString("0") + " ms）";
}

/// <summary>资源扫描动作（移除引用 / 删除孤儿）的执行结果。</summary>
public sealed record ResourceScanAction(bool Ok, string Message);