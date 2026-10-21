using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using EbookToolSuite.App.ViewModels;
using EbookToolSuite.App.Views.Pages;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Infrastructure.Config;

namespace EbookToolSuite.App.Smoke;

/// <summary>
/// T2-3 网格冒烟。样本直接写库造 1000 本（这一层验的是网格与查询的性能，不是导入）：
/// 1000 本首屏 <= 2 秒、虚拟化只实现可见行、滚动不卡、搜索 <= 300 ms（含 200 ms 防抖）、
/// 组合筛选、三种空状态。
/// </summary>
public static class GridSmokeTest
{
    private const int BookCount = 1000;

    public static async Task<SmokeReport> RunAsync(
        AppPaths paths,
        MainWindow window,
        LibraryPage page,
        IBookRepository repo)
    {
        var startedAt = DateTimeOffset.Now;
        var stopwatch = Stopwatch.StartNew();
        var checks = new List<SmokeCheck>();
        var vm = page.ViewModel;

        window.Show();
        window.UpdateLayout();

        // ---------- E0：库空时的空状态 ----------
        await vm.LoadAsync().ConfigureAwait(true);
        window.UpdateLayout();

        checks.Add(new SmokeCheck(
            "E0-empty-library-state",
            vm.EmptyTitle == "书库还没有书" && !vm.HasEmptyAction,
            "标题=" + vm.EmptyTitle + "，有清除按钮=" + vm.HasEmptyAction));

        // ---------- 造 1000 本 ----------
        var seedWatch = Stopwatch.StartNew();
        await SeedAsync(repo).ConfigureAwait(true);
        seedWatch.Stop();

        // ---------- A：1000 本首屏 ----------
        var loadWatch = Stopwatch.StartNew();
        await vm.LoadAsync().ConfigureAwait(true);
        window.UpdateLayout();
        loadWatch.Stop();

        checks.Add(new SmokeCheck(
            "A-1000-books-first-screen-under-2s",
            vm.Books.Count == BookCount && loadWatch.ElapsedMilliseconds <= 2000,
            "加载+布局 " + loadWatch.ElapsedMilliseconds + " ms（判据 <= 2000），本页 "
                + vm.Books.Count + " 本，折成 " + vm.Rows.Count + " 行"
                + "（造样本另花 " + seedWatch.ElapsedMilliseconds + " ms，不计入）"));

        var list = (ListBox?)page.FindName("BookList");
        var lastRow = Math.Max(0, vm.Rows.Count - 1);
        var firstRealized = list?.ItemContainerGenerator.ContainerFromIndex(0) is not null;
        var lastRealized = list?.ItemContainerGenerator.ContainerFromIndex(lastRow) is not null;

        checks.Add(new SmokeCheck(
            "A-virtualized-only-visible-rows-realized",
            list is not null && firstRealized && !lastRealized,
            "第 1 行已实现=" + firstRealized + "，第 " + (lastRow + 1) + " 行已实现=" + lastRealized
                + "（后者必须是 False，否则等于没虚拟化）"));

        checks.Add(new SmokeCheck(
            "A-covers-not-all-decoded",
            vm.Books.Count(c => c.HasCover) < BookCount,
            "1000 本里已解码封面 " + vm.Books.Count(c => c.HasCover) + " 张（必须小于总本数）"));

        // ---------- B：连续滚动 2 秒，看 UI 线程有没有被卡住 ----------
        var beats = 0;
        var maxStall = 0L;
        var lastBeat = Stopwatch.StartNew();

        var timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(25),
        };

        timer.Tick += (_, _) =>
        {
            beats++;
            var gap = lastBeat.ElapsedMilliseconds;
            lastBeat.Restart();

            if (gap > maxStall)
            {
                maxStall = gap;
            }
        };

        timer.Start();

        var scrollViewer = list is null ? null : FindScrollViewer(list);
        var scrollWatch = Stopwatch.StartNew();

        /* 不在这里显式 UpdateLayout：真实用户滚动也不会这么干，而那会把"冒烟自己的开销"
           算进卡顿里。这里只是把滚动位置来回推，让 WPF 按正常的渲染节拍去布局。 */
        while (scrollViewer is not null && scrollWatch.ElapsedMilliseconds < 3000)
        {
            var ratio = (scrollWatch.ElapsedMilliseconds % 1500) / 1500.0;
            scrollViewer.ScrollToVerticalOffset(scrollViewer.ScrollableHeight * ratio);
            await Task.Delay(50).ConfigureAwait(true);
        }

        timer.Stop();

        checks.Add(new SmokeCheck(
            "B-scroll-no-block-over-200ms",
            // 心跳次数只作"计时器真的在跑"的兜底；通过与否看的是有没有超过 200 ms 的停顿
            scrollViewer is not null && beats >= 5 && maxStall <= 200,
            "滚动 3 秒：UI 心跳 " + beats + " 次，最大间隔 " + maxStall + " ms（判据 <= 200）"));

        // ---------- C：搜索（判据 3：停止输入后 <= 300 ms 出结果，含 200 ms 防抖）----------
        vm.ClearAllFilters();
        await vm.FlushSearchAsync().ConfigureAwait(true);

        var searchWatch = Stopwatch.StartNew();
        vm.SearchText = "冒烟书 0500";
        var found = await WaitForAsync(() => vm.Books.Count == 1, TimeSpan.FromSeconds(5)).ConfigureAwait(true);
        searchWatch.Stop();

        checks.Add(new SmokeCheck(
            "C-search-under-300ms-including-debounce",
            found && searchWatch.ElapsedMilliseconds <= 300,
            "敲下最后一个字到出结果 " + searchWatch.ElapsedMilliseconds + " ms（判据 <= 300，含 200 ms 防抖），命中 "
                + vm.Books.Count + " 本"));

        await vm.FlushSearchAsync().ConfigureAwait(true);

        checks.Add(new SmokeCheck(
            "C-search-matches-title-terms-with-and",
            vm.Books.Count == 1 && vm.Books[0].Title == "冒烟书 0500",
            "两词 AND：命中 " + vm.Books.Count + " 本，第一本=" + (vm.Books.Count > 0 ? vm.Books[0].Title : "(无)")));

        // ---------- D：组合筛选（格式=EPUB + 有封面 + 未读）----------
        vm.ClearAllFilters();
        vm.SelectedFormat = vm.FormatOptions.First(o => o.Kind == BookFormatKind.Epub);
        vm.OnlyWithCover = true;
        vm.SelectedStatus = vm.StatusOptions.First(o => o.Status == ReadingStatus.Unread);
        await vm.FlushSearchAsync().ConfigureAwait(true);

        // 样本按 i 的奇偶/模数造，期望值用同一套规则算出来，不依赖检索实现
        var expected = 0;
        for (var i = 0; i < BookCount; i++)
        {
            if (i % 2 == 0 && i % 5 != 0 && i % 3 != 0)
            {
                expected++;
            }
        }

        checks.Add(new SmokeCheck(
            "D-combo-filter-epub-cover-unread",
            vm.Books.Count == expected && vm.Chips.Count == 3,
            "EPUB + 有封面 + 未读：命中 " + vm.Books.Count + " 本（期望 " + expected + "），chip 数 "
                + vm.Chips.Count + "（期望 3）"));

        // ---------- D2：只按"在读"筛（这条是用户报过的 bug 的回归判据）----------
        vm.ClearAllFilters();
        vm.SelectedStatus = vm.StatusOptions.First(o => o.Status == ReadingStatus.Reading);
        await vm.FlushSearchAsync().ConfigureAwait(true);

        var expectedReading = 0;
        for (var i = 0; i < BookCount; i++)
        {
            if (i % 3 == 0)
            {
                expectedReading++;
            }
        }

        checks.Add(new SmokeCheck(
            "D2-reading-status-filter-not-empty",
            vm.Books.Count == expectedReading && vm.Books.Count > 0,
            "按在读筛：命中 " + vm.Books.Count + " 本（期望 " + expectedReading + "）"));

        // ---------- E：三种空状态 + chip 能点掉 ----------
        vm.ClearAllFilters();
        vm.SearchText = "绝对找不到的关键词xyz";
        await vm.FlushSearchAsync().ConfigureAwait(true);

        var searchEmptyTitle = vm.EmptyTitle;
        var searchEmptyOk = searchEmptyTitle.Contains("没有找到")
            && vm.HasEmptyAction && vm.EmptyActionText == "清除筛选";

        vm.ClearAllFilters();
        vm.SelectedFormat = vm.FormatOptions.First(o => o.Kind == BookFormatKind.Cbz);
        await vm.FlushSearchAsync().ConfigureAwait(true);

        var filterEmptyTitle = vm.EmptyTitle;
        var filterEmptyOk = filterEmptyTitle == "当前筛选条件下没有书"
            && vm.HasEmptyAction && vm.Books.Count == 0;
        var chipCount = vm.Chips.Count;

        if (chipCount > 0)
        {
            vm.Chips[0].Remove.Execute(null);
        }

        await vm.FlushSearchAsync().ConfigureAwait(true);
        var restored = vm.Books.Count == BookCount && vm.Chips.Count == 0;

        checks.Add(new SmokeCheck(
            "E-three-empty-states-and-chips",
            searchEmptyOk && filterEmptyOk && chipCount == 1 && restored,
            "搜不到时标题=" + searchEmptyTitle + "（判定 " + searchEmptyOk + "），筛空时标题="
                + filterEmptyTitle + "（判定 " + filterEmptyOk + "），chip=" + chipCount
                + "，点掉 chip 后恢复 " + vm.Books.Count + " 本（判定 " + restored + "）"));

        // ---------- F：筛选栏控件的对齐（用户报过"框内文字偏上"）----------
        var combo = (ComboBox?)page.FindName("FormatFilter");
        combo?.UpdateLayout();

        var text = combo is null ? null : FindDescendant<ContentPresenter>(combo);
        double comboCenter = 0, textCenter = 0, textTop = 0, textBottom = 0;

        if (combo is not null && text is not null)
        {
            var origin = text.TransformToAncestor(combo).Transform(new Point(0, 0));
            textTop = origin.Y;
            textBottom = origin.Y + text.ActualHeight;
            textCenter = (textTop + textBottom) / 2;
            comboCenter = combo.ActualHeight / 2;
        }

        var offset = Math.Abs(textCenter - comboCenter);

        checks.Add(new SmokeCheck(
            "F-filter-combo-text-vertically-centered",
            combo is not null && text is not null && offset <= 2,
            "格式下拉：框高 " + combo?.ActualHeight.ToString("0.#") + "，文字 " + textTop.ToString("0.#")
                + "~" + textBottom.ToString("0.#") + "，中心差 " + offset.ToString("0.#") + " px（判据 <= 2）"));

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
            Path.Combine(paths.TempDir, "grid-smoke.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));

        return report;
    }

    /// <summary>直接写库造 1000 本。规则要和判据里那个 expected 循环保持一致。</summary>
    private static async Task SeedAsync(IBookRepository repo)
    {
        var now = DateTimeOffset.Now;

        for (var i = 0; i < BookCount; i++)
        {
            var book = new Book
            {
                Title = "冒烟书 " + (i + 1).ToString("D4"),
                Authors = "冒烟作者",
                Status = i % 3 == 0 ? ReadingStatus.Reading : ReadingStatus.Unread,
                CoverPath = i % 5 == 0 ? null : "Covers/fake-" + i + ".jpg",
                CreatedAt = now.AddSeconds(-i),
                UpdatedAt = now,
            };

            book.Formats.Add(new BookFormat
            {
                BookId = book.Id,
                Format = i % 2 == 0 ? BookFormatKind.Epub : BookFormatKind.Mobi,
                FilePath = "Library/smoke-" + i.ToString("D4") + (i % 2 == 0 ? ".epub" : ".mobi"),
                FileSize = 1000 + i,
                FileModifiedAt = now,
                AddedAt = now,
            });

            await repo.AddAsync(book).ConfigureAwait(true);
        }
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.Now + timeout;

        while (DateTimeOffset.Now < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(5).ConfigureAwait(true);
        }

        return condition();
    }

    /// <summary>在可视树里找第一个指定类型的后代。量控件对齐时要用。</summary>
    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);

        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);

            if (child is T match)
            {
                return match;
            }

            var nested = FindDescendant<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    /// <summary>在可视树里找第一个 ScrollViewer（ListBox 的滚动条藏在里面）。</summary>
    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);

        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);

            if (child is ScrollViewer found)
            {
                return found;
            }

            var nested = FindScrollViewer(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }
}
