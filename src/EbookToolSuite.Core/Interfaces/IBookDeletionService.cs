using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Core.Interfaces;

/// <summary>
/// 从书库里删除书（T1-3 追加的补丁需求）。
/// </summary>
/// <remarks>
/// 删除是**物理删除**：数据库记录、正式区文件、两档封面、阅读缓存一起清掉，不可撤销。
/// 所以调用方必须先做二次确认，本接口只管删、不管问。
/// 单本失败不影响其它本；结果逐本汇报，不用异常表达业务失败。
/// </remarks>
public interface IBookDeletionService
{
    /// <summary>删除若干本书。重复的 Id 会被去重。</summary>
    Task<BookDeleteReport> DeleteAsync(IReadOnlyList<Guid> bookIds, CancellationToken ct = default);
}