using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using EbookToolSuite.App.ViewModels;
using EbookToolSuite.App.Views;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Infrastructure.Config;

namespace EbookToolSuite.App.Smoke;

/// <summary>
/// T2-2 重复检测冒烟。样本当场构造（直接写库，不走导入  为了验重复检测去造 1000 本真 EPUB 没有意义）：
/// 三本同名的 + 两本同名的（内容不一致）+ 一本独苗。
/// 验：扫描出组、窗口真渲染出卡片、合并后副本从书库网格消失、被忽略的组不再出现，
/// 最后再灌 1000 本验性能判据（<= 10 秒）。
/// </summary>
public static class DuplicateSmokeTest
{
    private const int PerfBookCount = 1000;

    public static async Task<SmokeReport> RunAsync(
        AppPaths paths,
        IBookRepository repo,
        DuplicateScanViewModel scanVm,
        LibraryViewModel libraryVm)
    {
        var startedAt = DateTimeOffset.Now;
        var stopwatch = Stopwatch.StartNew();
        var checks = new List<SmokeCheck>();

        var seedDir = Path.Combine(paths.TempDir, "dup-seed");
        Directory.CreateDirectory(seedDir);

        // 组 1：三本同名同作者，内容一模一样（全文件哈希应当一致）
        var g1a = NewSeedFile(seedDir, "g1-a.epub", "AAA");
        var g1b = NewSeedFile(seedDir, "g1-b.epub", "AAA");
        var g1c = NewSeedFile(seedDir, "g1-c.epub", "AAA");

        // 组 2：两本同名同作者，但内容不同（哈希不一致，应提示可能是不同版本）
        var g2a = NewSeedFile(seedDir, "g2-a.epub", "BBB");
        var g2b = NewSeedFile(seedDir, "g2-b.epub", "CCC");

        // 独苗：不属于任何组（file_path 有 UNIQUE 约束，所以必须另造一个文件）
        var solo = NewSeedFile(seedDir, "solo.epub", "DDD");

        await SeedAsync(repo, "重复的书", "同一个作者", g1a).ConfigureAwait(true);
        await SeedAsync(repo, "重复的书", "同一个作者", g1b).ConfigureAwait(true);
        await SeedAsync(repo, "重复的书", "同一个作者", g1c).ConfigureAwait(true);
        await SeedAsync(repo, "另一本重复", "另一个作者", g2a).ConfigureAwait(true);
        await SeedAsync(repo, "另一本重复", "另一个作者", g2b).ConfigureAwait(true);
        await SeedAsync(repo, "独一份", "没人", solo).ConfigureAwait(true);

        var window = new DuplicateScanWindow(scanVm) { AutoScanOnLoad = false };
        window.Show();

        var groups = await window.ScanForSmokeAsync().ConfigureAwait(true);

        checks.Add(new SmokeCheck(
            "A-scan-finds-two-groups",
            groups == 2,
            "扫出 " + groups + " 组（期望 2：三本同名一组，两本同名一组）"));

        window.UpdateLayout();
        var realized = window.RealizedCardCount();

        checks.Add(new SmokeCheck(
            "A-window-realizes-cards",
            realized == 2,
            "窗口里生成了 " + realized + " 张组卡片（期望 2）"));

        var i1 = window.FindGroupIndexByMemberTitle("重复的书");
        var i2 = window.FindGroupIndexByMemberTitle("另一本重复");

        checks.Add(new SmokeCheck(
            "A-group-sizes",
            i1 >= 0 && i2 >= 0 && window.MemberCountOf(i1) == 3 && window.MemberCountOf(i2) == 2,
            "同名三本那组 " + window.MemberCountOf(i1) + " 本，同名两本那组 " + window.MemberCountOf(i2) + " 本"));

        checks.Add(new SmokeCheck(
            "A-hash-verdict-differs",
            i1 >= 0 && i2 >= 0
                && scanVm.Groups[i1].HashVerdict.Contains("一致", StringComparison.Ordinal)
                && !scanVm.Groups[i1].HashVerdict.Contains("不一致", StringComparison.Ordinal)
                && scanVm.Groups[i2].HashVerdict.Contains("不一致", StringComparison.Ordinal),
            "内容相同那组：" + (i1 >= 0 ? scanVm.Groups[i1].HashVerdict : "?")
                + "；内容不同那组：" + (i2 >= 0 ? scanVm.Groups[i2].HashVerdict : "?")));

        // ---------- 合并：保留一本，其余标为副本 ----------
        var before = await repo.CountAsync().ConfigureAwait(true);
        var marked = await window.MergeForSmokeAsync(i1, 0).ConfigureAwait(true);

        await libraryVm.LoadAsync().ConfigureAwait(true);
        var shownAfterMerge = libraryVm.Books.Count;

        checks.Add(new SmokeCheck(
            "B-merge-marks-copies",
            marked == 2,
            "合并后标记副本 " + marked + " 本（期望 2，那组共 3 本）"));

        checks.Add(new SmokeCheck(
            "B-merge-hides-from-grid",
            shownAfterMerge == before - 2,
            "网格从 " + before + " 本降到 " + shownAfterMerge + " 本（期望 " + (before - 2) + "）"));

        var left = await window.ScanForSmokeAsync().ConfigureAwait(true);

        checks.Add(new SmokeCheck(
            "B-merged-group-gone-after-rescan",
            left == 1,
            "重新扫描后剩 " + left + " 组（期望 1）"));

        // ---------- 忽略 ----------
        var iLeft = window.FindGroupIndexByMemberTitle("另一本重复");
        var ignored = await window.IgnoreForSmokeAsync(iLeft).ConfigureAwait(true);
        var afterIgnore = await window.ScanForSmokeAsync().ConfigureAwait(true);

        checks.Add(new SmokeCheck(
            "C-ignore-silences-group",
            ignored && afterIgnore == 0,
            "忽略返回 " + ignored + "，重新扫描后剩 " + afterIgnore + " 组（期望 0）"));

        // ---------- 性能：1000 本 ----------
        for (var n = 1; n <= PerfBookCount; n++)
        {
            await SeedAsync(
                repo,
                "批量书 " + n.ToString("D4"),
                "批量作者",
                Path.Combine(seedDir, "perf-" + n.ToString("D4") + ".epub")).ConfigureAwait(true);
        }

        var perfWatch = Stopwatch.StartNew();
        var perfGroups = await window.ScanForSmokeAsync().ConfigureAwait(true);
        perfWatch.Stop();

        checks.Add(new SmokeCheck(
            "D-1000-books-under-10s",
            perfGroups == 0 && perfWatch.ElapsedMilliseconds <= 10000,
            "1000 本扫描 " + perfWatch.ElapsedMilliseconds + " ms（判据 <= 10000），扫出 " + perfGroups + " 组。"
                + "注意：全文件哈希是懒算的，只对分到组里的候选算，所以这个数字反映的是分组成本"));

        window.Close();

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
            Path.Combine(paths.TempDir, "duplicate-smoke.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));

        return report;
    }

    private static string NewSeedFile(string dir, string name, string content)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    /// <summary>直接写库造样本：这一层要验的是重复检测，不是导入。</summary>
    private static async Task SeedAsync(IBookRepository repo, string title, string authors, string filePath)
    {
        var now = DateTimeOffset.Now;

        var book = new Book
        {
            Title = title,
            Authors = authors,
            CreatedAt = now,
            UpdatedAt = now,
        };

        book.Formats.Add(new BookFormat
        {
            BookId = book.Id,
            Format = BookFormatKind.Epub,
            FilePath = filePath,
            FileSize = File.Exists(filePath) ? new FileInfo(filePath).Length : 1024,
            QuickFingerprint = Guid.NewGuid().ToString("N").ToUpperInvariant(),
            Missing = false,
            AddedAt = now,
        });

        await repo.AddAsync(book).ConfigureAwait(true);
    }
}
