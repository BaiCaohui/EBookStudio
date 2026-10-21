using EbookToolSuite.Core.Interfaces;

namespace EbookToolSuite.Services.Library;

/// <summary>
/// 书库封面缓存的刷新（执行计划 T3-3 判据 4，设计文档 6.4）。
///
/// 书库网格不直接读 EPUB，读的是 <see cref="IAppPaths.CoversCacheDir"/> 下的两档 JPEG，
/// 导入时由 ImportPipeline 写好。换了封面之后这两张必须跟着换，否则网格还是老图。
/// </summary>
public sealed class LibraryCoverService : ILibraryCoverService
{
    /// <summary>详情页大图长边上限，与 <c>ImportOptions.CoverFullSizePx</c> 的默认值一致。</summary>
    public const int FullSizePx = 1200;

    /// <summary>网格缩略图宽度，与 <c>ImportOptions.CoverThumbWidthPx</c> 的默认值一致。</summary>
    public const int ThumbWidthPx = 320;

    private readonly IAppPaths _paths;
    private readonly IImageService _images;

    public LibraryCoverService(IAppPaths paths, IImageService images)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _images = images ?? throw new ArgumentNullException(nameof(images));
    }

    /// <inheritdoc />
    public string FullPath(Guid bookId)
        => Path.Combine(_paths.CoversCacheDir, bookId.ToString("D") + ".jpg");

    /// <inheritdoc />
    public string ThumbPath(Guid bookId)
        => Path.Combine(_paths.CoversCacheDir, bookId.ToString("D") + "_t.jpg");

    /// <inheritdoc />
    public Task<bool> RefreshAsync(Guid bookId, byte[] imageBytes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);

        if (imageBytes.Length == 0)
        {
            // 空图直接当"读不出来"，绝不拿旧图去顶
            return Task.FromResult(false);
        }

        _paths.EnsureCreated();

        // WriteCoverVariantsAsync 自己会先把两档写成 .part 再改名：
        // 图解不开时它一个文件都不动，所以"换封面失败"不会把旧封面搞坏。
        return _images.WriteCoverVariantsAsync(
            imageBytes,
            FullPath(bookId),
            ThumbPath(bookId),
            FullSizePx,
            ThumbWidthPx,
            ct);
    }
}