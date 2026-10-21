using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.IO;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Engines.Epub;
using Microsoft.Extensions.Logging;

namespace EbookToolSuite.Services.Editing;

/// <summary>
/// EPUB 工作区工厂（执行计划 T3-1，设计文档 6.1.2）。
///
/// 打开流程：
///   1. 校验原文件存在且非空
///   2. DRM 拒收（只有 IDPF/Adobe 字体混淆放行，与导入路径同一套判据）
///   3. 生成 sessionId，创建 Workspaces/{sessionId}/
///   4. Zip Slip / 绝对路径 / 盘符 / 符号链接 -> 整包拒绝，一个字节都不落
///   5. 安全解压；校验 mimetype；读 container.xml -> 定位 OPF -> 取 version
///   6. 写 .session.json
///
/// 【铁律】原文件全程只读。工作区是副本；"放弃编辑"只删工作区目录。
/// 【失败即清】任何一步抛异常，都把刚建的会话目录删干净，不留半成品。
/// </summary>
public sealed class EpubWorkspaceFactory : IEpubWorkspaceFactory
{
    private const int MaxEntries = 20000;
    private const long MaxEntryBytes = 200L * 1024 * 1024;
    private const long MaxTotalBytes = 2L * 1024 * 1024 * 1024;

    internal const string SessionFileName = ".session.json";
    internal const string MimetypeEntryName = "mimetype";
    internal const string ExpectedMimetype = "application/epub+zip";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        // 中文书名不要被转成 \uXXXX，否则 .session.json 没法直接看
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IAppPaths _paths;
    private readonly IClock _clock;
    private readonly ILogger<EpubWorkspaceFactory> _log;

    /// <summary>快照服务（T5-4）。为 null 表示按旧行为走（部分单测不关心快照）。</summary>
    private readonly IWorkspaceSnapshotService? _snapshots;

    public EpubWorkspaceFactory(
        IAppPaths paths,
        IClock clock,
        ILogger<EpubWorkspaceFactory> log,
        IWorkspaceSnapshotService? snapshots = null)
    {
        _paths = paths;
        _clock = clock;
        _log = log;
        _snapshots = snapshots;
    }

    public Task<IEpubWorkspace> OpenAsync(WorkspaceOpenRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 解压是同步 IO，挪到线程池，别卡 UI 线程
        return Task.Run<IEpubWorkspace>(() => Open(request, ct), ct);
    }

    private IEpubWorkspace Open(WorkspaceOpenRequest request, CancellationToken ct)
    {
        var sourcePath = ValidateSource(request.EpubPath);

        ct.ThrowIfCancellationRequested();

        // 1. DRM：只有"算法是字体混淆且覆盖字体资源"才放行，其余一律拒收
        var encryption = EpubEncryptionInspector.Inspect(sourcePath);
        if (encryption.IsDrm)
        {
            throw new AppException(
                ErrorCode.DrmDetected,
                "这本书是加密的（算法 " + encryption.DrmAlgorithm + "，覆盖 "
                    + encryption.EncryptedResourceCount + " 个资源），本工具不处理 DRM。",
                technical: "EpubWorkspace: encryption.xml, algorithms="
                    + string.Join(", ", encryption.Algorithms),
                context: new Dictionary<string, object?> { ["sourcePath"] = sourcePath });
        }

        var sessionId = NewSessionId();
        var sessionRoot = Path.Combine(_paths.WorkspacesDir, sessionId);

        // 防御性断言：会话目录必须在工作区根之下
        var workRoot = Path.GetFullPath(_paths.WorkspacesDir) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(sessionRoot).StartsWith(workRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException(ErrorCode.InternalError, "工作区路径计算异常，已中止。");
        }

        Directory.CreateDirectory(sessionRoot);

        try
        {
            var extracted = ExtractSafe(sourcePath, sessionRoot, ct);

            var session = new WorkspaceSession
            {
                SessionId = sessionId,
                BookId = request.BookId,
                FormatId = request.FormatId,
                SourcePath = sourcePath,
                OpenedAt = _clock.Now,
                ChangedFiles = new List<string>(),
                OpfPath = extracted.OpfPath,
                EpubVersion = extracted.Version,
                HasEncryption = encryption.HasEncryptionFile,
            };

            WriteSession(sessionRoot, session);

            /* 【T5-4 判据 1】打开工作区就建一份全量基线快照：以后「还原到打开时」（一切复原）靠它。
               快照建不成就不把工作区交出去（下面的 catch 会把会话目录清干净）：
               宁可当场失败，也不要给用户一个改坏了退不回去的会话。 */
            _snapshots?.TakeFullAsync(
                new EpubWorkspace(sessionRoot, session),
                "打开工作区时的全量快照",
                ct).GetAwaiter().GetResult();

            _log.LogInformation(
                "工作区已打开：{SessionId}，源 {Source}，{Count} 个条目，OPF {Opf}",
                sessionId, sourcePath, extracted.EntryCount, extracted.OpfPath);

            return new EpubWorkspace(sessionRoot, session);
        }
        catch
        {
            // 不留半成品会话目录；清理失败也不掩盖原始异常
            try
            {
                DeleteDirectory(sessionRoot);
            }
            catch
            {
                // ignored
            }

            throw;
        }
    }

    // ---------- 解压 ----------

    private sealed record Extracted(string? OpfPath, string Version, int EntryCount);

    private Extracted ExtractSafe(string sourcePath, string sessionRoot, CancellationToken ct)
    {
        var root = Path.GetFullPath(sessionRoot) + Path.DirectorySeparatorChar;

        using var fs = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);

        // ---- 第一遍：整包拒绝任何不安全条目，一个字节都不落 ----
        if (zip.Entries.Count > MaxEntries)
        {
            throw new AppException(
                ErrorCode.CorruptFile,
                "这个 EPUB 的条目数（" + zip.Entries.Count + "）异常多，已拒绝打开。",
                technical: "EpubWorkspace: entries=" + zip.Entries.Count + " > " + MaxEntries);
        }

        foreach (var entry in zip.Entries)
        {
            if (EpubPackageIndex.IsUnsafeEntryName(entry.FullName) || IsSymlinkEntry(entry))
            {
                throw new AppException(
                    ErrorCode.UnsafeArchiveEntry,
                    "压缩包有非法路径：" + entry.FullName + "。这本 EPUB 可能被篡改过，已拒绝打开。",
                    technical: "EpubWorkspace: unsafe zip entry '" + entry.FullName + "'",
                    context: new Dictionary<string, object?>
                    {
                        ["entry"] = entry.FullName,
                        ["sourcePath"] = sourcePath,
                    });
            }
        }

        // ---- 结构探测：container.xml -> OPF -> version（复用导入路径的索引器）----
        var index = EpubPackageIndex.TryRead(zip, out var reason);
        if (index is null)
        {
            throw new AppException(
                ErrorCode.EpubInvalid,
                "不是有效的 EPUB 文件：" + reason,
                technical: "EpubWorkspace: " + reason,
                context: new Dictionary<string, object?> { ["sourcePath"] = sourcePath });
        }

        ct.ThrowIfCancellationRequested();

        // ---- 第二遍：安全解压 ----
        var count = 0;
        long total = 0;
        string? mimetype = null;

        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();

            // 目录项没有内容
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                continue;
            }

            if (entry.Length > MaxEntryBytes)
            {
                throw new AppException(
                    ErrorCode.CorruptFile,
                    "压缩包里的 " + entry.FullName + " 解压后过大（超过 "
                        + (MaxEntryBytes / 1024 / 1024) + " MB），已拒绝打开。",
                    technical: "EpubWorkspace: entry '" + entry.FullName + "' length=" + entry.Length);
            }

            total += entry.Length;
            if (total > MaxTotalBytes)
            {
                throw new AppException(
                    ErrorCode.CorruptFile,
                    "这本 EPUB 解压后体积异常大，已拒绝打开（可能是压缩炸弹）。",
                    technical: "EpubWorkspace: total uncompressed > " + MaxTotalBytes);
            }

            var relative = EpubPackageIndex.NormalizeEntryPath(entry.FullName)
                .Replace('/', Path.DirectorySeparatorChar);
            var target = Path.GetFullPath(Path.Combine(sessionRoot, relative));

            // 双保险：算出来的绝对路径必须仍在工作区内
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new AppException(
                    ErrorCode.UnsafeArchiveEntry,
                    "压缩包有非法路径：" + entry.FullName + "。已拒绝打开。",
                    technical: "EpubWorkspace: resolved path escaped workspace: " + target);
            }

            var parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            using (var input = entry.Open())
            using (var output = new FileStream(
                target, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 81920))
            {
                input.CopyTo(output, 81920);
            }

            if (string.Equals(entry.FullName, MimetypeEntryName, StringComparison.OrdinalIgnoreCase))
            {
                mimetype = ReadMimetype(target);
            }

            count++;
        }

        // mimetype 不符只告警，不拦（有些老书不规范，保存时由打包器修正）
        if (mimetype is null)
        {
            _log.LogWarning("工作区缺少 mimetype 条目：{Source}", sourcePath);
        }
        else if (!string.Equals(mimetype, ExpectedMimetype, StringComparison.Ordinal))
        {
            _log.LogWarning(
                "mimetype 内容不是 {Expected}，而是 {Actual}：{Source}",
                ExpectedMimetype, mimetype, sourcePath);
        }

        var opfPath = EpubPackageIndex.NormalizeEntryPath(index.OpfPath);
        return new Extracted(opfPath, index.Version ?? "2.0", count);
    }

    // ---------- 恢复 ----------

    public async Task<IEpubWorkspace?> ResumeAsync(string sessionId, CancellationToken ct = default)
    {
        if (!IsSafeSessionId(sessionId))
        {
            throw new AppException(
                ErrorCode.InvalidPath,
                "会话号不合法。",
                technical: "EpubWorkspace: bad sessionId '" + sessionId + "'");
        }

        var sessionRoot = Path.Combine(_paths.WorkspacesDir, sessionId);
        var session = TryReadSession(sessionRoot);
        if (session is null)
        {
            return null;
        }

        var workspace = new EpubWorkspace(sessionRoot, session);

        /* 【T5-4 判据 1 补漏】旧会话（T5-4 之前建的、或上一版没补过基线的）也要有一份全量基线：
           否则"历史"里一条都没有、也没法"一切复原"。只在确实没有基线时补，有就不动。
           补失败只记日志、不拦着打开工作区（新建会话那条路才是"建不成就失败"
           那时候还没有任何可丢的东西）。 */
        if (_snapshots is not null)
        {
            try
            {
                var existing = await _snapshots.ListAsync(workspace, ct).ConfigureAwait(false);
                var hasBaseline = false;

                foreach (var snapshot in existing)
                {
                    if (snapshot.Kind == WorkspaceSnapshotKinds.Initial)
                    {
                        hasBaseline = true;
                        break;
                    }
                }

                if (!hasBaseline)
                {
                    await _snapshots
                        .TakeFullAsync(workspace, "打开工作区时的全量快照", ct)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception error)
            {
                _log.LogWarning(error, "给已有会话补全量基线快照失败：{SessionId}", sessionId);
            }
        }

        return workspace;
    }

    public IReadOnlyList<WorkspaceSession> ListResumable()
    {
        if (!Directory.Exists(_paths.WorkspacesDir))
        {
            return Array.Empty<WorkspaceSession>();
        }

        var list = new List<WorkspaceSession>();
        foreach (var dir in Directory.EnumerateDirectories(_paths.WorkspacesDir))
        {
            var session = TryReadSession(dir);
            if (session is not null)
            {
                list.Add(session);
            }
        }

        return list.OrderByDescending(s => s.OpenedAt).ToList();
    }

    // ---------- 小工具 ----------

    private string NewSessionId()
    {
        for (var i = 0; i < 16; i++)
        {
            var id = Guid.NewGuid().ToString("N")[..8];
            if (!Directory.Exists(Path.Combine(_paths.WorkspacesDir, id)))
            {
                return id;
            }
        }

        throw new AppException(ErrorCode.InternalError, "无法分配工作区会话号，请重试。");
    }

    private static string ValidateSource(string epubPath)
    {
        if (string.IsNullOrWhiteSpace(epubPath))
        {
            throw new AppException(
                ErrorCode.InvalidPath, "没有指定要打开的 EPUB 文件。", "EpubWorkspace: empty path");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(epubPath);
        }
        catch (Exception ex)
        {
            throw new AppException(
                ErrorCode.InvalidPath,
                "文件路径不合法：" + epubPath,
                technical: ex.GetType().Name + ": " + ex.Message,
                inner: ex);
        }

        FileInfo fi;
        try
        {
            fi = new FileInfo(fullPath);
        }
        catch (Exception ex)
        {
            throw new AppException(
                ErrorCode.InvalidPath,
                "无法访问这个文件：" + fullPath,
                technical: ex.GetType().Name + ": " + ex.Message,
                inner: ex);
        }

        if (!fi.Exists)
        {
            throw new AppException(
                ErrorCode.FileNotFound, "文件不存在：" + fullPath,
                technical: "EpubWorkspace: FileInfo.Exists == false");
        }

        if (fi.Length == 0)
        {
            throw new AppException(
                ErrorCode.CorruptFile, "这是一个 0 字节的文件，不是有效的 EPUB。",
                technical: "EpubWorkspace: file length == 0");
        }

        return fullPath;
    }

    public static void WriteSession(string sessionRoot, WorkspaceSession session)
    {
        var json = JsonSerializer.Serialize(session, JsonOptions);
        AtomicFile.WriteAllText(
            Path.Combine(sessionRoot, SessionFileName),
            json,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    internal static WorkspaceSession? TryReadSession(string sessionRoot)
    {
        try
        {
            var file = Path.Combine(sessionRoot, SessionFileName);
            if (!File.Exists(file))
            {
                return null;
            }

            var json = File.ReadAllText(file);
            return JsonSerializer.Deserialize<WorkspaceSession>(json, JsonOptions);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsSafeSessionId(string? id)
        => !string.IsNullOrEmpty(id)
           && id.Length <= 64
           && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>符号链接条目（Unix 文件模式 S_IFLNK 在高 16 位）。Windows 打包的 zip 这里是 0。</summary>
    private static bool IsSymlinkEntry(ZipArchiveEntry entry)
    {
        var mode = (entry.ExternalAttributes >> 16) & 0xF000;
        return mode == 0xA000;
    }

    private static string ReadMimetype(string path)
    {
        try
        {
            return File.ReadAllText(path, Encoding.ASCII).Trim();
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>递归删除目录，给杀软/索引器短暂占用留 4 次重试。</summary>
    internal static void DeleteDirectory(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return;
        }

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 4)
            {
                Thread.Sleep(50 * (attempt + 1));
            }
        }
    }
}
