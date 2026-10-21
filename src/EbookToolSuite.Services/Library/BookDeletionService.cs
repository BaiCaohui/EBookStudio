using EbookToolSuite.Core;
using EbookToolSuite.Core.Errors;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Infrastructure.Files;
using Microsoft.Extensions.Logging;

namespace EbookToolSuite.Services.Library;

/// <summary>
/// 从书库里删除书：数据库记录 + 正式区文件 + 两档封面 + 阅读缓存。
/// 规格见执行计划 T1-3 的补丁需求（单本删除与批量删除）。
/// </summary>
/// <remarks>
/// 顺序是「先删库、再删文件」，不是反过来。理由：
/// 用户要的是「这本书从书库里消失」，库记录才是主契约；
/// 反过来先删文件，万一写库失败就留下一行指向不存在文件的书（界面上一本打不开的书）。
/// 文件删不掉的后果轻得多（占点磁盘），而且会作为 Warning 逐本汇报给用户。
///
/// 本类**只负责删，不负责问**：二次确认在界面层（LibraryViewModel），
/// 因为「要不要弹对话框」是界面决策，不该让服务层碰 MessageBox。
/// </remarks>
public sealed class BookDeletionService : IBookDeletionService
{
    private readonly IBookRepository _repo;
    private readonly IAppPaths _paths;
    private readonly ILogger<BookDeletionService> _log;

    public BookDeletionService(IBookRepository repo, IAppPaths paths, ILogger<BookDeletionService> log)
    {
        _repo = repo;
        _paths = paths;
        _log = log;
    }

    public async Task<BookDeleteReport> DeleteAsync(
        IReadOnlyList<Guid> bookIds, CancellationToken ct = default)
    {
        var outcomes = new List<BookDeleteOutcome>();

        // 同一本被勾两次只删一次（批量选择里 Ctrl+A 之类很容易出现重复）
        foreach (var id in bookIds.Distinct())
        {
            ct.ThrowIfCancellationRequested();
            outcomes.Add(await DeleteOneAsync(id, ct).ConfigureAwait(false));
        }

        return new BookDeleteReport(outcomes);
    }

    private async Task<BookDeleteOutcome> DeleteOneAsync(Guid id, CancellationToken ct)
    {
        Book? book;

        try
        {
            book = await _repo.GetByIdAsync(id, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "删除前读书失败：{BookId}", id);
            return new BookDeleteOutcome(id, id.ToString("D"), false, "读不到这本书：" + ex.SafeMessage());
        }

        if (book is null)
        {
            return new BookDeleteOutcome(id, id.ToString("D"), false, "书库里已经没有这本书了。");
        }

        // 1. 先删库。book_formats 与 reading_progress 靠外键级联一起走。
        try
        {
            await _repo.DeleteAsync(id, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "删库失败：《{Title}》{BookId}", book.Title, id);
            return new BookDeleteOutcome(id, book.Title, false, "删除数据库记录失败：" + ex.SafeMessage());
        }

        // 2. 再删文件。到这一步「书已经从书库消失」已经成立，
        //    所以任何文件层面的失败都只记警告，不影响 Removed。
        //    book.Formats 是删库之前读出来的，删完库仍然有效。
        var warnings = new List<string>();

        foreach (var format in book.Formats)
        {
            TryDeleteFile(format.FilePath, warnings);

            // 阅读缓存按指纹分目录。同一本书的多个格式理论上可能共用一个指纹，重复删也无害。
            TryDeleteDirectory(
                Path.Combine(_paths.ReaderCacheDir, ReaderEndpoints.CacheFolderName(format.QuickFingerprint)),
                warnings);
        }

        // 3. 正式区是「一本书一个目录」，文件删完把目录本身也收掉
        TryDeleteDirectory(PathUtil.BookFolder(_paths.LibraryDir, id), warnings);

        // 4. 两档封面（导入时定下的约定路径，见 ImportPipeline）
        TryDeleteFile(Path.Combine(_paths.CoversCacheDir, id.ToString("D") + ".jpg"), warnings);
        TryDeleteFile(Path.Combine(_paths.CoversCacheDir, id.ToString("D") + "_t.jpg"), warnings);

        _log.LogInformation(
            "已删除《{Title}》{BookId}：{Formats} 个格式，残留警告 {Warnings} 条",
            book.Title, id, book.Formats.Count, warnings.Count);

        return new BookDeleteOutcome(
            id, book.Title, true, warnings.Count == 0 ? null : string.Join("；", warnings));
    }

    /// <summary>删文件。文件本来就不在不算失败（上次没删干净、或用户手动删过）。</summary>
    private void TryDeleteFile(string? path, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            warnings.Add(SafeName(path) + " 没删掉：" + ex.SafeMessage());
            _log.LogWarning(ex, "删除文件失败：{Path}", path);
        }
    }

    /// <summary>删目录（整棵）。目录不存在直接跳过。</summary>
    private void TryDeleteDirectory(string? path, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex)
        {
            warnings.Add(SafeName(path) + " 没删掉：" + ex.SafeMessage());
            _log.LogWarning(ex, "删除目录失败：{Path}", path);
        }
    }

    /// <summary>警告文案里只放文件名/目录名，不放整个绝对路径（路径里带用户名）。</summary>
    private static string SafeName(string path)
    {
        try
        {
            var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
            return string.IsNullOrEmpty(name) ? "文件" : name;
        }
        catch (ArgumentException)
        {
            return "文件";
        }
    }
}