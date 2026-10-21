namespace EbookToolSuite.Core.Models;

/// <summary>
/// 编辑保存后重读出来的元数据与目录（只读快照，**不碰**阅读缓存）。
/// </summary>
/// <remarks>
/// 用途只有一个：阅读器里点「编辑目录」→ 保存 → 目录抽屉与书籍信息自动刷新。
/// 为什么不直接重开书：重开会丢掉当前章节与滚动位置，用户失去"我正在看哪"。
/// 为什么单独一个 record：它只描述"书上现在写的是什么"，与
/// <see cref="ReaderManifest"/>（已物化的阅读缓存）职责不同，混用会把缓存当成事实来源。
/// </remarks>
/// <param name="Title">OPF 里的书名（空串 = 书里没写，调用方保留旧标题）。</param>
/// <param name="Author">OPF 里的作者（null = 书里没写）。</param>
/// <param name="Toc">重新解析出来的目录（EPUB2 取 NCX / EPUB3 取 nav）。</param>
/// <param name="TocFallback">目录是不是按 spine 降级出来的。</param>
/// <param name="SpineCount">当前的章数——调用方据此判断"章数变了，跳转要重开才准"。</param>
public sealed record ReaderMetadataSnapshot(
    string Title,
    string? Author,
    IReadOnlyList<TocEntry> Toc,
    bool TocFallback,
    int SpineCount);
