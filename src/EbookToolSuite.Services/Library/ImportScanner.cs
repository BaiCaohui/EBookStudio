using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace EbookToolSuite.Services.Library;

/// <summary>
/// 把用户选的文件与文件夹展开成待导入的源文件列表（执行计划 T2-1）。
/// 纯路径操作：不读内容、不查库、无副作用。
/// </summary>
/// <remarks>
/// 【为什么不用 Directory.EnumerateFiles(recursive)】它遇到一个没权限的子目录会从
/// MoveNext 里抛异常，整个扫描就断了：200 本书的目录里混一个系统文件夹就全废。
/// 这里改成显式栈式遍历，一个目录读不到只记一笔、继续走别的；同时跳过重解析点
/// （符号链接、联接点），避免目录环把扫描卡死。
/// </remarks>
public sealed class ImportScanner : IImportScanner
{
    private readonly ILogger<ImportScanner> _log;

    public ImportScanner(ILogger<ImportScanner> log)
    {
        _log = log;
    }

    public ImportScanResult Scan(
        IReadOnlyList<string> paths, bool recursive, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        var skippedUnsupported = 0;
        var skippedUnreadable = 0;
        var foldersScanned = 0;

        foreach (var raw in paths)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            string full;
            try
            {
                full = Path.GetFullPath(raw);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                missing.Add(raw);
                continue;
            }

            if (File.Exists(full))
            {
                Accept(full, files, seen, ref skippedUnsupported);
            }
            else if (Directory.Exists(full))
            {
                Walk(full, recursive, files, seen,
                    ref skippedUnsupported, ref skippedUnreadable, ref foldersScanned, ct);
            }
            else
            {
                missing.Add(raw);
            }
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);

        var result = new ImportScanResult
        {
            Files = files,
            SkippedUnsupported = skippedUnsupported,
            SkippedUnreadable = skippedUnreadable,
            FoldersScanned = foldersScanned,
            MissingPaths = missing,
        };

        _log.LogInformation("扫描完成：" + result.Summary + "，缺失路径 " + missing.Count + " 个");
        return result;
    }
    private void Walk(
        string root,
        bool recursive,
        List<string> files,
        HashSet<string> seen,
        ref int skippedUnsupported,
        ref int skippedUnreadable,
        ref int foldersScanned,
        CancellationToken ct)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            foldersScanned++;

            foreach (var entry in ReadDirectory(dir, ref skippedUnreadable))
            {
                ct.ThrowIfCancellationRequested();

                var attrs = TryGetAttributes(entry);
                if (attrs is null)
                {
                    skippedUnreadable++;
                    continue;
                }

                if ((attrs.Value & FileAttributes.ReparsePoint) != 0)
                {
                    // 符号链接 / 联接点一律不进：目录环会让扫描无限跑下去。
                    continue;
                }

                if ((attrs.Value & FileAttributes.Directory) != 0)
                {
                    if (recursive)
                    {
                        stack.Push(entry);
                    }

                    continue;
                }

                Accept(entry, files, seen, ref skippedUnsupported);
            }
        }
    }

    /// <summary>
    /// 把一个目录的条目读进列表。必须缓冲：Directory.EnumerateFileSystemEntries 是惰性的，
    /// 访问中途抛的异常会从 MoveNext 里冒出来，不缓冲就没法在"这一个目录"的层面兜住。
    /// </summary>
    private List<string> ReadDirectory(string dir, ref int skippedUnreadable)
    {
        var buffer = new List<string>();
        IEnumerator<string>? enumerator = null;
        try
        {
            enumerator = Directory.EnumerateFileSystemEntries(dir).GetEnumerator();
            while (enumerator.MoveNext())
            {
                buffer.Add(enumerator.Current);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException
            or System.Security.SecurityException or DirectoryNotFoundException)
        {
            skippedUnreadable++;
            _log.LogWarning("目录读不到，已跳过：" + dir + "（" + ex.GetType().Name + "）");
        }
        finally
        {
            enumerator?.Dispose();
        }

        return buffer;
    }

    private static FileAttributes? TryGetAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException
            or System.Security.SecurityException or FileNotFoundException
            or DirectoryNotFoundException or PathTooLongException)
        {
            return null;
        }
    }

    private static void Accept(
        string file, List<string> files, HashSet<string> seen, ref int skippedUnsupported)
    {
        if (BookFormatKinds.FromExtension(file) == BookFormatKind.Unknown)
        {
            skippedUnsupported++;
            return;
        }

        if (seen.Add(file))
        {
            files.Add(file);
        }
    }
}