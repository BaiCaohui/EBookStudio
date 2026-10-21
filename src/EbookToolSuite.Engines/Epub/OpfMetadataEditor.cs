using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Engines.Epub;

/// <summary>
/// OPF 元数据读写（执行计划 T3-2）。同时兼容 EPUB2 与 EPUB3，是全项目最容易写错的一块。
/// </summary>
/// <remarks>
/// <para><b>六条硬约束</b>（执行计划 T3-2 的"必须遵守的 6 条"，来自审计）：</para>
/// <list type="number">
/// <item>用 <see cref="LoadOptions.PreserveWhitespace"/>，否则整份 OPF 的缩进会被重排；</item>
/// <item>按 package@version 分支（3 开头 = EPUB3，其余按 EPUB2）；</item>
/// <item>写 <c>opf:role</c> / <c>opf:file-as</c> 前必须声明 <c>xmlns:opf</c>，否则不是良构 XML；</item>
/// <item>EPUB3 一个 <c>opf:*</c> 属性都不许写，作者/译者改走 <c>refines</c>；</item>
/// <item>EPUB3 的 <c>dcterms:modified</c> 恰好一个，且每次保存刷成当前 UTC；</item>
/// <item>写回用无 BOM 的 UTF-8，且不动原有换行。</item>
/// </list>
/// <para>
/// <b>一个反直觉的坑（实测得来，见 DECISIONS.md）</b>：LINQ to XML 不保存元素前缀，
/// 序列化时自己按"作用域内最后一条同命名空间声明"挑前缀。<c>xmlns:opf</c> 绑的就是 OPF 默认命名空间，
/// 所以只要它出现在默认 <c>xmlns</c> 之后（Calibre/Sigil 的书几乎都这样），
/// 整份 OPF 的元素会被写成 <c>&lt;opf:package&gt;</c> / <c>&lt;opf:meta&gt;</c>
/// 实测一本 1742 行的真实电子书会变 1725 行。因此本类在保存前会把 <c>xmlns:opf</c>
/// <b>统一挪到根元素的第一个属性位置</b>，让元素继续走默认命名空间、只有 <c>opf:*</c> 属性带前缀。
/// </para>
/// <para>
/// <b>自闭合写法的还原</b>：System.Xml 写空元素一律输出 <c>&lt;x /&gt;</c>（斜杠前带空格），
/// 而真实电子书里 <c>&lt;x/&gt;</c> 与 <c>&lt;x /&gt;</c> 两种写法混着用。写回时按原文件的
/// 多数派风格还原，否则一本 1742 行的 OPF 会因为 1722 处 <c>&lt;x/&gt;</c>
/// 把"只改一行"变成"改了上千行"。两种写法数量接近的文件（实测有一本是 66 : 40）
/// 只能保住多数派，少数派那些行会各差一个空格  语意等价。
/// </para>
/// <para>
/// <b>声明归位的一个副作用</b>：原文件若把 <c>&lt;opf:metadata&gt;</c> 这类带前缀的写法
/// 用在了 OPF 元素本身，归位后它们会改回默认命名空间（语义等价，实测最多几行）。
/// </para>
/// </remarks>
public sealed class OpfMetadataEditor
{
    /// <summary>OPF 命名空间。</summary>
    public static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";

    /// <summary>Dublin Core 命名空间。</summary>
    public static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";

    private const string MetaNameCover = "cover";
    private const string MetaNameSeries = "calibre:series";
    private const string MetaNameSeriesIndex = "calibre:series_index";
    private const string MetaNameRating = "calibre:rating";

    private readonly XDocument _doc;
    private readonly XElement _package;
    private readonly XElement _metadata;
    private readonly IClock _clock;
    private readonly string _sourcePath;

    /// <summary>原文件的 XML 声明，逐字保留（只认 utf-8；其它编码一律改写回 utf-8 声明）。</summary>
    private readonly string? _declaration;

    /// <summary>原文件的换行风格。见 Save 里的说明。</summary>
    private readonly string _newLine;

    /// <summary>原文件是否主要使用 &lt;x/&gt; 这种紧凑的自闭合写法。</summary>
    private readonly bool _compactSelfClosing;

    /// <summary>本次编辑是否真的写入过 opf:* 属性  决定保存时要不要补 xmlns:opf。</summary>
    private bool _wroteOpfAttribute;

    /// <summary>package@version 原文。缺失时按 2.0 处理（执行计划第 2 条）。</summary>
    public string Version { get; }

    public bool IsEpub3 { get; }

    public OpfMetadataEditor(string opfPath, IClock? clock = null)
    {
        if (string.IsNullOrWhiteSpace(opfPath))
        {
            throw new ArgumentException("OPF 路径不能为空", nameof(opfPath));
        }

        _sourcePath = opfPath;
        _clock = clock ?? UtcClock.Instance;

        try
        {
            _doc = XDocument.Load(opfPath, LoadOptions.PreserveWhitespace);
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            throw new AppException(
                ErrorCode.EpubInvalid,
                "不是有效的 EPUB 文件",
                "OPF 读不出或不是合法 XML：" + opfPath + " -> " + ex.Message,
                ex);
        }

        _package = _doc.Root
            ?? throw new AppException(ErrorCode.EpubInvalid, "不是有效的 EPUB 文件", "OPF 没有根元素：" + opfPath);

        _metadata = _package.Element(Opf + "metadata")
            ?? throw new AppException(ErrorCode.EpubInvalid, "不是有效的 EPUB 文件", "OPF 缺少 <metadata>：" + opfPath);

        var declared = ((string?)_package.Attribute("version"))?.Trim();
        Version = string.IsNullOrEmpty(declared) ? "2.0" : declared;
        IsEpub3 = Version.StartsWith("3", StringComparison.Ordinal);

        var raw = ReadRawText(opfPath);
        _declaration = ExtractDeclaration(raw);
        _newLine = DetectNewLine(raw);
        _compactSelfClosing = DetectCompactSelfClosing(raw);
    }

    // ================= 读 =================

    /// <summary>把当前 OPF 的元数据读成一个模型。</summary>
    public OpfMetadata Read()
    {
        var metadata = new OpfMetadata { Version = Version, IsEpub3 = IsEpub3 };

        foreach (var title in _metadata.Elements(Dc + "title"))
        {
            var type = TitleTypeOf(title);
            if (type is "subtitle" or "副标题")
            {
                metadata.Subtitle ??= title.Value.Trim();
            }
            else
            {
                metadata.Title ??= title.Value.Trim();
            }
        }

        foreach (var creator in _metadata.Elements(Dc + "creator"))
        {
            metadata.Authors.Add(ReadContributor(creator));
        }

        foreach (var contributor in _metadata.Elements(Dc + "contributor"))
        {
            metadata.Contributors.Add(ReadContributor(contributor));
        }

        metadata.Language = FirstDcValue("language");
        metadata.Publisher = FirstDcValue("publisher");
        metadata.Description = FirstDcValue("description");

        if (TryReadDate(FirstDcValue("date"), out var date))
        {
            metadata.PublicationDate = date;
        }

        metadata.Subjects.AddRange(_metadata.Elements(Dc + "subject")
            .Select(s => s.Value.Trim())
            .Where(s => s.Length > 0));

        metadata.Series = MetaNameValue(MetaNameSeries);
        if (double.TryParse(MetaNameValue(MetaNameSeriesIndex), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var seriesIndex))
        {
            metadata.SeriesIndex = seriesIndex;
        }

        if (int.TryParse(MetaNameValue(MetaNameRating), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var calibreRating))
        {
            metadata.Rating = calibreRating / 2;   // Calibre 0-10 -> 0-5 星
        }

        metadata.CoverItemId = MetaNameValue(MetaNameCover);
        metadata.Isbn = ReadIsbn();
        return metadata;
    }

    // ================= 写 =================

    /// <summary>替换主标题（EPUB2/EPUB3 都是 dc:title）。全空白视为删除。</summary>
    public void SetTitle(string? title)
    {
        var element = MainTitleElement();
        if (string.IsNullOrWhiteSpace(title))
        {
            if (element is not null)
            {
                RemoveRefinesFor(element);
                element.Remove();
            }

            return;
        }

        if (element is null)
        {
            element = new XElement(Dc + "title");
            _metadata.Add(element);
        }

        element.Value = title.Trim();
    }

    /// <summary>替换副标题。只有 EPUB3 能表达（refines 的 title-type=subtitle）；EPUB2 会忽略并删除已有的。</summary>
    public void SetSubtitle(string? subtitle)
    {
        var element = _metadata.Elements(Dc + "title").FirstOrDefault(t => IsSubtitle(t));

        if (string.IsNullOrWhiteSpace(subtitle) || !IsEpub3)
        {
            if (element is not null)
            {
                RemoveRefinesFor(element);
                element.Remove();
            }

            return;
        }

        if (element is null)
        {
            element = new XElement(Dc + "title");
            var id = "title-" + NewSuffix();
            element.SetAttributeValue("id", id);
            _metadata.Add(element);
            _metadata.Add(RefinesMeta(id, "title-type", "subtitle", scheme: null));
        }

        element.Value = subtitle.Trim();
    }

    /// <summary>替换作者（dc:creator），顺序即写入顺序。</summary>
    public void SetAuthors(IReadOnlyList<EpubContributor> authors) => ReplacePeople("creator", authors);

    /// <summary>替换其它贡献者（dc:contributor，典型是译者）。</summary>
    public void SetContributors(IReadOnlyList<EpubContributor> contributors)
        => ReplacePeople("contributor", contributors);

    public void SetLanguage(string? language) => SetDcSingle("language", language);

    public void SetPublisher(string? publisher) => SetDcSingle("publisher", publisher);

    public void SetDescription(string? description) => SetDcSingle("description", description);

    /// <summary>出版日期。EPUB2 写 dc:date + opf:event="publication"；EPUB3 只写 dc:date。</summary>
    public void SetPublicationDate(DateOnly? date)
    {
        var element = _metadata.Elements(Dc + "date")
            .FirstOrDefault(e => (string?)e.Attribute(Opf + "event") is null or "publication");

        if (date is null)
        {
            element?.Remove();
            return;
        }

        if (element is null)
        {
            element = new XElement(Dc + "date");
            _metadata.Add(element);
        }

        element.Value = date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        if (IsEpub3)
        {
            // 规则 4：EPUB3 不留 opf:* 属性
            element.Attribute(Opf + "event")?.Remove();
        }
        else
        {
            element.SetAttributeValue(Opf + "event", "publication");
            _wroteOpfAttribute = true;
        }
    }

    /// <summary>整体替换标签（dc:subject）。</summary>
    public void SetSubjects(IReadOnlyList<string> subjects)
    {
        _metadata.Elements(Dc + "subject").Remove();
        foreach (var subject in subjects.Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            _metadata.Add(new XElement(Dc + "subject", subject.Trim()));
        }
    }

    /// <summary>系列与系列序号。按计划表走 Calibre 的 meta name/content 约定（两个版本一致）。</summary>
    public void SetSeries(string? series, double? index)
    {
        SetMetaName(MetaNameSeries, string.IsNullOrWhiteSpace(series) ? null : series.Trim());
        SetMetaName(MetaNameSeriesIndex, index?.ToString("0.###", CultureInfo.InvariantCulture));
    }

    /// <summary>评分，入参是 0-5 星，落盘乘 2 成 Calibre 的 0-10。0 或负数视为清除。</summary>
    public void SetRating(int? stars)
    {
        if (stars is null or <= 0)
        {
            SetMetaName(MetaNameRating, null);
            return;
        }

        var clamped = Math.Clamp(stars.Value, 1, 5);
        SetMetaName(MetaNameRating, (clamped * 2).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>ISBN。EPUB2 走 opf:scheme="ISBN"；EPUB3 走 refines 的 identifier-type。</summary>
    public void SetIsbn(string? isbn)
    {
        var existing = FindIsbnElement();

        if (string.IsNullOrWhiteSpace(isbn))
        {
            if (existing is not null)
            {
                RemoveRefinesFor(existing);
                existing.Remove();
            }

            return;
        }

        if (existing is null)
        {
            existing = new XElement(Dc + "identifier");
            _metadata.Add(existing);
        }

        var value = isbn.Trim();
        existing.Value = value;

        if (IsEpub3)
        {
            var id = (string?)existing.Attribute("id");
            if (string.IsNullOrEmpty(id))
            {
                id = "isbn";
                existing.SetAttributeValue("id", id);
            }

            RemoveRefinesFor(existing);
            // onix:codelist5 的 15 = ISBN-13，02 = ISBN-10
            _metadata.Add(RefinesMeta(id, "identifier-type", value.Length == 13 ? "15" : "02", "onix:codelist5"));
        }
        else
        {
            existing.SetAttributeValue(Opf + "scheme", "ISBN");
            _wroteOpfAttribute = true;
        }
    }

    /// <summary>
    /// 封面声明：&lt;meta name="cover" content="itemId"/&gt;（EPUB2/EPUB3 通用）。
    /// 替换图片资源本身与 manifest 的 properties/cover-image 是 T3-3 的事。
    /// </summary>
    public void SetCoverItemId(string? itemId)
        => SetMetaName(MetaNameCover, string.IsNullOrWhiteSpace(itemId) ? null : itemId.Trim());

    // ================= manifest（T3-3 封面替换要用） =================

    /// <summary>
    /// 读 manifest 的全部 item，顺序保持原样。没有 &lt;manifest&gt; 时返回空表（交给调用方判断）。
    /// </summary>
    public IReadOnlyList<OpfManifestItem> ReadManifest()
    {
        var manifest = _package.Element(Opf + "manifest");
        if (manifest is null)
        {
            return Array.Empty<OpfManifestItem>();
        }

        return manifest.Elements(Opf + "item")
            .Select(item => new OpfManifestItem(
                (string?)item.Attribute("id") ?? string.Empty,
                (string?)item.Attribute("href") ?? string.Empty,
                (string?)item.Attribute("media-type") ?? string.Empty,
                (string?)item.Attribute("properties")))
            .ToList();
    }

    /// <summary>
    /// 新增或更新一条 manifest item（按 id 匹配）；新增时追加在 &lt;manifest&gt; 末尾。
    /// </summary>
    /// <param name="entry">要写的条目。</param>
    /// <param name="properties">
    /// 要写的 properties 值。传 null 表示**不写这个属性**
    /// EPUB2（OPF 2.0）没有 properties 属性，写了 EpubCheck 会报错（设计文档 6.1.3 规则 4 的同类问题）。
    /// </param>
    public void UpsertManifestItem(OpfManifestItem entry, string? properties = null)
    {
        var manifest = _package.Element(Opf + "manifest")
            ?? throw new AppException(
                ErrorCode.EpubInvalid,
                "不是有效的 EPUB 文件",
                "OPF 缺少 <manifest>：" + _sourcePath);

        var item = manifest.Elements(Opf + "item")
            .FirstOrDefault(e => string.Equals((string?)e.Attribute("id"), entry.Id, StringComparison.Ordinal));

        if (item is null)
        {
            item = new XElement(Opf + "item", new XAttribute("id", entry.Id));
            manifest.Add(item);
        }

        item.SetAttributeValue("href", entry.Href);
        item.SetAttributeValue("media-type", entry.MediaType);

        if (properties is null)
        {
            item.Attribute("properties")?.Remove();
        }
        else
        {
            item.SetAttributeValue("properties", properties);
        }
    }

    /// <summary>
    /// 从 manifest 里删掉一条 item；顺带把 spine 里指向它的 itemref 也删掉（否则 spine 会指向不存在的 id）。
    /// 返回是否真的删掉了（id 不存在时为 false）。
    /// </summary>
    /// <remarks>用于"一键修复"的第二类错误：OPF 声明了、包体里却没有这个文件，EpubCheck 报 RSC-001。</remarks>
    /// <summary>
    /// 删掉 &lt;guide&gt; 里指向某个文件的 &lt;reference&gt;。
    /// 删除章节时必须一起清：manifest/spine 清了但 guide 留着，就是悬空引用
    /// （实测：Sample Book F 删封面章后 OPF 仍留
    /// &lt;reference href="Text/opencover.html" type="cover page"/&gt;）。
    /// </summary>
    public int RemoveGuideReferences(string href)
    {
        if (string.IsNullOrWhiteSpace(href))
        {
            return 0;
        }

        var guide = _package.Element(Opf + "guide");

        if (guide is null)
        {
            return 0;
        }

        var removed = 0;

        foreach (var reference in guide.Elements(Opf + "reference")
                     .Where(e => string.Equals((string?)e.Attribute("href"), href, StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            RemoveWithIndent(reference);
            removed++;
        }

        return removed;
    }


    public bool RemoveManifestItem(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        var manifest = _package.Element(Opf + "manifest");
        var item = manifest?.Elements(Opf + "item")
            .FirstOrDefault(e => string.Equals((string?)e.Attribute("id"), id, StringComparison.Ordinal));

        if (item is null)
        {
            return false;
        }

        item.Remove();

        // spine 里指向被删 id 的 itemref 也要一起删：留着就是新的错误（指向不存在的 idref）。
        foreach (var itemref in _package.Element(Opf + "spine")?.Elements(Opf + "itemref")
                     .Where(e => string.Equals((string?)e.Attribute("idref"), id, StringComparison.Ordinal))
                     .ToList() ?? new List<XElement>())
        {
            itemref.Remove();
        }

        return true;
    }

    // ================= spine（T5-1 章节结构管理） =================

    /// <summary>读 spine 的 itemref（顺序就是阅读顺序）。没有 &lt;spine&gt; 时返回空表。</summary>
    public IReadOnlyList<OpfSpineItem> ReadSpine()
    {
        var spine = _package.Element(Opf + "spine");
        if (spine is null)
        {
            return Array.Empty<OpfSpineItem>();
        }

        return spine.Elements(Opf + "itemref")
            .Select((itemref, index) => new OpfSpineItem(
                index,
                (string?)itemref.Attribute("idref") ?? string.Empty,
                !string.Equals((string?)itemref.Attribute("linear"), "no", StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>spine@toc 指向的 manifest id（EPUB2 的 NCX）；没有就是 null。</summary>
    public string? ReadSpineToc()
        => (string?)_package.Element(Opf + "spine")?.Attribute("toc");

    /// <summary>
    /// 写/删 spine@toc。EPUB3 不许有 toc 属性（写了 EpubCheck 报错），所以只给 EPUB2 用，传 null 删掉。
    /// </summary>
    public void SetSpineToc(string? idRef)
    {
        var spine = EnsureSpine();

        if (string.IsNullOrWhiteSpace(idRef))
        {
            spine.Attribute("toc")?.Remove();
            return;
        }

        spine.SetAttributeValue("toc", idRef.Trim());
    }

    /// <summary>
    /// 把一条 itemref 插到 spine 的第 index 个位置。index &lt;= 0 插到最前，&gt;= 现有条数追加到末尾。
    /// </summary>
    public void InsertSpineItem(string idRef, int index)
    {
        if (string.IsNullOrWhiteSpace(idRef))
        {
            throw new ArgumentException("idref 不能为空", nameof(idRef));
        }

        InsertChild(EnsureSpine(), new XElement(Opf + "itemref", new XAttribute("idref", idRef.Trim())), index);
    }

    /// <summary>删掉 spine 里指向 idRef 的 itemref；返回是否真删到了。</summary>
    public bool RemoveSpineItem(string idRef)
    {
        var spine = _package.Element(Opf + "spine");
        var removed = false;

        foreach (var itemref in spine?.Elements(Opf + "itemref")
                     .Where(e => string.Equals((string?)e.Attribute("idref"), idRef, StringComparison.Ordinal))
                     .ToList() ?? new List<XElement>())
        {
            RemoveWithIndent(itemref);
            removed = true;
        }

        return removed;
    }

    /// <summary>
    /// 把第 from 条 itemref 移到第 to 条的位置（0 起，两个下标都必须落在 spine 内）。越界返回 false。
    /// </summary>
    public bool MoveSpineItem(int from, int to)
    {
        var spine = _package.Element(Opf + "spine");
        var items = spine?.Elements(Opf + "itemref").ToList() ?? new List<XElement>();

        if (from < 0 || from >= items.Count || to < 0 || to >= items.Count)
        {
            return false;
        }

        if (from == to)
        {
            return true;
        }

        var moving = items[from];
        RemoveWithIndent(moving);
        InsertChild(spine!, moving, to);
        return true;
    }

    // ---------- 私有小工具：插入/删除时保住原有缩进与换行 ----------
    // 真实电子书的 OPF 是缩进 + CRLF 的，直接 Add() 会让新行顶格、多出空行，
    // 把"只改一行"变成"整份文件都变了"（T3-2 踩过同类坑）。

    private XElement EnsureSpine()
    {
        var spine = _package.Element(Opf + "spine");
        if (spine is not null)
        {
            return spine;
        }

        spine = new XElement(Opf + "spine");
        var manifest = _package.Element(Opf + "manifest");

        if (manifest is null)
        {
            _package.Add(spine);
            return spine;
        }

        var siblings = _package.Elements().ToList();
        InsertChild(_package, spine, siblings.IndexOf(manifest) + 1);
        return spine;
    }

    /// <summary>子元素前用的缩进空白；没有换行就返回空串（紧凑写法不要硬加换行）。</summary>
    private static string IndentOf(XElement parent)
    {
        foreach (var node in parent.Nodes())
        {
            if (node is XText text && text.Value.Trim().Length == 0 && text.Value.Contains('\n'))
            {
                return text.Value;
            }
        }

        return string.Empty;
    }

    /// <summary>在 parent 的第 index 个子元素前插入 child，并沿用原有缩进。</summary>
    private static void InsertChild(XElement parent, XElement child, int index)
    {
        var children = parent.Elements().ToList();
        var indent = IndentOf(parent);

        var reference = index <= 0
            ? children.FirstOrDefault()
            : (index < children.Count ? children[index] : null);

        if (reference is not null)
        {
            if (indent.Length > 0)
            {
                reference.AddBeforeSelf(new XText(indent));
            }

            reference.AddBeforeSelf(child);
            return;
        }

        // 追加到末尾：插在收尾空白之前，否则会多一截空白跑到 </spine> 后面去
        var last = children.LastOrDefault();
        if (last?.NextNode is XText trailing && trailing.Value.Trim().Length == 0)
        {
            if (indent.Length > 0)
            {
                trailing.AddBeforeSelf(new XText(indent));
            }

            trailing.AddBeforeSelf(child);
            return;
        }

        if (indent.Length > 0)
        {
            parent.Add(new XText(indent));
        }

        parent.Add(child);
    }

    /// <summary>删元素时连它前面的缩进空白一起删，否则原地会多出一行空白。</summary>
    private static void RemoveWithIndent(XElement element)
    {
        if (element.PreviousNode is XText indent && indent.Value.Trim().Length == 0)
        {
            indent.Remove();
        }

        element.Remove();
    }

    // ================= 保存 =================

    /// <summary>写回 OPF。默认写回加载时的路径。</summary>
    public void Save(string? opfPath = null)
    {
        var target = opfPath ?? _sourcePath;

        if (IsEpub3)
        {
            RefreshDctermsModified();
        }

        NormalizeOpfNamespaceDeclaration(_wroteOpfAttribute);

        // 规则 1/6：不缩进、不动换行；声明由我们自己逐字写回，所以这里一律不输出它。
        // 换行：XML 解析器在读取时就已经按规范把 \r\n 归一成 \n 了，所以这里再用
        // NewLineHandling.None 会写出纯 \n，把一份 CRLF 的 OPF 每一行都改掉。
        // 正确做法是 Replace + NewLineChars = 原文件的换行风格，才能逐字节还原。
        var settings = new XmlWriterSettings
        {
            Indent = false,
            OmitXmlDeclaration = true,
            NewLineHandling = NewLineHandling.Replace,
            NewLineChars = _newLine,
        };

        var body = new StringBuilder();
        using (var writer = XmlWriter.Create(body, settings))
        {
            _doc.Save(writer);
        }

        var text = _compactSelfClosing ? ToCompactSelfClosing(body.ToString()) : body.ToString();

        // 规则 6：无 BOM 的 UTF-8。先写临时文件再原子替换，避免写到一半失败把工作区 OPF 弄坏。
        var temp = target + ".ets-tmp";
        try
        {
            File.WriteAllText(temp, (_declaration ?? string.Empty) + text, new UTF8Encoding(false));
            File.Move(temp, target, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    // ================= 私有：EPUB3 的 dcterms:modified =================

    private void RefreshDctermsModified()
    {
        var existing = _metadata.Elements(Opf + "meta")
            .Where(m => (string?)m.Attribute("property") == "dcterms:modified")
            .ToList();

        // 规则 5：多出来的必须删掉，否则 EpubCheck 报错
        foreach (var extra in existing.Skip(1))
        {
            extra.Remove();
        }

        var value = _clock.Now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

        if (existing.Count == 0)
        {
            _metadata.Add(new XElement(Opf + "meta",
                new XAttribute("property", "dcterms:modified"), value));
        }
        else
        {
            existing[0].Value = value;
        }
    }

    // ================= 私有：命名空间声明归位 =================

    private static bool IsOpfDeclaration(XAttribute attribute)
        => attribute.IsNamespaceDeclaration
           && string.Equals(attribute.Name.LocalName, "opf", StringComparison.Ordinal);

    /// <summary>
    /// 把 xmlnns:opf 收敛成"根元素第一个属性"这一处。见类注释里解释的前缀改写坑。
    /// <paramref name="force"/> 为 true 时（本次真的写了 opf:* 属性）没有声明就补一个。
    /// </summary>
    private void NormalizeOpfNamespaceDeclaration(bool force)
    {
        var root = _doc.Root!;
        var declarations = root.DescendantsAndSelf()
            .SelectMany(e => e.Attributes().Where(IsOpfDeclaration))
            .ToList();

        if (declarations.Count == 0)
        {
            if (!force)
            {
                return;
            }

            var added = root.Attributes().ToList();
            added.Insert(0, new XAttribute(XNamespace.Xmlns + "opf", Opf.NamespaceName));
            root.ReplaceAttributes(added);
            return;
        }

        // 已经就是根元素第一个属性、且没有重复声明 -> 一个字符都不用动
        if (declarations.Count == 1
            && ReferenceEquals(declarations[0].Parent, root)
            && ReferenceEquals(root.Attributes().First(), declarations[0]))
        {
            return;
        }

        var uri = declarations[0].Value;
        foreach (var declaration in declarations)
        {
            declaration.Remove();
        }

        var attributes = root.Attributes().ToList();
        attributes.Insert(0, new XAttribute(XNamespace.Xmlns + "opf", uri));
        root.ReplaceAttributes(attributes);
    }

    // ================= 私有：dc 元素 =================

    private void SetDcSingle(string localName, string? value)
    {
        var element = _metadata.Elements(Dc + localName).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(value))
        {
            element?.Remove();
            return;
        }

        if (element is null)
        {
            element = new XElement(Dc + localName);
            _metadata.Add(element);
        }

        element.Value = value.Trim();
    }

    private string? FirstDcValue(string localName)
        => _metadata.Elements(Dc + localName).FirstOrDefault()?.Value.Trim();

    // ================= 私有：meta name/property =================

    private string? MetaNameValue(string name)
        => _metadata.Elements(Opf + "meta")
            .FirstOrDefault(m => (string?)m.Attribute("name") == name)
            ?.Attribute("content")?.Value.Trim();

    /// <summary>Calibre 的老式写法：值放在 content 属性里（不是元素文本）。</summary>
    private void SetMetaName(string name, string? value)
    {
        var element = _metadata.Elements(Opf + "meta")
            .FirstOrDefault(m => (string?)m.Attribute("name") == name);

        if (value is null)
        {
            element?.Remove();
            return;
        }

        if (element is null)
        {
            element = new XElement(Opf + "meta", new XAttribute("name", name));
            _metadata.Add(element);
        }

        element.RemoveNodes();
        element.SetAttributeValue("content", value);
    }

    private static XElement RefinesMeta(string id, string property, string value, string? scheme)
    {
        var meta = new XElement(Opf + "meta",
            new XAttribute("refines", "#" + id),
            new XAttribute("property", property));

        if (scheme is not null)
        {
            meta.SetAttributeValue("scheme", scheme);
        }

        meta.Value = value;
        return meta;
    }

    private void RemoveRefinesFor(XElement element)
    {
        var id = (string?)element.Attribute("id");
        if (string.IsNullOrEmpty(id))
        {
            return;
        }

        foreach (var meta in _metadata.Elements(Opf + "meta")
                     .Where(m => (string?)m.Attribute("refines") == "#" + id)
                     .ToList())
        {
            meta.Remove();
        }
    }

    private bool HasRefinesProperty(XElement element, string property)
    {
        var id = (string?)element.Attribute("id");
        if (string.IsNullOrEmpty(id))
        {
            return false;
        }

        return _metadata.Elements(Opf + "meta").Any(m =>
            (string?)m.Attribute("refines") == "#" + id
            && (string?)m.Attribute("property") == property);
    }

    // ================= 私有：作者/译者 =================

    private void ReplacePeople(string localName, IReadOnlyList<EpubContributor> people)
    {
        foreach (var old in _metadata.Elements(Dc + localName).ToList())
        {
            RemoveRefinesFor(old);
            old.Remove();
        }

        foreach (var person in people)
        {
            if (string.IsNullOrWhiteSpace(person.Name))
            {
                continue;
            }

            var name = person.Name.Trim();
            var role = string.IsNullOrWhiteSpace(person.Role) ? "aut" : person.Role!.Trim();
            var fileAs = string.IsNullOrWhiteSpace(person.FileAs) ? FileAsOf(name) : person.FileAs!.Trim();

            var element = new XElement(Dc + localName, name);

            if (IsEpub3)
            {
                // 规则 4：EPUB3 用 refines，绝不写 opf:*
                var id = localName + "-" + NewSuffix();
                element.SetAttributeValue("id", id);
                _metadata.Add(element);
                _metadata.Add(RefinesMeta(id, "role", role, "marc:relators"));
                _metadata.Add(RefinesMeta(id, "file-as", fileAs, scheme: null));
            }
            else
            {
                // 规则 3：EPUB2 用 opf:role / opf:file-as（xmlns:opf 由保存阶段补）
                element.SetAttributeValue(Opf + "role", role);
                element.SetAttributeValue(Opf + "file-as", fileAs);
                _wroteOpfAttribute = true;
                _metadata.Add(element);
            }
        }
    }

    private EpubContributor ReadContributor(XElement element)
    {
        var name = element.Value.Trim();

        if (IsEpub3)
        {
            var (role, fileAs) = ReadRefines(element);
            return new EpubContributor(name, role, fileAs);
        }

        return new EpubContributor(
            name,
            ((string?)element.Attribute(Opf + "role"))?.Trim(),
            ((string?)element.Attribute(Opf + "file-as"))?.Trim());
    }

    private (string? Role, string? FileAs) ReadRefines(XElement element)
    {
        var id = (string?)element.Attribute("id");
        if (string.IsNullOrEmpty(id))
        {
            return (null, null);
        }

        string? role = null, fileAs = null;
        foreach (var meta in _metadata.Elements(Opf + "meta")
                     .Where(m => (string?)m.Attribute("refines") == "#" + id))
        {
            switch ((string?)meta.Attribute("property"))
            {
                case "role":
                    role = meta.Value.Trim();
                    break;
                case "file-as":
                    fileAs = meta.Value.Trim();
                    break;
            }
        }

        return (role, fileAs);
    }

    // ================= 私有：标题 =================

    private XElement? MainTitleElement()
        => _metadata.Elements(Dc + "title").FirstOrDefault(t => !IsSubtitle(t));

    private bool IsSubtitle(XElement title)
        => TitleTypeOf(title) is "subtitle" or "副标题";

    private string? TitleTypeOf(XElement title)
    {
        var id = (string?)title.Attribute("id");
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        return _metadata.Elements(Opf + "meta")
            .FirstOrDefault(m => (string?)m.Attribute("property") == "title-type"
                                 && (string?)m.Attribute("refines") == "#" + id)
            ?.Value.Trim();
    }

    // ================= 私有：ISBN =================

    private XElement? FindIsbnElement()
    {
        var identifiers = _metadata.Elements(Dc + "identifier").ToList();

        if (IsEpub3)
        {
            var refined = identifiers.FirstOrDefault(e => HasRefinesProperty(e, "identifier-type"));
            if (refined is not null)
            {
                return refined;
            }
        }
        else
        {
            var scheme = identifiers.FirstOrDefault(e =>
                ((string?)e.Attribute(Opf + "scheme"))?.Equals("ISBN", StringComparison.OrdinalIgnoreCase) == true);
            if (scheme is not null)
            {
                return scheme;
            }
        }

        return identifiers.FirstOrDefault(e =>
            e.Value.TrimStart().StartsWith("urn:isbn:", StringComparison.OrdinalIgnoreCase));
    }

    private string? ReadIsbn()
    {
        var element = FindIsbnElement();
        if (element is null)
        {
            return null;
        }

        var value = element.Value.Trim();
        return value.StartsWith("urn:isbn:", StringComparison.OrdinalIgnoreCase)
            ? value["urn:isbn:".Length..].Trim()
            : value;
    }

    // ================= 私有：杂项 =================

    /// <summary>
    /// opf:file-as 的取值。中文姓名无需反转；已经是「Last, First」的原样保留。
    /// 西文自动反转（"John Doe" -> "Doe, John"）等真正需要时再加  猜错比不猜更糟。
    /// </summary>
    private static string FileAsOf(string name) => name.Trim();

    private static string NewSuffix() => Guid.NewGuid().ToString("N")[..8];

    private static bool TryReadDate(string? raw, out DateOnly value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var trimmed = raw.Trim();

        // 真实电子书里 dc:date 常写成 "2016-05-31 16:00:00+00:00" 甚至更乱，先切前 10 位
        if (trimmed.Length >= 10
            && DateOnly.TryParseExact(trimmed.AsSpan(0, 10), "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out value))
        {
            return true;
        }

        if (DateOnly.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out value))
        {
            return true;
        }

        if (DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            value = DateOnly.FromDateTime(parsed.Date);
            return true;
        }

        return false;
    }

    /// <summary>
    /// 读原文件原文。让 StreamReader 自己按 BOM 判编码，UTF-16 的文件也能读对；
    /// 读不到（权限、占用）返回 null，后面各处的推断就走各自的保守默认值。
    /// </summary>
    private static string? ReadRawText(string path)
    {
        try
        {
            return File.ReadAllText(path, new UTF8Encoding(false));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 抓住原文件的 XML 声明逐字保留：System.Xml 会按自己的编码设置重写声明
    /// （把 UTF-8 写成 utf-8），对"只改一行"的判据来说那也是一处多余 diff。
    /// 声明里的编码不是 utf-8 时不照抄，改用 utf-8（否则会写出与声明不符的字节）。
    /// </summary>
    private static string? ExtractDeclaration(string? raw)
    {
        if (raw is null)
        {
            return null;
        }

        var match = Regex.Match(raw.TrimStart('\uFEFF'), @"^<\?xml[^?]*\?>");
        if (!match.Success)
        {
            return null;
        }

        var declaration = match.Value;
        var hasEncoding = Regex.IsMatch(declaration, "encoding\\s*=", RegexOptions.IgnoreCase);
        var isUtf8 = Regex.IsMatch(declaration, "encoding\\s*=\\s*[\"']utf-?8[\"']", RegexOptions.IgnoreCase);
        return !hasEncoding || isUtf8 ? declaration : "<?xml version=\"1.0\" encoding=\"utf-8\"?>";
    }

    /// <summary>判断原文件是 CRLF 还是 LF：多数决，取不到就按 Windows 习惯用 CRLF。</summary>
    private static string DetectNewLine(string? raw)
    {
        if (raw is null)
        {
            return "\r\n";
        }

        var crlf = Regex.Matches(raw, "\r\n").Count;
        var lf = Regex.Matches(raw, "\n").Count - crlf;
        return lf > crlf ? "\n" : "\r\n";
    }

    /// <summary>
    /// 原文件主要用紧凑写法（&lt;itemref idref="x"/&gt;）还是带空格写法（... /&gt;）？
    /// 真实电子书两种都有，必须照原样还回去，否则一份 1742 行的 OPF 会平白改掉 1722 行
    ///  那正是判据 6 要防的"重排整个 OPF"。
    /// </summary>
    private static bool DetectCompactSelfClosing(string? raw)
    {
        if (raw is null)
        {
            return false;
        }

        var spaced = Regex.Matches(raw, @"\s/>").Count;
        var all = Regex.Matches(raw, @"/>").Count;
        return all - spaced > spaced;
    }

    /// <summary>
    /// 把 XmlWriter 输出的 <c>&lt;x /&gt;</c> 改回原文件的 <c>&lt;x/&gt;</c>。
    /// 只动"标签末尾斜杠前那一个空格"：游标在标签内、不在引号里，且缓冲区以 " /" 收尾时才删。
    /// 引号里的 " /" 不可能是标签结尾（后面必有引号），所以属性值里带 " />" 也不会被误伤；
    /// 注释/CDATA/PI 单独识别，避免把里面的 " />" 当成标签。
    /// </summary>
    private static string ToCompactSelfClosing(string xml)
    {
        var result = new StringBuilder(xml.Length);
        var tag = new StringBuilder();
        var inTag = false;
        var quote = '\0';

        foreach (var c in xml)
        {
            if (!inTag)
            {
                if (c == '<')
                {
                    inTag = true;
                    tag.Clear();
                    tag.Append(c);
                    continue;
                }

                result.Append(c);
                continue;
            }

            tag.Append(c);

            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                continue;
            }

            if (c != '>')
            {
                continue;
            }

            inTag = false;
            var text = tag.ToString();

            // 注释 / CDATA / 处理指令里的 '>' 不结束标签。继续攒着，别往 result 里追加（否则会重复一遍）
            if (text.StartsWith("<!--", StringComparison.Ordinal) && !text.EndsWith("-->", StringComparison.Ordinal))
            {
                inTag = true;
                continue;
            }

            if (text.StartsWith("<![CDATA[", StringComparison.Ordinal) && !text.EndsWith("]]>", StringComparison.Ordinal))
            {
                inTag = true;
                continue;
            }

            if (text.StartsWith("<?", StringComparison.Ordinal) && !text.EndsWith("?>", StringComparison.Ordinal))
            {
                inTag = true;
                continue;
            }

            // "... />" 的末尾三字符是 ' '、'/'、'>'，所以空格在倒数第 3 位
            if (text.Length >= 3 && text[^2] == '/' && text[^3] == ' ')
            {
                tag.Remove(tag.Length - 3, 1);
            }

            result.Append(tag);
        }

        if (inTag)
        {
            result.Append(tag);
        }

        return result.ToString();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 临时文件清不掉不影响主流程
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>引擎层不引 Infrastructure，默认时钟就地实现。</summary>
    private sealed class UtcClock : IClock
    {
        public static UtcClock Instance { get; } = new();

        public DateTimeOffset Now => DateTimeOffset.UtcNow;
    }
}







