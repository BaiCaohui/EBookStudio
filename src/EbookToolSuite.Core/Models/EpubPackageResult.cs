namespace EbookToolSuite.Core.Models;

/// <summary>
/// 打包结果（T3-4）。
/// <see cref="BackupPath"/> 是打包器自己的备份（只有调用方指定了 backupDirectory 时才非空）；
/// 保存闭环（T3-6）用的是还原点，见 <see cref="RestorePointId"/>。
/// </summary>
/// <param name="OutputPath">成品 EPUB 的绝对路径。</param>
/// <param name="BackupPath">打包器留下的备份文件路径；调用方没要备份时为 null。</param>
/// <param name="EntryCount">打进包的条目数（不含 mimetype）。</param>
/// <param name="ByteCount">成品字节数。</param>
/// <param name="Duration">打包 + 自检 + 原子替换的总耗时。</param>
/// <param name="RestorePointId">
/// 本次保存**替换前**自动创建的还原点 id（T3-6）；没建还原点（比如目标文件还不存在）时为 null。
/// </param>
public sealed record EpubPackageResult(
    string OutputPath,
    string? BackupPath,
    int EntryCount,
    long ByteCount,
    TimeSpan Duration,
    string? RestorePointId = null)
{
    /// <summary>是否留下了可回滚的备份文件。</summary>
    public bool HasBackup => BackupPath is not null;
}
