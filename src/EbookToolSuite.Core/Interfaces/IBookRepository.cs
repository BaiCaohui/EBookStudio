using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Core.Interfaces;

/// <summary>
/// 书库的最小读写入口（阶段 1 只要这些）。手写 ADO，不引 EF Core。
/// </summary>
public interface IBookRepository
{
    /// <summary>插入一本书及其全部格式文件，单事务。</summary>
    Task AddAsync(Book book, CancellationToken ct = default);

    /// <summary>按 Id 取书，连同其格式文件。不存在返回 null。</summary>
    Task<Book?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>按 title_sort 取一页（title_sort 为空时退回 title）。</summary>
    Task<IReadOnlyList<Book>> ListAsync(int offset, int limit, CancellationToken ct = default);

    Task<int> CountAsync(CancellationToken ct = default);

    /// <summary>
    /// T2-3：按条件查询（关键词搜索 + 格式/状态/有封面筛选 + 排序 + 分页）。
    /// 实现必须全部走参数化 SQL；被标为副本的书（duplicate_copy）不出现在结果里。
    /// </summary>
    Task<IReadOnlyList<Book>> QueryAsync(BookQuery query, CancellationToken ct = default);

    /// <summary>T2-3：同一套条件下命中的总数（给顶栏"共 N 本"用）。</summary>
    Task<int> CountQueryAsync(BookQuery query, CancellationToken ct = default);

    /// <summary>
    /// 按快速指纹找已入库的书。用于导入去重（设计文档 7.1.2）。
    /// 找不到返回 null。
    /// </summary>
    Task<Guid?> FindBookIdByFingerprintAsync(string quickFingerprint, CancellationToken ct = default);
    /// <summary>
    /// 批量按快速指纹找已入库的书（T2-1 预检去重用）。一次 IN 查询，内部按 900 分块。
    /// 只返回命中的那些，没命中的指纹不出现在结果里。
    /// </summary>
    Task<IReadOnlyList<FingerprintMatch>> FindBooksByFingerprintsAsync(
        IReadOnlyList<string> quickFingerprints, CancellationToken ct = default);

    /// <summary>某路径是否已被登记（UNIQUE(file_path) 的预检）。</summary>
    Task<bool> FormatPathExistsAsync(string filePath, CancellationToken ct = default);

    /// <summary>写入或覆盖阅读进度。</summary>
    Task SaveReadingProgressAsync(ReadingProgress progress, CancellationToken ct = default);

    Task<ReadingProgress?> GetReadingProgressAsync(Guid bookId, CancellationToken ct = default);

    /// <summary>
    /// 手动设置阅读状态（卡片右键菜单的「标记为」用）。返回是否真的改到了行。
    /// 注意：导入后的自动维护（在读 / 已读完）不经过这里，见 SaveReadingProgressAsync。
    /// </summary>
    Task<bool> SetReadingStatusAsync(Guid bookId, ReadingStatus status, CancellationToken ct = default);

    /// <summary>
    /// 一批书各自的阅读进度，键是书 Id。网格上的「继续阅读」角标用它：
    /// 一次 IN 查询取回，避免每本书查一次（N+1）。没有进度的书不出现在字典里。
    /// </summary>
    Task<IReadOnlyDictionary<Guid, ReadingProgress>> GetProgressMapAsync(
        IReadOnlyList<Guid> bookIds, CancellationToken ct = default);

    /// <summary>
    /// 这本书当前的阅读格式（用户在详情页选的那个；用户 2026-10-21）。
    /// 没选过返回 null，此时阅读服务按 <see cref="Models.ReaderFormatSupport.PreferenceOrder"/> 的默认链挑。
    /// </summary>
    Task<BookFormatKind?> GetReadingFormatAsync(Guid bookId, CancellationToken ct = default);

    /// <summary>
    /// 一批书的当前阅读格式，键是书 Id。书库网格一次查回来，避免每本书查一次（N+1）。
    /// 没选过的书不出现在字典里。
    /// </summary>
    Task<IReadOnlyDictionary<Guid, BookFormatKind>> GetReadingFormatMapAsync(
        IReadOnlyList<Guid> bookIds, CancellationToken ct = default);

    /// <summary>
    /// 记下用户选的当前阅读格式。传 null 表示清掉选择、回到默认优先级链。
    /// 只影响"点阅读打开哪个文件"，不动阅读进度与阅读状态。
    /// </summary>
    Task SetReadingFormatAsync(Guid bookId, BookFormatKind? format, CancellationToken ct = default);

    /// <summary>该书的某个格式文件是否已登记（转换前预检 UNIQUE(book_id, format)）。</summary>
    Task<bool> HasFormatAsync(Guid bookId, BookFormatKind format, CancellationToken ct = default);

    /// <summary>新增一条 book_formats 记录（转换产物落地后调用）。</summary>
    Task AddFormatAsync(BookFormat format, CancellationToken ct = default);

    /// <summary>
    /// 删掉一条 book_formats 记录（用户 2026-10-20：「移除该格式」）。
    /// 只删数据库里的格式记录，**不删文件**（文件本来就已经不在了）；
    /// 书的其它格式与阅读进度 / 阅读状态都不受影响（reading_progress 按书 Id 存）。
    /// </summary>
    /// <returns>是否真的删掉了一行。记录本来就不在时返回 false。</returns>
    Task<bool> DeleteFormatAsync(Guid formatId, CancellationToken ct = default);

    /// <summary>
    /// 就地更新sample某个格式的体积与指纹（polish 就地替换文件后调用）。
    /// fullHash 传 null 表示内容已变、旧的全量哈希作废（等下次精确判重再算）。
    /// modifiedAt 传 null 表示用当前时间（我们自己刚写完文件）；接受外部改动时要传磁盘上的真实修改时间。
    /// </summary>
    /// <returns>是否真的更新了一行。</returns>
    Task<bool> UpdateFormatMetadataAsync(
        Guid bookId,
        BookFormatKind format,
        long fileSize,
        string? quickFingerprint,
        string? fullHash,
        DateTimeOffset? modifiedAt = null,
        CancellationToken ct = default);

    /// <summary>
    /// T3-6：编辑保存后把元数据回写 books（连同 updated_at）。
    /// 网格里的书名/作者读的是这张表，不回写就等于"改了看不见"。
    /// </summary>
    /// <returns>是否真的改到了一行。</returns>
    Task<bool> UpdateMetadataAsync(BookMetadataUpdate update, CancellationToken ct = default);

    /// <summary>扫描用：一次 JOIN 取回全部格式文件与书名（T2-6 文件监控）。</summary>
    Task<IReadOnlyList<FormatScanRow>> ListFormatsForScanAsync(CancellationToken ct = default);

    /// <summary>
    /// 扫描之后批量改 missing 标记（T2-6）。markMissing = 文件没了、要标成缺失的；
    /// clearMissing = 文件回来了、要取消缺失标记的。
    /// </summary>
    /// <returns>实际改动的行数。</returns>
    Task<int> UpdateMissingFlagsAsync(
        IReadOnlyList<Guid> markMissing,
        IReadOnlyList<Guid> clearMissing,
        CancellationToken ct = default);

    /// <summary>
    /// 删除一本书。book_formats 与 reading_progress 都是 ON DELETE CASCADE，
    /// 所以删 books 这一行就够了。返回是否真的删掉了一行。
    /// </summary>
    /// <remarks>只删数据库记录，正式区文件由 IBookDeletionService 负责。</remarks>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);

}
