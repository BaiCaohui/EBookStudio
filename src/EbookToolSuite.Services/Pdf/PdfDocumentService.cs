using Docnet.Core;
using Docnet.Core.Exceptions;
using Docnet.Core.Models;
using Docnet.Core.Readers;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Memory;
using SixLabors.ImageSharp.PixelFormats;

namespace EbookToolSuite.Services.Pdf;

/// <summary>
/// <see cref="IPdfDocumentService"/> 的 pdfium 实现，底层用 NuGet 包 Docnet.Core。
/// </summary>
/// <remarks>
/// <para>
/// 设计文档 §2.3.6 的首选就是 Docnet.Core（「比自己封 pdfium.dll 省 80% 代码」），
/// 实测在三份真实样本上跑通，因此没有退到 PDFtoImage（那条路还要多背一条 SkiaSharp 原生链）。
/// </para>
/// <para>
/// **线程安全**：Docnet.Core 内部对每次 pdfium 原生调用加锁
/// （源码注释：PDFium is not thread-safe so we need to lock every native call），
/// 所以本类注册成单例、被多个后台封面任务同时调用是安全的。代价是原生调用会串行化——
/// 封面生成本来就是低优先级后台任务，不值得为此开多进程。
/// </para>
/// <para>
/// **不持有 pdfium 的生命周期**：本类不实现 IDisposable。原生库由 Docnet.Core 的进程级单例持有，
/// 这里只借用；DI 容器不该去 Dispose 一个它没有创建的全局单例。
/// </para>
/// </remarks>
public sealed class PdfDocumentService : IPdfDocumentService
{
    /// <summary>默认 JPEG 质量。跟封面管线的取值保持一致，肉眼无差别的前提下体积可控。</summary>
    private const int DefaultJpegQuality = 85;

    /// <summary>
    /// 只用来读页数时传给 pdfium 的尺寸。
    /// </summary>
    /// <remarks>
    /// 读页数不需要像素尺寸，但 <see cref="Open"/> 必须给一个；给 1 是为了让 pdfium
    /// 不要为一次纯探测预分配大点阵缓存。**渲染路径绝不能复用它**——
    /// 尺寸会被固定在 reader 上，渲染出来的位图就只有 1 像素宽（探针实测踩过这个坑）。
    /// </remarks>
    private const int PageCountProbeEdge = 1;

    /// <summary>
    /// 读**字符框**时传给 pdfium 的尺寸。
    /// </summary>
    /// <remarks>
    /// 必须比 <see cref="PageCountProbeEdge"/> 大得多。实测：Docnet 的
    /// <c>Character.Box</c>（<c>BoundBox</c>）是 **int**，而且**已经乘过渲染缩放**——
    /// 同一页在 edge=600 与 edge=1200 下取框，数值恰好翻倍（实测 '丰' 的
    /// Left 在 424x600 下是 127、在 848x1200 下是 254）。所以框的精度下限是
    /// "1 个渲染像素"：探测尺寸越小，归一化后的误差越大（约 <c>1/edge</c>）。
    /// 取 1000 时误差约 0.1%（1200 像素宽的页面上约 1 像素），对叠文本层足够。
    /// 这里**不会渲染位图**（只调 <c>GetText/GetCharacters</c>），所以放大尺寸不付渲染代价。
    /// </remarks>
    private const int TextProbeEdge = 1000;

    /// <summary>pdfium 开文档失败时 ReasonPhrase 里出现这些字样 → 判定为加密。</summary>
    private static readonly string[] PasswordMarkers = ["password", "encrypt"];

    /// <summary>pdfium 打开入口。抽成接口只为可测，见 <see cref="IPdfiumLibrary"/> 的注释。</summary>
    private readonly IPdfiumLibrary _library;

    /// <summary>渲染结果的 20 页 LRU，见 <see cref="PdfPageCache"/>。</summary>
    private readonly PdfPageCache _cache;

    /// <summary>DI 用：由容器注入 <see cref="IPdfiumLibrary"/> 与共享的页缓存。</summary>
    /// <param name="library">pdfium 打开入口；生产环境传 <see cref="PdfiumLibrary"/>。</param>
    /// <param name="cache">
    /// 页缓存；不传就自己新建一个（容量默认 20）。留着可选是为了不动既有测试与调用点：
    /// 老代码 <c>new PdfDocumentService(new PdfiumLibrary())</c> 仍然编译得过。
    /// </param>
    public PdfDocumentService(IPdfiumLibrary library, PdfPageCache? cache = null)
    {
        _library = library;
        _cache = cache ?? new PdfPageCache();
    }

    /// <inheritdoc />
    public int GetPageCount(string path, string? password = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var reader = Open(path, new PageDimensions(PageCountProbeEdge, PageCountProbeEdge), password);

        var count = reader.GetPageCount();

        // pdfium 对合法 PDF 不会返回 0。真返回 0 说明文件结构能打开但没有可解析的页树，
        // 当成损坏处理：返回 0 会让上层误以为「这本书是空的」，与「读不出来」混为一谈。
        return count > 0
            ? count
            : throw new AppException(
                ErrorCode.CorruptFile,
                "这个 PDF 打不开（没有可读取的页面），请确认文件完整后重新下载。",
                technical: $"pdfium 返回页数 {count}：{path}",
                context: new Dictionary<string, object?> { ["path"] = path });
    }

    /// <inheritdoc />
    public byte[] RenderPage(string path, int pageIndex, int maxLongEdge)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLongEdge, 1);

        // dimOne/dimTwo 是「等比缩放后要塞进的矩形」，都填 maxLongEdge 就是
        // 「长边最多 maxLongEdge」——正好是接口承诺的语义。
        // 必须在打开文档时就定下来：reader 会把尺寸固定住。
        using var reader = Open(path, new PageDimensions(maxLongEdge, maxLongEdge));

        var pageCount = reader.GetPageCount();

        // 页码越界归 UnsupportedFormat：这是调用方页码算错了，不是文件损坏。
        // 用与「文件坏了」不同的码，UI 才会给「这本书只有 N 页」而不是「请重新下载」。
        if (pageIndex >= pageCount)
        {
            throw new AppException(
                ErrorCode.UnsupportedFormat,
                $"这本书只有 {pageCount} 页，第 {pageIndex + 1} 页不存在。",
                technical: $"pageIndex {pageIndex} >= pageCount {pageCount}：{path}",
                context: new Dictionary<string, object?>
                {
                    ["path"] = path,
                    ["pageIndex"] = pageIndex,
                    ["pageCount"] = pageCount,
                });
        }

        using var page = GetPageReader(reader, pageIndex, path);

        return RenderPageToJpeg(page, path, pageIndex);
    }

    /// <inheritdoc />
    public PdfPageImage RenderPage(string path, int pageIndex, PdfPageRenderOptions options, string? password = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxLongEdge, 1);

        // 旋转角在这里就校验掉：非法角度是调用方写错了参数，不该等到渲染完才发现。
        PdfPageEffects.NormalizeRotation(options.RotationDegrees);

        // 缓存键含最长边/旋转/主题三者，命中即返回（"翻回上一页"的主路径）。
        if (_cache.TryGet(path, pageIndex, options, out var cached))
        {
            return cached;
        }

        using var reader = Open(path, new PageDimensions(options.MaxLongEdge, options.MaxLongEdge), password);

        var pageCount = reader.GetPageCount();

        if (pageIndex >= pageCount)
        {
            throw PageOutOfRange(path, pageIndex, pageCount);
        }

        using var page = GetPageReader(reader, pageIndex, path);

        var image = RenderPageWithOptions(page, path, pageIndex, options);

        _cache.Set(path, pageIndex, options, image);

        return image;
    }

    /// <inheritdoc />
    public PdfPageText ReadPageText(string path, int pageIndex, string? password = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);

        // 用较大的探测尺寸换字符框精度，见 TextProbeEdge 的注释（不渲染，不付代价）。
        using var reader = Open(path, new PageDimensions(TextProbeEdge, TextProbeEdge), password);

        var pageCount = reader.GetPageCount();

        if (pageIndex >= pageCount)
        {
            throw PageOutOfRange(path, pageIndex, pageCount);
        }

        using var page = GetPageReader(reader, pageIndex, path);

        return new PdfPageText(ReadPageTextOf(page, path, pageIndex), ReadCharBoxes(page, path, pageIndex));
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ReadAllText(string path, string? password = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // 探测尺寸用 1x1：这条路径**只取文本、绝不渲染**，尺寸对 GetText 没有影响
        // （它返回的是 PDF 页面空间里的文本，不经过位图）。用同一个 PageCountProbeEdge
        // 是因为语义完全相同——"只探测、不渲染"，避免再多一个同值常量。
        using var reader = Open(path, new PageDimensions(PageCountProbeEdge, PageCountProbeEdge), password);

        var pageCount = reader.GetPageCount();
        var texts = new string[pageCount];

        for (var index = 0; index < pageCount; index++)
        {
            using var page = GetPageReader(reader, index, path);

            texts[index] = ReadPageTextOf(page, path, index);
        }

        return texts;
    }

    /// <inheritdoc />
    public IReadOnlyList<PdfOutlineEntry> ReadOutline(string path, string? password = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var entries = PdfOutlineReader.TryRead(path, password);

        if (entries is not null)
        {
            return entries;
        }

        // 原生 pdfium 打不开这份文档。**不在这里重复一套错误判定**：
        // 借既有的 Open()（Docnet 那条通路）把文件不存在/损坏/加密/缺 pdfium.dll
        // 翻译成与 GetPageCount/RenderPage 完全相同的错误码。它抛异常，我们就自然结束。
        using (Open(path, new PageDimensions(PageCountProbeEdge, PageCountProbeEdge), password))
        {
            // 能走到这里说明 Docnet 打得开、原生却打不开（两者本就是同一个 pdfium.dll，
            // 属于极罕见情形）。落到下面的 CorruptFile。
        }

        throw new AppException(
            ErrorCode.CorruptFile,
            "这个 PDF 的目录读不出来，文件可能已损坏。",
            technical: $"FPDF_LoadDocument 返回 NULL，但 Docnet 能打开：{path}",
            context: new Dictionary<string, object?> { ["path"] = path });
    }

    /// <summary>
    /// 按 <see cref="PdfPageRenderOptions"/> 渲染一页：解码 → 判图片页 → 旋转 → （文字页才）反色 → 编码。
    /// </summary>
    /// <remarks>
    /// <para>
    /// **顺序有讲究**：
    /// 图片页判定必须在任何像素改写**之前**做——彩色占比是对原图量的，
    /// 先反色再判会让"彩色"的定义被自己改掉；旋转虽然不改颜色，也一并放在前面，
    /// 让 <c>Width/Height</c> 天然就是旋转后的真实尺寸。
    /// </para>
    /// <para>
    /// 错误翻译与 <see cref="RenderPageToJpeg"/> 同形（尺寸非法/字节数不足/内存不足/解码失败）。
    /// 这里没有把它抽成两处共用的方法：那条路径被封面管线用着、行为已冻结，
    /// 为了共用去改动它比多这十几行校验的代价更大。
    /// </para>
    /// </remarks>
    private static PdfPageImage RenderPageWithOptions(
        IPageReader page,
        string path,
        int pageIndex,
        PdfPageRenderOptions options)
    {
        var (width, height, bgra) = ReadPageBitmap(page, path, pageIndex);

        try
        {
            using var image = Image.LoadPixelData<Bgra32>(bgra, width, height);

            // 判定用原图：文本长度优先（扫描页一次字符串长度就判完了，不必抽样像素）。
            var isImagePage = PdfPageEffects.IsImagePage(page.GetText(), image);

            PdfPageEffects.Rotate(image, options.RotationDegrees);

            // 图片页不反色：照片反成底片比"深色底上深色字"糟糕得多。
            if (options.Theme == PdfPageTheme.Dark && !isImagePage)
            {
                PdfPageEffects.InvertRgb(image);
            }

            using var buffer = new MemoryStream();
            image.Save(buffer, new JpegEncoder { Quality = DefaultJpegQuality });

            return new PdfPageImage(buffer.ToArray(), image.Width, image.Height, isImagePage);
        }
        catch (InvalidMemoryOperationException ex)
        {
            throw new AppException(
                ErrorCode.InternalError,
                "渲染 PDF 页面时内存不足，请关闭其它程序后重试。",
                technical: $"页 {pageIndex} {width}x{height} 位图转 JPEG 时内存不足：{ex.Message}",
                inner: ex,
                context: new Dictionary<string, object?> { ["path"] = path, ["pageIndex"] = pageIndex });
        }
        catch (Exception ex) when (ex is DocnetException or ImageFormatException)
        {
            // GetImage 是懒渲染：pdfium 的异常可能在真正取像素时才抛出来。
            throw new AppException(
                ErrorCode.CorruptFile,
                "这个 PDF 的这一页渲染失败，文件可能已损坏。",
                technical: $"页 {pageIndex} 渲染抛 {ex.GetType().FullName}：{ex.Message}",
                inner: ex,
                context: new Dictionary<string, object?> { ["path"] = path, ["pageIndex"] = pageIndex });
        }
    }

    /// <summary>取一页的位图，并校验尺寸与字节数（<see cref="RenderPageToJpeg"/> 的同形前置检查）。</summary>
    private static (int Width, int Height, byte[] Bgra) ReadPageBitmap(
        IPageReader page,
        string path,
        int pageIndex)
    {
        int width = page.GetPageWidth();
        int height = page.GetPageHeight();

        if (width <= 0 || height <= 0)
        {
            throw new AppException(
                ErrorCode.CorruptFile,
                "这个 PDF 的这一页没有有效尺寸，文件可能已损坏。",
                technical: $"页 {pageIndex} 渲染尺寸 {width}x{height}：{path}",
                context: new Dictionary<string, object?> { ["path"] = path, ["pageIndex"] = pageIndex });
        }

        byte[] bgra = page.GetImage();
        long expected = (long)width * height * 4;

        if (bgra.LongLength < expected)
        {
            throw new AppException(
                ErrorCode.CorruptFile,
                "这个 PDF 的这一页渲染结果不完整，文件可能已损坏。",
                technical: $"页 {pageIndex} 位图字节数 {bgra.LongLength} < 期望 {expected}（{width}x{height}x4）：{path}",
                context: new Dictionary<string, object?> { ["path"] = path, ["pageIndex"] = pageIndex });
        }

        /* pdfium 不给页面铺背景：没被内容覆盖的像素保持缓冲区初值（全 0），
           JPEG 丢掉 alpha 之后就是纯黑页。这里是 Docnet 字节进入本管线的**唯一入口**，
           所以白底合成放在这一处，页面渲染与封面渲染两条路都覆盖到。
           详细证据与"为什么按 alpha 而不是按颜色刷白"见 PdfPageEffects.FlattenOntoWhite。 */
        PdfPageEffects.FlattenOntoWhite(bgra);

        return (width, height, bgra);
    }

    /// <summary>取一页的纯文本；pdfium 的懒异常在这一处翻译（扫描页返回空串，不算错误）。</summary>
    private static string ReadPageTextOf(IPageReader page, string path, int pageIndex)
    {
        try
        {
            return page.GetText() ?? string.Empty;
        }
        catch (DocnetException ex)
        {
            throw new AppException(
                ErrorCode.CorruptFile,
                "这个 PDF 的这一页文字读不出来，文件可能已损坏。",
                technical: $"页 {pageIndex} GetText 抛 {ex.GetType().Name}：{ex.Message}",
                inner: ex,
                context: new Dictionary<string, object?> { ["path"] = path, ["pageIndex"] = pageIndex });
        }
    }

    /// <summary>
    /// 把 pdfium 的字符框归一化到 [0,1]。
    /// </summary>
    /// <remarks>
    /// <para>
    /// **原点方向（实测结论，不是猜的）**：pdfium/Docnet 给的是
    /// **左上角为原点、y 轴向下**的框，而且**已经乘过本次渲染的缩放**。
    /// 实测方法：造一份 MediaBox 400x600 的单页 PDF，"TOP" 画在 PDF 坐标 y=500、
    /// "BOTTOM" 画在 y=100（PDF 坐标原点在左下）。以 edge=400 渲染得到 266x400：
    /// TOP 的框是 <c>L=34 T=55 R=43 B=67</c>、BOTTOM 是 <c>L=34 T=322 R=43 B=333</c>。
    /// 距页顶 100pt 的那行 T=55（≈100×2/3 再减字号高度），距页顶 500pt 的那行 T=322
    /// （≈500×2/3），**T 小的在上方** → 原点在左上。同一页换成 edge=800（533x800）
    /// 数值全部翻倍（T=110 / T=644），证明框随渲染尺寸缩放。
    /// </para>
    /// <para>
    /// 因此归一化用**本次渲染的宽高** <c>GetPageWidth/GetPageHeight()</c> 做分母，
    /// 结果与渲染分辨率无关（实测 '丰' 的 Left：424x600 下 127/424 = 0.2995，
    /// 848x1200 下 254/848 = 0.2995，完全一致）。
    /// </para>
    /// <para>
    /// 夹到 [0,1] 是**保证**而不是修正：pdfium 对越界字形（旋转文字、悬挂的标点）
    /// 可能给出落在页面框外的框，上层拿它算 CSS 定位时不会自己做边界处理。
    /// 左右/上下反序（<c>Right &lt; Left</c>）时交换，保证 Width/Height 恒非负。
    /// </para>
    /// </remarks>
    private static IReadOnlyList<PdfCharBox> ReadCharBoxes(IPageReader page, string path, int pageIndex)
    {
        int width = page.GetPageWidth();
        int height = page.GetPageHeight();

        if (width <= 0 || height <= 0)
        {
            throw new AppException(
                ErrorCode.CorruptFile,
                "这个 PDF 的这一页没有有效尺寸，文件可能已损坏。",
                technical: $"页 {pageIndex} 文本层尺寸 {width}x{height}：{path}",
                context: new Dictionary<string, object?> { ["path"] = path, ["pageIndex"] = pageIndex });
        }

        var boxes = new List<PdfCharBox>();

        foreach (var character in page.GetCharacters())
        {
            var box = character.Box;

            var left = Clamp01(box.Left / (double)width);
            var right = Clamp01(box.Right / (double)width);
            var top = Clamp01(box.Top / (double)height);
            var bottom = Clamp01(box.Bottom / (double)height);

            if (right < left)
            {
                (left, right) = (right, left);
            }

            if (bottom < top)
            {
                (top, bottom) = (bottom, top);
            }

            boxes.Add(new PdfCharBox(character.Char, left, top, right - left, bottom - top));
        }

        return boxes;
    }

    private static double Clamp01(double value) => Math.Clamp(value, 0d, 1d);

    /// <summary>页码越界：归 UnsupportedFormat（调用方算错页码，不是文件坏了）。</summary>
    private static AppException PageOutOfRange(string path, int pageIndex, int pageCount)
        => new(
            ErrorCode.UnsupportedFormat,
            $"这本书只有 {pageCount} 页，第 {pageIndex + 1} 页不存在。",
            technical: $"pageIndex {pageIndex} >= pageCount {pageCount}：{path}",
            context: new Dictionary<string, object?>
            {
                ["path"] = path,
                ["pageIndex"] = pageIndex,
                ["pageCount"] = pageCount,
            });

    /// <summary>取单页读者，把「页数读得出来、单页却打不开」的异常翻译掉。</summary>
    private static IPageReader GetPageReader(IDocReader reader, int pageIndex, string path)
    {
        try
        {
            return reader.GetPageReader(pageIndex);
        }
        catch (DocnetException ex)
        {
            // 页数读得出来、单页却打不开：pdfium 对「页对象引用损坏」的典型表现。
            throw new AppException(
                ErrorCode.CorruptFile,
                "这个 PDF 的这一页读不出来，文件可能不完整，请重新下载。",
                technical: $"GetPageReader({pageIndex}) 抛 {ex.GetType().Name}：{ex.Message}",
                inner: ex,
                context: new Dictionary<string, object?> { ["path"] = path, ["pageIndex"] = pageIndex });
        }
    }

    /// <summary>把一页的 pdfium 位图转成 JPEG 字节。</summary>
    private static byte[] RenderPageToJpeg(IPageReader page, string path, int pageIndex)
    {
        int width = page.GetPageWidth();
        int height = page.GetPageHeight();

        if (width <= 0 || height <= 0)
        {
            throw new AppException(
                ErrorCode.CorruptFile,
                "这个 PDF 的这一页没有有效尺寸，文件可能已损坏。",
                technical: $"页 {pageIndex} 渲染尺寸 {width}x{height}：{path}",
                context: new Dictionary<string, object?> { ["path"] = path, ["pageIndex"] = pageIndex });
        }

        byte[] bgra = page.GetImage();

        // GetImage 承诺返回 width*height*4（BGRA）字节。数量不符说明原生层给了别的东西，
        // 与其拿错位的内存去解码、得到一个诡异的错误，不如在这里就报损坏。
        long expected = (long)width * height * 4;

        if (bgra.LongLength < expected)
        {
            throw new AppException(
                ErrorCode.CorruptFile,
                "这个 PDF 的这一页渲染结果不完整，文件可能已损坏。",
                technical: $"页 {pageIndex} 位图字节数 {bgra.LongLength} < 期望 {expected}（{width}x{height}x4）：{path}",
                context: new Dictionary<string, object?> { ["path"] = path, ["pageIndex"] = pageIndex });
        }

        try
        {
            using var image = Image.LoadPixelData<Bgra32>(bgra, width, height);

            using var buffer = new MemoryStream();
            image.Save(buffer, new JpegEncoder { Quality = DefaultJpegQuality });

            return buffer.ToArray();
        }
        // ImageSharp 对超大位图会主动抛这个（而不是等 OOM）。放在最前面，
        // 因为它不是 pdfium 的错误，报「内存不足」比报「文件损坏」对用户更有指导意义。
        catch (InvalidMemoryOperationException ex)
        {
            throw new AppException(
                ErrorCode.InternalError,
                "渲染 PDF 封面时内存不足，请关闭其它程序后重试。",
                technical: $"页 {pageIndex} {width}x{height} 位图转 JPEG 时内存不足：{ex.Message}",
                inner: ex,
                context: new Dictionary<string, object?> { ["path"] = path, ["pageIndex"] = pageIndex });
        }
        // ImageFormatException 已经覆盖它的子类 InvalidImageContentException / UnknownImageFormatException，
        // 不必逐个列。它表示"这段字节不是能解码的位图"——对上层同样是"这个文件坏了"。
        catch (Exception ex) when (ex is DocnetException or ImageFormatException)
        {
            // GetImage 是懒渲染：pdfium 的异常可能在真正取像素时才抛出来。
            throw new AppException(
                ErrorCode.CorruptFile,
                "这个 PDF 的这一页渲染失败，文件可能已损坏。",
                technical: $"页 {pageIndex} 渲染抛 {ex.GetType().FullName}：{ex.Message}",
                inner: ex,
                context: new Dictionary<string, object?> { ["path"] = path, ["pageIndex"] = pageIndex });
        }
    }

    /// <summary>
    /// 打开文档并统一把底层异常翻译成 <see cref="AppException"/>。
    /// 所有 pdfium 错误都在这一个出口处理，避免每个调用点各写一遍映射、逐渐跑偏。
    /// </summary>
    /// <param name="dimensions">
    /// **必须是为本次用途算好的渲染尺寸**。<see cref="DocLib"/> 打开时就把尺寸固定下来，
    /// 之后 <c>GetPageWidth/GetPageHeight</c> 返回的是这个尺寸下的结果——
    /// 早期版本图省事对"读页数"和"渲染"共用了一个 1x1 的探测尺寸，
    /// 结果渲染拿到的位图恒为 1 像素宽、直接被当成损坏文件拒掉（探针实测暴露）。
    /// </param>
    /// <param name="password">
    /// PDF 口令；null/空走不带口令的重载。给了口令仍打不开（口令错）时，
    /// pdfium 报的同样是 "password required or incorrect password" → 仍然翻译成
    /// <see cref="ErrorCode.DrmDetected"/>，上层据此再弹一次密码框。
    /// </param>
    private IDocReader Open(string path, PageDimensions dimensions, string? password = null)
    {
        // 先自己判一次文件在不在：pdfium 对「不存在」和「不是 PDF」都只回一个笼统的
        // ErrorCode，拿不到路径信息；这里先判能给出更准的提示，也让测试不必依赖 pdfium 的措辞。
        if (!File.Exists(path))
        {
            throw new AppException(
                ErrorCode.FileNotFound,
                "找不到这个 PDF 文件，它可能已被移动或删除。",
                technical: $"File.Exists 为 false：{path}",
                context: new Dictionary<string, object?> { ["path"] = path });
        }

        try
        {
            return string.IsNullOrEmpty(password)
                ? _library.GetDocReader(path, dimensions)
                : _library.GetDocReader(path, password, dimensions);
        }
        catch (DocnetLoadDocumentException ex)
        {
            throw TranslateLoadFailure(ex, path);
        }
        catch (DocnetException ex)
        {
            throw new AppException(
                ErrorCode.CorruptFile,
                "这个 PDF 文件读不出来，可能已损坏，请重新下载。",
                technical: $"{ex.GetType().Name}：{ex.Message}",
                inner: ex,
                context: new Dictionary<string, object?> { ["path"] = path });
        }
        // 原生 pdfium.dll 没跟着输出目录走、或位数不对时抛的是这两个，而不是 DocnetException。
        // 会在这种时候才暴露：实测把 runtimes\win-x64\native\pdfium.dll 改名后，
        // 所有调用都直接抛 DllNotFoundException，绕过 AppException 一路冒到 UI。
        // 归 MissingTool（"环境缺东西"那一组）而不是 CorruptFile：文件没坏，是部署缺件，
        // 用户的正确动作是重装而不是重新下载这本书。
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
        {
            throw new AppException(
                ErrorCode.MissingTool,
                "PDF 渲染组件（pdfium）没有正确安装，请重新安装本软件。",
                technical: $"加载原生 pdfium 失败：{ex.GetType().Name}：{ex.Message}",
                inner: ex,
                context: new Dictionary<string, object?> { ["path"] = path });
        }
    }

    /// <summary>
    /// 把 pdfium 的加载失败（<see cref="DocnetLoadDocumentException.ErrorCode"/>）映射到既有错误码。
    /// </summary>
    /// <remarks>
    /// pdfium 的错误码本身信息量很小，且没找到公开稳定的一一对应表，所以按 ReasonPhrase
    /// 文本特征来判，判不出来归 <see cref="ErrorCode.CorruptFile"/>。
    /// 实测（本机 Docnet.Core 2.6.0 自带 pdfium）：
    /// ErrorCode=2 → "file not found or could not be opened"；
    /// ErrorCode=3 → "file not in PDF format or corrupted"；
    /// ErrorCode=4 → "password required or incorrect password"。
    /// 即便如此也不按数值分支：这三个值来自实测而非文档化契约，换 pdfium 版本就可能变。
    /// </remarks>
    private static AppException TranslateLoadFailure(DocnetLoadDocumentException ex, string path)
    {
        var technical = $"pdfium ErrorCode={ex.ErrorCode}，Message={ex.Message}";
        var context = new Dictionary<string, object?> { ["path"] = path, ["pdfiumErrorCode"] = ex.ErrorCode };

        if (ContainsPasswordMarker(ex.Message))
        {
            // 设计文档 §6.5.4 允许加密 PDF 以 placeholder 封面进库、不阻塞导入，
            // 所以这里必须给 DrmDetected 而不是 CorruptFile，上层才有依据改走占位图。
            return new AppException(
                ErrorCode.DrmDetected,
                "这个 PDF 有密码保护，暂时无法读取。",
                technical: technical,
                inner: ex,
                context: context);
        }

        // 注意：不按 ErrorCode 数值硬编码 2/3 分支。那两个值来自一次实测，不是文档化契约，
        // 写死以后换 pdfium 版本就会静默走错分支。文本特征更稳，判不出就按损坏处理。
        return new AppException(
            ErrorCode.CorruptFile,
            "这个 PDF 读不出来，文件可能已损坏或加了密，请重新下载后再试。",
            technical: technical,
            inner: ex,
            context: context);
    }

    /// <summary>在 pdfium 的 ReasonPhrase 里找加密特征词（忽略大小写）。</summary>
    private static bool ContainsPasswordMarker(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return false;
        }

        foreach (var marker in PasswordMarkers)
        {
            if (message.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
