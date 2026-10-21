using System.Text;
using System.Xml;
using System.Xml.Linq;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.IO;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Engines.Text;

namespace EbookToolSuite.Engines.Epub;

/// <summary>
/// EPUB 章节结构编辑（执行计划 T5-1）：读章节树，重命名 / 移动 / 删除 / 新增 / 导入 / 合并。
/// </summary>
/// <remarks>
/// <para>
/// <b>只动工作区</b>：所有操作都改工作区里解包出来的文件，原 EPUB 一个字节都不碰
/// （计划"关键实现点 1"：会话内变更，保存时才落盘）。
/// </para>
/// <para>
/// <b>spine 与 manifest 必须同步改</b>（关键实现点 2）：只改一边，EpubCheck 立刻报 RSC-001 / OPF-xxx。
/// 所以每个操作都走同一个顺序先改 OPF（manifest + spine） 再改目录（nav/NCX） 最后动文件。
/// </para>
/// <para>
/// <b>删除先入暂存区</b>：章节文件挪进 <c>.deleted/</c>，打包器会忽略这个目录；
/// 这样"删错了"在保存前还能把文件捞回来（判据 7 的撤销基础）。
/// </para>
/// </remarks>
public sealed class EpubStructureEditor
{
    /// <summary>删除章节的暂存目录（保存时随工作区一起丢弃，打包器不打进 EPUB）。</summary>
    public const string DeletedDirectoryName = ".deleted";

    private const string XhtmlMediaType = "application/xhtml+xml";
    private const string NcxMediaType = "application/x-dtbncx+xml";
    private const int MaxTitleLength = 200;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _workDir;
    private readonly string _opfEntry;
    private readonly IClock? _clock;

    public EpubStructureEditor(string workDir, string opfEntry, IClock? clock = null)
    {
        _workDir = workDir ?? throw new ArgumentNullException(nameof(workDir));
        _opfEntry = NormalizeEntry(opfEntry);
        _clock = clock;
    }

    /// <summary>工作区根目录。</summary>
    public string WorkDir => _workDir;

    /// <summary>OPF 在包内的路径。</summary>
    public string OpfEntry => _opfEntry;

    // ================= 读 =================

    /// <summary>
    /// 读章节树：<b>以 spine 为权威</b>，标题与层级从目录（nav/NCX）补，目录里没有就退回文件名。
    /// </summary>
    public IReadOnlyList<ChapterNode> ReadChapters()
    {
        var opf = Open();

        var manifest = new Dictionary<string, OpfManifestItem>(StringComparer.Ordinal);
        foreach (var item in opf.ReadManifest())
        {
            manifest.TryAdd(item.Id, item);
        }

        var tocTitles = new Dictionary<string, NavDocumentBuilder.NavEntry>(StringComparer.OrdinalIgnoreCase);
        var tocDocument = FindTocDocument(opf);
        if (tocDocument is not null)
        {
            foreach (var entry in NavDocumentBuilder.ReadEntries(_workDir, tocDocument))
            {
                tocTitles.TryAdd(entry.Href, entry);
            }
        }

        var nodes = new List<ChapterNode>();

        foreach (var spineItem in opf.ReadSpine())
        {
            var href = manifest.TryGetValue(spineItem.IdRef, out var item)
                ? NavDocumentBuilder.ResolveHref(_opfEntry, item.Href)
                : string.Empty;

            NavDocumentBuilder.NavEntry? toc = null;
            if (href.Length > 0)
            {
                tocTitles.TryGetValue(href, out toc);
            }

            var hasTitle = toc is not null;

            nodes.Add(new ChapterNode
            {
                Index = spineItem.Index,
                Id = spineItem.IdRef,
                Href = href,
                Title = hasTitle ? toc!.Title : FileNameOf(href),
                TitleFromToc = hasTitle,
                Level = hasTitle ? toc!.Level : 0,
                Linear = spineItem.Linear,
                Exists = href.Length > 0 && File.Exists(FullPath(href)),
            });
        }

        return nodes;
    }

    // ================= 重命名 =================

    /// <summary>
    /// 改章节标题：只改目录里的文字（缺条目就补一条），<b>不动文件名</b>（关键实现点 3）。
    /// </summary>
    public ChapterOperationResult Rename(int index, string newTitle)
    {
        var title = RequireTitle(newTitle);
        var chapters = ReadChapters();
        var node = At(chapters, index);
        var opf = Open();
        var tocDocument = FindTocDocument(opf);
        var changed = new List<string>();

        if (tocDocument is null)
        {
            return new ChapterOperationResult(
                "rename",
                index,
                node.Href,
                changed,
                "这本书没有目录文件（nav/NCX），章节标题暂时改不了：阅读器的目录是按文件名兜底的");
        }

        var entry = NavDocumentBuilder.ReadEntries(_workDir, tocDocument)
            .FirstOrDefault(e => string.Equals(e.Href, node.Href, StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            NavDocumentBuilder.InsertEntry(_workDir, tocDocument, AnchorBefore(chapters, index, tocDocument), node.Href, title);
        }
        else
        {
            NavDocumentBuilder.RenameTitle(_workDir, tocDocument, node.Href, title);
        }

        changed.Add(tocDocument);
        return new ChapterOperationResult("rename", index, node.Href, changed, $"第 {index + 1} 章已改名为「{title}」");
    }

    // ================= 移动 =================

    /// <summary>上下移动章节：改 spine 顺序，并让目录顶层顺序跟着走。</summary>
    public ChapterOperationResult Move(int from, int to)
    {
        var chapters = ReadChapters();

        if (from < 0 || from >= chapters.Count || to < 0 || to >= chapters.Count)
        {
            throw new AppException(
                ErrorCode.ValidationFailed,
                "章节序号超出范围",
                $"move {from} -> {to}，当前共 {chapters.Count} 章");
        }

        var opf = Open();
        if (!opf.MoveSpineItem(from, to))
        {
            throw new AppException(ErrorCode.ValidationFailed, "移动章节失败", $"spine 下标 {from} -> {to} 越界");
        }

        opf.Save();

        var changed = new List<string> { _opfEntry };
        var tocDocument = FindTocDocument(opf);
        if (tocDocument is not null)
        {
            NavDocumentBuilder.AlignTopLevelOrder(_workDir, tocDocument, HrefsInSpineOrder(opf));
            changed.Add(tocDocument);
        }

        return new ChapterOperationResult(
            "move", to, chapters[from].Href, changed, $"已把「{chapters[from].Title}」移到第 {to + 1} 章");
    }

    // ================= 删除 =================

    /// <summary>
    /// 删除章节：spine + manifest + 目录 + 文件。文件先进 <c>.deleted/</c> 暂存区（保存时才真丢）。
    /// </summary>
    public ChapterOperationResult Delete(int index) => DeleteCore(index, "delete");

    private ChapterOperationResult DeleteCore(int index, string operation)
    {
        var chapters = ReadChapters();

        if (chapters.Count <= 1)
        {
            throw new AppException(ErrorCode.EpubInvalid, "至少要保留一章", "spine 里只剩这一章了");
        }

        var node = At(chapters, index);
        var opf = Open();

        opf.RemoveSpineItem(node.Id);
        opf.RemoveManifestItem(node.Id);   // 顺带清掉 spine 里残留的 itemref

        // OPF 的 guide 里可能还有指向这一章的 <reference>（如封面页），不清掉就是悬空引用
        opf.RemoveGuideReferences(NavDocumentBuilder.RelativeHref(_opfEntry, node.Href));
        opf.Save();

        var changed = new List<string> { _opfEntry };
        var tocDocument = FindTocDocument(opf);
        if (tocDocument is not null)
        {
            NavDocumentBuilder.RemoveEntries(_workDir, tocDocument, new[] { node.Href });
            changed.Add(tocDocument);
        }

        var source = FullPath(node.Href);
        if (File.Exists(source))
        {
            var target = Path.Combine(_workDir, DeletedDirectoryName, node.Href.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(source, target, overwrite: true);
        }

        changed.Add(node.Href);
        return new ChapterOperationResult(operation, index, node.Href, changed, $"已删除第 {index + 1} 章「{node.Title}」");
    }

    // ================= 新增 / 导入 =================

    /// <summary>在指定位置插入一个空章节（模板 XHTML + manifest + spine + 目录）。</summary>
    public ChapterOperationResult AddEmpty(int insertIndex, string? title)
        => InsertNewChapter(insertIndex, RequireTitle(string.IsNullOrWhiteSpace(title) ? "新章节" : title!), null, "add");

    /// <summary>从文件导入章节（支持 .xhtml / .html / .txt）。</summary>
    public ChapterOperationResult Import(int insertIndex, string sourcePath, string? title)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            throw new AppException(ErrorCode.FileNotFound, "找不到要导入的章节文件", sourcePath);
        }

        var fallback = Path.GetFileNameWithoutExtension(sourcePath);
        var resolved = RequireTitle(string.IsNullOrWhiteSpace(title) ? fallback : title!);
        var bytes = File.ReadAllBytes(sourcePath);
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();

        List<XNode> content;

        if (extension == ".txt")
        {
            TextEncodingSupport.EnsureRegistered();
            var detection = TextEncodingDetector.Detect(sourcePath);
            var text = detection.Encoding.GetString(bytes, detection.BomLength, bytes.Length - detection.BomLength);
            content = BuildParagraphs(text);
        }
        else
        {
            content = ExtractContent(DecodeText(bytes));
        }

        return InsertNewChapter(insertIndex, resolved, content, "import");
    }

    // ================= 合并 =================

    /// <summary>把下一章合并进当前章：后一章的 body 内容追加到前一章末尾，然后删掉后一章。</summary>
    public ChapterOperationResult Merge(int index)
    {
        var chapters = ReadChapters();
        var target = At(chapters, index);

        if (index + 1 >= chapters.Count)
        {
            throw new AppException(ErrorCode.ValidationFailed, "最后一章后面没有可合并的章节", $"merge {index}");
        }

        var source = chapters[index + 1];
        var targetPath = FullPath(target.Href);
        var sourcePath = FullPath(source.Href);

        var targetRaw = File.ReadAllText(targetPath);
        var targetDocument = ParseXhtml(targetRaw, target.Href);
        var sourceDocument = ParseXhtml(File.ReadAllText(sourcePath), source.Href);

        var targetBody = BodyOf(targetDocument)
            ?? throw new AppException(ErrorCode.EpubInvalid, "要合并的章节没有 <body>", target.Href);
        var sourceBody = BodyOf(sourceDocument)
            ?? throw new AppException(ErrorCode.EpubInvalid, "被合并的章节没有 <body>", source.Href);

        // 只搬 body 的子节点：<html>/<head>/<body> 一个都不重复（判据 6）。
        foreach (var node in sourceBody.Nodes().ToList())
        {
            var clone = CloneNode(node);
            if (clone is not null)
            {
                targetBody.Add(clone);
            }
        }

        NavDocumentBuilder.WriteBackPreservingStyle(targetDocument, targetPath, targetRaw);

        var deleted = DeleteCore(index + 1, "merge");
        var changed = new List<string> { target.Href };
        changed.AddRange(deleted.ChangedFiles);

        return new ChapterOperationResult(
            "merge", index, target.Href, changed, $"已把「{source.Title}」并入「{target.Title}」");
    }

    // ================= 拆分章节 =================

    /// <summary>列出这一章可以切开的位置（每个位置 = 从这一块开始归后半章）。</summary>
    public IReadOnlyList<ChapterSplitPoint> ReadSplitPoints(int index)
    {
        var chapters = ReadChapters();
        var node = At(chapters, index);
        var document = ParseXhtml(File.ReadAllText(FullPath(node.Href)), node.Href);
        var body = BodyOf(document);

        if (body is null)
        {
            return Array.Empty<ChapterSplitPoint>();
        }

        var blocks = body.Elements().ToList();
        var result = new List<ChapterSplitPoint>();

        for (var i = 1; i < blocks.Count && result.Count < 500; i++)
        {
            var preview = CollapseText(blocks[i].Value);

            if (preview.Length > 30)
            {
                preview = preview[..30] + "";
            }

            result.Add(new ChapterSplitPoint(i, preview.Length == 0 ? "（这一块没有文字）" : preview));
        }

        return result;
    }

    /// <summary>
    /// 拆分章节：在第 <paramref name="splitAt"/> 个正文块<b>之前</b>切开。
    /// 前半留在原文件，后半写成新章节文件并紧跟其后插进 spine / manifest / 目录。
    /// </summary>
    public ChapterOperationResult Split(int index, int splitAt)
    {
        var chapters = ReadChapters();
        var node = At(chapters, index);
        var sourcePath = FullPath(node.Href);
        var sourceRaw = File.ReadAllText(sourcePath);
        var document = ParseXhtml(sourceRaw, node.Href);
        var body = BodyOf(document)
            ?? throw new AppException(ErrorCode.EpubInvalid, "这一章没有 <body>，拆不开", node.Href);

        var blocks = body.Elements().ToList();

        if (blocks.Count < 2)
        {
            throw new AppException(ErrorCode.ValidationFailed, "这一章正文只有一块内容，拆不开", node.Href);
        }

        if (splitAt < 1 || splitAt >= blocks.Count)
        {
            throw new AppException(
                ErrorCode.ValidationFailed,
                "拆分点超出范围：要选 1 到 " + (blocks.Count - 1) + " 之间的块",
                node.Href);
        }

        var opf = Open();
        var newTitle = RequireTitle(node.Title + "（续）");
        var (href, id) = NextFreeEntry(opf, DirectoryOf(node.Href));

        // 后半章：整份克隆原章（head 里的样式与字体引用一起带走），body 换成后半段
        var tailDocument = new XDocument(document);
        var tailBody = BodyOf(tailDocument)!;
        var tailHead = tailDocument.Root?.Descendants().FirstOrDefault(e => e.Name.LocalName == "head");
        var tailTitle = tailHead?.Elements().FirstOrDefault(e => e.Name.LocalName == "title");

        if (tailTitle is not null)
        {
            tailTitle.Value = newTitle;
        }

        tailBody.RemoveNodes();
        tailBody.Add(new XElement(NavDocumentBuilder.Xhtml + "h1", new XText(newTitle)));

        foreach (var block in blocks.Skip(splitAt).ToList())
        {
            tailBody.Add(new XElement(block));
            RemoveBlock(block);
        }

        // 先写新章再写原章：万一中间失败，也不会出现"原章被切了、新章却不在"
        NavDocumentBuilder.WriteBackPreservingStyle(tailDocument, FullPath(href), sourceRaw);
        NavDocumentBuilder.WriteBackPreservingStyle(document, sourcePath, sourceRaw);

        opf.UpsertManifestItem(
            new OpfManifestItem(id, NavDocumentBuilder.RelativeHref(_opfEntry, href), XhtmlMediaType),
            properties: null);
        opf.InsertSpineItem(id, index + 1);
        opf.Save();

        var changed = new List<string> { node.Href, href };
        var tocDocument = FindTocDocument(opf);
        if (tocDocument is not null)
        {
            NavDocumentBuilder.InsertEntry(_workDir, tocDocument, node.Href, href, newTitle);
            changed.Add(tocDocument);
        }

        return new ChapterOperationResult(
            "split",
            index + 1,
            href,
            changed,
            "已把「" + node.Title + "」从第 " + (splitAt + 1) + " 块前切开，新章「" + newTitle + "」");
    }

    /// <summary>删掉一个正文块（连它前面的缩进空白一起删）。</summary>
    private static void RemoveBlock(XElement block)
    {
        if (block.PreviousNode is XText indent && indent.Value.Trim().Length == 0)
        {
            indent.Remove();
        }

        block.Remove();
    }

    private static string CollapseText(string? value)
        => System.Text.RegularExpressions.Regex.Replace((value ?? string.Empty).Trim(), "\\s+", " ");

    // ================= 目录层级（升级 / 降级） =================

    /// <summary>
    /// 把章节在目录里降一级（挂到前一个同级条目底下）。只改目录文档，不动 spine / manifest / 文件。
    /// </summary>
    public ChapterOperationResult Demote(int index) => ChangeLevel(index, demote: true);

    /// <summary>把章节在目录里升一级（从父项里提出来，排在父项后面）。</summary>
    public ChapterOperationResult Promote(int index) => ChangeLevel(index, demote: false);

    private ChapterOperationResult ChangeLevel(int index, bool demote)
    {
        var chapters = ReadChapters();
        var node = At(chapters, index);
        var opf = Open();
        var tocDocument = FindTocDocument(opf)
            ?? throw new AppException(
                ErrorCode.EpubInvalid,
                "这本书没有目录文件（nav/NCX），目录层级改不了",
                _opfEntry);

        var inToc = NavDocumentBuilder.ReadEntries(_workDir, tocDocument)
            .Any(entry => string.Equals(entry.Href, node.Href, StringComparison.OrdinalIgnoreCase));

        if (!inToc)
        {
            throw new AppException(
                ErrorCode.ValidationFailed,
                "这一章不在目录里：先用「重命名」把它补进目录，再调层级",
                node.Href);
        }

        var changed = demote
            ? NavDocumentBuilder.DemoteEntry(_workDir, tocDocument, node.Href)
            : NavDocumentBuilder.PromoteEntry(_workDir, tocDocument, node.Href);

        if (!changed)
        {
            throw new AppException(
                ErrorCode.ValidationFailed,
                demote
                    ? "这一章在目录里是同级第一项，没有上一章可以挂靠"
                    : "这一章在目录里已经是顶层了",
                node.Href);
        }

        return new ChapterOperationResult(
            demote ? "demote" : "promote",
            index,
            node.Href,
            new[] { tocDocument },
            demote
                ? "已把「" + node.Title + "」在目录里降一级"
                : "已把「" + node.Title + "」在目录里升一级");
    }

    // ================= 内部实现 =================

    private ChapterOperationResult InsertNewChapter(
        int insertIndex, string title, IReadOnlyList<XNode>? content, string operation)
    {
        var chapters = ReadChapters();
        var index = Math.Clamp(insertIndex, 0, chapters.Count);
        var opf = Open();

        var neighbour = index < chapters.Count ? chapters[index] : chapters.LastOrDefault();
        var directory = DirectoryOf(neighbour?.Href ?? _opfEntry);
        var (href, id) = NextFreeEntry(opf, directory);

        var document = BuildChapterDocument(opf, title, content);
        var absolute = FullPath(href);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        WriteNewXhtml(document, absolute, opf.IsEpub3);

        opf.UpsertManifestItem(
            new OpfManifestItem(id, NavDocumentBuilder.RelativeHref(_opfEntry, href), XhtmlMediaType),
            properties: null);
        opf.InsertSpineItem(id, index);
        opf.Save();

        var changed = new List<string> { _opfEntry, href };
        var tocDocument = FindTocDocument(opf);
        if (tocDocument is not null)
        {
            NavDocumentBuilder.InsertEntry(_workDir, tocDocument, AnchorBefore(chapters, index, tocDocument), href, title);
            changed.Add(tocDocument);
        }

        var verb = operation == "import" ? "已导入章节" : "已新增章节";
        return new ChapterOperationResult(operation, index, href, changed, $"{verb}「{title}」（第 {index + 1} 章）");
    }

    /// <summary>找目录文档（EPUB3 = manifest 里 properties 含 nav 的那条；EPUB2 = spine@toc 或 NCX）。</summary>
    private string? FindTocDocument(OpfMetadataEditor opf)
    {
        var manifest = opf.ReadManifest();

        if (opf.IsEpub3)
        {
            var nav = manifest.FirstOrDefault(i =>
                i.Properties is not null
                && i.Properties.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Contains("nav", StringComparer.OrdinalIgnoreCase));

            if (nav is not null)
            {
                var navHref = NavDocumentBuilder.ResolveHref(_opfEntry, nav.Href);
                if (File.Exists(FullPath(navHref)))
                {
                    return navHref;
                }
            }
        }

        var tocId = opf.ReadSpineToc();
        if (!string.IsNullOrWhiteSpace(tocId))
        {
            var item = manifest.FirstOrDefault(i => string.Equals(i.Id, tocId, StringComparison.Ordinal));
            if (item is not null)
            {
                var tocHref = NavDocumentBuilder.ResolveHref(_opfEntry, item.Href);
                if (File.Exists(FullPath(tocHref)))
                {
                    return tocHref;
                }
            }
        }

        var ncx = manifest.FirstOrDefault(i =>
            string.Equals(i.MediaType, NcxMediaType, StringComparison.OrdinalIgnoreCase));

        if (ncx is null)
        {
            return null;
        }

        var ncxHref = NavDocumentBuilder.ResolveHref(_opfEntry, ncx.Href);
        return File.Exists(FullPath(ncxHref)) ? ncxHref : null;
    }

    /// <summary>插入新目录项时插在哪：取它前面最近的、目录里有的那一章；没有就回 null（追加到末尾）。</summary>
    private string? AnchorBefore(IReadOnlyList<ChapterNode> chapters, int index, string tocDocument)
    {
        var known = new HashSet<string>(
            NavDocumentBuilder.ReadEntries(_workDir, tocDocument).Select(e => e.Href),
            StringComparer.OrdinalIgnoreCase);

        for (var i = Math.Min(index, chapters.Count) - 1; i >= 0; i--)
        {
            if (known.Contains(chapters[i].Href))
            {
                return chapters[i].Href;
            }
        }

        return null;
    }

    private IReadOnlyList<string> HrefsInSpineOrder(OpfMetadataEditor opf)
    {
        var manifest = new Dictionary<string, OpfManifestItem>(StringComparer.Ordinal);
        foreach (var item in opf.ReadManifest())
        {
            manifest.TryAdd(item.Id, item);
        }

        return opf.ReadSpine()
            .Select(s => manifest.TryGetValue(s.IdRef, out var item)
                ? NavDocumentBuilder.ResolveHref(_opfEntry, item.Href)
                : string.Empty)
            .Where(href => href.Length > 0)
            .ToList();
    }

    /// <summary>给新章节起一个没被占用的 id 与包内路径（同目录下的 chapter-NNNN.xhtml）。</summary>
    private (string Href, string Id) NextFreeEntry(OpfMetadataEditor opf, string directory)
    {
        var usedIds = new HashSet<string>(opf.ReadManifest().Select(i => i.Id), StringComparer.Ordinal);
        var usedHrefs = new HashSet<string>(
            opf.ReadManifest().Select(i => NavDocumentBuilder.ResolveHref(_opfEntry, i.Href)),
            StringComparer.OrdinalIgnoreCase);

        for (var n = 1; n < 100000; n++)
        {
            var name = "chapter-" + n.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) + ".xhtml";
            var href = directory.Length == 0 ? name : directory + "/" + name;
            var id = "chapter-" + n.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);

            if (!usedIds.Contains(id) && !usedHrefs.Contains(href))
            {
                return (href, id);
            }
        }

        throw new AppException(ErrorCode.InternalError, "这本书的章节太多了，起不出新文件名", directory);
    }

    private XDocument BuildChapterDocument(OpfMetadataEditor opf, string title, IReadOnlyList<XNode>? content)
    {
        var xhtml = NavDocumentBuilder.Xhtml;
        var language = opf.Read().Language;
        if (string.IsNullOrWhiteSpace(language))
        {
            language = "und";
        }

        var head = new XElement(xhtml + "head", new XElement(xhtml + "title", new XText(title)));

        if (opf.IsEpub3)
        {
            head.AddFirst(new XElement(xhtml + "meta", new XAttribute("charset", "utf-8")));
        }
        else
        {
            // XHTML 1.1（EPUB2）里没有 <meta charset>，只有 http-equiv 这一种写法（写了 charset EpubCheck 会报错）。
            head.Add(new XElement(
                xhtml + "meta",
                new XAttribute("http-equiv", "Content-Type"),
                new XAttribute("content", "text/html; charset=utf-8")));
        }

        var body = new XElement(xhtml + "body", new XElement(xhtml + "h1", new XText(title)));

        if (content is null)
        {
            body.Add(new XElement(xhtml + "p"));
        }
        else
        {
            foreach (var node in content)
            {
                body.Add(node);
            }
        }

        var root = new XElement(
            xhtml + "html",
            new XAttribute(XNamespace.Xml + "lang", language),
            opf.IsEpub3 ? new XAttribute("lang", language) : null,
            head,
            body);

        return new XDocument(new XDeclaration("1.0", "utf-8", null), root);
    }

    /// <summary>写一个新章节文件：声明 +（EPUB2 的 DOCTYPE）+ 缩进好的 XHTML，UTF-8 无 BOM、LF。</summary>
    private static void WriteNewXhtml(XDocument document, string path, bool epub3)
    {
        var newLine = "\n";
        var doctype = epub3
            ? "<!DOCTYPE html>"
            : "<!DOCTYPE html PUBLIC \"-//W3C//DTD XHTML 1.1//EN\" \"http://www.w3.org/TR/xhtml11/DTD/xhtml11.dtd\">";

        var settings = new XmlWriterSettings
        {
            Encoding = Utf8NoBom,
            OmitXmlDeclaration = true,
            NewLineHandling = NewLineHandling.Replace,
            NewLineChars = newLine,
            Indent = true,
            IndentChars = "  ",
            CloseOutput = false,
        };

        AtomicFile.Write(path, stream =>
        {
            var header = Utf8NoBom.GetBytes("<?xml version=\"1.0\" encoding=\"utf-8\"?>" + newLine + doctype + newLine);
            stream.Write(header, 0, header.Length);

            using var writer = XmlWriter.Create(stream, settings);
            document.Save(writer);
        });
    }

    /// <summary>
    /// 把纯文本切成段落（一行一段，空行丢掉），直接生成良构的 XHTML 片段。
    /// </summary>
    /// <remarks>
    /// 【为什么不用 TxtChapterHtml.Build】那套是给阅读器 iframe 用的 HTML 片段
    /// （阅读器在 HTML 上下文里渲染，不是 XML），拿它拼 EPUB 章节文件会写出非良构 XML
    /// T5-1 实测踩过：导入 GBK 小说直接抛"解析失败"。章节文件必须是良构 XHTML，所以这里自己拼。
    /// </remarks>
    private static List<XNode> BuildParagraphs(string text)
    {
        var xhtml = NavDocumentBuilder.Xhtml;
        var result = new List<XNode>();
        var normalized = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');

        foreach (var line in normalized.Split('\n'))
        {
            var value = line.Trim();
            if (value.Length == 0)
            {
                continue;
            }

            result.Add(new XElement(xhtml + "p", new XText(value)));

            if (result.Count >= 20000)
            {
                break;   // 防御：畸形/超大文件别把内存吃干
            }
        }

        return result;
    }

    /// <summary>把导入的内容拆成 body 的子节点；无论输入是整份文档还是片段都能吃。</summary>
    private static List<XNode> ExtractContent(string html)
    {
        var document = TryParseXml(html);
        if (document?.Root is not null)
        {
            var body = document.Root.Descendants().FirstOrDefault(e => e.Name.LocalName == "body");
            var container = body ?? document.Root;
            return container.Nodes().Select(CloneNode).OfType<XNode>().ToList();
        }

        var wrapped = TryParseXml("<wrapper xmlns=\"" + NavDocumentBuilder.Xhtml + "\">" + html + "</wrapper>");
        if (wrapped?.Root is not null)
        {
            return wrapped.Root.Nodes().Select(CloneNode).OfType<XNode>().ToList();
        }

        throw new AppException(ErrorCode.EpubInvalid, "导入的章节不是良构的 XHTML/HTML，导入会把书弄坏", "解析失败");
    }

    private static XNode? CloneNode(XNode node) => node switch
    {
        XElement element => new XElement(element),
        XCData cdata => new XCData(cdata.Value),          // XCData 是 XText 的子类，必须排在前面
        XText text => new XText(text.Value),
        _ => null,
    };

    private static XDocument ParseXhtml(string text, string entry)
    {
        try
        {
            return XDocument.Parse(text, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException ex)
        {
            throw new AppException(
                ErrorCode.EpubInvalid,
                "这一章不是良构的 XHTML，动它会把这章弄坏",
                entry,
                ex);
        }
    }

    private static XDocument? TryParseXml(string text)
    {
        try
        {
            return XDocument.Parse(text, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static XElement? BodyOf(XDocument document)
        => document.Root?.Descendants().FirstOrDefault(e => e.Name.LocalName == "body");

    /// <summary>读导入文件：认 BOM，其次严格 UTF-8，最后按 GB18030 兜底（中文小说常见）。</summary>
    private static string DecodeText(byte[] bytes)
    {
        TextEncodingSupport.EnsureRegistered();

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return TextEncodingSupport.Resolve("GB18030")?.GetString(bytes) ?? Encoding.UTF8.GetString(bytes);
        }
    }

    private OpfMetadataEditor Open() => new(FullPath(_opfEntry), _clock);

    private static ChapterNode At(IReadOnlyList<ChapterNode> chapters, int index)
    {
        if (index < 0 || index >= chapters.Count)
        {
            throw new AppException(
                ErrorCode.ValidationFailed,
                "章节序号超出范围",
                $"index={index}，当前共 {chapters.Count} 章");
        }

        return chapters[index];
    }

    private static string RequireTitle(string title)
    {
        var value = (title ?? string.Empty).Trim();

        if (value.Length == 0)
        {
            throw new AppException(ErrorCode.ValidationFailed, "章节标题不能为空", "空标题会让目录看起来像坏了");
        }

        return value.Length > MaxTitleLength ? value[..MaxTitleLength] : value;
    }

    private static string FileNameOf(string href)
    {
        if (string.IsNullOrWhiteSpace(href))
        {
            return "未命名章节";
        }

        var name = href[(href.LastIndexOf('/') + 1)..];
        var dot = name.LastIndexOf('.');
        var title = Uri.UnescapeDataString(dot > 0 ? name[..dot] : name).Trim();
        return title.Length == 0 ? "未命名章节" : title;
    }

    private static string DirectoryOf(string entry)
    {
        var value = NormalizeEntry(entry);
        var separator = value.LastIndexOf('/');
        return separator < 0 ? string.Empty : value[..separator];
    }

    private static string NormalizeEntry(string? entry)
        => string.IsNullOrWhiteSpace(entry) ? "content.opf" : entry.Trim().Replace('\\', '/').TrimStart('/');

    private string FullPath(string entry)
        => Path.Combine(_workDir, NormalizeEntry(entry).Replace('/', Path.DirectorySeparatorChar));
}
