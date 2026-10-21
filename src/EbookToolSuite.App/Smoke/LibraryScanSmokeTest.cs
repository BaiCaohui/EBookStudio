using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using EbookToolSuite.App.Views.Pages;
using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Infrastructure.Config;

namespace EbookToolSuite.App.Smoke;

/// <summary>
/// T2-6 文件监控与缺失检测冒烟。样本用一本真实电子书，按执行计划的四条判据走一遍：
/// 删文件 -> 刷新 -> 缺失；改文件 -> 刷新 -> 已修改；改回来 -> 刷新 -> 恢复正常；
/// 另外再灌 1000 本真文件验「全库扫描 <= 3 秒」。
/// </summary>
public static class LibraryScanSmokeTest
{
    private const int PerfBookCount = 1000;

    public static async Task<SmokeReport> RunAsync(
        AppPaths paths,
        MainWindow window,
        LibraryPage page,
        IBookRepository repo,
        IBookImportService import,
        ILibraryScanService scan,
        string sourceFile)
    {
        var startedAt = DateTimeOffset.Now;
        var stopwatch = Stopwatch.StartNew();
        var checks = new List<SmokeCheck>();
        var vm = page.ViewModel;

        window.Show();
        window.UpdateLayout();

        // ---------- 0：导一本真实电子书当样本 ----------
        var imported = await import.ImportAsync(sourceFile, ImportOptions.Default).ConfigureAwait(true);
        var bookId = imported.BookId ?? Guid.Empty;
        var libraryPath = imported.LibraryPath ?? string.Empty;

        checks.Add(new SmokeCheck(
            "0-import-ok",
            imported.Outcome == ImportOutcome.Imported && bookId != Guid.Empty && File.Exists(libraryPath),
            "导入结果=" + imported.Outcome + "，文件=" + Path.GetFileName(libraryPath)));

        // ---------- A：基线刷新，状态正常 ----------
        await vm.RefreshWithScanAsync().ConfigureAwait(true);

        var card = vm.Books.FirstOrDefault(b => b.Book.Id == bookId);
        var missingInDb = await IsMissingInDbAsync(repo, bookId).ConfigureAwait(true);

        checks.Add(new SmokeCheck(
            "A-baseline-normal",
            card is not null && !card.HasFileProblem && !missingInDb && !vm.HasFileProblems,
            "卡片在网格里=" + (card is not null) + "，文件状态=" + (card?.FileStateText ?? "-")
                + "，库 missing=" + missingInDb
                + "，诊断 " + DescribeProblem(scan, bookId)));

        // ---------- B：手工删掉文件 -> 刷新 -> 显示缺失 ----------
        var backup = libraryPath + ".smoke-backup";
        File.Move(libraryPath, backup);

        await vm.RefreshWithScanAsync().ConfigureAwait(true);

        card = vm.Books.FirstOrDefault(b => b.Book.Id == bookId);
        missingInDb = await IsMissingInDbAsync(repo, bookId).ConfigureAwait(true);

        checks.Add(new SmokeCheck(
            "B-deleted-shows-missing",
            card is not null && card!.FileState == BookFileState.Missing
                && card.FileStateText == "文件缺失" && card.HasFileProblem
                && missingInDb && vm.HasFileProblems,
            "状态=" + (card?.FileStateText ?? "-") + "，库 missing=" + missingInDb
                + "，摘要=" + vm.FileScanSummary));

        var summaryText = page.FindName("FileScanSummaryText") as TextBlock;

        checks.Add(new SmokeCheck(
            "B2-summary-visible",
            summaryText is not null && summaryText.IsVisible,
            "工具栏摘要可见=" + summaryText?.IsVisible + "，文案=" + vm.FileScanSummary));

        // ---------- C：文件改回来 -> 刷新 -> 恢复正常 ----------
        File.Move(backup, libraryPath);

        await vm.RefreshWithScanAsync().ConfigureAwait(true);

        card = vm.Books.FirstOrDefault(b => b.Book.Id == bookId);
        missingInDb = await IsMissingInDbAsync(repo, bookId).ConfigureAwait(true);

        checks.Add(new SmokeCheck(
            "C-restored-back-to-normal",
            card is not null && !card.HasFileProblem && !missingInDb && !vm.HasFileProblems,
            "状态=" + (card?.FileStateText ?? "-") + "，库 missing=" + missingInDb));

        // ---------- D：外部改了文件 -> 刷新 -> 显示已修改 ----------
        await File.AppendAllTextAsync(libraryPath, "外部改动").ConfigureAwait(true);

        await vm.RefreshWithScanAsync().ConfigureAwait(true);

        card = vm.Books.FirstOrDefault(b => b.Book.Id == bookId);

        checks.Add(new SmokeCheck(
            "D-modified-shows-modified",
            card is not null && card!.FileState == BookFileState.ExternallyModified
                && card.FileStateText == "已修改" && card.CanAcceptModification && vm.HasFileProblems,
            "状态=" + (card?.FileStateText ?? "-") + "，可接受改动=" + (card?.CanAcceptModification)
                + "，摘要=" + vm.FileScanSummary));

        // ---------- D2：详情浮层里「接受改动」必须完整落在面板内 ----------
        // 这一条是为一个真 bug 立的：横向 StackPanel 测量时给子元素无限宽度，
        // 长说明文字按自然宽度铺开，把按钮挤出了面板（截图里被裁掉）。
        vm.OpenDetail(card);
        window.UpdateLayout();

        var stateBar = page.FindName("DetailFileStateBar") as FrameworkElement;
        var acceptButton = page.FindName("DetailAcceptChangeButton") as FrameworkElement;
        var acceptInside = false;
        var acceptDetail = "找不到 DetailFileStateBar / DetailAcceptChangeButton";

        if (stateBar is not null && acceptButton is not null && acceptButton.ActualWidth > 0)
        {
            var bounds = acceptButton
                .TransformToVisual(stateBar)
                .TransformBounds(new Rect(acceptButton.RenderSize));

            acceptInside = bounds.Right <= stateBar.ActualWidth + 1 && bounds.Left >= -1;
            acceptDetail = "按钮 " + bounds.Left.ToString("0.#") + "~" + bounds.Right.ToString("0.#")
                + "，面板宽 " + stateBar.ActualWidth.ToString("0.#")
                + "（按钮必须完全落在这个宽度里）";
        }

        checks.Add(new SmokeCheck("D2-accept-button-not-clipped", acceptInside, acceptDetail));

        // 详情里的说明不能是卡片 ToolTip 那句（"打开详情可以接受这次改动"在详情里自相矛盾）
        var detailText = (page.FindName("DetailFileStateText") as TextBlock)?.Text ?? string.Empty;

        checks.Add(new SmokeCheck(
            "D3-detail-wording-matches-place",
            detailText.Contains("点「接受改动」") && !detailText.Contains("打开详情"),
            "详情文案=" + detailText));

        vm.CloseDetail();
        window.UpdateLayout();

        // ---------- E：接受改动 -> 回到正常 ----------
        var accepted = card is not null && await vm.AcceptModificationAsync(card).ConfigureAwait(true);

        card = vm.Books.FirstOrDefault(b => b.Book.Id == bookId);

        checks.Add(new SmokeCheck(
            "E-accept-change-back-to-normal",
            accepted && card is not null && !card.HasFileProblem && !vm.HasFileProblems,
            "接受成功=" + accepted + "，状态=" + (card?.FileStateText ?? "-")
                + "，摘要=" + vm.FileScanSummary));

        // ---------- F：1000 本的全库扫描 <= 3 秒 ----------
        var seeded = await SeedThousandAsync(paths, repo).ConfigureAwait(true);

        var scanWatch = Stopwatch.StartNew();
        var perfReport = await scan.ScanAsync().ConfigureAwait(true);
        scanWatch.Stop();

        checks.Add(new SmokeCheck(
            "F-1000-books-scan-under-3s",
            seeded == PerfBookCount && perfReport.ScannedFormats >= PerfBookCount
                && scanWatch.ElapsedMilliseconds <= 3000,
            "灌了 " + seeded + " 本，扫描 " + perfReport.ScannedFormats + " 个文件 / "
                + perfReport.ScannedBooks + " 本书，用时 " + scanWatch.ElapsedMilliseconds
                + " ms（判据 <= 3000），缺失 " + perfReport.MissingCount + " 个"));

        stopwatch.Stop();

        var report = new SmokeReport(
            startedAt.ToString("O"),
            DateTimeOffset.Now.ToString("O"),
            stopwatch.ElapsedMilliseconds,
            paths.DataRoot,
            paths.WebView2DataDir,
            null,
            checks);

        File.WriteAllText(
            Path.Combine(paths.TempDir, "scan-smoke.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));

        return report;
    }

    /// <summary>把扫描报告里这本书的那条问题摊开写，失败时不用再去猜是哪一项对不上。</summary>
    private static string DescribeProblem(ILibraryScanService scan, Guid bookId)
    {
        var problem = scan.LastReport?.Problems.FirstOrDefault(p => p.BookId == bookId);

        return problem is null
            ? "报告里没有这本书的问题项"
            : "状态=" + problem.State
                + "，体积 期望=" + problem.ExpectedSize + " 实际=" + problem.ActualSize
                + "，时间 期望=" + problem.ExpectedModifiedAt?.ToString("O")
                + " 实际=" + problem.ActualModifiedAt?.ToString("O");
    }

    private static async Task<bool> IsMissingInDbAsync(IBookRepository repo, Guid bookId)
    {
        var book = await repo.GetByIdAsync(bookId).ConfigureAwait(true);
        return book is not null && book.Formats.Any(f => f.Missing);
    }

    /// <summary>
    /// 造 1000 本**真文件**（每本一个小文件），库里的体积与修改时间按磁盘上的真值写。
    /// 直接写库、不走导入：这一层验的是"扫 1000 个文件要多久"，不是导入有多快。
    /// </summary>
    private static async Task<int> SeedThousandAsync(AppPaths paths, IBookRepository repo)
    {
        var dir = Path.Combine(paths.LibraryDir, "scan-smoke-files");
        Directory.CreateDirectory(dir);

        var now = DateTimeOffset.Now;

        for (var i = 0; i < PerfBookCount; i++)
        {
            var file = Path.Combine(dir, "scan-" + i.ToString("D4") + ".epub");
            await File.WriteAllTextAsync(file, "smoke-" + i).ConfigureAwait(true);

            var info = new FileInfo(file);

            var book = new Book
            {
                Title = "扫描冒烟书 " + (i + 1).ToString("D4"),
                Authors = "冒烟作者",
                CreatedAt = now,
                UpdatedAt = now,
            };

            book.Formats.Add(new BookFormat
            {
                BookId = book.Id,
                Format = BookFormatKind.Epub,
                FilePath = file,
                FileSize = info.Length,
                FileModifiedAt = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                AddedAt = now,
            });

            await repo.AddAsync(book).ConfigureAwait(true);
        }

        return PerfBookCount;
    }
}
