using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EbookToolSuite.App.Navigation;
using EbookToolSuite.App.ViewModels;
using EbookToolSuite.App.Views;
using EbookToolSuite.App.Views.Pages;
using EbookToolSuite.Core;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Infrastructure.Config;

namespace EbookToolSuite.App.Smoke;

/// <summary>
/// 书库网格冒烟（T1-3 验收里能自动化的部分）。
/// 50 本书的样本由本类当场构造：每本一个占位文件，前 20 本再配一张真 JPEG 缩略图。
/// 只验证列表、封面、双击、滚动这几件事，不碰任何解析。
/// </summary>
public static class LibrarySmokeTest
{
    private const int BookCount = 50;
    private const int CoverCount = 20;

    public static async Task<SmokeReport> RunAsync(
        MainWindow window,
        NavigationService nav,
        PageRegistry registry,
        AppPaths paths,
        IBookRepository repo,
        IConversionService conversion)
    {
        var startedAt = DateTimeOffset.Now;
        var stopwatch = Stopwatch.StartNew();
        var checks = new List<SmokeCheck>();

        FrameworkElement? current = null;
        nav.ContentChanged += (_, element) => current = element;

        // 必须真的量一次布局才能拿到卡片容器：窗口不显示时 ItemsControl 不会生成容器。
        // 真机里窗口当然是显示的，冒烟照做才不会验出假问题。
        if (!window.IsVisible)
        {
            window.Show();
            await Task.Delay(250).ConfigureAwait(true);
        }

        // ---------- 空库：空状态可见、网格隐藏 ----------
        await nav.NavigateAsync(Routes.Queue).ConfigureAwait(true);
        await nav.NavigateAsync(Routes.Library).ConfigureAwait(true);
        window.UpdateLayout();

        var page = current as LibraryPage;
        if (page is null)
        {
            checks.Add(new SmokeCheck("library-page-reachable", false, "导航到书库页没拿到 LibraryPage"));
            return Finish(paths, startedAt, stopwatch, checks);
        }

        var vm = page.ViewModel;
        checks.Add(new SmokeCheck(
            "empty-state-when-no-books",
            vm.ShowEmptyState && !vm.ShowGrid && vm.Books.Count == 0,
            "ShowEmptyState=" + vm.ShowEmptyState + " ShowGrid=" + vm.ShowGrid));

        // ---------- 造 50 本书 ----------
        await SeedAsync(paths, repo, BookCount, CoverCount).ConfigureAwait(true);

        await nav.NavigateAsync(Routes.Queue).ConfigureAwait(true);
        await nav.NavigateAsync(Routes.Library).ConfigureAwait(true);
        window.UpdateLayout();

        // T2-3：网格换成虚拟化的"行"了（ListBox 的 Items 是行，不是书）。
        var list = (ItemsControl?)page.FindName("BookList");
        var rowCount = vm.Rows.Count;
        var inRows = vm.Rows.Sum(r => r.Cards.Count);

        checks.Add(new SmokeCheck(
            "grid-renders-50-books",
            list is not null && vm.Books.Count == BookCount
                && list.Items.Count == rowCount
                && inRows == BookCount,
            "VM=" + vm.Books.Count + " 本，折成 " + rowCount + " 行，行内合计 " + inRows
                + " 本，ListBox.Items=" + (list?.Items.Count ?? -1)));

        // ---------- 封面：T2-3 起改成"实现出来才解码"，所以只等屏幕上那几张 ----------
        await WaitForCoversAsync(vm, 1, TimeSpan.FromSeconds(10)).ConfigureAwait(true);
        window.UpdateLayout();

        var withCover = vm.Books.Count(c => c.HasCover);
        var allFrozen = vm.Books.Where(c => c.HasCover).All(c => c.Cover!.IsFrozen);

        checks.Add(new SmokeCheck(
            "covers-decoded-and-frozen",
            withCover >= 1 && allFrozen,
            "可见卡片已解码 " + withCover + " 张，全部已 Freeze=" + allFrozen));

        // 懒解码的反向判据：50 本里绝不该有 50 张都被解码（那样 1000 本首屏就废了）
        checks.Add(new SmokeCheck(
            "covers-decoded-lazily",
            withCover < BookCount,
            "共 " + BookCount + " 本，已解码 " + withCover + " 张（必须小于总本数，证明没有一次性全解）"));

        var damaged = vm.Books[0];
        var damagedHasRow = damaged.Metadata.Any(r => r.Label == "校验");
        checks.Add(new SmokeCheck(
            "damaged-flag-visible",
            damaged.IsDamaged && damagedHasRow && !vm.Books[1].IsDamaged,
            "第 1 本 IsDamaged=" + damaged.IsDamaged + " 详情有校验行=" + damagedHasRow
                + " 第 2 本 IsDamaged=" + vm.Books[1].IsDamaged));

        await CheckDoubleClick(page, vm, window, repo, checks).ConfigureAwait(true);
        CheckDetailContent(page, vm, window, checks);
        CheckScrollKept(page, vm, window, checks);
        await CheckCoversMissingAsync(paths, nav, window, checks).ConfigureAwait(true);
        // ---------- 继续阅读角标（T1-6 判据 4）----------

        /* 样本要挑一本"别的判据不碰"的书：CheckDoubleClick 会把 Books[0] 标成弃读/已读完，
           而这条判据要的是"有进度 -> 在读"。用 Books[6]。 */
        var badgeTarget = vm.Books[6].Book;

        await repo.SaveReadingProgressAsync(new ReadingProgress
        {
            BookId = badgeTarget.Id,
            SpineIndex = 1,
            Percent = 0.42,
            UpdatedAt = DateTimeOffset.UtcNow,
        }).ConfigureAwait(true);

        await vm.LoadAsync().ConfigureAwait(true);
        window.UpdateLayout();
        await WaitForCoversAsync(vm, CoverCount, TimeSpan.FromSeconds(10)).ConfigureAwait(true);

        var badged = vm.Books.FirstOrDefault(c => c.Id == badgeTarget.Id);
        var othersClean = vm.Books.Where(c => c.Id != badgeTarget.Id).All(c => !c.IsContinueReading);

        checks.Add(new SmokeCheck(
            "continue-reading-badge",
            badged is not null
                && badged.IsContinueReading
                && !string.IsNullOrEmpty(badged.ProgressText)
                && Math.Abs(badged.ProgressPercent - 0.42) < 0.001
                // T2-3：在读的书角标**只显示进度数字**（用户要求），完整说法在 ToolTip 里
                && badged.ReadingStateText == "42%"
                && badged.ReadingStateFullText == "在读 42%"
                && othersClean,
            "有进度的书 IsContinueReading=" + (badged is null ? "找不到卡片" : badged.IsContinueReading.ToString()) +
            " 文案=" + (badged?.ProgressText ?? "-") +
            " 进度=" + (badged?.ProgressPercent.ToString("0.###") ?? "-") +
            " 角标=" + (badged?.ReadingStateText ?? "-") + "（ToolTip=" + (badged?.ReadingStateFullText ?? "-") + "）"
            + " 其它书都没有角标=" + othersClean));

        // ---------- 删除流程（单本 + 批量，含二次确认的两条路径）----------
        await CheckDeleteFlowAsync(page, vm, paths, repo, checks).ConfigureAwait(true);


        CheckBatchConvert(page, vm, conversion, checks);
        await CheckListViewAsync(page, vm, paths, checks).ConfigureAwait(true);

        // 用户 2026-10-20：详情里的「（已删除）」+「移除该格式」整条链路（真删库、真刷新界面）
        await CheckDeletedFormatFlowAsync(page, vm, window, repo, paths, checks).ConfigureAwait(true);

        // 用户 2026-10-21：多格式书籍的格式角标 + 详情里的「切换此格式」+ MOBI 转 EPUB 的显示口径
        await CheckReadingFormatFlowAsync(page, vm, window, repo, paths, checks).ConfigureAwait(true);

        checks.Add(new SmokeCheck(
            "manual-checks-remaining",
            true,
            "以下项只能人眼验：50 本滚动是否跟手、快速滚动时封面逐张出现的观感、"
                + "卡片文字两行省略的实际效果"));

        return Finish(paths, startedAt, stopwatch, checks);
    }
    // ---------- 样本构造 ----------

    /// <summary>把窗口画成 PNG（详情浮层是 WPF 控件，RenderTargetBitmap 拍得到）。</summary>
    private static void CaptureWindow(Window window, string path)
    {
        window.UpdateLayout();

        var dpi = VisualTreeHelper.GetDpi(window);
        var width = Math.Max(1, (int)Math.Round(window.ActualWidth * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Round(window.ActualHeight * dpi.DpiScaleY));
        var bitmap = new RenderTargetBitmap(width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static async Task SeedAsync(AppPaths paths, IBookRepository repo, int books, int covers)
    {
        for (var i = 1; i <= books; i++)
        {
            var id = Guid.NewGuid();
            var dir = Path.Combine(paths.LibraryDir, id.ToString("D"));
            Directory.CreateDirectory(dir);

            var file = Path.Combine(dir, "smoke-" + i.ToString("D2") + ".epub");
            await File.WriteAllTextAsync(file, "冒烟占位文件，不参与解析").ConfigureAwait(true);

            var now = DateTimeOffset.Now;
            var book = new Book
            {
                Id = id,
                Title = "冒烟书籍 " + i.ToString("D2"),
                Authors = "作者" + i,
                Language = "zh",
                Status = ReadingStatus.Unread,
                Damaged = i == 1,   // 第 1 本标成"宽松兜底读入"，验证未校验角标
                CreatedAt = now,
                UpdatedAt = now,
                Formats =
                {
                    new BookFormat
                    {
                        Id = Guid.NewGuid(),
                        BookId = id,
                        Format = BookFormatKind.Epub,
                        FilePath = file,
                        FileSize = 12345,
                        AddedAt = now,
                    },
                },
            };

            await repo.AddAsync(book).ConfigureAwait(true);

            if (i <= covers)
            {
                WriteThumbnail(paths, id);
            }
        }
    }

    /// <summary>当场画一张真 JPEG 缩略图，文件名与导入管线的约定一致。</summary>
    private static void WriteThumbnail(AppPaths paths, Guid id)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.SteelBlue, null, new Rect(0, 0, 320, 480));
            dc.DrawRectangle(Brushes.White, null, new Rect(40, 60, 240, 14));
        }

        var bitmap = new RenderTargetBitmap(320, 480, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new JpegBitmapEncoder { QualityLevel = 80 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        Directory.CreateDirectory(paths.CoversCacheDir);
        using var stream = File.Create(
            Path.Combine(paths.CoversCacheDir, id.ToString("D") + "_t.jpg"));
        encoder.Save(stream);
    }

    private static async Task WaitForCoversAsync(LibraryViewModel vm, int expected, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.Now + timeout;
        while (DateTimeOffset.Now < deadline && vm.Books.Count(c => c.HasCover) < expected)
        {
            await Task.Delay(50).ConfigureAwait(true);
        }
    }

    /// <summary>可视树里带 InputBindings 的 Border 个数（诊断用，确认容器有没有生成）。</summary>
    private static int CountCards(DependencyObject root)
    {
        var found = root is Border { InputBindings.Count: > 0 } ? 1 : 0;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            found += CountCards(VisualTreeHelper.GetChild(root, i));
        }

        return found;
    }

    /// <summary>在可视树里找第一张卡片：带 InputBindings 的那个 Border。</summary>
    /// <summary>在可视树里一路往下走到叶子（用来验"从很深的元素也能找回卡片"）。</summary>
    private static DependencyObject? DescendToLeaf(DependencyObject? node)
    {
        while (node is not null && VisualTreeHelper.GetChildrenCount(node) > 0)
        {
            node = VisualTreeHelper.GetChild(node, 0);
        }

        return node;
    }
    private static Border? FindFirstCard(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Border { InputBindings.Count: > 0 })
            {
                return (Border)child;
            }

            var deeper = FindFirstCard(child);
            if (deeper is not null)
            {
                return deeper;
            }
        }

        return null;
    }

    // ---------- 各项检查 ----------

    private static async Task CheckDoubleClick(
        LibraryPage page, LibraryViewModel vm, MainWindow window, IBookRepository repo, List<SmokeCheck> checks)
    {
        window.UpdateLayout();

        if (vm.Books.Count == 0)
        {
            checks.Add(new SmokeCheck("double-click-binding-wired", false, "书库是空的，没法验"));
            return;
        }

        var card = FindFirstCard(page);
        var bindings = card?.InputBindings.OfType<MouseBinding>().ToList()
            ?? new List<MouseBinding>();

        // 卡片上有两条绑定（单击选中、双击打开详情），必须按动作挑，不能拿第一条。
        var binding = bindings.FirstOrDefault(b => b.MouseAction == MouseAction.LeftDoubleClick);
        var selectBinding = bindings.FirstOrDefault(b => b.MouseAction == MouseAction.LeftClick);

        var wired = binding is not null
            && binding.MouseAction == MouseAction.LeftDoubleClick
            && binding.Command is not null;

        checks.Add(new SmokeCheck(
            "double-click-binding-wired",
            wired,
            "找到卡片=" + (card is not null) + " 动作=" + (binding?.MouseAction.ToString() ?? "(无)")
                + " 卡片数=" + CountCards(page)
                + " 有命令=" + (binding?.Command is not null)));

        // ---------- 转换入口（T1-4 收尾加的）：单击选中 + 工具栏按钮 + 右键菜单 ----------
        checks.Add(new SmokeCheck(
            "card-single-click-selects",
            selectBinding is not null,
            "单击绑定动作=" + (selectBinding?.MouseAction.ToString() ?? "(无)")));

        var toolbar = page.FindName("ToolbarConvertButton") as Button;
        checks.Add(new SmokeCheck(
            "toolbar-convert-disabled-without-selection",
            toolbar is { IsEnabled: false },
            "按钮存在=" + (toolbar is not null) + " 初始可用=" + (toolbar?.IsEnabled == true)));
        // 工具栏上的导入入口（与「文件」菜单、空状态按钮共用同一个 ImportCommand）。
        // XAML 的 Click="..." 由 XAML 编译器校验（名字写错直接编译失败），
        // 这里验冒烟能验的部分：文案、可见、可用、以及真实布局位置。
        var importFile = page.FindName("ToolbarImportFileButton") as Button;
        var importFolder = page.FindName("ToolbarImportFolderButton") as Button;
        var countText = page.FindName("CountText") as TextBlock;
        var importPanel = importFile?.Parent;
        var countPanel = countText?.Parent;
        var sameRowAsCount = importPanel is not null && ReferenceEquals(importPanel, countPanel);

        // 量真实布局：两个按钮要在「共 N 本」右边、「批量选择」左边，且与右侧按钮组同一条横带
        // （不是被挤到下一行，也不是压在别的控件上）。
        page.UpdateLayout();
        var countRect = BoundsOf(countText, page);
        var fileRect = BoundsOf(importFile, page);
        var folderRect = BoundsOf(importFolder, page);
        var batchRect = BoundsOf(page.FindName("ToggleSelectionButton") as Button, page);

        var laidOut = countRect.Width > 0 && fileRect.Width > 0 && folderRect.Width > 0 && batchRect.Width > 0;
        var rightOfCount = laidOut && fileRect.Left >= countRect.Right - 0.5;
        var leftOfBatch = laidOut && folderRect.Right <= batchRect.Left + 0.5;
        var sameRowBand = laidOut && fileRect.Bottom > batchRect.Top && fileRect.Top < batchRect.Bottom;

        checks.Add(new SmokeCheck(
            "toolbar-import-buttons",
            importFile is { IsEnabled: true, Visibility: Visibility.Visible }
                && importFolder is { IsEnabled: true, Visibility: Visibility.Visible }
                && importFile.Content as string == "导入文件"
                && importFolder.Content as string == "导入文件夹"
                && sameRowAsCount
                && rightOfCount && leftOfBatch && sameRowBand,
            "导入文件=" + (importFile?.Content ?? "(缺)")
                + " 导入文件夹=" + (importFolder?.Content ?? "(缺)")
                + " 同一容器=" + sameRowAsCount
                + " x=[计数 " + Fmt(countRect) + " 文件 " + Fmt(fileRect) + " 文件夹 " + Fmt(folderRect)
                + " 批量选择 " + Fmt(batchRect) + "]"
                + " 在计数右侧=" + rightOfCount + " 在批量选择左侧=" + leftOfBatch + " 同一行=" + sameRowBand));
        static Rect BoundsOf(FrameworkElement? element, Visual ancestor)
            => element is null
                ? Rect.Empty
                : new Rect(element.TransformToAncestor(ancestor).Transform(new Point(0, 0)), element.RenderSize);

        static string Fmt(Rect r) => r.Width <= 0 ? "(未布局)" : $"{r.Left:F0}..{r.Right:F0}";        if (selectBinding is not null && toolbar is not null)
        {
            // 选中态是"整块卡片换底色"，不是加一圈外框。
            // 先记下未选中时的样子，选中后再比一次。
            var backgroundBefore = card?.Background;
            var thicknessBefore = card?.BorderThickness ?? default;

            selectBinding.Command!.Execute(selectBinding.CommandParameter);
            window.UpdateLayout();

            var backgroundAfter = card?.Background;
            var thicknessAfter = card?.BorderThickness ?? default;

            checks.Add(new SmokeCheck(
                "selection-changes-card-background",
                backgroundBefore is not null
                    && backgroundAfter is not null
                    && !ReferenceEquals(backgroundBefore, backgroundAfter),
                "选中前底色=" + DescribeBrush(backgroundBefore)
                    + " 选中后底色=" + DescribeBrush(backgroundAfter)));

            checks.Add(new SmokeCheck(
                "selection-does-not-add-a-frame",
                thicknessBefore.Equals(thicknessAfter),
                "选中前边框=" + thicknessBefore + " 选中后边框=" + thicknessAfter
                    + "（要求不变：用底色表达选中，不加外框）"));

            // 主窗口菜单项的可用性由同一条事件驱动：靠它验跨组件联动真的接上了。
            var convertMenuItem = window.FindName("ConvertMenuItem") as MenuItem;

            checks.Add(new SmokeCheck(
                "toolbar-convert-enables-on-select",
                vm.HasSelection && ReferenceEquals(vm.SelectedBook, vm.Books[0]) && toolbar.IsEnabled,
                "已选中=" + vm.HasSelection + " 按钮可用=" + toolbar.IsEnabled
                    + " 命中的是第 0 张=" + ReferenceEquals(vm.SelectedBook, vm.Books[0])));

            checks.Add(new SmokeCheck(
                "menu-convert-enables-on-select",
                convertMenuItem is { IsEnabled: true },
                "菜单项存在=" + (convertMenuItem is not null)
                    + " 可用=" + (convertMenuItem?.IsEnabled == true)));
        }

        var cardContextMenu = page.CardContextMenu;

        // 右键菜单是共享实例，处理器靠"从被点元素往上找卡片"。从卡片里最深的一个元素去找，
        // 找到的必须还是这张卡：这是共享菜单方案里唯一没法绕过去验证的逻辑。
        // 注意：这里 card 是**卡片根元素**（Border），不是 ViewModel；
        // 要比较的是它 DataContext 上那张卡，拿 Border 跟 VM 比会永远为假（我第一版就写错了）。
        var cardVm = card?.DataContext as BookCardViewModel;
        var deepest = DescendToLeaf(card?.Child as DependencyObject);
        var foundCard = deepest is null ? null : page.FindCardForSmoke(deepest);

        checks.Add(new SmokeCheck(
            "context-menu-card-lookup",
            foundCard is not null && ReferenceEquals(foundCard, cardVm),
            "最深元素=" + (deepest?.GetType().Name ?? "(null)")
                + "，往上找到的卡片=" + (foundCard?.Title ?? "(null)")
                + "，期望=" + (cardVm?.Title ?? "(null)")));
        // 用 OfType 而不是 Cast：右键菜单里已经有 Separator 这类非 MenuItem 元素，
        // Cast 遇到它就抛异常，会把「判据写得太脆」伪装成「产品有 bug」。
        var menuHeaders = cardContextMenu is null
            ? new List<string?>()
            : cardContextMenu.Items.OfType<MenuItem>().Select(m => m.Header as string).ToList();

        checks.Add(new SmokeCheck(
            "card-context-menu-wired",
            cardContextMenu is not null
                && menuHeaders.Count == 5
                && menuHeaders.Contains("查看详情")
                && menuHeaders.Contains("编辑书籍")
                && menuHeaders.Contains("转换格式")
                && menuHeaders.Contains("标记为")
                && menuHeaders.Contains("删除"),
            "菜单项=" + string.Join(" / ", menuHeaders)));

        /* T2-3 补丁：手动标阅读状态。走的是菜单里那个「标记为 -> 弃读」的处理器，
           验三件事：库里真的写进去了、卡片内存里的状态跟着变了、详情那行文案也变了。 */
        var markMenu = cardContextMenu?.Items.OfType<MenuItem>()
            .FirstOrDefault(m => (m.Header as string) == "标记为");
        var abandonItem = markMenu?.Items.OfType<MenuItem>()
            .FirstOrDefault(m => (m.Tag as string) == "Abandoned");

        /* 必须先"打开"菜单：ContextMenu 是打开那一刻才从 PlacementTarget 拿到 DataContext 的，
           不打开就直接 RaiseEvent，处理器里 DataContext 是 null 会直接 return 
           那是冒烟不真实，不是产品 bug（用户右键时菜单一定会打开）。 */
        var beforeStatus = vm.Books[0].Book.Status;

        if (cardContextMenu is not null)
        {
            page.OpenCardMenu(vm.Books[0], card?.Child as FrameworkElement);
            window.UpdateLayout();
            await Task.Delay(80).ConfigureAwait(true);
        }
        /* T2-3 补丁六（用户要求）：右键菜单「标记为」里，当前状态那一项左边要有个对勾。
           判据读的是 MenuItem.Icon 上那个 TextBlock 的 Visibility，纯只读显示。
           刻意不用 MenuItem.IsCheckable：那样点击会把 IsChecked 自己翻面，
           而「点当前状态」这条路径被 MarkStatusAsync 早退（不落库也不发通知），
           对勾就会停在错误的一侧。 */
        /* 必须把「标记为」子菜单真的展开：MenuItem.Icon 里的 TextBlock 要等子菜单弹出
           才会进可视树、拿到 DataContext 并求值绑定。不展开就读到一堆默认的 Visible，
           那是冒烟不真实（用户要看这个对勾，一定得先把子菜单拉出来）。 */
        if (markMenu is not null)
        {
            markMenu.IsSubmenuOpen = true;
            window.UpdateLayout();
            await Task.Delay(120).ConfigureAwait(true);
        }

        static List<MenuItem> MarkItems(MenuItem? menu)
            => menu is null ? new List<MenuItem>() : menu.Items.OfType<MenuItem>().ToList();

        static string VisibleCheckTag(IEnumerable<MenuItem> items)
            => string.Join(",", items
                .Where(i => i.Icon is FrameworkElement fe && fe.Visibility == Visibility.Visible)
                .Select(i => i.Tag as string ?? "(无 Tag)"));

        var markItems = MarkItems(markMenu);
        var checkedTagBefore = VisibleCheckTag(markItems);

        checks.Add(new SmokeCheck(
            "mark-menu-shows-current-status-check",
            markItems.Count == 4 && checkedTagBefore == beforeStatus.ToString(),
            "标记前状态=" + beforeStatus + "，有对勾的那项=" + checkedTagBefore
                + "，子项数=" + markItems.Count));
        var itemContextOk = abandonItem?.DataContext is BookCardViewModel;
        abandonItem?.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        if (cardContextMenu is not null)
        {
            cardContextMenu.IsOpen = false;
        }

        // 处理器是 async void，RaiseEvent 不等它，所以这里等库里的值真的变过来
        // 处理器是 async void，RaiseEvent 不会等它跑完，所以这里等状态真的变成弃读
        var statusWritten = await WaitForStatusAsync(
            vm.Books[0], ReadingStatus.Abandoned, TimeSpan.FromSeconds(3)).ConfigureAwait(true);

        checks.Add(new SmokeCheck(
            "card-mark-status-via-menu",
            abandonItem is not null && statusWritten
                && vm.Books[0].Metadata.Any(r => r.Label == "阅读状态" && r.Value == "弃读"),
            "标记前=" + beforeStatus + "，标记后=" + vm.Books[0].Book.Status
                + "，详情那行=" + (vm.Books[0].Metadata.FirstOrDefault(r => r.Label == "阅读状态")?.Value ?? "(无)")
                + "，菜单项存在=" + (abandonItem is not null) + "，菜单项的 DataContext 是卡片=" + itemContextOk));

        // 对勾要跟着状态走：重开一次菜单，看它是不是挪到「弃读」上了
        var checkedTagAfter = "(没打开菜单)";
        if (cardContextMenu is not null)
        {
            page.OpenCardMenu(vm.Books[0], card?.Child as FrameworkElement);
            window.UpdateLayout();
            await Task.Delay(80).ConfigureAwait(true);
            /* 必须把「标记为」子菜单真的展开：MenuItem.Icon 里的 TextBlock 要等子菜单弹出
               才会进可视树、拿到 DataContext 并求值绑定。不展开就读到一堆默认的 Visible，
               那是冒烟不真实（用户要看这个对勾，一定得先把子菜单拉出来）。 */
            if (markMenu is not null)
            {
                markMenu.IsSubmenuOpen = true;
                window.UpdateLayout();
                await Task.Delay(120).ConfigureAwait(true);
            }
    
            checkedTagAfter = VisibleCheckTag(markItems);
            cardContextMenu.IsOpen = false;
        }

        checks.Add(new SmokeCheck(
            "mark-menu-check-follows-status",
            checkedTagAfter == "Abandoned",
            "标记后状态=" + vm.Books[0].Book.Status + "，有对勾的那项=" + checkedTagAfter));
        // ---------- 用户要求：卡片上要看得见阅读状态，弃读标红 ----------
        var badgeCard = FindCardFor(page, vm.Books[0]);
        var badgeTexts = new List<string>();
        var badgeColors = new List<Color>();

        if (badgeCard is not null)
        {
            CollectTexts(badgeCard, badgeTexts);
            CollectBackgrounds(badgeCard, badgeColors);
        }

        // 颜色从主题资源取，别写死：深/浅色主题的红绿不一样
        var expectedRed = (Application.Current.TryFindResource("DangerBackgroundBrush") as SolidColorBrush)?.Color;
        var expectedGreen = (Application.Current.TryFindResource("SuccessBrush") as SolidColorBrush)?.Color;
        var abandonedRed = badgeTexts.Contains("弃读")
            && expectedRed is not null && badgeColors.Contains(expectedRed.Value);

        checks.Add(new SmokeCheck(
            "card-shows-abandoned-in-red",
            abandonedRed,
            "角标文字含有「弃读」=" + badgeTexts.Contains("弃读") + "，出现弃读红 " + expectedRed
                + "=" + (expectedRed is not null && badgeColors.Contains(expectedRed.Value))));

        // 再标成已读完：验第三种状态的文案与绿色角标
        await vm.MarkStatusAsync(vm.Books[0], ReadingStatus.Finished).ConfigureAwait(true);
        window.UpdateLayout();

        var finishedTexts = new List<string>();
        var finishedColors = new List<Color>();

        if (badgeCard is not null)
        {
            CollectTexts(badgeCard, finishedTexts);
            CollectBackgrounds(badgeCard, finishedColors);
        }

        checks.Add(new SmokeCheck(
            "card-shows-finished-in-green",
            finishedTexts.Contains("已读完") && expectedGreen is not null
                && finishedColors.Contains(expectedGreen.Value),
            "角标文字含有「已读完」=" + finishedTexts.Contains("已读完") + "，出现完成绿 " + expectedGreen
                + "=" + (expectedGreen is not null && finishedColors.Contains(expectedGreen.Value))));

        // ---------- 用户要求：从详情返回时状态要实时刷新 ----------
        var refreshTarget = vm.Books[1];
        var beforeDetail = refreshTarget.ReadingStateText;

        vm.OpenDetail(refreshTarget);

        // 绕过 VM 直接改库：模拟"用户在详情页里点阅读、把那本书读完了"
        await repo.SetReadingStatusAsync(refreshTarget.Book.Id, ReadingStatus.Finished).ConfigureAwait(true);

        var stillStale = refreshTarget.ReadingStateText;

        // 就是详情浮层「返回」按钮绑的那条命令
        vm.CloseDetailCommand.Execute(null);

        var refreshed = await WaitForStatusAsync(refreshTarget, ReadingStatus.Finished, TimeSpan.FromSeconds(3))
            .ConfigureAwait(true);

        checks.Add(new SmokeCheck(
            "detail-return-refreshes-state",
            beforeDetail == "未读" && stillStale == "未读" && refreshed,
            "进详情前=" + beforeDetail + "；改库后仍显示=" + stillStale + "（应为未读，说明确实没实时推）；"
                + "点返回后=" + refreshTarget.ReadingStateText + "（应为已读完）"));

        if (!wired || binding is null)
        {
            return;
        }

        // 执行的就是双击会执行的那条命令，不是另走一条路
        binding.Command!.Execute(binding.CommandParameter);
        window.UpdateLayout();

        var overlay = page.FindName("DetailOverlay") as Grid;
        var ok = vm.IsDetailOpen
            && ReferenceEquals(vm.DetailBook, vm.Books[0])
            && overlay is { Visibility: Visibility.Visible };

        checks.Add(new SmokeCheck(
            "double-click-opens-detail",
            ok,
            "详情已开=" + vm.IsDetailOpen + " 浮层=" + (overlay?.Visibility.ToString() ?? "(无)")
                + " 命中的是第 0 张=" + ReferenceEquals(vm.DetailBook, vm.Books[0])));
    }

    private static void CheckDetailContent(
        LibraryPage page, LibraryViewModel vm, MainWindow window, List<SmokeCheck> checks)
    {
        window.UpdateLayout();

        var title = page.FindName("DetailTitle") as TextBlock;
        var metadata = page.FindName("DetailMetadata") as ItemsControl;
        var read = page.FindName("DetailReadButton") as Button;
        var convert = page.FindName("DetailConvertButton") as Button;
        var close = page.FindName("DetailCloseButton") as Button;

        var expected = vm.Books.Count > 0 ? vm.Books[0].Title : null;

        checks.Add(new SmokeCheck(
            "detail-shows-metadata",
            title?.Text == expected && metadata is not null && metadata.Items.Count > 0,
            "标题=" + (title?.Text ?? "(无)") + " 元数据行=" + (metadata?.Items.Count ?? -1)));

        checks.Add(new SmokeCheck(
            // T1-5 之前这里是"阅读禁用"：阅读器还没做，按钮故意置灰并写着
            // "阅读器在 T1-5 提供"。T1-5 交付后它必须是可用的，
            // 这条断言也就跟着翻过来，否则阅读入口被人改坏了反而没人发现。
            "detail-buttons-state",
            read is { IsEnabled: true }
                && convert is { IsEnabled: true }
                && read.ToolTip is not null
                && convert.ToolTip is not null
                && close is not null,
            "阅读可用=" + (read?.IsEnabled == true) + " 转换可用=" + (convert?.IsEnabled == true)
                + " 都有说明=" + (read?.ToolTip is not null && convert?.ToolTip is not null)));
    }

    /// <summary>把画刷说成人话：断言失败时要能看到具体颜色，而不是只有一个 false。</summary>
    private static string DescribeBrush(System.Windows.Media.Brush? brush) => brush switch
    {
        null => "(无)",
        System.Windows.Media.SolidColorBrush solid => solid.Color.ToString(),
        _ => brush.GetType().Name,
    };
    /// <summary>找到某张卡片对应的那个 Border（可视树里 DataContext 等于它的最外层一个）。</summary>
    private static Border? FindCardFor(DependencyObject root, BookCardViewModel card)
    {
        if (root is Border border && ReferenceEquals(border.DataContext, card))
        {
            return border;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);

        for (var i = 0; i < count; i++)
        {
            var found = FindCardFor(VisualTreeHelper.GetChild(root, i), card);

            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }
    /// <summary>把一棵可视树里所有 TextBlock 的文字收上来（验卡片角标用）。</summary>
    private static void CollectTexts(DependencyObject root, List<string> into)
    {
        if (root is TextBlock tb && !string.IsNullOrEmpty(tb.Text))
        {
            into.Add(tb.Text);
        }

        var count = VisualTreeHelper.GetChildrenCount(root);

        for (var i = 0; i < count; i++)
        {
            CollectTexts(VisualTreeHelper.GetChild(root, i), into);
        }
    }
    /// <summary>把一棵可视树里所有 Border 的背景色收上来（验"弃读标红"用）。</summary>
    private static void CollectBackgrounds(DependencyObject root, List<Color> into)
    {
        if (root is Border { Background: SolidColorBrush solid })
        {
            into.Add(solid.Color);
        }

        var count = VisualTreeHelper.GetChildrenCount(root);

        for (var i = 0; i < count; i++)
        {
            CollectBackgrounds(VisualTreeHelper.GetChild(root, i), into);
        }
    }
    /// <summary>在可视树里找第一个 ScrollViewer（ListBox 的滚动条藏在这里）。</summary>
    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);

        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

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

    private static void CheckScrollKept(
        LibraryPage page, LibraryViewModel vm, MainWindow window, List<SmokeCheck> checks)
    {
        // T2-3：外层 ScrollViewer 拆掉了（它会让虚拟化失效），滚动条在 ListBox 内部
        var list = page.FindName("BookList") as ListBox;
        var scroll = list is null ? null : FindScrollViewer(list);

        if (scroll is null || !vm.IsDetailOpen)
        {
            checks.Add(new SmokeCheck("scroll-offset-kept-after-close", false, "滚动条或详情状态不对"));
            return;
        }

        scroll.ScrollToVerticalOffset(160);
        window.UpdateLayout();
        var before = scroll.VerticalOffset;

        // 走的就是"返回"按钮绑的那条命令
        vm.CloseDetailCommand.Execute(null);
        window.UpdateLayout();
        var after = scroll.VerticalOffset;

        var overlay = page.FindName("DetailOverlay") as Grid;

        checks.Add(new SmokeCheck(
            "scroll-offset-kept-after-close",
            before > 0 && Math.Abs(before - after) < 0.5 && !vm.IsDetailOpen
                && overlay is { Visibility: Visibility.Collapsed },
            "返回前 " + before.ToString("0.#") + " 返回后 " + after.ToString("0.#")
                + " 可滚动 " + scroll.ScrollableHeight.ToString("0.#")
                + " 详情已关=" + !vm.IsDetailOpen));
    }

    private static async Task CheckCoversMissingAsync(
        AppPaths paths, NavigationService nav, MainWindow window, List<SmokeCheck> checks)
    {
        // 模拟用户把 Cache/Covers 整个删了：封面全没了，网格必须还能用
        if (Directory.Exists(paths.CoversCacheDir))
        {
            Directory.Delete(paths.CoversCacheDir, recursive: true);
        }

        LibraryPage? page = null;
        void Capture(object? sender, FrameworkElement element) => page = element as LibraryPage;

        nav.ContentChanged += Capture;
        try
        {
            await nav.NavigateAsync(Routes.Queue).ConfigureAwait(true);
            await nav.NavigateAsync(Routes.Library).ConfigureAwait(true);
        }
        finally
        {
            nav.ContentChanged -= Capture;
        }

        window.UpdateLayout();

        var vmLive = page?.ViewModel;
        var withCover = vmLive?.Books.Count(c => c.HasCover) ?? -1;

        checks.Add(new SmokeCheck(
            "placeholder-when-cover-file-missing",
            vmLive is not null && vmLive.Books.Count == BookCount && withCover == 0,
            "书 " + (vmLive?.Books.Count ?? -1) + " 本，仍有封面 " + withCover + " 张（应为 0），未崩溃"));
    }

    /// <summary>
    /// T2-3 补丁八：列表视图与视图切换。
    /// 要点：切换只换渲染（同一份查询结果、同一批 BookCardViewModel），
    /// 所以列表行数必须与当前筛选结果一致，勾选状态也不能丢。
    /// </summary>
    private static async Task CheckListViewAsync(
        LibraryPage page, LibraryViewModel vm, AppPaths paths, List<SmokeCheck> checks)
    {
        var toggle = page.ViewToggleButton;
        var gridList = page.BookList;
        var rowList = page.BookRowList;

        var total = vm.Books.Count;
        // 用 IsVisible（有效可见性）而不是 Visibility：列表的 Visibility 绑在外层 Grid 上（它同时管表头），
        // 父级折叠不会改子元素自己的 Visibility 值，只有 IsVisible 会跟着变。
        var gridVisibleBefore = gridList.IsVisible;
        var listVisibleBefore = rowList.IsVisible;
        var textBefore = vm.ViewToggleText;

        // 点按钮而不是直接调 VM：要验的就是这条接线真的通。
        toggle.RaiseEvent(new RoutedEventArgs(
            System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        page.UpdateLayout();

        var listVisibleAfter = rowList.IsVisible;
        var gridVisibleAfter = gridList.IsVisible;

        checks.Add(new SmokeCheck(
            "list-view-toggle-in-toolbar",
            toggle.IsEnabled && gridVisibleBefore && !listVisibleBefore
                && listVisibleAfter && !gridVisibleAfter
                && vm.ViewToggleText != textBefore
                && rowList.Items.Count == total,
            "按钮可用=" + toggle.IsEnabled
                + "；切换前 网格=" + gridVisibleBefore + " 列表=" + listVisibleBefore
                + "；切换后 列表=" + listVisibleAfter + " 网格=" + gridVisibleAfter
                + " 列表行数=" + rowList.Items.Count + "（应为 " + total + "）"
                + " 文案 " + textBefore + " -> " + vm.ViewToggleText));

        // ---- 2. 切换要落盘（两个方向都验：只看内存等于没验）----
        await vm.ToggleViewAsync().ConfigureAwait(true);      // 回网格
        var gridStored = RegexIsMatch(ReadConfigText(paths), "\"libraryView\"\\s*:\\s*\"grid\"");

        await vm.ToggleViewAsync().ConfigureAwait(true);      // 再到列表
        var listStored = RegexIsMatch(ReadConfigText(paths), "\"libraryView\"\\s*:\\s*\"list\"");

        checks.Add(new SmokeCheck(
            "list-view-persists",
            gridStored && listStored && vm.IsListView,
            "切回网格后 Config 里是 grid=" + gridStored
                + "，切到列表后是 list=" + listStored
                + "，VM.IsListView=" + vm.IsListView + "（" + paths.ConfigFile + "）"));

        // ---- 3. 列表行数跟着查询走（证明只是换渲染，不是各自查库）----
        vm.SearchDebounce = TimeSpan.Zero;
        vm.SearchText = "冒烟书籍 0";
        await vm.FlushSearchAsync().ConfigureAwait(true);
        page.UpdateLayout();

        var filtered = vm.Books.Count;
        var filteredRows = rowList.Items.Count;

        vm.ClearSearch();
        await vm.FlushSearchAsync().ConfigureAwait(true);
        page.UpdateLayout();

        checks.Add(new SmokeCheck(
            "list-view-rows-match-query",
            filtered > 0 && filtered < total && filteredRows == filtered,
            "搜索命中 " + filtered + " 本（应大于 0 且小于 " + total + "），列表行数=" + filteredRows));

        // ---- 4. 行里真的有那些字段 ----
        var firstCard = vm.Books[0];
        var firstRow = FindCardFor(rowList, firstCard);
        var rowTexts = new List<string>();

        if (firstRow is not null)
        {
            CollectTexts(firstRow, rowTexts);
        }

        var texts = string.Join(" | ", rowTexts);

        checks.Add(new SmokeCheck(
            "list-view-row-fields",
            firstRow is not null
                && texts.Contains(firstCard.Title)
                && texts.Contains(firstCard.Authors)
                && texts.Contains(firstCard.FormatBadge)
                && texts.Contains(firstCard.SizeText)
                && texts.Contains(firstCard.AddedAtText),
            "第一行找到=" + (firstRow is not null)
                + " 需要 书名=" + firstCard.Title + " 作者=" + firstCard.Authors
                + " 格式=" + firstCard.FormatBadge + " 大小=" + firstCard.SizeText
                + " 加入=" + firstCard.AddedAtText + "；行内文字=" + texts));

        // ---- 5. 行是虚拟化的（只实现看得见的那些）----
        var realized = 0;

        for (var i = 0; i < rowList.Items.Count; i++)
        {
            if (rowList.ItemContainerGenerator.ContainerFromIndex(i) is not null)
            {
                realized++;
            }
        }

        checks.Add(new SmokeCheck(
            "list-view-virtualizes",
            realized > 0 && realized < rowList.Items.Count,
            "共 " + rowList.Items.Count + " 行，已实现 " + realized + " 行（要求大于 0 且小于总行数）"));

        // ---- 6. 批量勾选在列表视图里一样好使 ----
        vm.ToggleSelectionMode();
        vm.ToggleChecked(firstCard);
        page.UpdateLayout();

        var checkedRow = FindCardFor(rowList, firstCard);
        var rowBrush = checkedRow?.Background;
        var accent = System.Windows.Application.Current.TryFindResource("AccentSurfaceBrush")
            as System.Windows.Media.Brush;
        var sameAsAccent = rowBrush is not null && accent is not null
            && DescribeBrush(rowBrush) == DescribeBrush(accent);

        checks.Add(new SmokeCheck(
            "list-view-batch-select",
            vm.CheckedCount == 1 && firstCard.IsChecked && sameAsAccent,
            "已选=" + vm.CheckedCount + " IsChecked=" + firstCard.IsChecked
                + "，行底色=" + DescribeBrush(rowBrush) + " 期望=" + DescribeBrush(accent)
                + "，计数文案=" + vm.CheckedText));

        vm.ClearChecked();
        vm.ToggleSelectionMode();
        page.UpdateLayout();
    }

    private static string ReadConfigText(AppPaths paths)
    {
        try
        {
            return System.IO.File.ReadAllText(paths.ConfigFile);
        }
        catch (Exception ex)
        {
            return "<读不到配置：" + ex.GetType().Name + ">";
        }
    }

    private static bool RegexIsMatch(string text, string pattern)
        => System.Text.RegularExpressions.Regex.IsMatch(text, pattern);

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
            Path.Combine(paths.TempDir, "library-smoke.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));

        return report;
    }
    // ---------- 删除流程（单本 + 批量，含二次确认两条路径） ----------

    /// <summary>
    /// 删除这条路能进自动化，全靠把「二次确认」做成可替换的委托：
    /// 先让确认返回 false（验证取消时什么都没变），再让它返回 true（验证真删）。
    /// 否则一段要人手点对话框的逻辑永远只能人眼验。
    /// </summary>
    /// <summary>
    /// 批量转换入口：按钮只在批量模式出现、勾了书才可用；
    /// 窗口能按这批书算出可用的目标格式。这里不真的入队（那会去跑 Calibre）。
    /// </summary>
    private static void CheckBatchConvert(
        LibraryPage page, LibraryViewModel vm, IConversionService conversion, List<SmokeCheck> checks)
    {
        var button = page.BatchConvertButton;

        var hiddenWhenNormal = button.Visibility != Visibility.Visible;

        vm.ToggleSelectionMode();
        page.UpdateLayout();

        var visibleInSelectionMode = button.Visibility == Visibility.Visible;
        var disabledWithNothingChecked = !button.IsEnabled;

        vm.CheckAll();
        page.UpdateLayout();

        var enabledWithChecked = button.IsEnabled;

        var books = vm.Books.Take(2).Select(c => c.Book).ToList();
        var window = new BatchConvertWindow(conversion, books);
        var targetCount = window.TargetCount;
        var startEnabled = window.StartEnabled;
        window.Close();

        vm.ClearChecked();
        vm.ToggleSelectionMode();

        checks.Add(new SmokeCheck(
            "batch-convert-button-in-selection-mode",
            hiddenWhenNormal && visibleInSelectionMode && disabledWithNothingChecked && enabledWithChecked,
            "普通模式隐藏=" + hiddenWhenNormal + " 批量模式可见=" + visibleInSelectionMode
                + " 未勾选时禁用=" + disabledWithNothingChecked + " 勾选后可用=" + enabledWithChecked));

        checks.Add(new SmokeCheck(
            "batch-convert-window-formats",
            targetCount > 0 && startEnabled,
            "可转出的目标格式 " + targetCount + " 个，开始按钮可用=" + startEnabled
                + "（样本 " + books.Count + " 本）"));
    }

    private static async Task CheckDeleteFlowAsync(
        LibraryPage page, LibraryViewModel vm, AppPaths paths, IBookRepository repo, List<SmokeCheck> checks)
    {
        var toggle = page.FindName("ToggleSelectionButton") as Button;
        var selectAll = page.FindName("SelectAllButton") as Button;
        var clear = page.FindName("ClearCheckedButton") as Button;
        var deleteSelected = page.FindName("DeleteSelectedButton") as Button;
        var convert = page.FindName("ToolbarConvertButton") as Button;
        var detailDelete = page.FindName("DetailDeleteButton") as Button;

        // 走按钮真实的 Click，而不是直接调 VM：这样接线断了也能被发现
        toggle?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        var modeOn = vm.IsSelectionMode;
        var modeText = vm.SelectionModeText;
        var deleteVisible = deleteSelected?.Visibility == Visibility.Visible;
        var convertHidden = convert?.Visibility == Visibility.Collapsed;

        checks.Add(new SmokeCheck(
            "selection-mode-toggle",
            modeOn
                && modeText == "退出批量选择"
                && deleteVisible
                && convertHidden
                && selectAll is { Visibility: Visibility.Visible }
                && clear is { Visibility: Visibility.Visible },
            "进入批量模式=" + modeOn + " 按钮文案=" + modeText
                + " 删除按钮可见=" + deleteVisible + " 转换按钮已隐藏=" + convertHidden
                + " 全选=" + (selectAll?.Visibility.ToString() ?? "-")
                + " 清空=" + (clear?.Visibility.ToString() ?? "-")));

        // 勾 3 本（走卡片单击实际调用的那个方法）
        var targets = vm.Books.Take(3).ToList();
        foreach (var card in targets)
        {
            vm.Select(card);
        }

        checks.Add(new SmokeCheck(
            "selection-check-and-count",
            vm.CheckedCount == 3
                && vm.DeleteSelectedText == "删除选中的 3 本"
                && targets.All(c => c.IsChecked),
            "已选 " + vm.CheckedCount + " 本，按钮文案=" + vm.DeleteSelectedText));

        var targetIds = targets.Select(c => c.Id).ToList();
        var folders = targetIds.Select(id => Path.Combine(paths.LibraryDir, id.ToString("D"))).ToList();

        // --- 第一条：二次确认点「取消」 ---
        string? confirmText = null;
        var notices = new List<string>();
        vm.ConfirmDelete = (message, _) => { confirmText = message; return false; };
        vm.Notify = (message, _) => notices.Add(message);

        await vm.DeleteAsync(targets).ConfigureAwait(true);

        var mentionsCount = confirmText is not null && confirmText.Contains("3 本");
        var keptAll = folders.All(Directory.Exists);

        checks.Add(new SmokeCheck(
            "delete-cancelled-keeps-books",
            mentionsCount && keptAll && vm.Books.Count == BookCount && notices.Count == 0,
            "确认文案带本数=" + mentionsCount + " 目录都还在=" + keptAll
                + " 网格仍是 " + vm.Books.Count + " 本，未弹结果提示=" + (notices.Count == 0)));

        // --- 第二条：二次确认点「确定」 ---
        vm.ConfirmDelete = (_, _) => true;

        await vm.DeleteAsync(targets).ConfigureAwait(true);

        var afterBatch = await repo.CountAsync().ConfigureAwait(true);
        var filesGone = folders.All(f => !Directory.Exists(f));
        var goneFromGrid = vm.Books.All(c => !targetIds.Contains(c.Id));

        checks.Add(new SmokeCheck(
            "delete-batch-removes-books-and-files",
            afterBatch == BookCount - 3 && filesGone && goneFromGrid && !vm.IsSelectionMode,
            "库剩 " + afterBatch + " 本（应为 " + (BookCount - 3) + "）"
                + " 正式区目录已删=" + filesGone + " 网格无残留=" + goneFromGrid
                + " 已自动退出批量模式=" + !vm.IsSelectionMode));

        // --- 第三条：详情浮层里的单本删除 ---
        if (vm.Books.Count == 0)
        {
            checks.Add(new SmokeCheck("delete-single-removes-book", false, "没有书可用于单本删除"));
            return;
        }

        var victim = vm.Books[0];
        var victimFolder = Path.Combine(paths.LibraryDir, victim.Id.ToString("D"));

        vm.OpenDetail(victim);
        detailDelete?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        // 处理器是 async void，RaiseEvent 不会等它，所以这里等库里的本数真的降下来
        var reached = await WaitForBookCountAsync(repo, BookCount - 4, TimeSpan.FromSeconds(5))
            .ConfigureAwait(true);
        var left = await repo.CountAsync().ConfigureAwait(true);

        checks.Add(new SmokeCheck(
            "delete-single-removes-book",
            reached && !Directory.Exists(victimFolder) && !vm.IsDetailOpen,
            "库剩 " + left + " 本（应为 " + (BookCount - 4) + "）"
                + " 目录已删=" + !Directory.Exists(victimFolder)
                + " 详情浮层已自动关闭=" + !vm.IsDetailOpen));
    }

    /// <summary>
    /// 用户 2026-10-21：多格式书籍的格式角标、当前阅读格式与切换。
    /// </summary>
    /// <remarks>
    /// 覆盖四件事：
    /// 1. 卡片左下角角标把**所有**格式都显示出来（以前只显示前两个，第三种被吃掉）；
    /// 2. 详情里「切换此格式」只给"非当前、且真能读"的格式，点它真的改库；
    /// 3. MOBI 以 EPUB 形态阅读、且转换产物已存在时：角标前面多一个 EPUB、
    ///    详情多一行 EPUB（大小=产物大小）、底部小字「当前阅读格式：EPUB」；
    /// 4. 转换产物不存在（从没读过）：详情不显示 EPUB 行，底部小字换成
    ///    「当前格式不支持阅读，点击阅读后自动转换为 EPUB 格式进行阅读」。
    /// 转换产物用占位文件即可：这条检查验的是"界面怎么显示"，不是真去转一本书。
    /// </remarks>
    private static async Task CheckReadingFormatFlowAsync(
        LibraryPage page,
        LibraryViewModel vm,
        MainWindow window,
        IBookRepository repo,
        AppPaths paths,
        List<SmokeCheck> checks)
    {
        var card = vm.Books.LastOrDefault();

        if (card is null)
        {
            checks.Add(new SmokeCheck("format-badge-shows-all-formats", false, "书库是空的，没法验格式角标"));
            return;
        }

        var bookId = card.Id;
        var directory = Path.GetDirectoryName(card.Book.Formats.FirstOrDefault()?.FilePath ?? paths.LibraryDir)
            ?? paths.LibraryDir;

        /* 改造成"没有 EPUB、只有 PDF + MOBI"的多格式书：
           详情里那条 EPUB 行只能是"转换产物"那一行，否则验不出区别。 */
        foreach (var format in card.Book.Formats.ToList())
        {
            await repo.DeleteFormatAsync(format.Id).ConfigureAwait(true);
        }

        var pdfPath = Path.Combine(directory, "smoke-reading-format.pdf");
        var mobiPath = Path.Combine(directory, "smoke-reading-format.mobi");
        await File.WriteAllTextAsync(pdfPath, "冒烟占位 PDF，不参与解析").ConfigureAwait(true);
        await File.WriteAllTextAsync(mobiPath, "冒烟占位 MOBI，不参与解析").ConfigureAwait(true);

        await repo.AddFormatAsync(new BookFormat
        {
            BookId = bookId,
            Format = BookFormatKind.Pdf,
            FilePath = pdfPath,
            FileSize = new FileInfo(pdfPath).Length,
            AddedAt = DateTimeOffset.Now,
        }).ConfigureAwait(true);

        const string fingerprint = "abcdef0123456789";
        var mobiInfo = new FileInfo(mobiPath);

        await repo.AddFormatAsync(new BookFormat
        {
            BookId = bookId,
            Format = BookFormatKind.Mobi,
            FilePath = mobiPath,
            FileSize = mobiInfo.Length,
            QuickFingerprint = fingerprint,
            AddedAt = DateTimeOffset.Now,
        }).ConfigureAwait(true);

        // MOBI 读成 EPUB 的那份缓存产物（界面只认它在不在、多大）
        const long artifactBytes = 12345;
        var cacheKey = ReaderEndpoints.CacheFolderName(
            ReaderEndpoints.ConvertedCacheKey(fingerprint, mobiInfo.LastWriteTimeUtc, mobiInfo.Length));
        var artifact = Path.Combine(paths.ReaderCacheDir, cacheKey + ".source.epub");

        Directory.CreateDirectory(paths.ReaderCacheDir);
        await File.WriteAllBytesAsync(artifact, new byte[artifactBytes]).ConfigureAwait(true);

        // 当前阅读格式 = MOBI；进度来源 = EPUB（换格式续读要用的那条信息）
        await repo.SetReadingFormatAsync(bookId, BookFormatKind.Mobi).ConfigureAwait(true);
        await repo.SaveReadingProgressAsync(new ReadingProgress
        {
            BookId = bookId,
            SpineIndex = 5,
            Percent = 0.42,
            UpdatedAt = DateTimeOffset.UtcNow,
            SourceFormat = BookFormatKind.Epub,
        }).ConfigureAwait(true);

        // 前面列表视图的用例可能把视图留在列表上；角标截图必须看网格
        await vm.SetListViewAsync(false).ConfigureAwait(true);

        await vm.LoadAsync().ConfigureAwait(true);
        window.UpdateLayout();

        /* 先给网格里的格式角标拍一张：虚拟化只实现可见行，所以滚到底把这本书露出来。
           这张图是"三种格式全部显示、没被截断"的直接证据（用户 2026-10-21 的截图反馈）。 */
        var bookList = page.FindName("BookList") as ListBox;

        if (bookList is not null)
        {
            FindScrollViewer(bookList)?.ScrollToEnd();
        }
        window.UpdateLayout();

        Directory.CreateDirectory(paths.TempDir);
        CaptureWindow(window, Path.Combine(paths.TempDir, "library-reading-format-badge.png"));

        var target = vm.Books.FirstOrDefault(b => b.Id == bookId);

        if (target is null)
        {
            checks.Add(new SmokeCheck("format-badge-shows-all-formats", false, "改造后找不到这本书的卡片"));
            return;
        }

        // ---------- 1. 角标：三种格式一个不少（多了"以 EPUB 阅读"的 EPUB 项） ----------
        var badgeParts = target.FormatBadge
            .Split('+', StringSplitOptions.RemoveEmptyEntries)
            .OrderBy(part => part, StringComparer.Ordinal)
            .ToList();

        checks.Add(new SmokeCheck(
            "format-badge-shows-all-formats",
            badgeParts.SequenceEqual(new[] { "EPUB", "MOBI", "PDF" }),
            "角标=" + target.FormatBadge + "（期望含 EPUB+MOBI+PDF，且 EPUB 代表转换后的阅读形态）"));

        // ---------- 2. 详情：EPUB 行 + MOBI 注记 + 切换按钮 + 底部小字 ----------
        vm.OpenDetail(target);
        window.UpdateLayout();

        var metadata = page.FindName("DetailMetadata") as ItemsControl;
        var formatText = page.FindName("DetailReadingFormatText") as TextBlock;

        var formatRows = target.Metadata.Where(r => r.FormatKind is not null).ToList();
        var epubRow = target.Metadata.FirstOrDefault(r => r.FormatKind is null && r.Label == "EPUB");
        var mobiRow = formatRows.FirstOrDefault(r => r.FormatKind == BookFormatKind.Mobi);
        var pdfRow = formatRows.FirstOrDefault(r => r.FormatKind == BookFormatKind.Pdf);

        var buttons = metadata is null ? new List<Button>() : VisibleButtons(metadata);
        var switchButtons = buttons.Where(b => b.Content as string == "切换此格式").ToList();

        checks.Add(new SmokeCheck(
            "detail-reading-format-mobi-shows-epub",
            epubRow is not null
                && epubRow.Value == BookCardViewModel.FormatSize(artifactBytes)
                // 虚拟 EPUB 行不是库里的格式记录：不给「移除该格式」，也不给「切换此格式」
                && !epubRow.IsDeletedFormat
                && !epubRow.IsSwitchable
                && mobiRow is not null
                && mobiRow.Value.EndsWith(BookCardViewModel.ConvertedFormatNote, StringComparison.Ordinal)
                && !mobiRow.IsSwitchable
                && pdfRow is { IsSwitchable: true }
                && switchButtons.Count == 1
                && formatText?.Text == "当前阅读格式：EPUB",
            "EPUB 行=" + (epubRow?.Value ?? "(没有)")
                + " MOBI 行=" + (mobiRow?.Value ?? "(没有)")
                + " PDF 可切换=" + (pdfRow?.IsSwitchable ?? false)
                + " 可见切换按钮=" + switchButtons.Count
                + " 底部小字=" + (formatText?.Text ?? "(没有)")));

        Directory.CreateDirectory(paths.TempDir);
        CaptureWindow(window, Path.Combine(paths.TempDir, "library-reading-format-epub.png"));

        /* 用户 2026-10-21 截图反馈：
           ① 「切换此格式」要放在大小右边、不要贴面板右边缘；
           ② 格式行行高固定（按钮不许把行撑高），上面的信息行保持默认高度；
           ③ MOBI 要显示在 EPUB 前面。这里量的是真实可视树里的行高与按钮位置，不靠"看着差不多"。 */
        var rowVisuals = metadata is null
            ? new List<(FrameworkElement Element, MetadataRow Row)>()
            : MetadataRowVisuals(metadata);

        var formatVisuals = rowVisuals.Where(v => v.Row.IsFormatRow).ToList();
        var infoVisuals = rowVisuals.Where(v => !v.Row.IsFormatRow).ToList();

        var heightsFixed = formatVisuals.Count >= 2
            && formatVisuals.All(v => Math.Abs(v.Element.ActualHeight - 22) < 0.6)
            && infoVisuals.Count > 0
            && infoVisuals.Any(v => Math.Abs(v.Element.ActualHeight - 22) > 0.6);

        var firstSwitchButton = switchButtons.FirstOrDefault();
        var buttonRight = firstSwitchButton is null || metadata is null
            ? null
            : RightEdgeWithin(firstSwitchButton, metadata);
        var buttonInline = firstSwitchButton is not null
            && metadata is not null
            && firstSwitchButton.ActualHeight <= 22.5
            && buttonRight is not null
            && buttonRight < metadata.ActualWidth * 0.8;

        var allRows = target.Metadata.ToList();
        var mobiIndex = allRows.FindIndex(r => r.FormatKind == BookFormatKind.Mobi);
        var epubIndex = allRows.FindIndex(r => r.IsFormatRow && r.FormatKind is null);

        checks.Add(new SmokeCheck(
            "detail-format-row-layout",
            heightsFixed && buttonInline && mobiIndex >= 0 && epubIndex >= 0 && mobiIndex < epubIndex,
            "格式行高=" + string.Join("/", formatVisuals.Select(v => v.Element.ActualHeight.ToString("0.#")))
                + "（应为 22）；信息行高=" + string.Join("/",
                    infoVisuals.Take(3).Select(v => v.Element.ActualHeight.ToString("0.#")))
                + "（应不是 22）；按钮高=" + (firstSwitchButton?.ActualHeight.ToString("0.#") ?? "-")
                + " 按钮右边界=" + (buttonRight?.ToString("0.#") ?? "-")
                + " 详情宽=" + (metadata?.ActualWidth.ToString("0.#") ?? "-")
                + "（右边界要明显小于 80% 详情宽 = " + (metadata is null ? "-" : (metadata.ActualWidth * 0.8).ToString("0.#")) + "）"
                + "；MOBI 行=" + mobiIndex + " EPUB 行=" + epubIndex + "（MOBI 必须在前）"));

        // ---------- 3. 点「切换此格式」-> PDF，落库 + 界面 + READ 口径一起变 ----------
        var pdfButton = switchButtons.FirstOrDefault(
            b => b.DataContext is MetadataRow { FormatKind: BookFormatKind.Pdf });

        if (pdfButton is not null)
        {
            pdfButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }

        var switched = await WaitForReadingFormatAsync(
            repo, bookId, BookFormatKind.Pdf, TimeSpan.FromSeconds(10)).ConfigureAwait(true);

        window.UpdateLayout();

        checks.Add(new SmokeCheck(
            "detail-switch-reading-format",
            switched
                && target.EffectiveFormat?.Format == BookFormatKind.Pdf
                && formatText?.Text == "当前阅读格式：PDF"
                // 不再以 EPUB 形态读：角标里那个"额外"的 EPUB 也就没了，但真实格式一个不少
                && target.FormatBadge
                    .Split('+', StringSplitOptions.RemoveEmptyEntries)
                    .OrderBy(part => part, StringComparer.Ordinal)
                    .SequenceEqual(new[] { "MOBI", "PDF" }),
            "库里当前格式=" + (await repo.GetReadingFormatAsync(bookId).ConfigureAwait(true))?.ToString()
                + " 卡片当前格式=" + (target.EffectiveFormat?.Format.ToString() ?? "(无)")
                + " 底部小字=" + (formatText?.Text ?? "(没有)")
                + " 角标=" + target.FormatBadge));

        // ---------- 4. 没有转换产物（从没读过）时的显示口径 ----------
        File.Delete(artifact);

        await repo.SetReadingFormatAsync(bookId, BookFormatKind.Mobi).ConfigureAwait(true);
        await vm.RefreshBookAsync(target).ConfigureAwait(true);
        window.UpdateLayout();

        var noEpubRow = target.Metadata.All(r => r.FormatKind is not null || r.Label != "EPUB");

        checks.Add(new SmokeCheck(
            "detail-reading-format-not-converted-hint",
            noEpubRow
                && formatText?.Text == "当前格式不支持阅读，点击阅读后自动转换为 EPUB 格式进行阅读",
            "还有 EPUB 行=" + !noEpubRow
                + " 底部小字=" + (formatText?.Text ?? "(没有)")));

        CaptureWindow(window, Path.Combine(paths.TempDir, "library-reading-format-not-converted.png"));

        // ---------- 5. 来回切换格式没有动阅读进度 ----------
        var progress = await repo.GetReadingProgressAsync(bookId).ConfigureAwait(true);

        checks.Add(new SmokeCheck(
            "reading-format-switch-keeps-progress",
            progress is not null
                && Math.Abs(progress.Percent - 0.42) < 0.001
                && progress.SourceFormat == BookFormatKind.Epub,
            "进度=" + (progress?.Percent.ToString("0.###") ?? "(没有)")
                + " 来源格式=" + (progress?.SourceFormat?.ToString() ?? "(没有)")));
    }

    /// <summary>等sample的当前阅读格式变成期望值（切换按钮的处理器是 async void）。</summary>
    private static async Task<bool> WaitForReadingFormatAsync(
        IBookRepository repo, Guid bookId, BookFormatKind expected, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.Now + timeout;
        BookFormatKind? actual = null;

        while (DateTimeOffset.Now < deadline)
        {
            actual = await repo.GetReadingFormatAsync(bookId).ConfigureAwait(true);

            if (actual == expected)
            {
                return true;
            }

            await Task.Delay(50).ConfigureAwait(true);
        }

        return actual == expected;
    }

    /// <summary>
    /// 用户 2026-10-20：详情里的「（已删除）」格式与「移除该格式」按钮。
    /// 真走一遍：构造双格式书 -> 删掉 EPUB 文件 -> 扫描 -> 详情显示「（已删除）」+ 按钮
    /// -> 点按钮 -> 库里 EPUB 记录消失 / 徽标与大小切到 PDF / FileState 回 Normal / 缺失提示消失
    /// -> 阅读进度与阅读状态一个都没变。
    /// 再补一条边界：把最后一个格式也移掉时，详情要保留「可以从书库移除这本书」的提示。
    /// </summary>
    private static async Task CheckDeletedFormatFlowAsync(
        LibraryPage page,
        LibraryViewModel vm,
        MainWindow window,
        IBookRepository repo,
        AppPaths paths,
        List<SmokeCheck> checks)
    {
        if (vm.Books.Count < 2)
        {
            checks.Add(new SmokeCheck("deleted-format-flow", false, "书不够两本，没法验移除格式"));
            return;
        }

        // ---------- 造书：给第一本加一个真的 PDF 文件，再把它的 EPUB 文件删掉 ----------
        var original = vm.Books[0];
        var bookId = original.Id;
        var epubPath = original.Book.Formats[0].FilePath;
        var dir = Path.GetDirectoryName(epubPath)!;
        var pdfPath = Path.Combine(dir, "smoke-extra.pdf");

        await File.WriteAllTextAsync(pdfPath, "冒烟占位 PDF，不参与解析").ConfigureAwait(true);

        var pdfSize = new FileInfo(pdfPath).Length;

        await repo.AddFormatAsync(new BookFormat
        {
            BookId = bookId,
            Format = BookFormatKind.Pdf,
            FilePath = pdfPath,
            FileSize = pdfSize,
            AddedAt = DateTimeOffset.Now,
        }).ConfigureAwait(true);

        File.Delete(epubPath);

        /* 进度与状态先写进去：移除格式之后这两样必须原封不动。
           状态特意用「已读完」——它不会被 SaveReadingProgress 顺手改成「在读」，
           所以"没变"这件事验得干净。 */
        await repo.SaveReadingProgressAsync(new ReadingProgress
        {
            BookId = bookId,
            SpineIndex = 3,
            Location = "{\"chapterIndex\":3,\"percentInChapter\":0.25}",
            Percent = 0.31,
            UpdatedAt = DateTimeOffset.UtcNow,
        }).ConfigureAwait(true);

        await repo.SetReadingStatusAsync(bookId, ReadingStatus.Finished).ConfigureAwait(true);

        // 扫描：EPUB 被标缺失，卡片进入 Missing（用户看到「已删除」的前提）
        await vm.RefreshWithScanAsync().ConfigureAwait(true);
        window.UpdateLayout();

        var card = vm.Books.FirstOrDefault(b => b.Id == bookId);
        var missingState = card?.FileState == BookFileState.Missing;

        checks.Add(new SmokeCheck(
            "deleted-format-card-marked-missing",
            card is not null
                && card.Book.Formats.Count == 2
                && missingState
                && card.Book.Formats.Any(f => f.Format == BookFormatKind.Epub && f.Missing),
            "格式数=" + (card?.Book.Formats.Count ?? -1)
                + " FileState=" + (card?.FileState.ToString() ?? "(无)")
                + " EPUB 已标缺失=" + (card?.Book.Formats
                    .FirstOrDefault(f => f.Format == BookFormatKind.Epub)?.Missing ?? false)));

        if (card is null)
        {
            checks.Add(new SmokeCheck("detail-shows-deleted-format", false, "扫描后找不到这本书的卡片"));
            return;
        }

        // ---------- 详情：格式行显示「（已删除）」+ 可见的「移除该格式」按钮 ----------
        vm.OpenDetail(card);
        window.UpdateLayout();

        var metadata = page.FindName("DetailMetadata") as ItemsControl;
        var deletedRow = card.Metadata.FirstOrDefault(
            r => r.IsDeletedFormat && r.Label == "EPUB");
        var rowTexts = new List<string>();

        if (metadata is not null)
        {
            CollectTexts(metadata, rowTexts);
        }

        var visibleButtons = metadata is null
            ? new List<Button>()
            : VisibleButtons(metadata);
        var removeButton = visibleButtons.FirstOrDefault(b => b.Content as string == "移除该格式");

        checks.Add(new SmokeCheck(
            "detail-shows-deleted-format",
            metadata is not null
                && deletedRow is { Value: BookCardViewModel.DeletedFormatText }
                && rowTexts.Contains(BookCardViewModel.DeletedFormatText)
                // 大小位置不再显示大小、也不再用旧的「（文件缺失）」后缀
                && !rowTexts.Any(t => t.Contains("文件缺失"))
                && removeButton is not null,
            "EPUB 行=" + (deletedRow is null
                ? "(没有标记为已删除的行)"
                : "「" + deletedRow.Value + "」")
                + " 详情文字里有「" + BookCardViewModel.DeletedFormatText + "」="
                + rowTexts.Contains(BookCardViewModel.DeletedFormatText)
                + " 可见的移除按钮=" + (removeButton is not null)));

        /* 截图存证（用户 2026-10-20）：详情里「（已删除）」+「移除该格式」按钮的真实观感。 */
        Directory.CreateDirectory(paths.TempDir);
        CaptureWindow(window, Path.Combine(paths.TempDir, "library-deleted-format.png"));

        if (removeButton is null)
        {
            checks.Add(new SmokeCheck("remove-format-via-button", false, "详情里没有可点的「移除该格式」按钮"));
            return;
        }

        // ---------- 点按钮（走真实 Click，不是直接调 VM）----------
        removeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        var removedFromDb = await WaitForFormatCountAsync(repo, bookId, 1, TimeSpan.FromSeconds(10))
            .ConfigureAwait(true);

        /* DB 里记录删掉只说明第一步完成；后面还要重扫 + 刷新卡片，界面才跟上。
           处理器是 async void，RaiseEvent 不等它，所以这里等卡片刷到新状态。 */
        var cardRefreshed = await WaitForCardAsync(
            vm,
            bookId,
            c => c.Book.Formats.Count == 1 && c.FileState == BookFileState.Normal,
            TimeSpan.FromSeconds(10)).ConfigureAwait(true);

        window.UpdateLayout();

        var after = vm.Books.FirstOrDefault(b => b.Id == bookId);
        var freshBook = await repo.GetByIdAsync(bookId).ConfigureAwait(true);
        var progressAfter = await repo.GetReadingProgressAsync(bookId).ConfigureAwait(true);

        var remainingIsPdf = freshBook is { Formats.Count: 1 }
            && freshBook.Formats[0].Format == BookFormatKind.Pdf;

        // 「点阅读会用剩下的格式」：阅读器选文件的口径就是"没缺失且文件还在"
        var readableAfter = freshBook?.Formats
            .Where(f => !f.Missing && File.Exists(f.FilePath))
            .Select(f => f.Format)
            .ToList() ?? new List<BookFormatKind>();
        var readOpensPdf = readableAfter.Count == 1 && readableAfter[0] == BookFormatKind.Pdf;

        var fileStateBar = page.FindName("DetailFileStateBar") as Border;
        var noFormatsBar = page.FindName("DetailNoFormatsBar") as Border;
        var detailStillOpen = vm.IsDetailOpen && ReferenceEquals(vm.DetailBook, after);

        checks.Add(new SmokeCheck(
            "remove-format-via-button",
            removedFromDb
                && cardRefreshed
                && remainingIsPdf
                && after is not null
                && after.FormatBadge == "PDF"
                && after.SizeText == BookCardViewModel.FormatSize(pdfSize)
                && after.FileState == BookFileState.Normal
                && after.Metadata.All(r => r.Label != "EPUB")
                && after.Metadata.All(r => !r.IsDeletedFormat)
                && (fileStateBar?.Visibility ?? Visibility.Visible) != Visibility.Visible
                && !after.HasFileProblem
                && (noFormatsBar?.Visibility ?? Visibility.Visible) != Visibility.Visible
                && detailStillOpen
                && readOpensPdf,
            "DB 里格式数=" + (freshBook?.Formats.Count ?? -1) + "（剩 PDF=" + remainingIsPdf + "）"
                + " 卡片已刷新=" + cardRefreshed
                + " 徽标=" + (after?.FormatBadge ?? "(无)")
                + " 大小=" + (after?.SizeText ?? "(无)") + "（期望 " + BookCardViewModel.FormatSize(pdfSize) + "）"
                + " FileState=" + (after?.FileState.ToString() ?? "(无)")
                + " 还有已删除行=" + (after?.Metadata.Any(r => r.IsDeletedFormat) ?? true)
                + " 缺失提示条隐藏=" + ((fileStateBar?.Visibility ?? Visibility.Visible) != Visibility.Visible)
                + " 空格式提示隐藏=" + ((noFormatsBar?.Visibility ?? Visibility.Visible) != Visibility.Visible)
                + " 阅读会打开 PDF=" + readOpensPdf));

        checks.Add(new SmokeCheck(
            "remove-format-keeps-progress-and-status",
            progressAfter is not null
                && progressAfter.Percent.ToString("0.###") == "0.31"
                && progressAfter.SpineIndex == 3
                && freshBook?.Status == ReadingStatus.Finished,
            "进度=" + (progressAfter?.Percent.ToString("0.###") ?? "(无)")
                + " 章号=" + (progressAfter?.SpineIndex?.ToString() ?? "(无)")
                + " 状态=" + (freshBook?.Status.ToString() ?? "(无)")));

        // ---------- 边界：把最后一个格式也移掉，才保留「可以从书库移除这本书」 ----------
        var lastCard = vm.Books.FirstOrDefault(b => b.Id != bookId && b.Book.Formats.Count == 1);

        if (lastCard is null)
        {
            checks.Add(new SmokeCheck("last-format-removed-shows-book-hint", false, "找不到只有单格式的书"));
            return;
        }

        File.Delete(lastCard.Book.Formats[0].FilePath);
        await vm.RefreshWithScanAsync().ConfigureAwait(true);
        window.UpdateLayout();

        var lonely = vm.Books.FirstOrDefault(b => b.Id == lastCard.Id);
        var lonelyKind = lonely?.Book.Formats[0].Format ?? BookFormatKind.Unknown;

        if (lonely is null)
        {
            checks.Add(new SmokeCheck("last-format-removed-shows-book-hint", false, "扫描后找不到那本书"));
            return;
        }

        vm.OpenDetail(lonely);
        window.UpdateLayout();

        var removed = await vm.RemoveFormatAsync(lonely, lonelyKind).ConfigureAwait(true);

        window.UpdateLayout();

        var empty = vm.Books.FirstOrDefault(b => b.Id == lonely.Id);
        var noFormatsBarAfter = page.FindName("DetailNoFormatsBar") as Border;
        var noFormatsText = page.FindName("DetailNoFormatsText") as TextBlock;
        var fileStateBarAfter = page.FindName("DetailFileStateBar") as Border;

        checks.Add(new SmokeCheck(
            "last-format-removed-shows-book-hint",
            removed
                && empty is not null
                && empty.HasNoFormats
                && empty.Book.Formats.Count == 0
                && empty.FileState == BookFileState.Normal
                && (noFormatsBarAfter?.Visibility ?? Visibility.Collapsed) == Visibility.Visible
                && (noFormatsText?.Text ?? string.Empty).Contains("可以从书库移除这本书")
                && (fileStateBarAfter?.Visibility ?? Visibility.Visible) != Visibility.Visible,
            "移除成功=" + removed
                + " 格式数=" + (empty?.Book.Formats.Count ?? -1)
                + " FileState=" + (empty?.FileState.ToString() ?? "(无)")
                + " 提示条可见=" + ((noFormatsBarAfter?.Visibility ?? Visibility.Collapsed) == Visibility.Visible)
                + " 提示文字=" + (noFormatsText?.Text ?? "(无)")));
    }

    /// <summary>
    /// 收集详情元数据每一行的可视元素（行 Grid）与它绑定的 MetadataRow。
    /// 只认"第一层 DataContext 是 MetadataRow 的 Grid"，行内部的小 Grid 不再往下钻。
    /// </summary>
    private static List<(FrameworkElement Element, MetadataRow Row)> MetadataRowVisuals(
        DependencyObject root)
    {
        var found = new List<(FrameworkElement Element, MetadataRow Row)>();

        void Walk(DependencyObject node)
        {
            if (node is Grid grid && grid.DataContext is MetadataRow row)
            {
                found.Add((grid, row));
                return;
            }

            var count = VisualTreeHelper.GetChildrenCount(node);

            for (var i = 0; i < count; i++)
            {
                Walk(VisualTreeHelper.GetChild(node, i));
            }
        }

        Walk(root);
        return found;
    }

    /// <summary>把控件右边界换算到指定祖先的坐标系（用来判断按钮有没有贴面板右边缘）。</summary>
    private static double? RightEdgeWithin(FrameworkElement element, Visual ancestor)
    {
        try
        {
            var origin = element.TransformToAncestor(ancestor).Transform(new Point(0, 0));
            return origin.X + element.ActualWidth;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>收集一棵可视树里所有"真的看得见"的按钮（折叠的模板按钮不算）。</summary>
    private static List<Button> VisibleButtons(DependencyObject root)
    {
        var found = new List<Button>();

        void Walk(DependencyObject node)
        {
            if (node is Button button && button.Visibility == Visibility.Visible)
            {
                found.Add(button);
            }

            var count = VisualTreeHelper.GetChildrenCount(node);

            for (var i = 0; i < count; i++)
            {
                Walk(VisualTreeHelper.GetChild(node, i));
            }
        }

        Walk(root);
        return found;
    }

    /// <summary>
    /// 等某张卡片满足条件（移除格式的处理器是 async void，界面刷新会晚于"记录已删"）。
    /// </summary>
    private static async Task<bool> WaitForCardAsync(
        LibraryViewModel vm, Guid bookId, Func<BookCardViewModel, bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.Now + timeout;
        BookCardViewModel? card = null;

        while (DateTimeOffset.Now < deadline)
        {
            card = vm.Books.FirstOrDefault(b => b.Id == bookId);

            if (card is not null && predicate(card))
            {
                return true;
            }

            await Task.Delay(50).ConfigureAwait(true);
        }

        return card is not null && predicate(card);
    }

    /// <summary>等sample的格式数变成期望值（移除按钮的处理器是 async void）。</summary>
    private static async Task<bool> WaitForFormatCountAsync(
        IBookRepository repo, Guid bookId, int expected, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.Now + timeout;
        Book? book = null;

        while (DateTimeOffset.Now < deadline)
        {
            book = await repo.GetByIdAsync(bookId).ConfigureAwait(true);

            if (book is not null && book.Formats.Count == expected)
            {
                return true;
            }

            await Task.Delay(50).ConfigureAwait(true);
        }

        return book is not null && book.Formats.Count == expected;
    }

    /// <summary>等某张卡片的阅读状态变成期望值（手动标记走的是 async void 处理器）。</summary>
    private static async Task<bool> WaitForStatusAsync(
        BookCardViewModel card,
        ReadingStatus expected,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.Now + timeout;

        while (DateTimeOffset.Now < deadline)
        {
            if (card.Book.Status == expected)
            {
                return true;
            }

            await Task.Delay(25).ConfigureAwait(true);
        }

        return card.Book.Status == expected;
    }

    private static async Task<bool> WaitForBookCountAsync(IBookRepository repo, int expected, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.Now + timeout;
        var actual = -1;

        while (DateTimeOffset.Now < deadline)
        {
            actual = await repo.CountAsync().ConfigureAwait(true);
            if (actual == expected)
            {
                return true;
            }

            await Task.Delay(50).ConfigureAwait(true);
        }

        return actual == expected;
    }
}
