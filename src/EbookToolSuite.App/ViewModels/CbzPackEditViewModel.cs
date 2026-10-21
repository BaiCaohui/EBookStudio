using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using EbookToolSuite.Core.Common;
using EbookToolSuite.Core.Models;

namespace EbookToolSuite.App.ViewModels;

/// <summary>
/// 「CBZ 信息编辑窗」的一行：一张源图片 + 它在包里的条目名。
/// </summary>
/// <remarks>
/// <para>
/// 【为什么把条目名拆成「有序前缀 + 主干名 + 扩展名」三段】
/// 用户要改的是主干名，扩展名必须原样保留（改了扩展名，阅读器可能不认这张图）；
/// 而调序时自动加的 <c>0001_</c> 前缀是程序产物，不该让用户在输入框里跟着改。
/// 所以三者分开显示、只让主干名可编辑，提交时再拼回完整条目名。
/// </para>
/// <para>
/// 【为什么条目名同时是顺序】CBZ 没有页序字段，阅读器一律按包内文件名自然序排页，
/// 所以「用户排的顺序」只能由文件名承载（见 <see cref="CbzEntryNames"/>）。
/// </para>
/// </remarks>
public sealed class CbzPackEntryViewModel : INotifyPropertyChanged
{
    private string _entryName;
    private int _position;
    private bool _isEditing;
    private string _editStem = string.Empty;
    private string? _editError;

    /// <param name="sourcePath">磁盘上的源图片绝对路径。只读：整个窗口一个字节都不会改它。</param>
    /// <param name="entryName">初始包内条目名（= 源文件名）。</param>
    public CbzPackEntryViewModel(string sourcePath, string entryName)
    {
        SourcePath = sourcePath;
        _entryName = entryName;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>源图片绝对路径。打包时只读这个文件，绝不改名/移动/覆盖。</summary>
    public string SourcePath { get; }

    /// <summary>
    /// 包内条目名（含扩展名）。<b>它同时承载阅读顺序</b>，所以列表顺序必须与它的自然序一致。
    /// </summary>
    public string EntryName
    {
        get => _entryName;
        internal set
        {
            if (string.Equals(_entryName, value, StringComparison.Ordinal))
            {
                return;
            }

            _entryName = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(OrderPrefix));
            OnPropertyChanged(nameof(Stem));
            OnPropertyChanged(nameof(Extension));
        }
    }

    /// <summary>
    /// 程序加的有序前缀（例如 <c>0001_</c>）；没调过序时是空串。
    /// 显示成灰色小字，因为它不是用户起的名字。
    /// </summary>
    public string OrderPrefix
    {
        get
        {
            var stem = Path.GetFileNameWithoutExtension(_entryName);
            var bare = CbzEntryNames.StripOrderPrefix(stem);
            return stem.Length == bare.Length ? string.Empty : stem[..^bare.Length];
        }
    }

    /// <summary>主干名（去掉有序前缀与扩展名）：改名时只有这一截可编辑。</summary>
    public string Stem => CbzEntryNames.StripOrderPrefix(Path.GetFileNameWithoutExtension(_entryName));

    /// <summary>扩展名（含点，例如 <c>.jpg</c>）。强制保留，界面上只读。</summary>
    public string Extension => Path.GetExtension(_entryName);

    /// <summary>在列表里的序号（1 起），调序/重排后由视图模型整表刷新。</summary>
    public int Position
    {
        get => _position;
        internal set
        {
            if (_position == value)
            {
                return;
            }

            _position = value;
            OnPropertyChanged();
        }
    }

    /// <summary>是否正在就地改名（模板据此在「显示」与「编辑框」之间切换）。</summary>
    public bool IsEditing
    {
        get => _isEditing;
        private set
        {
            if (_isEditing == value)
            {
                return;
            }

            _isEditing = value;
            OnPropertyChanged();
        }
    }

    /// <summary>正在编辑的主干名（只回写到 <see cref="EditStem"/>，提交成功才落到 <see cref="EntryName"/>）。</summary>
    public string EditStem
    {
        get => _editStem;
        set
        {
            if (string.Equals(_editStem, value, StringComparison.Ordinal))
            {
                return;
            }

            _editStem = value;
            OnPropertyChanged();
        }
    }

    /// <summary>改名校验失败的中文原因；非空时行内显示一段红字，且保持编辑态。</summary>
    public string? EditError
    {
        get => _editError;
        set
        {
            if (string.Equals(_editError, value, StringComparison.Ordinal))
            {
                return;
            }

            _editError = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasEditError));
        }
    }

    public bool HasEditError => !string.IsNullOrEmpty(_editError);

    /// <summary>进入就地编辑：编辑框里放主干名（不含程序前缀、不含扩展名）。</summary>
    internal void BeginEdit()
    {
        EditStem = Stem;
        EditError = null;
        IsEditing = true;
    }

    /// <summary>退出就地编辑（提交成功或取消时都会走到这里）。</summary>
    internal void EndEdit()
    {
        IsEditing = false;
        EditError = null;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>
/// 「CBZ 信息编辑窗」的视图模型：图片顺序（改名 / 调序 / 重编号）+ 书籍信息表单。
/// </summary>
/// <remarks>
/// <para>
/// 【顺序契约，全窗口最关键的一条】CBZ 没有标准页序字段，阅读器打开时按**包内文件名自然序**
/// 排页。因此这里维持一条不变量：
/// <b>列表显示顺序 ≡ 包内条目名的自然序</b>。
/// 三个地方必须维护它：初始载入（按文件名自然序）、改名提交（立刻按新名重排）、
/// 调序之后（用 <see cref="CbzEntryNames.ApplyOrder"/> 把整表重编号成
/// <c>0001_原名.jpg</c>，重编号后的文件名自然序就是用户排的顺序）。
/// 少了任何一处，界面上的顺序都会和阅读器里打开的顺序不一致。
/// </para>
/// <para>
/// 【源文件不动】所有改名都只改「包内条目名」（<see cref="CbzPackEntryViewModel.EntryName"/>），
/// 源文件夹里的真实图片一个字节都不写。真正落盘是调用方（MainWindow）按
/// <see cref="CbzPackEntry"/> 列表写 zip 的时候。
/// </para>
/// <para>
/// 分工：本类只管顺序与表单数据，窗口只管控件、弹窗与关闭。
/// </para>
/// </remarks>
public sealed class CbzPackEditViewModel : INotifyPropertyChanged
{
    /// <summary>出版年份的合理范围（ComicInfo 的 Year 没有明确规定，拦住明显手滑）。</summary>
    private const int MinYear = 1000;

    private const int MaxYear = 2999;

    private const string DefaultOrderHint =
        "CBZ 没有页序字段：阅读器按包内文件名自然序排页。调序后会自动把整表重编号成 0001_原文件名.jpg。";

    private string _statusText;
    private string _orderHintText = DefaultOrderHint;
    private CbzPackEntryViewModel? _selectedRow;
    private string _dateError = string.Empty;
    private int? _year;
    private int? _month;
    private int? _day;

    // ---------- 基本信息 ----------
    private string _title = string.Empty;
    private string _series = string.Empty;
    private string _number = string.Empty;
    private string _count = string.Empty;
    private string _volume = string.Empty;
    private string _alternateSeries = string.Empty;
    private string _alternateNumber = string.Empty;
    private string _alternateCount = string.Empty;

    // ---------- 创作者 ----------
    private string _writer = string.Empty;
    private string _penciller = string.Empty;
    private string _inker = string.Empty;
    private string _colorist = string.Empty;
    private string _letterer = string.Empty;
    private string _coverArtist = string.Empty;
    private string _editor = string.Empty;

    // ---------- 出版 ----------
    private string _publisher = string.Empty;
    private string _imprint = string.Empty;
    private string _yearText = string.Empty;
    private string _monthText = string.Empty;
    private string _dayText = string.Empty;

    // ---------- 分类与内容 ----------
    private string _genre = string.Empty;
    private string _tags = string.Empty;
    private string _characters = string.Empty;
    private string _teams = string.Empty;
    private string _locations = string.Empty;
    private string _storyArc = string.Empty;
    private string _seriesGroup = string.Empty;
    private string _ageRating = string.Empty;

    // ---------- 说明 / 来源 ----------
    private string _summary = string.Empty;
    private string _notes = string.Empty;
    private string _web = string.Empty;
    private string _languageIso = string.Empty;
    private string _format = string.Empty;
    private string _scanInformation = string.Empty;
    private string _isbn = string.Empty;

    // ---------- 其它 ----------
    private int _blackAndWhiteIndex;
    private int _rightToLeftIndex;

    /// <param name="folder">图片文件夹（只用于显示；源文件不会被改）。</param>
    /// <param name="images">扫描出来的图片绝对路径列表（顺序不限，这里按文件名自然序重排）。</param>
    /// <param name="displayPath">
    /// 抬头显示用的路径。编辑书库里已有的 CBZ 时传原 CBZ 路径（文件夹只是临时解压目录），
    /// 新建打包时传 null（就显示文件夹）。
    /// </param>
    /// <param name="initialMetadata">
    /// 预填的书籍信息。编辑现有 CBZ 时传入包内 ComicInfo.xml 解析结果；新建时为 null。
    /// </param>
    public CbzPackEditViewModel(
        string folder,
        IReadOnlyList<string> images,
        string? displayPath = null,
        ComicMetadata? initialMetadata = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(images);

        Folder = string.IsNullOrWhiteSpace(displayPath) ? folder : displayPath;
        SourceLine = (string.IsNullOrWhiteSpace(displayPath) ? "文件夹：" : "文件：") + Folder;

        // 初始顺序 = 包内文件名自然序。MainWindow 传进来的已经是 ICbzPacker.ScanImages 的结果，
        // 这里再排一次是守住契约：顺序由本窗口负责，不依赖调用方的顺序。
        var rows = images
            .Select(path => (SourcePath: path, Name: Path.GetFileName(path)))
            .OrderBy(item => item.Name, NaturalStringComparer.Instance)
            .Select(item => new CbzPackEntryViewModel(item.SourcePath, item.Name))
            .ToList();

        Items = new ObservableCollection<CbzPackEntryViewModel>(rows);
        RefreshPositions();

        _selectedRow = Items.Count > 0 ? Items[0] : null;
        _statusText = Items.Count > 0
            ? "共 " + Items.Count + " 张图片。F2 改名，↑/↓ 或拖动调序，双击图片预览。"
            : "这个文件夹里没有图片。";

        if (initialMetadata is not null)
        {
            LoadMetadata(initialMetadata);
            StatusText = "正在编辑现有 CBZ：共 " + Items.Count
                + " 张图片。保存会重新打包并覆盖原文件（先自动备份）。";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>被编辑的图片文件夹；编辑现有 CBZ 时这里是原 CBZ 的路径（只显示用）。</summary>
    public string Folder { get; }

    /// <summary>抬头小字：「文件夹：…」（新建）或「文件：…」（编辑现有 CBZ）。</summary>
    public string SourceLine { get; }

    /// <summary>
    /// 把现有 CBZ 的书籍信息填进表单（编辑书库里的书时用）。
    /// 空字段保持空；年/月/日与两个三态由属性 setter 走正常校验路径。
    /// </summary>
    public void LoadMetadata(ComicMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        Title = metadata.Title ?? string.Empty;
        Series = metadata.Series ?? string.Empty;
        Number = metadata.Number ?? string.Empty;
        Count = metadata.Count ?? string.Empty;
        Volume = metadata.Volume ?? string.Empty;
        AlternateSeries = metadata.AlternateSeries ?? string.Empty;
        AlternateNumber = metadata.AlternateNumber ?? string.Empty;
        AlternateCount = metadata.AlternateCount ?? string.Empty;

        Writer = metadata.Writer ?? string.Empty;
        Penciller = metadata.Penciller ?? string.Empty;
        Inker = metadata.Inker ?? string.Empty;
        Colorist = metadata.Colorist ?? string.Empty;
        Letterer = metadata.Letterer ?? string.Empty;
        CoverArtist = metadata.CoverArtist ?? string.Empty;
        Editor = metadata.Editor ?? string.Empty;

        Publisher = metadata.Publisher ?? string.Empty;
        Imprint = metadata.Imprint ?? string.Empty;
        YearText = metadata.Year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        MonthText = metadata.Month?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        DayText = metadata.Day?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

        Genre = metadata.Genre ?? string.Empty;
        Tags = metadata.Tags ?? string.Empty;
        Characters = metadata.Characters ?? string.Empty;
        Teams = metadata.Teams ?? string.Empty;
        Locations = metadata.Locations ?? string.Empty;
        StoryArc = metadata.StoryArc ?? string.Empty;
        SeriesGroup = metadata.SeriesGroup ?? string.Empty;
        AgeRating = metadata.AgeRating ?? string.Empty;

        Summary = metadata.Summary ?? string.Empty;
        Notes = metadata.Notes ?? string.Empty;
        Web = metadata.Web ?? string.Empty;
        LanguageIso = metadata.LanguageISO ?? string.Empty;
        Format = metadata.Format ?? string.Empty;
        ScanInformation = metadata.ScanInformation ?? string.Empty;
        Isbn = metadata.Isbn ?? string.Empty;

        BlackAndWhiteIndex = metadata.BlackAndWhite switch
        {
            null => 0,
            false => 1,
            true => 2,
        };

        RightToLeftIndex = metadata.RightToLeft switch
        {
            null => 0,
            false => 1,
            true => 2,
        };
    }

    /// <summary>
    /// 图片列表，<b>顺序就是阅读顺序</b>，且恒等于条目名的自然序（见类备注的不变量）。
    /// </summary>
    public ObservableCollection<CbzPackEntryViewModel> Items { get; }

    /// <summary>给界面看的张数。</summary>
    public string CountText => "共 " + Items.Count + " 张";

    /// <summary>页数是打包时按实际条目数自动填的，不让用户手改（改了也会被打包器覆盖）。</summary>
    public string PageCountText => "打包时自动填 " + Items.Count + " 页（= 图片张数）";

    /// <summary>状态条文案。</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    /// <summary>列表下方的常驻说明；调过序之后换成「已按顺序重编号，阅读器按文件名排序」。</summary>
    public string OrderHintText
    {
        get => _orderHintText;
        private set => SetField(ref _orderHintText, value);
    }

    /// <summary>当前选中的那一行（上移/下移/重命名都作用于它）。</summary>
    public CbzPackEntryViewModel? SelectedRow
    {
        get => _selectedRow;
        set => SetField(ref _selectedRow, value);
    }

    /// <summary>出版日期填错时的中文原因（空串 = 没问题）。</summary>
    public string DateError
    {
        get => _dateError;
        private set
        {
            if (!SetField(ref _dateError, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasDateError));
        }
    }

    public bool HasDateError => _dateError.Length > 0;

    // ============================================================
    // 图片顺序：改名 / 上移下移 / 拖动 / 重编号
    // ============================================================

    /// <summary>
    /// 让某一行进入就地改名。若另一行正在改名，先尝试提交它（不合法就不让开始新的）。
    /// </summary>
    /// <returns>false = 没能进入编辑态（另一行的新名字不合法，界面应停在那一行）。</returns>
    public bool BeginRename(CbzPackEntryViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (!Items.Contains(row))
        {
            return false;
        }

        var editing = FindEditingRow();

        if (ReferenceEquals(editing, row))
        {
            // 已经在这一行的编辑态里：重复点按钮不该把用户刚打的名字冲掉（只把焦点留在这儿）。
            SelectedRow = row;
            return true;
        }

        if (editing is not null && !CommitRename(editing))
        {
            return false;
        }

        SelectedRow = row;
        row.BeginEdit();
        StatusText = "改主干名，扩展名「" + row.Extension + "」固定不变；回车提交，Esc 取消。";
        return true;
    }

    /// <summary>
    /// 提交就地改名：校验 → 写条目名 → 立刻按新名自然序重排。
    /// </summary>
    /// <returns>false = 名字不合法（行内已显示原因，且保持编辑态）。</returns>
    public bool CommitRename(CbzPackEntryViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (!row.IsEditing)
        {
            return true;
        }

        var stem = row.EditStem.Trim();

        // 用户常顺手把扩展名一起敲进来：这里剥掉，保证包里不会出现 a.jpg.jpg。
        var extension = row.Extension;
        if (extension.Length > 0 && stem.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            stem = stem[..^extension.Length].TrimEnd();
        }

        if (stem.Length == 0)
        {
            return FailRename(row, "文件名不能为空。");
        }

        var candidate = row.OrderPrefix + stem + extension;

        // 空名 / 非法字符 / 超长：直接复用 Core 的规则，保证和打包器口径一致。
        var reason = CbzEntryNames.InvalidEntryNameReason(candidate);
        if (reason is not null)
        {
            return FailRename(row, reason);
        }

        // 重名（忽略大小写）：包内条目重名会在解压时互相覆盖，必须先拦下来。
        foreach (var other in Items)
        {
            if (!ReferenceEquals(other, row)
                && string.Equals(other.EntryName, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return FailRename(row, "已经有同名条目「" + other.EntryName + "」了，请换一个名字。");
            }
        }

        var changed = !string.Equals(row.EntryName, candidate, StringComparison.Ordinal);

        row.EntryName = candidate;
        row.EndEdit();

        if (changed)
        {
            // 名字变了，它在新文件名自然序里的位置也可能变了：立刻重排，界面顺序才等于阅读顺序。
            ResortByEntryName();
        }

        SelectedRow = row;
        StatusText = changed
            ? "已改名为「" + row.EntryName + "」，列表已按新文件名重新排序。"
            : "名字没有改动。";
        return true;
    }

    /// <summary>放弃本次改名：条目名一个字都不动。</summary>
    public void CancelRename(CbzPackEntryViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (!row.IsEditing)
        {
            return;
        }

        row.EndEdit();
        StatusText = "已取消改名，条目名没有改动。";
    }

    /// <summary>把选中的图片上移/下移一位；随后整表重编号并刷新。</summary>
    /// <param name="delta">-1 = 上移，+1 = 下移。</param>
    public bool MoveSelected(int delta)
    {
        if (Items.Count == 0)
        {
            StatusText = "没有图片可以调序。";
            return false;
        }

        var row = SelectedRow ?? Items[0];
        var index = Items.IndexOf(row);
        var target = index + delta;

        if (index < 0)
        {
            return false;
        }

        if (target < 0)
        {
            StatusText = "已经是第一张了，没法再上移。";
            return false;
        }

        if (target >= Items.Count)
        {
            StatusText = "已经是最后一张了，没法再下移。";
            return false;
        }

        Items.Move(index, target);
        RenumberAfterReorder(row, delta < 0 ? "上移" : "下移");
        return true;
    }

    /// <summary>
    /// 拖动调序：把 <paramref name="dragged"/> 放到 <paramref name="target"/> 的位置上。
    /// <paramref name="target"/> 为 null（拖到列表空白处）= 放到最后。
    /// </summary>
    public void MoveRowTo(CbzPackEntryViewModel dragged, CbzPackEntryViewModel? target)
    {
        ArgumentNullException.ThrowIfNull(dragged);

        var from = Items.IndexOf(dragged);
        if (from < 0)
        {
            return;
        }

        var to = target is null || ReferenceEquals(target, dragged) ? Items.Count - 1 : Items.IndexOf(target);
        if (to < 0 || to == from)
        {
            return;
        }

        Items.Move(from, to);
        RenumberAfterReorder(dragged, "拖到第 " + (to + 1) + " 张");
    }

    /// <summary>
    /// 提交所有正在进行的改名（点「保存」时调用，避免用户改了名没回车就被吞掉）。
    /// </summary>
    public bool TryFinishPendingEdit()
    {
        var editing = FindEditingRow();
        return editing is null || CommitRename(editing);
    }

    // ============================================================
    // 保存：校验 + 产出契约需要的两样东西
    // ============================================================

    /// <summary>
    /// 校验并产出「有序条目 + 元数据」。窗口拿到之后拼成 <c>CbzPackEditResult</c> 再关窗。
    /// </summary>
    /// <remarks>
    /// 校验失败的文案写在 <see cref="StatusText"/> 里，窗口直接拿去弹窗。
    /// </remarks>
    public bool TryBuildResult(
        out IReadOnlyList<CbzPackEntry> entries,
        out ComicMetadata metadata)
    {
        entries = Array.Empty<CbzPackEntry>();
        metadata = new ComicMetadata();

        if (!TryFinishPendingEdit())
        {
            return false;
        }

        if (Items.Count == 0)
        {
            StatusText = "这个文件夹里没有图片，没法打包。";
            return false;
        }

        if (HasDateError)
        {
            StatusText = "出版日期填得不对：" + DateError;
            return false;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < Items.Count; i++)
        {
            var row = Items[i];
            var reason = CbzEntryNames.InvalidEntryNameReason(row.EntryName);

            if (reason is not null)
            {
                StatusText = "第 " + (i + 1) + " 张的条目名「" + row.EntryName + "」不合法：" + reason;
                return false;
            }

            // 最后一道闸：条目名必须唯一，否则 zip 里会出现两个同名条目。
            if (!seen.Add(row.EntryName))
            {
                StatusText = "第 " + (i + 1) + " 张的条目名「" + row.EntryName + "」和前面的重复了。";
                return false;
            }
        }

        entries = Items.Select(row => new CbzPackEntry(row.SourcePath, row.EntryName)).ToList();
        metadata = BuildMetadata();
        StatusText = "共 " + entries.Count + " 张图片，条目名合法且唯一，可以打包。";
        return true;
    }

    private ComicMetadata BuildMetadata()
    {
        var isBlackAndWhite = ThreeStateValue(BlackAndWhiteIndex);
        var isRightToLeft = ThreeStateValue(RightToLeftIndex);

        return new ComicMetadata
        {
            // 基本信息
            Title = TrimToNull(Title),
            Series = TrimToNull(Series),
            Number = TrimToNull(Number),
            Count = TrimToNull(Count),
            Volume = TrimToNull(Volume),
            AlternateSeries = TrimToNull(AlternateSeries),
            AlternateNumber = TrimToNull(AlternateNumber),
            AlternateCount = TrimToNull(AlternateCount),

            // 创作者
            Writer = TrimToNull(Writer),
            Penciller = TrimToNull(Penciller),
            Inker = TrimToNull(Inker),
            Colorist = TrimToNull(Colorist),
            Letterer = TrimToNull(Letterer),
            CoverArtist = TrimToNull(CoverArtist),
            Editor = TrimToNull(Editor),

            // 出版
            Publisher = TrimToNull(Publisher),
            Imprint = TrimToNull(Imprint),
            Year = _year,
            Month = _month,
            Day = _day,

            // 分类与内容
            Genre = TrimToNull(Genre),
            Tags = TrimToNull(Tags),
            Characters = TrimToNull(Characters),
            Teams = TrimToNull(Teams),
            Locations = TrimToNull(Locations),
            StoryArc = TrimToNull(StoryArc),
            SeriesGroup = TrimToNull(SeriesGroup),
            AgeRating = TrimToNull(AgeRating),

            // 说明 / 来源
            Summary = TrimToNull(Summary),
            Notes = TrimToNull(Notes),
            Web = TrimToNull(Web),
            LanguageISO = TrimToNull(LanguageIso),
            Format = TrimToNull(Format),
            ScanInformation = TrimToNull(ScanInformation),
            Isbn = TrimToNull(Isbn),

            // 其它
            BlackAndWhite = isBlackAndWhite,
            RightToLeft = isRightToLeft,

            // 页数由打包器按实际写入的图片数再确认一次；这里先给界面上的张数。
            PageCount = Items.Count,
        };
    }

    /// <summary>ComboBox 三态（0 不指定 / 1 否 / 2 是）转 <c>bool?</c>。</summary>
    private static bool? ThreeStateValue(int index) => index switch
    {
        1 => false,
        2 => true,
        _ => null,
    };

    private static string? TrimToNull(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    // ============================================================
    // 内部：重排 / 重编号 / 校验辅助
    // ============================================================

    /// <summary>
    /// 按条目名自然序重排列表（改名后必须调用）。
    /// 用相邻交换而不是重建集合：重建会让 ListBox 丢失选中项、并闪一下。
    /// </summary>
    private void ResortByEntryName()
    {
        for (var i = 1; i < Items.Count; i++)
        {
            var j = i;
            while (j > 0
                && NaturalStringComparer.Instance.Compare(Items[j - 1].EntryName, Items[j].EntryName) > 0)
            {
                Items.Move(j, j - 1);
                j--;
            }
        }

        RefreshPositions();
    }

    /// <summary>
    /// 调序后的收尾：整表重编号 + 刷新序号 + 更新提示。
    /// </summary>
    /// <remarks>
    /// 【为什么整表重编号而不是只改这一张】阅读器只看文件名自然序，只改一张的话
    /// 用户排的顺序表达不出来。<see cref="CbzEntryNames.ApplyOrder"/> 会先剥掉旧前缀
    /// （<c>^\d{4,}_</c>）再加新前缀，所以反复调序不会叠成 <c>0001_0002_xxx.jpg</c>。
    /// </remarks>
    private void RenumberAfterReorder(CbzPackEntryViewModel selected, string action)
    {
        var names = CbzEntryNames.ApplyOrder(Items.Select(row => row.EntryName).ToList());

        for (var i = 0; i < Items.Count; i++)
        {
            Items[i].EntryName = names[i];
        }

        RefreshPositions();
        SelectedRow = selected;
        OrderHintText = "已按顺序重编号，阅读器按文件名排序。";
        StatusText = "已" + action + "：包内条目名已按新顺序重编号（0001_原文件名.jpg），阅读器打开就是这个顺序。";
    }

    private void RefreshPositions()
    {
        for (var i = 0; i < Items.Count; i++)
        {
            Items[i].Position = i + 1;
        }
    }

    private CbzPackEntryViewModel? FindEditingRow()
    {
        foreach (var row in Items)
        {
            if (row.IsEditing)
            {
                return row;
            }
        }

        return null;
    }

    private bool FailRename(CbzPackEntryViewModel row, string message)
    {
        row.EditError = message;
        StatusText = "改名没生效：" + message;
        return false;
    }

    // ============================================================
    // 出版日期：三个可空的数字输入框
    // ============================================================

    public string YearText
    {
        get => _yearText;
        set
        {
            if (!SetField(ref _yearText, value))
            {
                return;
            }

            RevalidateDate();
        }
    }

    public string MonthText
    {
        get => _monthText;
        set
        {
            if (!SetField(ref _monthText, value))
            {
                return;
            }

            RevalidateDate();
        }
    }

    public string DayText
    {
        get => _dayText;
        set
        {
            if (!SetField(ref _dayText, value))
            {
                return;
            }

            RevalidateDate();
        }
    }

    /// <summary>每次改动都重新解析三个数字：可空、必须是数字、范围合理。</summary>
    private void RevalidateDate()
    {
        var errors = new List<string>();

        _year = ParseDatePart(_yearText, "年", MinYear, MaxYear, errors);
        _month = ParseDatePart(_monthText, "月", 1, 12, errors);
        _day = ParseDatePart(_dayText, "日", 1, 31, errors);

        DateError = string.Join(" ", errors);
    }

    private static int? ParseDatePart(string text, string label, int min, int max, List<string> errors)
    {
        var trimmed = text.Trim();

        if (trimmed.Length == 0)
        {
            return null;
        }

        if (!int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            errors.Add(label + "必须是数字。");
            return null;
        }

        if (value < min || value > max)
        {
            errors.Add(label + "应在 " + min + "–" + max + " 之间。");
            return null;
        }

        return value;
    }

    // ============================================================
    // 表单字段：全部实时写进视图模型（保存时一次性取走）
    // ============================================================
    public string Title { get => _title; set => SetField(ref _title, value); }

    public string Series { get => _series; set => SetField(ref _series, value); }

    public string Number { get => _number; set => SetField(ref _number, value); }

    public string Count { get => _count; set => SetField(ref _count, value); }

    public string Volume { get => _volume; set => SetField(ref _volume, value); }

    public string AlternateSeries { get => _alternateSeries; set => SetField(ref _alternateSeries, value); }

    public string AlternateNumber { get => _alternateNumber; set => SetField(ref _alternateNumber, value); }

    public string AlternateCount { get => _alternateCount; set => SetField(ref _alternateCount, value); }

    public string Writer { get => _writer; set => SetField(ref _writer, value); }

    public string Penciller { get => _penciller; set => SetField(ref _penciller, value); }

    public string Inker { get => _inker; set => SetField(ref _inker, value); }

    public string Colorist { get => _colorist; set => SetField(ref _colorist, value); }

    public string Letterer { get => _letterer; set => SetField(ref _letterer, value); }

    public string CoverArtist { get => _coverArtist; set => SetField(ref _coverArtist, value); }

    public string Editor { get => _editor; set => SetField(ref _editor, value); }

    public string Publisher { get => _publisher; set => SetField(ref _publisher, value); }

    public string Imprint { get => _imprint; set => SetField(ref _imprint, value); }

    public string Genre { get => _genre; set => SetField(ref _genre, value); }

    public string Tags { get => _tags; set => SetField(ref _tags, value); }

    public string Characters { get => _characters; set => SetField(ref _characters, value); }

    public string Teams { get => _teams; set => SetField(ref _teams, value); }

    public string Locations { get => _locations; set => SetField(ref _locations, value); }

    public string StoryArc { get => _storyArc; set => SetField(ref _storyArc, value); }

    public string SeriesGroup { get => _seriesGroup; set => SetField(ref _seriesGroup, value); }

    public string AgeRating { get => _ageRating; set => SetField(ref _ageRating, value); }

    public string Summary { get => _summary; set => SetField(ref _summary, value); }

    public string Notes { get => _notes; set => SetField(ref _notes, value); }

    public string Web { get => _web; set => SetField(ref _web, value); }

    public string LanguageIso { get => _languageIso; set => SetField(ref _languageIso, value); }

    public string Format { get => _format; set => SetField(ref _format, value); }

    public string ScanInformation { get => _scanInformation; set => SetField(ref _scanInformation, value); }

    public string Isbn { get => _isbn; set => SetField(ref _isbn, value); }

    /// <summary>黑白：0 = 不指定（不写这一项），1 = 否，2 = 是。</summary>
    public int BlackAndWhiteIndex
    {
        get => _blackAndWhiteIndex;
        set => SetField(ref _blackAndWhiteIndex, value);
    }

    /// <summary>阅读方向：0 = 不指定，1 = 否（从左往右），2 = 是（从右往左）。</summary>
    public int RightToLeftIndex
    {
        get => _rightToLeftIndex;
        set => SetField(ref _rightToLeftIndex, value);
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
