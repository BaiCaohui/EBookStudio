namespace EbookToolSuite.Core.Models;

/// <summary>
/// 图片文件夹打成 CBZ 的结果。
/// </summary>
/// <param name="OutputPath">产物绝对路径。</param>
/// <param name="ImageCount">真的写进包里的图片数。</param>
/// <param name="SizeBytes">产物字节数。</param>
/// <param name="Duration">耗时。</param>
/// <param name="Skipped">被跳过的文件名（不是图片、或者读不出来），最多记 50 条。</param>
public sealed record CbzPackResult(
    string OutputPath,
    int ImageCount,
    long SizeBytes,
    TimeSpan Duration,
    IReadOnlyList<string> Skipped)
{
    /// <summary>被跳过的文件数（Skipped 只留前 50 条，计数用它）。</summary>
    public int SkippedCount { get; init; }

    /// <summary>给界面用的一句话结果。</summary>
    public string Summary => SkippedCount == 0
        ? $"已打包 {ImageCount} 张图片，{FormatSize(SizeBytes)}。"
        : $"已打包 {ImageCount} 张图片，{FormatSize(SizeBytes)}；跳过 {SkippedCount} 个文件。";

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
        {
            return (bytes / 1024d / 1024 / 1024).ToString("0.##") + " GB";
        }

        if (bytes >= 1024 * 1024)
        {
            return (bytes / 1024d / 1024).ToString("0.##") + " MB";
        }

        return bytes >= 1024
            ? (bytes / 1024d).ToString("0.#") + " KB"
            : bytes + " 字节";
    }
}
