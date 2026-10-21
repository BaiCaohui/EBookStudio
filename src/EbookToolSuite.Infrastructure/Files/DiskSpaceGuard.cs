using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;

namespace EbookToolSuite.Infrastructure.Files;

/// <summary>
/// 转换/导入前的磁盘空间预检。
///
/// 目的：不要等 Calibre 跑到一半才发现塞不下。
/// 那时它给的是 Python traceback（退出码 1），不是能给用户看的话。
/// 宁可提前拒绝，并给出"还差多少、怎么腾地方"。
/// </summary>
public static class DiskSpaceGuard
{
    /// <summary>需要的字节数 = 源文件大小 x factor + 固定余量（向上取整）。</summary>
    public static long RequiredBytes(long sourceBytes, double factor, long headroomBytes = 0)
    {
        var safeFactor = factor <= 0 ? 1d : factor;
        return (long)Math.Ceiling(sourceBytes * safeFactor) + Math.Max(0, headroomBytes);
    }

    /// <summary>目标目录所在卷的可用字节数。取不到就当作"无限"，不阻断业务。</summary>
    public static long GetAvailableBytes(string directory)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(directory));
            if (string.IsNullOrEmpty(root))
            {
                return long.MaxValue;
            }

            var drive = new DriveInfo(root);
            return drive.IsReady ? drive.AvailableFreeSpace : long.MaxValue;
        }
        catch (Exception)
        {
            return long.MaxValue;
        }
    }

    /// <summary>空间不足时抛 DiskFull（带"需要/可用"两个数字，UI 直接可显示）。</summary>
    public static void Ensure(
        string targetDirectory, long sourceBytes, double factor, long headroomBytes = 0)
    {
        var required = RequiredBytes(sourceBytes, factor, headroomBytes);
        var available = GetAvailableBytes(targetDirectory);
        if (available >= required)
        {
            return;
        }

        throw new AppException(
            ErrorCode.DiskFull,
            $"目标位置空间不够：这次大约需要 {Format(required)}，当前只剩 {Format(available)}。"
                + "可以清理缓存，或把数据目录换到空间更大的盘。",
            technical: $"DiskSpaceGuard required={required} available={available} dir={targetDirectory}",
            context: new Dictionary<string, object?>
            {
                ["requiredBytes"] = required,
                ["availableBytes"] = available,
                ["directory"] = targetDirectory,
            });
    }

    /// <summary>人类可读的容量文本，界面与日志共用。</summary>
    public static string Format(long bytes)
    {
        const double kb = 1024d;
        if (bytes >= kb * kb * kb)
        {
            return $"{bytes / kb / kb / kb:F2} GB";
        }

        return bytes >= kb * kb
            ? $"{bytes / kb / kb:F1} MB"
            : $"{bytes / kb:F0} KB";
    }
}
