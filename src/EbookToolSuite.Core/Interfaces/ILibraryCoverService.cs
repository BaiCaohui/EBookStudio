namespace EbookToolSuite.Core.Interfaces;

/// <summary>
/// 书库封面缓存（<c>{bookId:D}.jpg</c> 详情页大图与 <c>{bookId:D}_t.jpg</c> 网格缩略图）的刷新。
/// 执行计划 T3-3 判据 4：换了 EPUB 里的封面之后，书库网格的缩略图必须跟着变。
/// </summary>
/// <remarks>
/// 与 <c>CoverService</c> 的分工：那个改的是 EPUB 工作区里的封面资源，不知道书库；
/// 这个只负责把新封面落到书库缓存。UI 侧"丢掉已解码的 ImageSource 重新解码"由
/// LibraryViewModel 负责。
/// </remarks>
public interface ILibraryCoverService
{
    /// <summary>详情页大图路径（<c>{bookId:D}.jpg</c>）。</summary>
    string FullPath(Guid bookId);

    /// <summary>网格缩略图路径（<c>{bookId:D}_t.jpg</c>）。</summary>
    string ThumbPath(Guid bookId);

    /// <summary>
    /// 用 <paramref name="imageBytes"/> 重写这本书的两档封面缓存。
    /// </summary>
    /// <returns>
    /// false 表示这张图解不开（损坏 / 不是图片），此时**两张旧图都不动**，调用方应保留原封面。
    /// </returns>
    Task<bool> RefreshAsync(Guid bookId, byte[] imageBytes, CancellationToken ct = default);
}