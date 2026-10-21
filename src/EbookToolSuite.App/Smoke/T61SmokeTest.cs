using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using EbookToolSuite.App.Views;
using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Infrastructure.Config;
using EbookToolSuite.Reader.WebView;
using EbookToolSuite.Services.Reader;
using Microsoft.Extensions.Logging;

namespace EbookToolSuite.App.Smoke;

/// <summary>
/// T6-1「MOBI / AZW3 惰性转 EPUB 缓存」的真实 Calibre 端到端冒烟。
///
/// 为什么要有它：判据 1/2 是时间判据，判据 4 是失败时的界面行为，
/// 判据 5 是队列插队，判据 6 是缓存清理跳过正在读的  这四条只有在
/// 真 Calibre + 真阅读服务 + 真转换队列串起来时才说得清，
/// 靠单元测试里的假件只能证明"接线对不对"，证明不了"真能读"。
/// </summary>
public static class T61SmokeTest
{
    /// <summary>判据 1：约 5 MB 的 MOBI 首次打开（含转换）不超过 15 秒。</summary>
    private static readonly TimeSpan FirstOpenBudget = TimeSpan.FromSeconds(15);

    /// <summary>判据 2：第二次打开（缓存命中）不超过 2 秒。</summary>
    private static readonly TimeSpan CacheHitBudget = TimeSpan.FromSeconds(2);

    public static async Task<SmokeReport> RunAsync(
        IBookImportService importer,
        IReaderService reader,
        IEpubCacheService epubCache,
        IConversionQueue queue,
        IBookRepository repo,
        IConfigStore config,
        IWebViewEnvironmentProvider environments,
        IReadingProgressService progress,
        ILoggerFactory loggerFactory,
        AppPaths paths,
        string fixtureDir)
    {
        var startedAt = DateTimeOffset.Now;
        var stopwatch = Stopwatch.StartNew();
        var checks = new List<SmokeCheck>();

        void Add(string name, bool ok, string detail) => checks.Add(new SmokeCheck(name, ok, detail));

        // ---------- 准备：样本由脚本用真 Calibre 造好 ----------
        var mobiA = Path.Combine(fixtureDir, "t61-a.mobi");
        var mobiB = Path.Combine(fixtureDir, "t61-b.mobi");
        var badMobi = Path.Combine(fixtureDir, "t61-bad.mobi");
        var azw3 = Path.Combine(fixtureDir, "t61.azw3");
        var fixtures = new[] { mobiA, mobiB, badMobi, azw3 };
        var missing = fixtures.Where(p => !File.Exists(p)).ToList();

        Add("setup-fixtures", missing.Count == 0,
            missing.Count == 0
                ? string.Join(" | ", fixtures.Select(p => Path.GetFileName(p) + "=" + new FileInfo(p).Length + "B"))
                : "缺少样本：" + string.Join(" | ", missing));

        if (missing.Count > 0)
        {
            return Finish(paths, startedAt, stopwatch, checks);
        }

        // ---------- 导入四本：A/B 两本 MOBI、一本坏 MOBI、一本 AZW3 ----------
        var importA = await importer.ImportAsync(mobiA, ImportOptions.Default, null).ConfigureAwait(true);
        var importB = await importer.ImportAsync(mobiB, ImportOptions.Default, null).ConfigureAwait(true);
        var importBad = await importer.ImportAsync(badMobi, ImportOptions.Default, null).ConfigureAwait(true);
        var importAzw3 = await importer.ImportAsync(azw3, ImportOptions.Default, null).ConfigureAwait(true);

        Add("setup-import", importA.BookId is not null && importB.BookId is not null
                && importBad.BookId is not null && importAzw3.BookId is not null,
            $"A={importA.Outcome} B={importB.Outcome} 坏书={importBad.Outcome} AZW3={importAzw3.Outcome}");

        if (importA.BookId is not { } bookIdA || importB.BookId is not { } bookIdB
            || importBad.BookId is not { } bookIdBad || importAzw3.BookId is not { } bookIdAzw3)
        {
            return Finish(paths, startedAt, stopwatch, checks);
        }

        // ---------- 判据 1：首次打开 MOBI -> 自动转换 + 缓存 + 显示 ----------
        var sw1 = Stopwatch.StartNew();
        ReaderSession? s1 = null;
        Exception? fail1 = null;
        try
        {
            s1 = await reader.OpenAsync(bookIdA).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            fail1 = ex;
        }

        sw1.Stop();

        Add("J1-first-open-converts",
            fail1 is null && s1 is not null && !s1.FromCache && s1.SourceFormat == BookFormatKind.Mobi
                && s1.Manifest.Spine.Count > 0,
            fail1 is null
                ? $"源={Path.GetFileName(mobiA)} {new FileInfo(mobiA).Length:N0} B；首次打开={sw1.ElapsedMilliseconds} ms；"
                    + $"FromCache={s1?.FromCache}；格式={s1?.SourceFormat}；章数={s1?.Manifest.Spine.Count}"
                : "失败：" + fail1.GetType().Name + " " + fail1.Message);

        Add("J1-under-15s", fail1 is null && sw1.Elapsed <= FirstOpenBudget,
            $"判据 1 上限 15s，实测首次打开（含转换）{sw1.ElapsedMilliseconds} ms");

        var cacheRootA = s1?.CacheRoot;
        var artifacts = Directory.Exists(paths.ReaderCacheDir)
            ? Directory.GetFiles(paths.ReaderCacheDir, "*.source.epub", SearchOption.TopDirectoryOnly)
            : Array.Empty<string>();

        Add("J1-converted-cache-layout",
            cacheRootA is not null && Directory.Exists(cacheRootA)
                && File.Exists(Path.Combine(cacheRootA, "reader.json"))
                && Directory.Exists(Path.Combine(cacheRootA, "content"))
                && artifacts.Length >= 1,
            $"缓存根={cacheRootA}；reader.json={cacheRootA is not null && File.Exists(Path.Combine(cacheRootA, "reader.json"))}；"
                + $"content/={cacheRootA is not null && Directory.Exists(Path.Combine(cacheRootA, "content"))}；"
                + $"转出的 .source.epub 数={artifacts.Length}（={string.Join(',', artifacts.Select(Path.GetFileName))}）");

        // ---------- 判据 2：第二次打开同一本 -> 缓存命中且 <= 2 秒 ----------
        var sw2 = Stopwatch.StartNew();
        ReaderSession? s2 = null;
        Exception? fail2 = null;
        try
        {
            s2 = await reader.OpenAsync(bookIdA).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            fail2 = ex;
        }

        sw2.Stop();

        Add("J2-second-open-cache-hit",
            fail2 is null && s2 is not null && s2.FromCache && sw2.Elapsed <= CacheHitBudget
                && string.Equals(s2.CacheRoot, cacheRootA, StringComparison.OrdinalIgnoreCase),
            fail2 is null
                ? $"判据 2 上限 2s，实测二次打开={sw2.ElapsedMilliseconds} ms；FromCache={s2?.FromCache}；"
                    + $"缓存根与首次一致={string.Equals(s2?.CacheRoot, cacheRootA, StringComparison.OrdinalIgnoreCase)}"
                : "失败：" + fail2.GetType().Name + " " + fail2.Message);

        string? href = null;
        Exception? fail2b = null;
        try
        {
            href = await reader.MaterializeChapterAsync(s2!, 0).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            fail2b = ex;
        }

        var hrefPath = href is null || s2 is null
            ? null
            : Path.Combine(s2.CacheRoot, href.Replace('/', Path.DirectorySeparatorChar));

        Add("J2-chapter-materializes",
            fail2b is null && hrefPath is not null && File.Exists(hrefPath) && new FileInfo(hrefPath).Length > 0,
            fail2b is null
                ? $"物化 href={href}，字节={(hrefPath is not null && File.Exists(hrefPath) ? new FileInfo(hrefPath).Length : 0)}"
                : "失败：" + fail2b.GetType().Name + " " + fail2b.Message);

        // ---------- 设计约束：缓存转出的 EPUB 不得登记成书库里的新格式 ----------
        var rowA = await repo.GetByIdAsync(bookIdA).ConfigureAwait(true);
        var formatsA = rowA?.Formats.Select(f => f.Format.ToString()).ToList() ?? new List<string>();

        Add("design-no-epub-row-registered", !formatsA.Contains("Epub"),
            "book-a 的格式记录=" + string.Join(',', formatsA) + "（转 EPUB 缓存不能写成新格式）");

        // ---------- 判据 3：源 MOBI 被替换 -> 缓存自动失效 ----------
        var libPathA = rowA?.Formats.FirstOrDefault(f => f.Format == BookFormatKind.Mobi)?.FilePath;
        var invalidated = false;
        var invalidateDetail = "";
        ReaderSession? s3 = null;

        if (libPathA is null || !File.Exists(libPathA))
        {
            invalidateDetail = "找不到库里的 MOBI 源文件：" + libPathA;
        }
        else
        {
            var beforeBytes = new FileInfo(libPathA).Length;
            File.Copy(mobiB, libPathA, overwrite: true);
            File.SetLastWriteTimeUtc(libPathA, DateTime.UtcNow.AddSeconds(3));
            var afterBytes = new FileInfo(libPathA).Length;

            Exception? fail3 = null;
            try
            {
                s3 = await reader.OpenAsync(bookIdA).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                fail3 = ex;
            }

            invalidated = fail3 is null && s3 is not null && !s3.FromCache
                && !string.Equals(s3.CacheRoot, cacheRootA, StringComparison.OrdinalIgnoreCase);
            invalidateDetail = fail3 is null
                ? $"源 {beforeBytes:N0} B -> {afterBytes:N0} B；替换后 FromCache={s3?.FromCache}；"
                    + $"旧缓存键={Path.GetFileName(cacheRootA)}；新缓存键={Path.GetFileName(s3?.CacheRoot)}"
                : "失败：" + fail3.GetType().Name + " " + fail3.Message;

            if (s3 is not null)
            {
                cacheRootA = s3.CacheRoot;
            }
        }

        Add("J3-source-replaced-invalidates", invalidated, invalidateDetail);

        // ---------- 判据 4：转换失败 -> 明确提示 + 外部打开按钮 ----------
        var badRow = await repo.GetByIdAsync(bookIdBad).ConfigureAwait(true);
        var badLibPath = badRow?.Formats.FirstOrDefault(f => f.Format == BookFormatKind.Mobi)?.FilePath;
        AppException? badError = null;
        Exception? fail4 = null;

        if (badLibPath is null || !File.Exists(badLibPath))
        {
            Add("J4-failure-explicit-message", false, "坏书没有可用的 MOBI 库内路径");
        }
        else
        {
            // 导入时是合法 MOBI，导入后把文件内容换掉：Calibre 必然转不动。
            File.WriteAllBytes(badLibPath, RandomBytes(64 * 1024));
            File.SetLastWriteTimeUtc(badLibPath, DateTime.UtcNow.AddSeconds(3));

            try
            {
                await reader.OpenAsync(bookIdBad).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                fail4 = ex;
                badError = ex as AppException;
            }

            Add("J4-failure-explicit-message",
                badError is not null
                    && badError.UserMessage.Contains("无法转换", StringComparison.Ordinal)
                    && badError.UserMessage.Contains("外部阅读器", StringComparison.Ordinal),
                fail4 is null
                    ? "坏 MOBI 居然打开成功了"
                    : $"{fail4.GetType().Name} code={badError?.Code} UserMessage=\"{badError?.UserMessage}\"");
        }

        // 界面：真构造 ReaderWindow，走真 ShowFatalAsync，再真的点一次按钮。
        var recorder = new RecordingShellLauncher();
        ReaderWindow? window = null;
        var uiOk = false;
        var uiDetail = "";

        try
        {
            window = new ReaderWindow(
                reader,
                environments,
                progress,
                loggerFactory.CreateLogger<ReaderWindow>(),
                bookIdBad,
                repository: repo,
                shell: recorder);

            var showFatal = typeof(ReaderWindow).GetMethod(
                "ShowFatalAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await ((Task)showFatal.Invoke(window, new object?[] { badError! })!).ConfigureAwait(true);

            var panelVisible = window.ErrorPanel.Visibility == Visibility.Visible;
            var webHidden = window.Web.Visibility == Visibility.Collapsed;
            var textOk = string.Equals(window.ErrorText.Text, badError?.UserMessage, StringComparison.Ordinal);
            var buttonVisible = window.OpenExternalButton.Visibility == Visibility.Visible;

            var click = typeof(ReaderWindow).GetMethod(
                "OnOpenExternalClick", BindingFlags.Instance | BindingFlags.NonPublic)!;
            click.Invoke(window, new object?[] { window, new RoutedEventArgs() });
            var handedToShell = string.Equals(recorder.LastOpenedFile, badLibPath, StringComparison.OrdinalIgnoreCase);

            uiOk = panelVisible && webHidden && textOk && buttonVisible && handedToShell;
            uiDetail = $"错误面板={panelVisible} 网页已藏={webHidden} 文案一致={textOk} 按钮可见={buttonVisible} "
                + $"点击后交给系统默认程序={handedToShell}（{recorder.LastOpenedFile}）";
        }
        catch (Exception ex)
        {
            uiDetail = "界面检查异常：" + ex.GetType().Name + " " + ex.Message;
        }

        Add("J4-ui-overlay-and-external-button", uiOk, uiDetail);

        // 反例：源文件没了，就不该再给"用外部阅读器打开"。
        var hiddenOk = false;
        var hiddenDetail = "";

        try
        {
            if (badLibPath is not null && File.Exists(badLibPath))
            {
                File.Delete(badLibPath);
            }

            Exception? fail4b = null;
            try
            {
                await reader.OpenAsync(bookIdBad).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                fail4b = ex;
            }

            if (window is not null && fail4b is AppException ex4b)
            {
                var showFatal = typeof(ReaderWindow).GetMethod(
                    "ShowFatalAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                await ((Task)showFatal.Invoke(window, new object?[] { ex4b })!).ConfigureAwait(true);

                hiddenOk = window.OpenExternalButton.Visibility == Visibility.Collapsed;
                hiddenDetail = $"源文件已删；按钮={window.OpenExternalButton.Visibility}；提示=\"{ex4b.UserMessage}\"";
            }
            else
            {
                hiddenDetail = "预期 AppException，实际=" + (fail4b?.GetType().Name ?? "无异常");
            }
        }
        catch (Exception ex)
        {
            hiddenDetail = "界面反例检查异常：" + ex.GetType().Name + " " + ex.Message;
        }

        Add("J4-button-hidden-without-source", hiddenOk, hiddenDetail);

        // ---------- 判据 6：缓存上限清理，跳过正在读的 ----------
        ReaderSession? sB = null;
        Exception? failB = null;
        try
        {
            sB = await reader.OpenAsync(bookIdB).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            failB = ex;
        }

        Add("J6-second-cache-exists",
            failB is null && sB is not null && Directory.Exists(sB.CacheRoot),
            failB is null ? "book-b 冷开建缓存：" + sB?.CacheRoot : "失败：" + failB.GetType().Name + " " + failB.Message);

        if (sB is not null)
        {
            // book-b 这一份要当"可回收对象"：先放掉它的租约。
            reader.Release(bookIdB);
        }

        // book-a 还持有判据 3 那次打开留下的"正在读"租约。
        await config.SetAsync(
            EpubCacheService.CacheLimitKey, JsonValue.Create(0.05d)).ConfigureAwait(true);
        await config.SaveAsync().ConfigureAwait(true);

        long freed = -1;
        Exception? fail6 = null;
        try
        {
            freed = await epubCache.EnforceLimitAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            fail6 = ex;
        }

        var inUseSurvives = cacheRootA is not null && Directory.Exists(cacheRootA);
        var otherGone = sB is not null && !Directory.Exists(sB.CacheRoot);

        Add("J6-evicts-oldest-skips-in-use",
            fail6 is null && freed > 0 && inUseSurvives && otherGone,
            fail6 is null
                ? $"上限 0.05 MB；本次释放={freed / 1048576d:F2} MB；正在读的（book-a）保住={inUseSurvives}；"
                    + $"没在读的（book-b）被清={otherGone}"
                : "失败：" + fail6.GetType().Name + " " + fail6.Message);

        // 关掉阅读窗口（释放租约）后，同一份缓存就该能被回收了。
        reader.Release(bookIdA);

        long freed2 = -1;
        Exception? fail7 = null;
        try
        {
            freed2 = await epubCache.EnforceLimitAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            fail7 = ex;
        }

        var inUseGone = cacheRootA is not null && !Directory.Exists(cacheRootA);

        Add("J6-release-then-evictable",
            fail7 is null && inUseGone,
            fail7 is null
                ? $"释放租约后再清：释放={freed2 / 1048576d:F2} MB；原「正在读」缓存已回收={inUseGone}"
                : "失败：" + fail7.GetType().Name + " " + fail7.Message);

        // 恢复上限，别让后续步骤受影响。
        await config.SetAsync(
            EpubCacheService.CacheLimitKey, JsonValue.Create(EpubCacheService.DefaultCacheLimitMb))
            .ConfigureAwait(true);
        await config.SaveAsync().ConfigureAwait(true);

        // ---------- 判据 5：打开操作插队到转换队列前面 ----------
        var backlog1 = await queue.EnqueueAsync(new ConversionRequest
        {
            BookId = bookIdA,
            TargetFormat = BookFormatKind.Epub,
        }).ConfigureAwait(true);

        var backlog2 = await queue.EnqueueAsync(new ConversionRequest
        {
            BookId = bookIdB,
            TargetFormat = BookFormatKind.Epub,
        }).ConfigureAwait(true);

        var sw5 = Stopwatch.StartNew();
        ReaderSession? s5 = null;
        Exception? fail5 = null;
        try
        {
            s5 = await reader.OpenAsync(bookIdAzw3).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            fail5 = ex;
        }

        sw5.Stop();

        // 这一步返回时，排在后面的批次任务还没做完，就说明打开这条插了队。
        var backlog2StillPending = !backlog2.IsTerminal;

        try
        {
            await queue.WaitForIdleAsync().ConfigureAwait(true);
        }
        catch (Exception)
        {
            // 空闲等待失败不影响已有证据。
        }

        var openTask = queue.Tasks.FirstOrDefault(
            t => t.BookId == bookIdAzw3 && t.OutFormat == BookFormatKind.Epub);

        var startedFirst = openTask?.StartedAt is not null && backlog2.StartedAt is not null
            && openTask.StartedAt < backlog2.StartedAt;
        var finishedFirst = openTask?.FinishedAt is not null && backlog2.FinishedAt is not null
            && openTask.FinishedAt < backlog2.FinishedAt;

        Add("J5-open-jumps-queue",
            fail5 is null && s5 is not null && !s5.FromCache && s5.SourceFormat == BookFormatKind.Azw3
                && backlog2StillPending && startedFirst && finishedFirst,
            fail5 is null
                ? $"AZW3 首次打开耗时={sw5.ElapsedMilliseconds} ms；打开返回时后一条批次任务仍未完成={backlog2StillPending}；"
                    + $"插队任务 StartedAt={openTask?.StartedAt:HH:mm:ss.fff} < 批次2 StartedAt={backlog2.StartedAt:HH:mm:ss.fff} = {startedFirst}；"
                    + $"FinishedAt 也早于批次2={finishedFirst}；章数={s5?.Manifest.Spine.Count}"
                : "失败：" + fail5.GetType().Name + " " + fail5.Message);

        return Finish(paths, startedAt, stopwatch, checks);
    }

    private static byte[] RandomBytes(int count)
    {
        var bytes = new byte[count];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }

    private static SmokeReport Finish(
        AppPaths paths, DateTimeOffset startedAt, Stopwatch stopwatch, List<SmokeCheck> checks)
    {
        var report = new SmokeReport(
            startedAt.ToString("O"),
            DateTimeOffset.Now.ToString("O"),
            stopwatch.ElapsedMilliseconds,
            paths.DataRoot,
            paths.TempDir,
            null,
            checks);

        Directory.CreateDirectory(paths.TempDir);
        File.WriteAllText(
            Path.Combine(paths.TempDir, "t61-smoke.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));

        return report;
    }

    /// <summary>只记录"点了哪个文件"，绝不真的弹资源管理器。</summary>
    private sealed class RecordingShellLauncher : IShellLauncher
    {
        public string? LastOpenedFile { get; private set; }

        public void OpenFolder(string path)
        {
        }

        public void RevealInExplorer(string filePath)
        {
        }

        public void OpenFile(string path) => LastOpenedFile = path;

        public void OpenUrl(string url)
        {
        }
    }
}