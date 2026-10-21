namespace EbookToolSuite.Core.Abstractions;

/// <summary>
/// 展开用户选择的结果（执行计划 T2-1："递归扫描一个含 200 个文件的目录"）。
/// 只做路径层面的收录，**不读文件内容、不碰数据库**。
/// </summary>
public sealed record ImportScanResult
{
    /// <summary>待导入的源文件绝对路径，已去重、已按路径排序（顺序稳定才好复现问题）。</summary>
    public List<string> Files { get; init; } = new();

    /// <summary>扩展名不在白名单内而跳过的文件数（例如同目录下的 .jpg / .opf）。</summary>
    public int SkippedUnsupported { get; init; }

    /// <summary>扫描时读不到的路径数（权限、文件被独占、坏符号链接）。</summary>
    public int SkippedUnreadable { get; init; }

    /// <summary>扫描过的目录数，用于日志与"扫了 12 个文件夹"这类提示。</summary>
    public int FoldersScanned { get; init; }

    /// <summary>用户选的顶层路径里，不存在的那些（提示用，不阻断）。</summary>
    public List<string> MissingPaths { get; init; } = new();

    /// <summary>扫描是否被取消。</summary>
    public bool Canceled { get; init; }

    public int Count => Files.Count;

    /// <summary>给对话框的一句话，例如"找到 200 个文件，跳过 13 个非电子书文件"。</summary>
    public string Summary
    {
        get
        {
            var text = "找到 " + Files.Count + " 个文件";
            if (SkippedUnsupported > 0)
            {
                text += "，跳过 " + SkippedUnsupported + " 个非电子书文件";
            }

            if (SkippedUnreadable > 0)
            {
                text += "，" + SkippedUnreadable + " 个读不到";
            }

            if (FoldersScanned > 0)
            {
                text += "（扫描 " + FoldersScanned + " 个文件夹）";
            }

            return text + "。";
        }
    }
}