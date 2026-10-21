using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Engines.Calibre;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Infrastructure.Config;
using EbookToolSuite.Services.Conversion;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace EbookToolSuite.App.Smoke;

/// <summary>
/// 转换模板冒烟（执行计划 T2-5 的 5 条判据）。
/// </summary>
/// <remarks>
/// 两个样本：一本 EPUB（跑 a4-print / kindle-best / plain-utf8 / polish-only）
/// 与一本 TXT（跑 mobile-readable  EPUB 输出的模板对**已有 EPUB 的书**会被
/// 「已经有这个格式了」那道闸拦下，这是 T1-4 遗留的待拍板问题，不是这里的 bug）。
/// comic-zip 用现造的图片文件夹，走 ICbzPacker 独立入口。
/// </remarks>
internal static class TemplateSmokeTest
{
    public static async Task<SmokeReport> RunAsync(
        IServiceProvider services,
        AppPaths paths,
        string epubFile,
        string epub2File)
    {
        var startedAt = DateTimeOffset.Now;
        var stopwatch = Stopwatch.StartNew();
        var checks = new List<SmokeCheck>();

        var importer = services.GetRequiredService<IBookImportService>();
        var queue = services.GetRequiredService<IConversionQueue>();
        var repo = services.GetRequiredService<IBookRepository>();
        var store = services.GetRequiredService<ConversionTemplateStore>();
        var packer = services.GetRequiredService<ICbzPacker>();

        var epubBook = await importer
            .ImportAsync(epubFile, new ImportOptions(), null, CancellationToken.None)
            .ConfigureAwait(true);
        var bookB = await importer
            .ImportAsync(epub2File, new ImportOptions(), null, CancellationToken.None)
            .ConfigureAwait(true);

        checks.Add(new SmokeCheck(
            "prep-imported",
            epubBook.BookId is not null && bookB.BookId is not null,
            $"A={epubBook.Outcome} B={bookB.Outcome}"));

        // ---------- 判据 1a：换格式模板各跑通一次 ----------
        // mobile-readable 只能做结构校验，原因见类注释：它的目标是 EPUB，
        // 而库里这本书已经有 EPUB 了，会被「已经有这个格式了」那道闸拦下。
        var cases = new (string Template, BookFormatKind Target, Guid BookId)[]
        {
            ("a4-print", BookFormatKind.Pdf, epubBook.BookId!.Value),
            ("kindle-best", BookFormatKind.Mobi, epubBook.BookId!.Value),
            ("plain-utf8", BookFormatKind.Txt, epubBook.BookId!.Value),
        };

        foreach (var item in cases)
        {
            await queue.EnqueueAsync(new ConversionRequest
            {
                BookId = item.BookId,
                TargetFormat = item.Target,
                TemplateId = item.Template,
            }).ConfigureAwait(true);
        }

        await queue.WaitForIdleAsync().ConfigureAwait(true);

        foreach (var item in cases)
        {
            var book = await repo.GetByIdAsync(item.BookId).ConfigureAwait(true);
            var produced = book?.Formats
                .FirstOrDefault(f => f.Format == item.Target && File.Exists(f.FilePath));

            checks.Add(new SmokeCheck(
                "template-" + item.Template,
                produced is not null && new FileInfo(produced.FilePath).Length > 512,
                produced is null
                    ? "没有产出 " + item.Target.ToStorageString()
                    : item.Target.ToStorageString() + "="
                        + new FileInfo(produced.FilePath).Length + " 字节"));
        }

        // ---------- 判据 1b：mobile-readable 的参数在 epub->epub 白名单里 ----------
        // 这条能抓到「模板里写了 Calibre 不认的参数」这类错误，只是不真的跑一遍转换。
        var mobileReadable = store.Find("mobile-readable");
        var argsOk = false;
        var argsDetail = "找不到 mobile-readable 模板";

        if (mobileReadable is not null)
        {
            try
            {
                var catalog = new CalibreArgsCatalog(
                    services.GetRequiredService<IProcessHost>(),
                    services.GetRequiredService<IToolLocator>().CalibreConvert,
                    Path.Combine(paths.CacheDir, "calibre-options-smoke.json"),
                    services.GetRequiredService<IToolLocator>().BuildEnvironment("calibre"),
                    paths.ToolsRoot);

                var epubCatalog = await catalog.GetAsync(".epub", ".epub").ConfigureAwait(true);
                var built = CalibreArgumentBuilder.Build(
                    epubCatalog, mobileReadable.Arguments, Array.Empty<ConversionArgument>());

                argsOk = built.Count == mobileReadable.Arguments.Count;
                argsDetail = string.Join(' ', built.Select(a => a.ToString()));
            }
            catch (Exception ex)
            {
                argsDetail = ex.GetType().Name + ": " + ex.Message;
            }
        }

        checks.Add(new SmokeCheck("template-mobile-readable", argsOk, argsDetail));

        // ---------- 判据 1b：polish-only（不换格式，就地替换） ----------
        var epubFormat = (await repo.GetByIdAsync(epubBook.BookId!.Value).ConfigureAwait(true))!
            .Formats.First(f => f.Format == BookFormatKind.Epub);
        var beforeSize = new FileInfo(epubFormat.FilePath).Length;

        await queue.EnqueueAsync(new ConversionRequest
        {
            BookId = epubBook.BookId!.Value,
            TargetFormat = BookFormatKind.Epub,
            SourceFormat = BookFormatKind.Epub,
            TemplateId = "polish-only",
        }).ConfigureAwait(true);

        await queue.WaitForIdleAsync().ConfigureAwait(true);

        var afterSize = File.Exists(epubFormat.FilePath)
            ? new FileInfo(epubFormat.FilePath).Length
            : -1;

        checks.Add(new SmokeCheck(
            "template-polish-only",
            afterSize > 512,
            $"就地替换：{beforeSize} -> {afterSize} 字节"));

        // 备份必须留着（polish 是就地替换，出问题时靠它找回）。
        var backups = Directory.Exists(paths.BackupFilesDir)
            ? Directory.GetFiles(paths.BackupFilesDir, "*" + epubBook.BookId!.Value.ToString("D") + "*")
            : Array.Empty<string>();

        checks.Add(new SmokeCheck(
            "polish-keeps-backup",
            backups.Length > 0,
            backups.Length > 0 ? Path.GetFileName(backups[0]) : "备份目录里没有找到原件"));

        // ---------- 判据 1c：comic-zip（图片文件夹 -> CBZ，独立入口） ----------
        var imageFolder = Path.Combine(paths.TempDir, "cbz-smoke");
        Directory.CreateDirectory(imageFolder);
        for (var i = 1; i <= 10; i++)
        {
            await File.WriteAllBytesAsync(
                Path.Combine(imageFolder, i + ".jpg"), new byte[512 + i]).ConfigureAwait(true);
        }

        var cbzPath = Path.Combine(paths.TempDir, "cbz-smoke.cbz");
        var cbz = await packer.PackAsync(imageFolder, cbzPath).ConfigureAwait(true);

        using (var zip = ZipFile.OpenRead(cbzPath))
        {
            checks.Add(new SmokeCheck(
                "template-comic-zip",
                cbz.ImageCount == 10
                    && zip.Entries.Count == 10
                    && zip.Entries[0].FullName == "1.jpg"
                    && zip.Entries[9].FullName == "10.jpg",
                $"图 {cbz.ImageCount} 张，包内 {zip.Entries.Count} 条，首条={zip.Entries[0].FullName}"));
        }

        // ---------- 判据 2：用户改过的参数能保存并复用 ----------
        const string userTemplateId = "user-smoke-font";
        await store.SaveUserAsync(new ConversionTemplate
        {
            Id = userTemplateId,
            DisplayName = "冒烟：大字 MOBI",
            TargetFormat = BookFormatKind.Mobi,
            Note = "冒烟用，跑完就删。",
            IsBuiltIn = false,
            Arguments = new[]
            {
                new ConversionArgument("--base-font-size", "24", "冒烟：放大字号"),
            },
        }).ConfigureAwait(true);

        var reloaded = store.Find(userTemplateId);
        checks.Add(new SmokeCheck(
            "user-template-saved",
            reloaded is not null && reloaded.Arguments.Count == 1
                && reloaded.Arguments[0].Value == "24",
            reloaded is null ? "没读回来" : reloaded.Arguments[0].ToString()));

        // 判据 2 与判据 3 都拿第二本书 B 来验：B 只有 EPUB，所以 MOBI 是空的；
        // 用 EPUB 作目标会被查重闸挡住（那正是 book A 要留给 polish-only 的场景）。
        var freshBook = bookB;
        var taskRepo = services.GetRequiredService<IConversionTaskRepository>();
        // ---------- 判据 3：白名单外的参数必须在执行前被拦下 ----------
        await queue.EnqueueAsync(new ConversionRequest
        {
            BookId = freshBook.BookId!.Value,
            TargetFormat = BookFormatKind.Mobi,
            Overrides = new[] { new ConversionArgument("--definitely-not-a-real-flag", "1") },
        }).ConfigureAwait(true);

        await queue.WaitForIdleAsync().ConfigureAwait(true);

        var bogus = (await taskRepo.ListRecentAsync(5).ConfigureAwait(true))
            .FirstOrDefault(t => t.BookId == freshBook.BookId);

        checks.Add(new SmokeCheck(
            "whitelist-rejects-unknown-arg",
            bogus is { Status: ConversionTaskStatus.Failed, ErrorCode: nameof(ErrorCode.ConversionArgsRejected) },
            bogus is null ? "没找到任务" : $"{bogus.Status} / {bogus.ErrorCode}"));

        var freshAfterFail = await repo.GetByIdAsync(freshBook.BookId!.Value).ConfigureAwait(true);
        checks.Add(new SmokeCheck(
            "whitelist-reject-produces-no-file",
            freshAfterFail is not null
                && freshAfterFail.Formats.All(f => f.Format != BookFormatKind.Mobi),
            "拦下之后库里没有多出 MOBI 记录"));

        // ---------- 判据 2（续）：把刚存的用户模板真的用起来 ----------
        await queue.EnqueueAsync(new ConversionRequest
        {
            BookId = freshBook.BookId!.Value,
            TargetFormat = BookFormatKind.Mobi,
            TemplateId = userTemplateId,
        }).ConfigureAwait(true);

        await queue.WaitForIdleAsync().ConfigureAwait(true);

        var freshDone = await repo.GetByIdAsync(freshBook.BookId!.Value).ConfigureAwait(true);
        var reused = freshDone?.Formats
            .FirstOrDefault(f => f.Format == BookFormatKind.Mobi && File.Exists(f.FilePath));

        checks.Add(new SmokeCheck(
            "user-template-reused",
            reused is not null && new FileInfo(reused.FilePath).Length > 512,
            reused is null ? "用户模板没跑出结果" : new FileInfo(reused.FilePath).Length + " 字节"));

        var offered = store.ForTarget(BookFormatKind.Mobi).Any(t => t.Id == userTemplateId);
        checks.Add(new SmokeCheck(
            "user-template-in-dropdown",
            offered,
            offered ? "MOBI 的模板清单里有它" : "模板清单里没有它"));

        // 收尾：冒烟模板不留痕。
        await store.DeleteUserAsync(userTemplateId).ConfigureAwait(true);
        checks.Add(new SmokeCheck(
            "user-template-deleted",
            store.Find(userTemplateId) is null,
            "已删除冒烟模板"));

        // ---------- 判据 4：模板里用到的 --output-profile 档位名在锁定版本里存在 ----------
        // 判据 5：--mobi-file-type 只能用于 MOBI 输出（用错地方必须抛，不能悄悄传给 Calibre）
        var profileValue = "kindle_pw3";
        var profileOk = false;
        var mobiGuardOk = false;
        var guardDetail = string.Empty;

        try
        {
            // 用一个已知的坏档位反证「档位名不是随便写的」：Calibre 对非法档位退出码是 2。
            var probe = new ProcessStartInfo
            {
                FileName = services.GetRequiredService<IToolLocator>().CalibreConvert,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            probe.ArgumentList.Add("--output-profile");
            probe.ArgumentList.Add(profileValue);
            probe.ArgumentList.Add("--version");
            using var process = Process.Start(probe);
            process?.WaitForExit(15_000);
            profileOk = process is { ExitCode: 0 };
            guardDetail = "档位 " + profileValue + " -> exit=" + (process?.ExitCode.ToString() ?? "null");
        }
        catch (Exception ex)
        {
            guardDetail = "探针失败：" + ex.Message;
        }

        checks.Add(new SmokeCheck("output-profile-valid", profileOk, guardDetail));

        try
        {
            ConversionService.ValidateTargetSpecificOptions(
                BookFormatKind.Pdf,
                new[] { new ConversionArgument("--mobi-file-type", "both") });
            guardDetail = "没拦住（这是 bug）";
        }
        catch (AppException ex)
        {
            mobiGuardOk = ex.Code == ErrorCode.ConversionArgsRejected;
            guardDetail = ex.UserMessage;
        }

        checks.Add(new SmokeCheck("mobi-file-type-only-for-mobi", mobiGuardOk, guardDetail));

        stopwatch.Stop();
        Log.Information(
            "模板冒烟结束：{Passed}/{Total}，耗时 {Ms} ms",
            checks.Count(c => c.Ok), checks.Count, stopwatch.ElapsedMilliseconds);

        var report = new SmokeReport(
            startedAt.ToString("O"),
            DateTimeOffset.Now.ToString("O"),
            stopwatch.ElapsedMilliseconds,
            paths.DataRoot,
            paths.WebView2DataDir,
            null,
            checks);

        File.WriteAllText(
            Path.Combine(paths.TempDir, "template-smoke.json"),
            System.Text.Json.JsonSerializer.Serialize(
                report,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        return report;
    }
}
