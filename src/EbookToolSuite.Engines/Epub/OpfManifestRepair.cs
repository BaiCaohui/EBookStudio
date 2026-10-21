using EbookToolSuite.Core.Models;

namespace EbookToolSuite.Engines.Epub;

/// <summary>补 manifest 的结果：补了哪些 href、跳过了哪些文件（认不出类型或本来就不该进 manifest）。</summary>
public sealed record ManifestRepairResult(
    IReadOnlyList<string> AddedHrefs,
    IReadOnlyList<string> SkippedFiles)
{
    public int AddedCount => AddedHrefs.Count;
}

/// <summary>
/// 把工作区里"在包里、但 OPF 的 manifest 没声明"的文件补进 manifest。
///
/// 【为什么需要它 / 实测依据】EpubCheck 5.4.0 对三种情况的态度（2026-10-08 逐条实测）：
///    文件在包里、**没人引用**、也没声明     -> 0 error 0 warning（它根本不管）；
///    文件被章节引用、但 manifest 没声明     -> <c>RSC-008 Referenced resource "" is not declared in the OPF manifest.</c>（error，带 path:line）；
///    OPF 声明了文件、文件不在包里           -> <c>RSC-001 File "" could not be found.</c>（error）。
/// 计划文档把第二种叫 <c>OPF-003</c>，实际跑出来是 <c>RSC-008</c>所以这里**不按错误号判断**，
/// 只按"工作区里确实有未声明的文件"这个事实干活。
///
/// 【不做的事】META-INF/ 下的 OCF 文件与 OPF 自身**不该**进 manifest（规范如此）；
/// 认不出介质类型的扩展名一律跳过并报告，绝不瞎猜一个 media-type 写进去。
/// </summary>
public static class OpfManifestRepair
{
    /// <summary>META-INF 下的都是 OCF 层面的文件，不是 publication resource。</summary>
    public const string MetaInfDirectoryName = "META-INF";

    /// <summary>只扫描、不修改：返回"该补"的 href（相对 OPF 所在目录，用 / 分隔）。</summary>
    public static IReadOnlyList<string> FindUnmanifested(string opfPath, string workDir)
    {
        var (editor, opfDirectory, declared, _) = Scan(opfPath, workDir);
        _ = editor;

        var missing = new List<string>();
        foreach (var file in EnumerateCandidates(workDir))
        {
            var relativeToOpf = RelativeToOpf(opfDirectory, file, workDir, out var relativeToWorkDir);
            if (relativeToOpf is null)
            {
                continue;
            }

            if (EpubPackager.IsPackagerGenerated(relativeToWorkDir))
            {
                continue;
            }

            if (declared.Contains(relativeToOpf))
            {
                continue;
            }

            if (MediaTypeFor(Path.GetExtension(file)) is null)
            {
                continue;
            }

            missing.Add(relativeToOpf);
        }

        return missing;
    }

    /// <summary>
    /// 只扫描、不修改：返回"OPF 声明了、但工作区里找不到对应文件"的那些 href。
    /// 对应 EpubCheck 的 <c>RSC-001 File "" could not be found.</c>（error）。
    /// </summary>
    public static IReadOnlyList<string> FindDangling(string opfPath, string workDir)
    {
        var (editor, opfDirectory, _, _) = Scan(opfPath, workDir);
        var dangling = new List<string>();

        foreach (var item in editor.ReadManifest())
        {
            if (ResolveInside(opfDirectory, item.Href, workDir) is { } path && !File.Exists(path))
            {
                dangling.Add(item.Href);
            }
        }

        return dangling;
    }

    /// <summary>
    /// 第二类修复：把"声明了但文件不在包里"的条目从 manifest（以及 spine 的 itemref）里删掉。
    /// 这是设计文档 9.4.6 说的"移除引用"，属于安全确定的修复留着它只会让 EpubCheck 一直报 RSC-001。
    /// </summary>
    public static IReadOnlyList<string> RemoveDangling(string opfPath, string workDir)
    {
        var (editor, opfDirectory, _, _) = Scan(opfPath, workDir);
        var removed = new List<string>();

        foreach (var item in editor.ReadManifest())
        {
            if (ResolveInside(opfDirectory, item.Href, workDir) is not { } path || File.Exists(path))
            {
                continue;
            }

            if (editor.RemoveManifestItem(item.Id))
            {
                removed.Add(string.IsNullOrWhiteSpace(item.Href) ? item.Id : item.Href);
            }
        }

        if (removed.Count > 0)
        {
            editor.Save();
        }

        return removed;
    }

    /// <summary>href 解析成工作区内的绝对路径；越界或空返回 null。</summary>
    private static string? ResolveInside(string opfDirectory, string? href, string workDir)
    {
        if (string.IsNullOrWhiteSpace(href) || href.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var decoded = href.Replace('\\', '/').Trim();
        try
        {
            decoded = Uri.UnescapeDataString(decoded);
        }
        catch (UriFormatException)
        {
            // 转义坏掉就按原样拼
        }

        try
        {
            var combined = Path.GetFullPath(Path.Combine(opfDirectory, decoded.Replace('/', Path.DirectorySeparatorChar)));
            return IsInside(workDir, combined) ? combined : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>把该补的文件补进 manifest 并落盘。返回补了哪些、跳过了哪些。</summary>
    public static ManifestRepairResult Repair(string opfPath, string workDir)
    {
        var (editor, opfDirectory, declared, existingIds) = Scan(opfPath, workDir);
        var added = new List<string>();
        var skipped = new List<string>();
        var sequence = 0;

        foreach (var file in EnumerateCandidates(workDir))
        {
            var relativeToOpf = RelativeToOpf(opfDirectory, file, workDir, out var relativeToWorkDir);
            if (relativeToOpf is null)
            {
                continue;
            }

            // 打包器自己生成的东西（mimetype / .session.json / .snapshots / 临时文件）永不进 manifest
            if (EpubPackager.IsPackagerGenerated(relativeToWorkDir))
            {
                continue;
            }

            if (declared.Contains(relativeToOpf))
            {
                continue;
            }

            var mediaType = MediaTypeFor(Path.GetExtension(file));
            if (mediaType is null)
            {
                skipped.Add(relativeToOpf + "（认不出介质类型）");
                continue;
            }

            string id;
            do
            {
                sequence++;
                id = "repair-" + sequence;
            }
            while (!existingIds.Add(id));

            editor.UpsertManifestItem(new OpfManifestItem(id, relativeToOpf, mediaType), properties: null);
            declared.Add(relativeToOpf);
            added.Add(relativeToOpf);
        }

        if (added.Count > 0)
        {
            editor.Save();
        }

        return new ManifestRepairResult(added, skipped);
    }

    /// <summary>扩展名 -> media-type。认不出来返回 null（宁可不补，也不瞎写）。</summary>
    public static string? MediaTypeFor(string? extension)
        => extension?.ToLowerInvariant() switch
        {
            ".xhtml" => "application/xhtml+xml",
            ".html" or ".htm" => "application/xhtml+xml",
            ".css" => "text/css",
            ".js" => "text/javascript",
            ".ncx" => "application/x-dtbncx+xml",
            ".xml" => "application/xml",
            ".txt" => "text/plain",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".webp" => "image/webp",
            ".ttf" => "font/ttf",
            ".otf" => "font/otf",
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            ".mp3" => "audio/mpeg",
            ".m4a" => "audio/mp4",
            ".mp4" => "video/mp4",
            _ => null,
        };

    private static (OpfMetadataEditor Editor, string OpfDirectory, HashSet<string> Declared, HashSet<string> Ids) Scan(
        string opfPath,
        string workDir)
    {
        if (string.IsNullOrWhiteSpace(opfPath) || !File.Exists(opfPath))
        {
            throw new FileNotFoundException("找不到 OPF，无法补 manifest。", opfPath);
        }

        var full = Path.GetFullPath(opfPath);
        var opfDirectory = Path.GetDirectoryName(full)
            ?? throw new InvalidOperationException("OPF 路径没有目录部分：" + full);

        var prefixes = new[] { Path.GetFullPath(workDir), opfDirectory };
        Array.Sort(prefixes, (left, right) => right.Length.CompareTo(left.Length));

        var editor = new OpfMetadataEditor(full);
        editor.Read();

        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in editor.ReadManifest())
        {
            ids.Add(item.Id);
            var normalized = NormalizeHref(item.Href, opfDirectory);
            if (normalized is not null)
            {
                declared.Add(normalized);
            }
        }

        return (editor, opfDirectory, declared, ids);
    }

    /// <summary>列出工作区里所有文件（跳过 META-INF/ 与 OPF 自身它们不该进 manifest）。</summary>
    private static IEnumerable<string> EnumerateCandidates(string workDir)
    {
        var root = Path.GetFullPath(workDir);
        if (!Directory.Exists(root))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.StartsWith(MetaInfDirectoryName + "/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (file.EndsWith(".opf", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return file;
        }
    }

    /// <summary>文件相对 OPF 目录的 href（用 / 分隔）；越界（跑到工作区外）返回 null。</summary>
    private static string? RelativeToOpf(string opfDirectory, string file, string workDir, out string relativeToWorkDir)
    {
        relativeToWorkDir = Path.GetRelativePath(Path.GetFullPath(workDir), file).Replace('\\', '/');
        var relative = Path.GetRelativePath(opfDirectory, file).Replace('\\', '/');
        if (relative.StartsWith("../", StringComparison.Ordinal) && !IsInside(workDir, file))
        {
            return null;
        }

        return relative;
    }

    private static bool IsInside(string directory, string file)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return file.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>把 manifest 里可能带 % 转义或 ./ 的 href 归一成相对 OPF 目录的路径。</summary>
    private static string? NormalizeHref(string? href, string opfDirectory)
    {
        if (string.IsNullOrWhiteSpace(href))
        {
            return null;
        }

        var decoded = href.Replace('\\', '/').Trim();
        try
        {
            decoded = Uri.UnescapeDataString(decoded);
        }
        catch (UriFormatException)
        {
            // 转义坏掉就按原样比
        }

        try
        {
            var combined = Path.GetFullPath(Path.Combine(opfDirectory, decoded.Replace('/', Path.DirectorySeparatorChar)));
            return Path.GetRelativePath(opfDirectory, combined).Replace('\\', '/');
        }
        catch (ArgumentException)
        {
            return decoded;
        }
    }
}
