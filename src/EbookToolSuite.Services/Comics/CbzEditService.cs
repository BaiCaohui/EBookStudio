using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.IO;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Engines.Comics;
using Microsoft.Extensions.Logging;

namespace EbookToolSuite.Services.Comics;

/// <summary>
/// 编辑现有 CBZ：解压到临时工作区，改完再重新打包覆盖原文件。
/// </summary>
/// <remarks>
/// <para>
/// 【为什么解压到工作区而不是在内存里改】编辑窗（<c>CbzPackEditWindow</c>）只认
/// 「一个图片文件夹 + 一份元数据」：它要逐张预览、改名、拖序。给它真实文件最省事，
/// 也复用了「新建 CBZ」那条完全相同的 UI 与打包路径。
/// </para>
/// <para>
/// 【保存为什么先备份】这是就地替换用户书库里的文件，属于不可逆操作。
/// 与 Calibre polish 就地替换同一口径：先 <c>AtomicFile.Copy</c> 到
/// <c>Data/Backup/Files/</c>，再原子替换，中途失败原文件仍在。
/// </para>
/// <para>
/// 【只支持 CBZ】CBR 是 rar；用 zip 重打包等于把格式偷偷改了，不在本服务的范围。
/// </para>
/// </remarks>
public sealed class CbzEditService : ICbzEditService
{
    /// <summary>包内元数据文件名（与打包/探测共用同一个常量，避免三处各写一遍字符串）。</summary>
    private static readonly string ComicInfoEntryName = ComicInfoXml.EntryName;

    private readonly ICbzPacker _packer;
    private readonly IAppPaths _paths;
    private readonly ILogger<CbzEditService> _log;

    public CbzEditService(ICbzPacker packer, IAppPaths paths, ILogger<CbzEditService> log)
    {
        _packer = packer;
        _paths = paths;
        _log = log;
    }

    public async Task<CbzEditWorkspace> OpenAsync(string cbzPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cbzPath);

        var workspaceDir = Path.Combine(
            _paths.WorkspacesDir, "cbz-" + Guid.NewGuid().ToString("N")[..12]);
        var folder = Path.Combine(workspaceDir, "images");

        Directory.CreateDirectory(folder);

        try
        {
            using var archive = ComicArchive.Open(cbzPath);

            var metadata = ReadMetadata(archive);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var images = new List<string>(archive.PageCount);

            for (var i = 0; i < archive.PageCount; i++)
            {
                ct.ThrowIfCancellationRequested();

                // 包内可能是 "第1话/001.jpg" 这种带子目录的条目：拍平成文件名。
                // 编辑窗与打包器都用「文件名自然序」表达页序，子路径没有位置可放。
                var name = UniqueName(FlattenName(archive.PageKeys[i]), used);
                var target = Path.Combine(folder, name);

                await File.WriteAllBytesAsync(target, archive.ReadPage(i), ct).ConfigureAwait(false);
                images.Add(target);
            }

            _log.LogInformation(
                "CBZ 编辑工作区已解压：{Path} -> {Dir}（{Count} 张）", cbzPath, folder, images.Count);

            return new CbzEditWorkspace
            {
                CbzPath = Path.GetFullPath(cbzPath),
                WorkspaceDir = workspaceDir,
                Folder = folder,
                Images = images,
                Metadata = metadata,
            };
        }
        catch
        {
            // 解压到一半失败（坏页/磁盘满）：不能留下半个工作区
            TryDeleteDirectory(workspaceDir);
            throw;
        }
    }

    public async Task<CbzEditSaveResult> SaveAsync(
        CbzEditWorkspace workspace,
        IReadOnlyList<CbzPackEntry> entries,
        ComicMetadata metadata,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(metadata);

        if (!File.Exists(workspace.CbzPath))
        {
            throw new AppException(
                ErrorCode.FileNotFound,
                "这本书的 CBZ 文件不在了，可能被移动或删除了。",
                technical: "cbz not found: " + workspace.CbzPath);
        }

        // 先打到工作区里（同一卷、不会碰原文件），成功后才备份 + 替换。
        var tempOutput = Path.Combine(workspace.WorkspaceDir, "repack.cbz");
        var packed = await _packer
            .PackAsync(entries, metadata, tempOutput, progress: null, ct)
            .ConfigureAwait(false);

        Directory.CreateDirectory(_paths.BackupFilesDir);

        var backupPath = Path.Combine(
            _paths.BackupFilesDir,
            DateTime.Now.ToString("yyyyMMddHHmmss") + "_" + Path.GetFileName(workspace.CbzPath) + ".bak");
        AtomicFile.Copy(workspace.CbzPath, backupPath);

        AtomicFile.Copy(tempOutput, workspace.CbzPath);

        var size = new FileInfo(workspace.CbzPath).Length;

        _log.LogInformation(
            "CBZ 已就地重打包：{Path}（{Images} 张，{Bytes} 字节，备份 {Backup}）",
            workspace.CbzPath, packed.ImageCount, size, backupPath);

        return new CbzEditSaveResult(workspace.CbzPath, packed.ImageCount, size, packed.Duration);
    }

    public void Cleanup(CbzEditWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        TryDeleteDirectory(workspace.WorkspaceDir);
    }

    /// <summary>读包内 ComicInfo.xml。没有 / 解析失败都返回空对象（编辑窗照常打开）。</summary>
    private static ComicMetadata ReadMetadata(ComicArchive archive)
    {
        if (archive.TryReadTextEntry(ComicInfoEntryName, out var xml)
            && ComicInfoXml.TryParse(xml, out var parsed))
        {
            return parsed;
        }

        return new ComicMetadata();
    }

    /// <summary>把 <c>a/b/c.jpg</c> 拍成 <c>c.jpg</c>。</summary>
    private static string FlattenName(string entryKey)
    {
        var normalized = entryKey.Replace('\\', '/');
        var slash = normalized.LastIndexOf('/');
        return slash >= 0 ? normalized[(slash + 1)..] : normalized;
    }

    /// <summary>拍平后可能撞名（a/1.jpg 与 b/1.jpg）：加 (2)、(3) 后缀保证唯一。</summary>
    private static string UniqueName(string name, HashSet<string> used)
    {
        if (used.Add(name))
        {
            return name;
        }

        var extension = Path.GetExtension(name);
        var stem = Path.GetFileNameWithoutExtension(name);

        for (var n = 2; ; n++)
        {
            var candidate = stem + " (" + n + ")" + extension;

            if (used.Add(candidate))
            {
                return candidate;
            }
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 工作区删不掉不致命：启动清理会扫 Data/Workspaces 下的残留
        }
    }
}
