using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Core.Interfaces;

/// <summary>
/// 书库文件状态扫描（T2-6）：把 book_formats 里的记录与磁盘上的真实文件比一遍。
/// 三种状态：正常 / 缺失（文件不在）/ 外部修改（大小或修改时间变了）。
/// </summary>
public interface ILibraryScanService
{
    /// <summary>最近一次扫描的结果。一次都没扫过时为 null。</summary>
    LibraryScanReport? LastReport { get; }

    /// <summary>扫描完成时触发。**在后台线程触发**，界面订阅者自己切 UI 线程。</summary>
    event Action<LibraryScanReport>? ScanCompleted;

    /// <summary>全库扫一遍。同一时刻只会真正跑一次（并发调用排队等前一次跑完）。</summary>
    Task<LibraryScanReport> ScanAsync(CancellationToken ct = default);

    /// <summary>取sample的文件状态（缺失优先于外部修改）。没扫过时返回 Normal。</summary>
    BookFileState GetBookState(Guid bookId);

    /// <summary>
    /// 接受外部改动：把库里记录的体积、修改时间与快速指纹更新成磁盘上的现状，状态回到正常。
    /// **只刷新文件信息，不重新解析元数据**（元数据重读属于元数据编辑器 T3-2）。
    /// </summary>
    Task<bool> AcceptModificationAsync(Guid bookId, BookFormatKind format, CancellationToken ct = default);

    /// <summary>
    /// 内部保存之后同步书库基线（用户 2026-10-20 要求）：我们自己的编辑器把文件写回磁盘了，
    /// 库里记的体积、修改时间与快速指纹要跟上，否则书库扫描会把这次**内部编辑**报成
    /// 「文件被外部修改」（用户实测：用 TXT 编辑器改完，书库卡片就挂上「已修改」角标）。
    /// 与 <see cref="AcceptModificationAsync"/> 的区别：不要求当前状态是"被外部修改"，
    /// 也不走"接受改动"那套用户确认语义。
    /// </summary>
    Task<bool> RecordInternalModificationAsync(
        Guid bookId, BookFormatKind format, CancellationToken ct = default);
}
