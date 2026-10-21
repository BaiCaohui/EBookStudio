using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Models;
using Microsoft.Extensions.Logging;

namespace EbookToolSuite.App.ViewModels;

/// <summary>
/// 详情面板里的一行元数据。
/// </summary>
/// <remarks>
/// 格式行多带两个字段（用户 2026-10-20）：文件已经删掉了（缺失）时，
/// 值显示「（已删除）」，行尾要出现「移除该格式」按钮。
/// 普通元数据行两个字段都是默认值，界面上的按钮因此不显示。
/// </remarks>
public sealed record MetadataRow(
    string Label,
    string Value,
    bool IsDeletedFormat = false,
    BookFormatKind? FormatKind = null,
    bool IsSwitchable = false,
    bool IsFormatRow = false);

/// <summary>
/// 书库网格里的一张卡片。规格见执行计划 T1-3。
/// </summary>
/// <remarks>
/// 封面加载的两条铁律（执行计划里写死的，这里照做）：
/// 1. CacheOption.OnLoad：立刻读完，不持有文件句柄，否则将来替换封面会"文件被占用"。
/// 2. Freeze()：冻结后才能跨线程，解码就不必占着 UI 线程。
/// </remarks>
public sealed class BookCardViewModel : INotifyPropertyChanged
{
    /// <summary>解码宽度。缩略图生成时宽 320，这里按 240 解码，高 DPI 下也不糊。</summary>
    public const int DecodePixelWidth = 240;

    private readonly SemaphoreSlim _coverGate;
    private readonly ILogger _log;

    private ImageSource? _cover;
    private bool _isSelected;
    private bool _isChecked;
    private BookFileState _fileState = BookFileState.Normal;
    private BookFormatKind? _preferredFormat;
    private long? _convertedEpubBytes;

    public BookCardViewModel(Book book, string? thumbnailPath, SemaphoreSlim coverGate, ILogger log)
    {
        Book = book;
        ThumbnailPath = thumbnailPath;
        _coverGate = coverGate;
        _log = log;
        Metadata = BuildMetadata(book);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Book Book { get; }

    public Guid Id => Book.Id;

    public string Title => Book.Title;

    public string Authors => string.IsNullOrWhiteSpace(Book.Authors) ? "未知作者" : Book.Authors;

    /// <summary>阅读状态（未读 / 在读 / 已读完 / 弃读）。网格左上角的角标用它。</summary>
    public ReadingStatus ReadingState => Book.Status;

    /// <summary>
    /// 状态角标的文案。**在读的书只显示进度数字**（"58%"） 用户 2026-10-03 要求：
    /// 在读不占着角标写"在读"两个字，那个信息由颜色（蓝）与封面底部的进度条表达。
    /// 例外：手标成"在读"但一点进度都没有的书，还是显示"在读"  显示"0%"没有意义。
    /// </summary>
    public string ReadingStateText => Book.Status switch
    {
        ReadingStatus.Reading => ProgressPercent > 0 ? ProgressText : "在读",
        ReadingStatus.Finished => "已读完",
        ReadingStatus.Abandoned => "弃读",
        _ => "未读",
    };

    /// <summary>
    /// 角标的完整说法（"在读 58%" / "未读" / "已读完" / "弃读"），给 ToolTip 用：
    /// 只看到一个数字时，鼠标停一下就能问出所以然。
    /// </summary>
    public string ReadingStateFullText => Book.Status switch
    {
        ReadingStatus.Reading => IsContinueReading ? "在读 " + ProgressText : "在读",
        ReadingStatus.Finished => "已读完",
        ReadingStatus.Abandoned => "弃读",
        _ => "未读",
    };

    /// <summary>
    /// 格式角标：**这本书所有格式都显示**（用户 2026-10-21：三种格式的书只显示两种是缺陷）。
    /// </summary>
    /// <remarks>
    /// MOBI / AZW3 是转成 EPUB 读的，所以当它就是"当前阅读格式"时，角标里再补一个 EPUB。
    /// **补充的 EPUB 排在真实格式后面**（用户 2026-10-21 截图反馈：MOBI 要显示在 EPUB 前面），
    /// 真实格式一个都不省略、顺序按书库里的格式顺序。
    /// </remarks>
    public string FormatBadge
    {
        get
        {
            var names = new List<string>(Book.Formats.Count + 1);

            names.AddRange(Book.Formats
                .Select(f => f.Format.ToStorageString().ToUpperInvariant())
                .Distinct());

            var effective = EffectiveFormat;

            if (effective is not null
                && ReaderFormatSupport.RequiresEpubConversion(effective.Format)
                && !names.Contains(BookFormatKind.Epub.ToStorageString()))
            {
                names.Add(BookFormatKind.Epub.ToStorageString());
            }

            return string.Join("+", names);
        }
    }

    /// <summary>
    /// 点「阅读」时实际会打开哪个格式文件（用户选过的优先，否则按默认优先级链）。
    /// 用户 2026-10-21：多格式共存的书籍要能在详情里切换它。
    /// </summary>
    public BookFormat? EffectiveFormat => ReaderFormatSupport.Resolve(Book.Formats, _preferredFormat);

    /// <summary>
    /// 详情页底部、紧挨「阅读」按钮左边那行小字（用户 2026-10-21）。
    /// </summary>
    /// <remarks>
    /// MOBI / AZW3 以 EPUB 形态阅读：
    /// 已经打开过一次（转换缓存里有产物）就说「当前阅读格式：EPUB」；
    /// 还没打开过（没有产物）就说清楚"当前格式不支持阅读，点击阅读后自动转换"——
    /// 这时详情里也**不会**出现 EPUB 那一行（那行代表真实存在的转换产物）。
    /// </remarks>
    public string ReadingFormatText
    {
        get
        {
            var effective = EffectiveFormat;

            if (effective is null)
            {
                return string.Empty;
            }

            if (ReaderFormatSupport.RequiresEpubConversion(effective.Format))
            {
                return _convertedEpubBytes is not null
                    ? "当前阅读格式：EPUB"
                    : "当前格式不支持阅读，点击阅读后自动转换为 EPUB 格式进行阅读";
            }

            return "当前阅读格式：" + effective.Format.ToStorageString();
        }
    }

    /// <summary>缩略图约定路径（Cache/Covers/{id:D}_t.jpg）。文件在不在由 LoadCoverAsync 判断。</summary>
    public string? ThumbnailPath { get; }

    /// <summary>详情面板的元数据行，空值不占位。</summary>
    public IReadOnlyList<MetadataRow> Metadata { get; private set; }

    public ImageSource? Cover
    {
        get => _cover;
        private set
        {
            if (ReferenceEquals(_cover, value))
            {
                return;
            }

            _cover = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasCover));
        }
    }

    public bool HasCover => _cover is not null;

    /// <summary>
    /// 这本书是**宽松兜底**读进来的（没通过 VersOne 的严格规范校验）。
    /// 卡片右上角要标一下，别让"能用"和"完好"看起来一样（设计文档 6.1.9）。
    /// </summary>
    public bool IsDamaged => Book.Damaged;

    /// <summary>
    /// 文件在磁盘上的状态（T2-6）：正常 / 缺失 / 被外部修改。
    /// 由 IBookDeletionService 之外的 ILibraryScanService 扫描后灌进来，卡片自己不算。
    /// </summary>
    public BookFileState FileState => _fileState;

    /// <summary>有问题时给用户看的一小句，正常时是空串。</summary>
    public string FileStateText => _fileState switch
    {
        BookFileState.Missing => "文件缺失",
        BookFileState.ExternallyModified => "已修改",
        _ => string.Empty,
    };

    /// <summary>
    /// 卡片 / 列表角标上的悬停说明。这里只能"指路"（详情在哪），不能假设用户已经打开详情。
    /// </summary>
    public string FileStateTip => _fileState switch
    {
        BookFileState.Missing => "文件不在原来的位置了（可能被手工删掉或移走）。",
        BookFileState.ExternallyModified => "文件被外部修改过（体积或修改时间变了）。打开详情可以接受这次改动。",
        _ => string.Empty,
    };

    /// <summary>详情浮层里的完整说明。用户已经在这里了，所以直接说"点哪个按钮"。</summary>
    public string FileStateDetail => _fileState switch
    {
        BookFileState.Missing => "文件不在原来的位置了（可能被手工删掉或移走）。可以从书库移除这本书。",
        BookFileState.ExternallyModified => "文件被外部修改过（体积或修改时间变了）。点「接受改动」把库里记的体积、修改时间与指纹更新成磁盘现状。",
        _ => string.Empty,
    };

    /// <summary>已经删掉的格式，详情里显示的字样（用户 2026-10-20 指定：大小位置改为这个）。</summary>
    public const string DeletedFormatText = "（已删除）";

    /// <summary>
    /// 用户 2026-10-21 指定的原话：MOBI / AZW3 那一行的大小后面要带这句括号说明。
    /// </summary>
    public const string ConvertedFormatNote = "（该格式不支持阅读，已自动转换为EPUB格式）";

    /// <summary>
    /// 这本书是不是已经一个格式都不剩了。
    /// 用户 2026-10-20：移掉最后一个「已删除」格式后，只剩这种情况才提示「可以从书库移除这本书」。
    /// </summary>
    public bool HasNoFormats => Book.Formats.Count == 0;

    /// <summary>没有任何格式时给用户看的一句话；还有格式时是空串。</summary>
    public string NoFormatsDetail => HasNoFormats
        ? "这本书已经没有任何可用的格式文件了。可以从书库移除这本书。"
        : string.Empty;

    /// <summary>能不能"接受改动"。文件缺失时没什么可接受的，按钮就不显示。</summary>
    public bool CanAcceptModification => _fileState == BookFileState.ExternallyModified;

    /// <summary>有没有文件层面的问题。卡片用它决定要不要显示角标。</summary>
    public bool HasFileProblem => _fileState != BookFileState.Normal;

    /// <summary>扫描完把状态灌进来（T2-6）。值没变就不发通知，省一次绑定求值。</summary>
    public void SetFileState(BookFileState state)
    {
        if (_fileState == state)
        {
            return;
        }

        _fileState = state;
        OnPropertyChanged(nameof(FileState));
        OnPropertyChanged(nameof(FileStateText));
        OnPropertyChanged(nameof(FileStateTip));
        OnPropertyChanged(nameof(FileStateDetail));
        OnPropertyChanged(nameof(HasFileProblem));
        OnPropertyChanged(nameof(CanAcceptModification));
    }

    /// <summary>
    /// 读缩略图。文件不在（比如用户删了 Covers 目录）就直接返回，卡片继续显示占位图。
    /// 单张封面坏掉只记 WRN，不能让整页崩掉。
    /// </summary>
    /// <summary>
    /// 网格里的选中态。工具栏的「转换」按钮与右键菜单都作用于当前选中的这一本。
    /// 用 VM 上的布尔值 + XAML 里的 DataTrigger 做高亮，不引额外的转换器。
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged();
        }
    }
    /// <summary>
    /// 批量删除模式下的勾选态。
    /// 和 IsSelected 是两件事：IsSelected 是「工具栏的转换按钮作用于哪一本」，
    /// IsChecked 是「哪几本等着被批量删除」。两者互斥（进入批量模式会清掉单选）。
    /// </summary>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value)
            {
                return;
            }

            _isChecked = value;
            OnPropertyChanged();
        }
    }

    /// <summary>阅读进度 0..1（T1-6）。0 表示没读过，1 表示读完。</summary>
    public double ProgressPercent { get; private set; }

    /// <summary>
    /// 显示「继续阅读」角标的条件：读了一点但还没读完（执行计划 T1-6 判据 4）。
    /// 读完的书不再提示"继续阅读"，那不是继续而是重读。
    /// </summary>
    public bool IsContinueReading => ProgressPercent > 0 && ProgressPercent < 1;

    /// <summary>百分比文案，给角标用。</summary>
    public string ProgressText => ProgressPercent <= 0
        ? string.Empty
        : Math.Round(ProgressPercent * 100) + "%";

    /// <summary>由书库页在加载时一次性灌进来，卡片自己不查库（避免每张卡一次查询）。</summary>
    public void SetProgress(double percent)
    {
        ProgressPercent = Math.Clamp(percent, 0, 1);
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(IsContinueReading));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(ReadingStateText));
        OnPropertyChanged(nameof(ReadingStateFullText));
    }
    public async Task LoadCoverAsync(CancellationToken ct = default)
    {
        if (_cover is not null || string.IsNullOrEmpty(ThumbnailPath))
        {
            return;
        }

        await _coverGate.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            if (_cover is not null || !File.Exists(ThumbnailPath))
            {
                return;
            }

            var path = ThumbnailPath;
            Cover = await Task.Run(() => Decode(path), ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "封面缩略图读不出来，该卡片显示占位图：{Path}", ThumbnailPath);
        }
        finally
        {
            _coverGate.Release();
        }
    }

    /// <summary>
    /// 封面文件被换过之后重新解一次码（执行计划 T3-3 判据 4：书库网格的缩略图要跟着变）。
    /// </summary>
    /// <remarks>
    /// 关键是**先把旧的 ImageSource 丢掉**：LoadCoverAsync 见到 _cover 已经有值就直接返回，
    /// 不清空的话网格永远显示老图。Decode 里用的是 IgnoreImageCache，重新解码拿到的就是新文件。
    /// </remarks>
    public async Task ReloadCoverAsync(CancellationToken ct = default)
    {
        Cover = null;
        await LoadCoverAsync(ct).ConfigureAwait(true);
    }

    private static ImageSource Decode(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;

        // 封面被替换后同名文件不能走 WPF 的图片缓存，否则还是旧图
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.DecodePixelWidth = DecodePixelWidth;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
    private IReadOnlyList<MetadataRow> BuildMetadata(Book book)
    {
        var rows = new List<MetadataRow>();

        Add(rows, "译者", book.Translators);
        Add(rows, "出版社", book.Publisher);
        Add(rows, "出版日期", book.PublishedDate?.ToString("yyyy-MM-dd"));
        Add(rows, "语言", book.Language);
        Add(rows, "ISBN", book.Isbn);
        Add(rows, "系列", SeriesText(book));
        Add(rows, "字数", book.WordCount is null ? null : book.WordCount.Value.ToString("N0") + " 字");
        Add(rows, "章节", book.ChapterCount is null ? null : book.ChapterCount.Value + " 章");
        Add(rows, "阅读状态", ReadingStatusText(book.Status));

        if (book.Damaged)
        {
            rows.Add(new MetadataRow("校验", "未通过严格校验（宽松读入）"));
        }

        var effective = EffectiveFormat;

        foreach (var format in book.Formats)
        {
            /* 用户 2026-10-20：文件已经删掉了（记录标了缺失，或者磁盘上真的没有）时，
               大小位置不再显示大小，改显示「（已删除）」，行尾给一个「移除该格式」按钮。
               这里会碰一次 File.Exists：扫描还没跑过时 Missing 标记可能还是旧的，
               光看标记会把"刚被删掉"的文件当正常。详情与列表共用这张表，
               每本书的格式数很少（1~3 个），这点 IO 可以接受。 */
            var deleted = format.Missing || !File.Exists(format.FilePath);
            var value = deleted ? DeletedFormatText : FormatSize(format.FileSize);

            /* 用户 2026-10-21：MOBI / AZW3 那一行的大小后面带上这句括号说明
               （原话：「该格式不支持阅读，已自动转换为EPUB格式」）。 */
            if (!deleted && ReaderFormatSupport.RequiresEpubConversion(format.Format))
            {
                value += ConvertedFormatNote;
            }

            /* 「切换此格式」只给"除当前格式之外、真能读"的格式：
               读不了的（FB2 / 未知）不给按钮，点了也打不开；
               当前格式自己也不给（它本来就是点阅读会打开的那个）。 */
            var switchable = !deleted
                && effective is not null
                && format.Format != effective.Format
                && ReaderFormatSupport.CanRead(format.Format);

            rows.Add(new MetadataRow(
                format.Format.ToStorageString().ToUpperInvariant(),
                value,
                deleted,
                format.Format,
                switchable,
                IsFormatRow: true));
        }

        /* 用户 2026-10-21：MOBI / AZW3 是转成 EPUB 读的。
           已经打开过（转换缓存里有产物）才补这一行 EPUB；从没读过时它并不存在，就不显示。
           **这一行排在真实格式之后**（用户 2026-10-21：MOBI 要显示在 EPUB 前面）。
           它不是 book_formats 里的记录（FormatKind 留空）：不能「移除该格式」，
           也不该给「切换此格式」——它本来就是当前阅读格式。 */
        if (effective is not null
            && ReaderFormatSupport.RequiresEpubConversion(effective.Format)
            && _convertedEpubBytes is { } convertedBytes
            && book.Formats.All(f => f.Format != BookFormatKind.Epub))
        {
            rows.Add(new MetadataRow(
                "EPUB", FormatSize(convertedBytes), IsFormatRow: true));
        }

        return rows;
    }

    private static string? SeriesText(Book book)
    {
        if (string.IsNullOrWhiteSpace(book.Series))
        {
            return null;
        }

        return book.SeriesIndex is null ? book.Series : book.Series + " #" + book.SeriesIndex;
    }

    private static void Add(List<MetadataRow> rows, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            rows.Add(new MetadataRow(label, value));
        }
    }

    /// <summary>
    /// 书库页灌进来的两件事（用户 2026-10-21）：
    /// 用户选的当前阅读格式，以及 MOBI/AZW3 的转换产物大小（null = 还没读过、还没转过）。
    /// 详情里的 EPUB 行、格式行尾的「切换此格式」、底部那行小字都依赖它，所以要整表重建一次。
    /// </summary>
    public void SetReadingFormat(BookFormatKind? preferred, long? convertedEpubBytes)
    {
        if (_preferredFormat == preferred && _convertedEpubBytes == convertedEpubBytes)
        {
            return;
        }

        _preferredFormat = preferred;
        _convertedEpubBytes = convertedEpubBytes;

        Metadata = BuildMetadata(Book);

        OnPropertyChanged(nameof(Metadata));
        OnPropertyChanged(nameof(FormatBadge));
        OnPropertyChanged(nameof(ReadingFormatText));
        OnPropertyChanged(nameof(EffectiveFormat));
    }

    /// <summary>
    /// 手动改了阅读状态之后刷新元数据那几行（MetadataRow 是不可变记录，整表重建最省事）。
    /// 只改内存里的对象 + 通知界面；落库由调用方（LibraryViewModel.MarkStatusAsync）负责。
    /// </summary>
    public void ApplyStatusChange(ReadingStatus status)
    {
        Book.Status = status;
        Metadata = BuildMetadata(Book);
        OnPropertyChanged(nameof(Metadata));
        OnPropertyChanged(nameof(ReadingState));
        OnPropertyChanged(nameof(ReadingStateText));
        OnPropertyChanged(nameof(ReadingStateFullText));
    }

    /// <summary>
    /// 把库里重新读回来的那份数据刷到这张卡片上（详情浮层返回、阅读窗口关闭后调）。
    /// 刷新的是"可能变过的"那几项：阅读状态、阅读进度、格式角标、未校验标记、标题作者。
    /// 封面缩略图路径与封面图**不动**  读了一会儿书不会改变这两样。
    /// </summary>
    public void ApplyRefresh(Book fresh, double percent)
    {
        Book.Title = fresh.Title;
        Book.Authors = fresh.Authors;
        Book.Status = fresh.Status;
        Book.Damaged = fresh.Damaged;

        Book.Formats.Clear();
        Book.Formats.AddRange(fresh.Formats);

        SetProgress(percent);

        // 格式集合可能刚变过（例如移除了一个缺失格式）：当前阅读格式与详情行要跟着重算
        _convertedEpubBytes = null;

        Metadata = BuildMetadata(Book);

        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Authors));
        OnPropertyChanged(nameof(FormatBadge));
        OnPropertyChanged(nameof(EffectiveFormat));
        OnPropertyChanged(nameof(ReadingFormatText));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(AddedAtText));
        OnPropertyChanged(nameof(IsDamaged));
        OnPropertyChanged(nameof(ReadingState));
        OnPropertyChanged(nameof(Metadata));
        OnPropertyChanged(nameof(HasNoFormats));
        OnPropertyChanged(nameof(NoFormatsDetail));
    }

    private static string ReadingStatusText(ReadingStatus status) => status switch
    {
        ReadingStatus.Reading => "在读",
        ReadingStatus.Finished => "已读完",
        ReadingStatus.Abandoned => "弃读",
        _ => "未读",
    };

    /// <summary>列表视图用的体积文案：这本书各格式文件的体积之和。</summary>
    public string SizeText
    {
        get
        {
            var total = Book.Formats.Sum(f => f.FileSize);
            return total <= 0 ? "未知" : FormatSize(total);
        }
    }

    /// <summary>列表视图用的「加入时间」，只到天。</summary>
    public string AddedAtText => Book.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd");

    /// <summary>文件大小给人看的写法。</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 1024)
        {
            return bytes + " B";
        }

        if (bytes < 1024 * 1024)
        {
            return (bytes / 1024.0).ToString("0.#") + " KB";
        }

        if (bytes < 1024L * 1024 * 1024)
        {
            return (bytes / (1024.0 * 1024)).ToString("0.#") + " MB";
        }

        return (bytes / (1024.0 * 1024 * 1024)).ToString("0.##") + " GB";
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}