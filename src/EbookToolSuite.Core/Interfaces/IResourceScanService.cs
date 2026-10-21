namespace EbookToolSuite.Core.Interfaces;

using EbookToolSuite.Core.Models;

/// <summary>
/// 资源引用扫描（执行计划 T5-5）：找出"引用了不存在的资源"（悬空）与"存在但没人引用的资源"（孤儿）。
/// <para>
/// 扫描范围：XHTML 的 &lt;img src&gt; / &lt;link href&gt; / 内部 &lt;a href&gt;（XML 解析，带行号），
/// 以及 CSS 的 url(...) 与 @import（正则，相对路径按 CSS 文件所在目录解析）。
/// </para>
/// </summary>
public interface IResourceScanService
{
    Task<ResourceScanReport> ScanAsync(IEpubWorkspace workspace, CancellationToken ct = default);

    /// <summary>
    /// 移除一条悬空引用（把引用的那个属性从源文件里去掉；只改命中的那一处，不重排整个文件）。
    /// </summary>
    Task<ResourceScanAction> RemoveReferenceAsync(
        IEpubWorkspace workspace, ResourceIssue issue, CancellationToken ct = default);

    /// <summary>
    /// 删除一个孤儿资源。安全约束（设计文档 6.1.7）：只允许删 Images/Fonts/Styles/Text 下的非结构性文件，
    /// 删前先建快照，删后把 manifest 里的条目一起摘掉（文件移到 .deleted/，不进 EPUB）。
    /// </summary>
    Task<ResourceScanAction> DeleteOrphanAsync(
        IEpubWorkspace workspace, ResourceIssue issue, CancellationToken ct = default);
}