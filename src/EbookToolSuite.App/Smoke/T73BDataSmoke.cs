using System.Reflection;
using EbookToolSuite.App.Commands;
using EbookToolSuite.App.ViewModels;
using EbookToolSuite.App.Views;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Errors;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Infrastructure.Config;
using Microsoft.Extensions.DependencyInjection;

namespace EbookToolSuite.App.Smoke;

/// <summary>
/// T7-3B「数据与采集」的冒烟检查（由外层汇总冒烟调用，不启动 app、不开 WebView2）：
/// <list type="bullet">
/// <item>A. 书库检索 / 筛选 / 排序状态持久化：设条件 → 重开书库 → 恢复一致。</item>
/// <item>B. 阅读会话采集端到端：Start → ReportActivity → End → 会话落库、统计快照合理。</item>
/// <item>C. 阅读窗口确实接了采集器（反射检查字段与构造参数，不必真开窗口）。</item>
/// </list>
/// </summary>
internal static class T73BDataSmoke
{
    /// <summary>检查 A 的探针搜索词（故意用不太可能出现在真实书库里的串）。</summary>
    private const string ProbeSearchText = "T73B持久化探针";

    /// <summary>检查 A 的探针排序：作者 A-Z（非默认排序，恢复时能看出差异）。</summary>
    private static readonly SortOption ProbeSort =
        SortOption.AllOptions.First(o => o.Sort == BookSort.Author);

    /// <summary>检查 A 的探针格式筛选：PDF（非"全部格式"）。</summary>
    private static readonly FormatOption ProbeFormat =
        FormatOption.AllOptions.First(o => o.Kind == BookFormatKind.Pdf);

    public static async Task<IReadOnlyList<SmokeCheck>> RunAsync(
        IServiceProvider services,
        AppPaths paths,
        string workDirectory,
        CancellationToken ct)
    {
        var checks = new List<SmokeCheck>
        {
            await CheckLibraryQueryStateAsync(services, ct).ConfigureAwait(true),
            await CheckSessionCaptureAsync(services, paths, ct).ConfigureAwait(true),
            CheckReaderWiring(),
        };

        return checks;
    }

    /// <summary>
    /// A. 书库状态持久化：用 DI 里的 LibraryViewModel 设搜索词 / 排序 / 格式筛选，
    /// 再新建一个实例模拟"重开书库"，断言三样都从 Config 恢复；最后恢复默认避免污染后续。
    /// </summary>
    private static async Task<SmokeCheck> CheckLibraryQueryStateAsync(
        IServiceProvider services,
        CancellationToken ct)
    {
        const string name = "T73B-A-library-query-state-persisted";

        var viewModel = services.GetRequiredService<LibraryViewModel>();
        var previousDebounce = viewModel.SearchDebounce;

        try
        {
            // 冒烟不等防抖：条件设完就 flush，把"保存 + 查询"这条真实路径走到。
            viewModel.SearchDebounce = TimeSpan.Zero;

            viewModel.SearchText = ProbeSearchText;
            viewModel.SelectedSort = ProbeSort;
            viewModel.SelectedFormat = ProbeFormat;

            await viewModel.FlushSearchAsync(ct).ConfigureAwait(true);

            // 模拟重开书库：构造时应从 Config 同步恢复，且不覆盖已保存的状态。
            var reopened = ActivatorUtilities.CreateInstance<LibraryViewModel>(services);

            var restored = reopened.SearchText == ProbeSearchText
                && reopened.SelectedSort == ProbeSort
                && reopened.SelectedFormat == ProbeFormat;

            var detail = restored
                ? "重开恢复：搜索=" + reopened.SearchText
                  + "，排序=" + reopened.SelectedSort.Label
                  + "，格式=" + reopened.SelectedFormat.Label
                : "重开不一致：搜索=" + reopened.SearchText
                  + "，排序=" + reopened.SelectedSort.Label
                  + "，格式=" + reopened.SelectedFormat.Label
                  + "（期望 搜索=" + ProbeSearchText
                  + "，排序=" + ProbeSort.Label
                  + "，格式=" + ProbeFormat.Label + "）";

            return new SmokeCheck(name, restored, detail);
        }
        catch (Exception ex)
        {
            return new SmokeCheck(name, false, "异常：" + ex.GetType().Name + "：" + ex.SafeMessage());
        }
        finally
        {
            try
            {
                // 恢复默认：探针条件只用于这次断言，不能留给后面的检查或真实使用。
                viewModel.SearchText = string.Empty;
                viewModel.SelectedFormat = FormatOption.All;
                viewModel.SelectedStatus = StatusOption.All;
                viewModel.SelectedSort = SortOption.Default;
                viewModel.OnlyWithCover = false;

                await viewModel.FlushSearchAsync(CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception)
            {
                // 恢复失败不能掩盖主检查的结果
            }
            finally
            {
                viewModel.SearchDebounce = previousDebounce;
            }
        }
    }

    /// <summary>
    /// B. 会话采集端到端：造一本探针书，Start → ReportActivity(2, 1200) → End，
    /// 断言仓储里正好一条会话且 pages/chars 对得上；再断言统计快照的数字合理。
    /// 结束时删掉探针书（会话随 ON DELETE CASCADE 一起删），不给后续检查留垃圾。
    /// </summary>
    private static async Task<SmokeCheck> CheckSessionCaptureAsync(
        IServiceProvider services,
        AppPaths paths,
        CancellationToken ct)
    {
        const string name = "T73B-B-reading-session-capture";

        var repo = services.GetRequiredService<IBookRepository>();
        var sessions = services.GetRequiredService<IReadingSessionService>();
        var statsRepo = services.GetRequiredService<IReadingStatsRepository>();
        var stats = services.GetRequiredService<IReadingStatsService>();

        var book = new Book
        {
            Id = Guid.NewGuid(),
            Title = "T73B 会话采集探针",
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now,
        };

        try
        {
            await repo.AddAsync(book, ct).ConfigureAwait(true);

            await sessions.StartAsync(book.Id, ct).ConfigureAwait(true);
            var activeAfterStart = sessions.IsActive(book.Id);

            sessions.ReportActivity(book.Id, 2, 1200);

            await sessions.EndAsync(book.Id, ct).ConfigureAwait(true);
            var inactiveAfterEnd = !sessions.IsActive(book.Id);

            var all = await statsRepo.ListSessionsAsync(ct).ConfigureAwait(true);
            var mine = all.Where(s => s.BookId == book.Id).ToList();

            var captured = mine.Count == 1
                && mine[0].PagesRead == 2
                && mine[0].CharsRead == 1200;

            // 统计页取一次全量快照：不断言具体数值（受同一数据目录里既有数据影响），只要"合理"。
            var snapshot = await stats.GetAsync(ReadingStatsRange.All, ct).ConfigureAwait(true);

            var statsSane = snapshot.TotalBooks >= 1
                && snapshot.RecentBooks.Count <= 8
                && snapshot.HasSessions;

            var ok = activeAfterStart && inactiveAfterEnd && captured && statsSane;

            var detail = ok
                ? "会话采集：pages=" + mine[0].PagesRead + " chars=" + mine[0].CharsRead
                  + "；统计 TotalBooks=" + snapshot.TotalBooks
                  + "，RecentBooks=" + snapshot.RecentBooks.Count
                  + "，HasSessions=" + snapshot.HasSessions
                  + "；数据根=" + paths.DataRoot
                : "Start 后 IsActive=" + activeAfterStart
                  + "，End 后 IsActive=" + !inactiveAfterEnd
                  + "，会话 " + mine.Count + " 条"
                  + (mine.Count == 1
                      ? "（pages=" + mine[0].PagesRead + " 期望 2，chars=" + mine[0].CharsRead + " 期望 1200）"
                      : string.Empty)
                  + "；统计 TotalBooks=" + snapshot.TotalBooks
                  + "，RecentBooks=" + snapshot.RecentBooks.Count
                  + "，HasSessions=" + snapshot.HasSessions;

            return new SmokeCheck(name, ok, detail);
        }
        catch (Exception ex)
        {
            return new SmokeCheck(name, false, "异常：" + ex.GetType().Name + "：" + ex.SafeMessage());
        }
        finally
        {
            try
            {
                await repo.DeleteAsync(book.Id, CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception)
            {
                // 清理失败只是留一本探针书，不能影响检查结论
            }
        }
    }

    /// <summary>
    /// C. ReaderWindow 已接采集器：反射检查私有字段与构造参数（不真开 WebView2），
    /// 顺带检查生产路径 ReadCommand 的构造函数也接了 IReadingSessionService。
    /// </summary>
    private static SmokeCheck CheckReaderWiring()
    {
        const string name = "T73B-C-reader-window-stats-wired";

        var statsField = typeof(ReaderWindow).GetField(
            "_stats", BindingFlags.Instance | BindingFlags.NonPublic);

        var readerCtorWired = typeof(ReaderWindow)
            .GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Any(p => p.ParameterType == typeof(IReadingSessionService));

        var commandCtorWired = typeof(ReadCommand)
            .GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Any(p => p.ParameterType == typeof(IReadingSessionService));

        var fieldWired = statsField is not null
            && statsField.FieldType == typeof(IReadingSessionService);

        var ok = fieldWired && readerCtorWired && commandCtorWired;

        return new SmokeCheck(
            name,
            ok,
            (fieldWired ? "ReaderWindow._stats 字段已存在" : "ReaderWindow 找不到 IReadingSessionService 字段")
            + "；"
            + (readerCtorWired ? "ReaderWindow 构造函数已接采集器" : "ReaderWindow 构造函数缺采集器参数")
            + "；"
            + (commandCtorWired ? "ReadCommand 构造函数已接采集器" : "ReadCommand 构造函数缺采集器参数"));
    }
}
