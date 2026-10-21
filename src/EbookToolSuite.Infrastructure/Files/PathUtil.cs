using System.Text;

namespace EbookToolSuite.Infrastructure.Files;

/// <summary>
/// 路径与文件名规范化。全工程只有这里能决定 Library/ 下的目录结构，
/// 别处不许自己 Path.Combine 拼正式区路径。
/// </summary>
public static class PathUtil
{
    /// <summary>Windows 文件名里非法的字符（含控制字符，Path 不覆盖控制字符）。</summary>
    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

    /// <summary>Windows 保留设备名。用这些名字建文件会失败或行为诡异。</summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>文件名主体（不含扩展名）的长度上限。给扩展名与去重后缀留出余量。</summary>
    private const int MaxStemLength = 120;

    /// <summary>
    /// 把任意字符串净化成一个可用的 Windows 文件名片段。
    /// 非法字符替换为下划线；去掉首尾空白与结尾的点；避开保留设备名；截断到 120 字符。
    /// 结果必定非空（空输入返回 fallback）。
    /// </summary>
    public static string SanitizeFileName(string? name, string fallback = "untitled")
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return fallback;
        }

        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            sb.Append(Array.IndexOf(InvalidChars, ch) >= 0 || char.IsControl(ch) ? '_' : ch);
        }

        // Windows 不允许文件名以点或空格结尾
        var result = sb.ToString().Trim().TrimEnd('.', ' ');

        if (result.Length == 0)
        {
            return fallback;
        }

        // CON.txt 也是保留名：只看第一个点之前的部分
        var dot = result.IndexOf('.');
        var stem = dot >= 0 ? result[..dot] : result;
        if (ReservedNames.Contains(stem))
        {
            result = "_" + result;
        }

        if (result.Length > MaxStemLength)
        {
            result = result[..MaxStemLength].TrimEnd('.', ' ');
        }

        return result.Length == 0 ? fallback : result;
    }

    /// <summary>
    /// 正式区里一本书的路径：{libraryRoot}/{bookId}/{净化后的原文件名}{扩展名}。
    /// 用 每本书一个目录 的结构（设计文档 4.1）：一本书的多个格式天然放一起，
    /// 且天然满足 book_formats 的 UNIQUE(file_path)。
    /// </summary>
    /// <param name="originalFileName">用户原始文件名，含扩展名。可为空。</param>
    /// <param name="extension">以点开头的扩展名，如 ".epub"。空则从 originalFileName 推断。</param>
    public static string BuildLibraryPath(
        string libraryRoot, Guid bookId, string? originalFileName, string? extension = null)
    {
        var ext = extension;
        var stemSource = originalFileName;

        if (string.IsNullOrEmpty(ext) && !string.IsNullOrWhiteSpace(originalFileName))
        {
            ext = Path.GetExtension(originalFileName);
            stemSource = Path.GetFileNameWithoutExtension(originalFileName);
        }
        else if (!string.IsNullOrWhiteSpace(originalFileName))
        {
            stemSource = Path.GetFileNameWithoutExtension(originalFileName);
        }

        if (string.IsNullOrEmpty(ext))
        {
            ext = ".bin";
        }

        var stem = SanitizeFileName(stemSource, bookId.ToString("N")[..8]);
        return Path.Combine(BookFolder(libraryRoot, bookId), stem + ext.ToLowerInvariant());
    }

    /// <summary>
    /// 正式区里一本书的目录：{libraryRoot}/{bookId:D}。
    /// 建路径（BuildLibraryPath）与删除（BookDeletionService）都必须走这里，
    /// 否则两边规则一漂移，删书就会留下一堆删不掉的目录。
    /// </summary>
    public static string BookFolder(string libraryRoot, Guid bookId)
        => Path.Combine(libraryRoot, bookId.ToString("D"));

    /// <summary>拼一个 .part 临时名，和正式名同目录以保证 File.Move 是纯改名（快且不跨卷）。</summary>
    public static string PartPath(string targetPath) => targetPath + ".part";

    /// <summary>绝对化并规范化大小写无关比较用的键。</summary>
    public static string Normalize(string path) => Path.GetFullPath(path);
}