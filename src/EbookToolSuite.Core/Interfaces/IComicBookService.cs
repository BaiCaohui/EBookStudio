using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Core.Interfaces;

/// <summary>
/// 漫画（CBZ/CBR）读取入口。T6-3。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="IPdfDocumentService"/> 同一分工：本接口只负责「按页给字节」，
/// 排版、缩放、单页/双页/日漫全部在阅读器外壳（reader.js）里，宿主不做视图状态。
/// </para>
/// <para>
/// <see cref="Open"/> 返回的句柄要在阅读窗口关闭时 <see cref="IComicBook.Dispose"/>：
/// RAR（尤其固实压缩）的条目读取需要保持归档句柄打开，每次请求重开归档会反复解压。
/// </para>
/// </remarks>
public interface IComicBookService
{
    /// <summary>只读探测：不解包、不读图，只确认「是不是漫画包、多少页」。永不抛异常。</summary>
    ComicProbeResult Probe(string path);

    /// <summary>打开一个漫画包（列出图片条目并按自然序排好）。失败抛 <c>AppException</c>。</summary>
    IComicBook Open(string path);

    /// <summary>
    /// 导入用：把第 1 页渲染成封面 JPEG。失败返回 null（书库走占位图，不阻塞导入）。
    /// </summary>
    byte[]? RenderCover(string path, int maxLongEdge);
}

/// <summary>一本打开着的漫画包。**不是线程安全的**，由调用方串行使用（阅读器只有一个渲染闸门）。</summary>
public interface IComicBook : IDisposable
{
    /// <summary>图片页数。</summary>
    int PageCount { get; }

    /// <summary>
    /// 把第 <paramref name="index"/> 页（0 基）解码、按需缩小、旋转、编码成 JPEG。
    /// </summary>
    /// <param name="index">页号，0 基。</param>
    /// <param name="maxLongEdge">最长边上限（像素）。只缩不放。</param>
    /// <param name="rotation">顺时针旋转角度（0/90/180/270）。</param>
    ComicPageImage RenderPage(int index, int maxLongEdge, int rotation);
}
