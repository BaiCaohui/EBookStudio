using System.Text;
using EbookToolSuite.Core.Common;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace EbookToolSuite.Engines.Comics;

/// <summary>
/// 打开一个漫画压缩包（CBZ=zip / CBR=rar），列出图片条目并按**自然序**排序。T6-3。
/// </summary>
/// <remarks>
/// <para>
/// 为什么 CBZ 与 CBR 走同一个类：CBZ 是 zip 改名，CBR 是 rar，.NET 内置只认 zip。
/// 用 SharpCompress 的 <see cref="ArchiveFactory"/> 按签名自动识别，两者共用一条
/// 「列条目 → 自然序 → 按页读」的路径，不会各写一套排序与读页逻辑（设计文档 §6.6）。
/// </para>
/// <para>
/// 只读：不解包到磁盘。页位图由调用方解码后按需返回，内存里只留 JPEG
/// （判据：漫画页缓存不落盘，见计划 T6-3）。
/// </para>
/// </remarks>
public sealed class ComicArchive : IDisposable
{
    /// <summary>单页上限 96 MB：再大就不是漫画页了，读进来只会把内存打爆。</summary>
    private const long MaxPageBytes = 96L * 1024 * 1024;

    /// <summary>文本条目上限 4 MB：ComicInfo.xml 只有几百字节，放大到 4 MB 已远超正常范围。</summary>
    private const long MaxTextEntryBytes = 4L * 1024 * 1024;

    private readonly IArchive _archive;
    private readonly IReadOnlyList<IArchiveEntry> _pages;
    private readonly object _gate = new();

    private bool _disposed;

    private ComicArchive(IArchive archive, IReadOnlyList<IArchiveEntry> pages, IReadOnlyList<string> pageKeys)
    {
        _archive = archive;
        _pages = pages;
        PageKeys = pageKeys;
    }

    /// <summary>图片页数。</summary>
    public int PageCount => _pages.Count;

    /// <summary>排好序的条目名（与页号一一对应，用于排障）。</summary>
    public IReadOnlyList<string> PageKeys { get; }

    /// <summary>RAR（CBR）。</summary>
    public bool IsRar => _archive.Type == ArchiveType.Rar;

    /// <summary>整个归档是否加密（CBR 常见）。</summary>
    public bool IsEncrypted => _archive.IsEncrypted;

    /// <summary>打开归档并排出图片页。失败抛 <see cref="AppException"/>（用户能看懂的话）。</summary>
    public static ComicArchive Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            throw new AppException(
                ErrorCode.FileNotFound,
                "这个漫画文件不在了，可能被移动或删除了。",
                technical: "comic not found: " + path);
        }

        IArchive archive;

        try
        {
            /* LeaveStreamOpen=false：归档自己管文件句柄，我们只持有 IArchive。
               LookForHeader 保持默认 true：按文件头识别格式，不依赖扩展名。 */
            archive = ArchiveFactory.OpenArchive(path, new ReaderOptions { LeaveStreamOpen = false });
        }
        catch (Exception ex)
        {
            throw Translate(ex, path);
        }

        try
        {
            /* ★ SharpCompress 的异常是**惰性**的：OpenArchive 只校验签名，
               真正读中央目录/条目发生在枚举 Entries 的时候（实测损坏 ZIP 会在这里抛
               ArchiveException: Failed to locate the Zip Header）。所以枚举也必须翻译，
               否则损坏包会把英文异常原样漏给用户。 */
            IReadOnlyList<IArchiveEntry> pages;

            try
            {
                pages = archive.Entries
                    .Where(entry => !entry.IsDirectory)
                    .Where(entry => entry.Size > 0 && entry.Size <= MaxPageBytes)
                    .Where(entry => !IsJunkPath(entry.Key))
                    .Where(entry => CbzPacker.IsSupportedImage(entry.Key ?? string.Empty))
                    .OrderBy(entry => entry.Key ?? string.Empty, NaturalStringComparer.Instance)
                    .ThenBy(entry => entry.Key ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex)
            {
                throw Translate(ex, path);
            }

            if (pages.Count == 0)
            {
                /* 是合法压缩包，但里面没有图片：与「无法识别的电子书格式」同类。
                   UnsupportedFormat 这个码让探测层能把它和「包坏了」区分开。 */
                throw new AppException(
                    ErrorCode.UnsupportedFormat,
                    "这个压缩包里没有可显示的图片（支持 jpg / png / webp / bmp / gif）。",
                    technical: "no image entries: " + path);
            }

            return new ComicArchive(
                archive,
                pages,
                pages.Select(entry => entry.Key ?? string.Empty).ToList());
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    /// <summary>读第 <paramref name="index"/> 页的原始字节（不解码）。</summary>
    public byte[] ReadPage(int index)
    {
        if (index < 0 || index >= _pages.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "页号超出范围");
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var entry = _pages[index];

            try
            {
                using var input = entry.OpenEntryStream();
                using var output = new MemoryStream(entry.Size is > 0 and <= int.MaxValue ? (int)entry.Size : 64 * 1024);

                input.CopyTo(output);
                return output.ToArray();
            }
            catch (Exception ex)
            {
                throw Translate(ex, entry.Key ?? "?");
            }
        }
    }

    /// <summary>
    /// 只读读取一个文本条目（目前用于包内 <c>ComicInfo.xml</c>）。
    /// </summary>
    /// <param name="entryName">包内条目名（含扩展名），大小写不敏感。</param>
    /// <param name="text">读到的文本；失败时为空串。</param>
    /// <returns>true = 找到并读出来了；false = 没有这个条目 / 不是文本 / 读失败。</returns>
    /// <remarks>
    /// **永不抛异常**：它服务于只读探测（<see cref="ComicProbe"/>），
    /// 「没有元数据」和「元数据读坏了」对导入来说都只是「这本书没有书籍信息」，
    /// 不该因此让整个 CBZ 导入失败。归档已释放时同样返回 false，不抛 ObjectDisposedException。
    /// </remarks>
    public bool TryReadTextEntry(string entryName, out string text)
    {
        text = string.Empty;

        if (string.IsNullOrWhiteSpace(entryName))
        {
            return false;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            try
            {
                // 条目里可能用反斜杠（有的打包工具在 RAR 里这么写），统一成正斜杠再比。
                var wanted = entryName.Replace('\\', '/');

                var entry = _archive.Entries.FirstOrDefault(candidate =>
                    !candidate.IsDirectory
                    && candidate.Size is > 0 and <= MaxTextEntryBytes
                    && string.Equals(
                        (candidate.Key ?? string.Empty).Replace('\\', '/'),
                        wanted,
                        StringComparison.OrdinalIgnoreCase));

                if (entry is null)
                {
                    return false;
                }

                using var input = entry.OpenEntryStream();

                /* 按 BOM 自动识别编码：别的工具写的 ComicInfo.xml 可能带 BOM 或是 UTF-16，
                   这种差异不该导致元数据读不出来。无 BOM 时按 UTF-8 解（规范要求）。 */
                using var reader = new StreamReader(
                    input, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);

                text = reader.ReadToEnd();
                return true;
            }
            catch (Exception)
            {
                // 条目损坏 / 解压算法不支持 / 编码异常：都按「读不到」处理（见 remarks）。
                text = string.Empty;
                return false;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _archive.Dispose();
        }
    }

    /// <summary>
    /// 资源管理器/打包工具留下的垃圾条目：<c>__MACOSX/</c>、<c>.DS_Store</c>、隐藏文件。
    /// 它们不是页，混进来会让页码整体错位。
    /// </summary>
    private static bool IsJunkPath(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return true;
        }

        var segments = key.Replace('\\', '/').Split('/');

        foreach (var segment in segments)
        {
            if (segment.Length == 0)
            {
                continue;
            }

            if (segment[0] == '.'
                || segment.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 把底层异常翻成用户能看懂的一句话。计划 T6-3 明确要求：
    /// 「遇到不支持的要明确提示，不要静默失败」。
    /// </summary>
    private static AppException Translate(Exception ex, string path) => ex switch
    {
        FileNotFoundException => new AppException(
            ErrorCode.FileNotFound, "这个漫画文件不在了，可能被移动或删除了。", technical: path, inner: ex),

        UnauthorizedAccessException => new AppException(
            ErrorCode.AccessDenied, "没有读取这个漫画文件的权限。", technical: path, inner: ex),

        SharpCompress.Common.CryptographicException => new AppException(
            ErrorCode.DrmDetected,
            "这个漫画压缩包有密码保护，暂时打不开。可以先用解压工具解出图片再看。",
            technical: path,
            inner: ex),

        _ => new AppException(
            ErrorCode.CorruptFile,
            "该压缩包使用了不支持的算法（例如加密或固实压缩），或者文件已损坏，打不开。",
            technical: path,
            inner: ex),
    };
}
