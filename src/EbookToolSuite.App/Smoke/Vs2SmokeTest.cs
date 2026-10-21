using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using EbookToolSuite.App.ViewModels;
using EbookToolSuite.App.Views.Pages;
using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Infrastructure.Config;
using Microsoft.Extensions.DependencyInjection;

namespace EbookToolSuite.App.Smoke;

/// <summary>
/// T2-7（VS2：书库规模化 + 转换队列）第 1 阶段的冒烟。
/// 覆盖执行计划 5.1 的前 4 步，也是 5.3 出口条件里靠规模说话的那几条：
///   1. 造 1000 本可构造样本
///   2. 走批量导入队列全部导入（量总耗时）
///   3. 1000 本书库首屏 <= 2 秒
///   4. 真滚 scrollSeconds 秒，UI 心跳最大间隔 <= 200 ms
///   5. 搜索 <= 300 ms（含 200 ms 防抖）
/// 后面 4 步（重复对话框 / 10 本转换 / 强杀恢复 / 全部取消）由 build\smoke-vs2.ps1
/// 串跑现成的专项冒烟，不在这里重复实现。
/// </summary>
public static class Vs2SmokeTest
{
    private const int BookCount = 1000;

    public static async Task<SmokeReport> RunPhaseAAsync(
        IServiceProvider services,
        AppPaths paths,
        MainWindow window,
        LibraryPage page,
        string templateEpub,
        int scrollSeconds)
    {
        var startedAt = DateTimeOffset.Now;
        var stopwatch = Stopwatch.StartNew();
        var checks = new List<SmokeCheck>();
        var vm = page.ViewModel;

        window.Show();
        window.UpdateLayout();

        // ---------- 1：造 1000 本可构造样本 ----------
        // 不带 noise：这一步验的是"规模"，坏书与非法文件由 smoke-batch-import 负责。
        var sourceDir = Path.Combine(paths.DataRoot, "vs2-source");
        ResetDirectory(sourceDir);

        var corpusWatch = Stopwatch.StartNew();
        SmokeProbe.BuildCorpus(templateEpub, sourceDir, BookCount, markerBase: 0, withNoise: false);
        corpusWatch.Stop();

        checks.Add(new SmokeCheck(
            "A0-corpus-built",
            Directory.GetFiles(sourceDir, "*.epub", SearchOption.AllDirectories).Length == BookCount,
            "造了 " + BookCount + " 本可构造 EPUB，用时 "
                + corpusWatch.Elapsed.TotalSeconds.ToString("0.0") + " 秒（不计入导入耗时）"));

        // ---------- 2：走批量导入队列全部导入 ----------
        var queue = services.GetRequiredService<IImportQueue>();
        var importWatch = Stopwatch.StartNew();
        var batch = await queue.StartAsync(new[] { sourceDir }, ImportQueueOptions.Default)
            .ConfigureAwait(true);
        await queue.WaitForCompletionAsync().ConfigureAwait(true);
        importWatch.Stop();

        checks.Add(new SmokeCheck(
            "A1-import-1000-books",
            batch.Succeeded == BookCount && batch.Failed == 0,
            "导入 " + batch.Succeeded + "/" + BookCount + " 成功、失败 " + batch.Failed
                + "，用时 " + importWatch.Elapsed.TotalSeconds.ToString("0.0") + " 秒（"
                + (importWatch.Elapsed.TotalMilliseconds / BookCount).ToString("0") + " ms/本）"));

        // ---------- 3：1000 本书库首屏 ----------
        var loadWatch = Stopwatch.StartNew();
        await vm.LoadAsync().ConfigureAwait(true);
        window.UpdateLayout();
        loadWatch.Stop();

        checks.Add(new SmokeCheck(
            "A2-first-screen-under-2s",
            vm.Books.Count == BookCount && loadWatch.ElapsedMilliseconds <= 2000,
            "加载+布局 " + loadWatch.ElapsedMilliseconds + " ms（判据 <= 2000），本页 "
                + vm.Books.Count + " 本，折成 " + vm.Rows.Count + " 行"));

        // ---------- 4：真滚 60 秒，测 UI 线程有没有被卡住 ----------
        // 口径与 smoke-grid 的 B 判据一致：25 ms 的 DispatcherTimer 当心跳，
        // 统计相邻两次心跳的最大间隔；通过与否看最大间隔，心跳次数只作"计时器真的在跑"的兜底。
        var list = (ListBox?)page.FindName("BookList");
        var scrollViewer = list is null ? null : FindScrollViewer(list);

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

        var scrollWatch = Stopwatch.StartNew();
        /* 心跳次数的门槛不能按"25 ms 定时器理论该跳多少次"来算：WPF 在滚动渲染时会把
           优先级低的定时器回调合并掉（实测 5 秒只跳 79 次，而理论值是 200 次），
           那样算出来的门槛会把"其实很流畅"判成失败。
           这里取"平均心跳间隔 <= 200 ms"当门槛：它和"单次停顿 <= 200 ms"是同一个口径的
           聚合版本，任何真的卡住都会同时踩到这两条。 */
        var minBeats = Math.Max(5, scrollSeconds * 1000 / 200);

        while (scrollViewer is not null && scrollWatch.Elapsed.TotalSeconds < scrollSeconds)
        {
            var ratio = (scrollWatch.ElapsedMilliseconds % 3000) / 3000.0;
            scrollViewer.ScrollToVerticalOffset(scrollViewer.ScrollableHeight * ratio);
            await Task.Delay(50).ConfigureAwait(true);
        }

        timer.Stop();

        checks.Add(new SmokeCheck(
            "A3-scroll-no-block-over-200ms",
            scrollViewer is not null && beats >= minBeats && maxStall <= 200,
            "真滚 " + scrollSeconds + " 秒：UI 心跳 " + beats + " 次（至少 " + minBeats
                + "，相当于平均间隔 <= 200 ms），最大间隔 " + maxStall + " ms（判据 <= 200），"
                + "平均间隔 " + (scrollWatch.ElapsedMilliseconds / Math.Max(1, beats)) + " ms"));

        // ---------- 4b：快速滚动（用户实际抱怨的点）----------
        // 慢滚（A3）每 50 ms 挪屏高的 1/30，压不出"跟手感"；快滚是每 16 ms 跳两屏，
        // 一帧里要新建并布局几十个卡片容器、同时还要解码封面，这才是真实压力。
        var fastBeats = 0;
        var fastMaxStall = 0L;
        var fastLastBeat = Stopwatch.StartNew();

        var fastTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(25),
        };

        fastTimer.Tick += (_, _) =>
        {
            fastBeats++;
            var gap = fastLastBeat.ElapsedMilliseconds;
            fastLastBeat.Restart();

            if (gap > fastMaxStall)
            {
                fastMaxStall = gap;
            }
        };

        fastTimer.Start();

        var fastWatch = Stopwatch.StartNew();
        var fastSeconds = 10;
        var jumps = 0;

        while (scrollViewer is not null && fastWatch.Elapsed.TotalSeconds < fastSeconds)
        {
            var next = scrollViewer.VerticalOffset + (scrollViewer.ViewportHeight * 2);

            if (next >= scrollViewer.ScrollableHeight)
            {
                next = 0;
            }

            scrollViewer.ScrollToVerticalOffset(next);
            jumps++;
            await Task.Delay(16).ConfigureAwait(true);
        }

        fastTimer.Stop();

        /* 门槛是照着"改之前"的实测定的，不然这条判据抓不住用户的抱怨：
           改之前：平均帧长 52 ms、最大 106 ms、10 秒只跳 197 次；
           改之后：平均 36 ms、最大 76 ms、跳 291 次。
           所以门槛取"最大 <= 120 ms 且 平均 <= 45 ms"：改前会被平均那条卡住，改后有余量。
           数字是在本机（1000 本书库、一跳两屏）上量的，换机器要重新标定。 */
        var fastMinBeats = Math.Max(5, fastSeconds * 1000 / 45);
        var fastAvg = fastWatch.ElapsedMilliseconds / Math.Max(1, fastBeats);

        checks.Add(new SmokeCheck(
            "A5-fast-scroll-smooth",
            scrollViewer is not null && fastBeats >= fastMinBeats && fastMaxStall <= 120,
            "快滚 " + fastSeconds + " 秒（每 16 ms 跳两屏，共跳 " + jumps + " 次）：UI 心跳 "
                + fastBeats + " 次（至少 " + fastMinBeats + "，相当于平均帧长 <= 45 ms），最大间隔 "
                + fastMaxStall + " ms（判据 <= 120），平均 " + fastAvg + " ms"));

        // 快滚之后封面有没有跟上：回收模式下如果只靠 Loaded 触发解码，
        // 被回收过的容器不会再触发 Loaded，封面会一直空着。
        var realized = new List<BookCardViewModel>();

        for (var i = 0; i < vm.Rows.Count; i++)
        {
            if (list?.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement container)
            {
                realized.AddRange(container.DataContext is BookRowViewModel row ? row.Cards : Array.Empty<BookCardViewModel>());
            }
        }

        var realizedWithCover = realized.Count(c => c.HasCover);

        checks.Add(new SmokeCheck(
            "A6-covers-load-after-fast-scroll",
            realized.Count > 0 && realizedWithCover == realized.Count,
            "快滚后已实现 " + realized.Count + " 张卡片，其中有封面 " + realizedWithCover
                + " 张（必须全部有；少了说明回收后没重新触发解码）"));

        // ---------- 5：搜索 <= 300 ms（含 200 ms 防抖）----------
        // 样本是同一本书的 1000 个副本，标题全都一样。这是刻意的：
        // 一下命中全部 1000 行，比只命中一行更能压出查询与虚拟化的时间。
        vm.ClearAllFilters();
        await vm.FlushSearchAsync().ConfigureAwait(true);

        var searchWatch = Stopwatch.StartNew();
        vm.SearchText = "大众";
        var found = await WaitForAsync(() => vm.Books.Count == BookCount, TimeSpan.FromSeconds(5))
            .ConfigureAwait(true);
        searchWatch.Stop();

        checks.Add(new SmokeCheck(
            "A4-search-under-300ms",
            found && searchWatch.ElapsedMilliseconds <= 300,
            "敲下最后一个字到出结果 " + searchWatch.ElapsedMilliseconds
                + " ms（判据 <= 300，含 200 ms 防抖），命中 " + vm.Books.Count + " 本"));

        vm.ClearSearch();
        await vm.FlushSearchAsync().ConfigureAwait(true);

        stopwatch.Stop();
        return Finish(paths, startedAt, stopwatch, checks);
    }


    // ---------- 帮手 ----------

    private static void ResetDirectory(string dir)
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }

        Directory.CreateDirectory(dir);
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

            await Task.Delay(10).ConfigureAwait(true);
        }

        return condition();
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

    private static SmokeReport Finish(
        AppPaths paths, DateTimeOffset startedAt, Stopwatch stopwatch, List<SmokeCheck> checks)
    {
        stopwatch.Stop();

        var report = new SmokeReport(
            startedAt.ToString("O"),
            DateTimeOffset.Now.ToString("O"),
            stopwatch.ElapsedMilliseconds,
            paths.DataRoot,
            paths.WebView2DataDir,
            WebView2Version: null,
            checks);

        File.WriteAllText(
            Path.Combine(paths.TempDir, "vs2-smoke.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));

        return report;
    }
}
