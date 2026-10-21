using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Core.Interfaces;

/// <summary>
/// 阅读器服务：把书变成"能直接喂给 WebView2 的一堆文件"。
/// 规格见执行计划 T1-5、设计文档 8 章。
/// </summary>
/// <remarks>
/// 【准备与渲染分离】这是本服务唯一要记住的设计点：
/// OpenAsync 只做"解压 + 解析 OPF + 写 reader.json"，【不规范化任何章节】。
/// 章节是翻到哪章才物化哪章（MaterializeChapterAsync）。
/// 原因是 500 章的书若在打开时全量规范化会要好几秒，
/// 而 T1-5 的判据要求首屏不超过 1.5 秒（执行计划 T1-5 判据 5）。
/// </remarks>
public interface IReaderService
{
    /// <summary>
    /// 打开一本书的阅读会话。缓存已就绪时直接命中（FromCache = true），
    /// 否则解压并写清单。格式不支持或文件缺失时抛
    /// <see cref="EbookToolSuite.Core.Exceptions.AppException"/>
    /// （ErrorCode.ReaderOpenFailed）。
    /// </summary>
    Task<ReaderSession> OpenAsync(Guid bookId, CancellationToken ct = default);

    /// <summary>
    /// TXT 专用（T4-5 判据 2）：用指定编码重新打开这本书。
    /// 名字为空或者不认识时退回自动识别；编码与已有缓存一致时直接命中缓存。
    /// </summary>
    Task<ReaderSession> OpenWithTextEncodingAsync(
        Guid bookId, string? encodingName, CancellationToken ct = default);

    /// <summary>
    /// 把某一章规范化成可渲染的 HTML 写进 chapters/。
    /// 已物化过则直接返回，不重做。返回相对缓存根的 href（如 "chapters/0001.html"）。
    /// </summary>
    Task<string> MaterializeChapterAsync(
        ReaderSession session, int spineIndex, CancellationToken ct = default);

    /// <summary>删掉sample的阅读缓存。返回是否真的删掉了东西。</summary>
    Task<bool> InvalidateAsync(Guid bookId, CancellationToken ct = default);

    /// <summary>
    /// 编辑保存后重读目录与元数据（不重建阅读缓存）：EPUB 直接重新解源文件的 OPF / NCX / nav。
    /// 阅读器里点「编辑目录」→ 保存 → 目录抽屉与书籍信息自动刷新，靠的就是它。
    /// </summary>
    /// <param name="sourcePath">书的 EPUB 源文件绝对路径（ReaderSession.SourcePath）。</param>
    Task<ReaderMetadataSnapshot> ReadMetadataAsync(string sourcePath, CancellationToken ct = default);

    /// <summary>
    /// 用户 2026-10-21：换格式继续读时的「文字精确找位」。
    /// 在目标书的章节里找出包含给定文字的那一章，返回章号（0 基）；找不到返回 null。
    /// </summary>
    /// <remarks>
    /// 搜索从 <paramref name="hintIndex"/>（通常由已读百分比估出来）开始向两侧扩散，
    /// 最多看 <paramref name="maxChapters"/> 章，命中即返回——几百章的书不能整本扫。
    /// 只对文字类格式有意义（章节为空的书直接返回 null）。
    /// </remarks>
    Task<int?> FindChapterByTextAsync(
        ReaderSession session, string text, int hintIndex, int maxChapters, CancellationToken ct = default);

    /// <summary>
    /// 阅读窗口关闭：释放这本书占用的阅读缓存租约，让缓存上限清理可以回收它（T6-1 判据 6）。
    /// 没有打开过的书调它也无害（空操作）。
    /// </summary>
    void Release(Guid bookId);

    /// <summary>
    /// T5-2W 第三步「从阅读模式进编辑」：把工作区里**刚改好**的这一章重新物化进阅读缓存（覆盖旧文件），
    /// 供"保存后重新加载当前章"用。<paramref name="xhtml"/> 是工作区里的整章 XHTML（包内路径那份）。
    /// </summary>
    /// <remarks>
    /// 和 <see cref="MaterializeChapterAsync"/> 的区别只有两点：内容不从原书 zip 取（原书还没落盘），
    /// 而且**不认缓存**（同名文件直接覆盖）这正是计划里说的"章节缓存失效"。
    /// 返回相对缓存根的 href（与 MaterializeChapterAsync 一致）。
    /// </remarks>
    Task<string> RematerializeChapterAsync(
        ReaderSession session, int spineIndex, string xhtml, CancellationToken ct = default);

    /// <summary>
    /// T5-2W：把工作区里"这一章所在目录"的文件同步进阅读缓存的 content/。
    /// 可视编辑新插入的图片在工作区里、缓存里没有，不同步的话重新加载后会是一张裂图。
    /// 只同步这一个目录（章节的包内目录），不碰书的其它部分。返回复制的文件数。
    /// </summary>
    Task<int> SyncChapterAssetsAsync(
        ReaderSession session, string workspaceRoot, string chapterEntry, CancellationToken ct = default);
}