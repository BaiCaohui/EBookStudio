using System.Windows;
using EbookToolSuite.App.ViewModels;

namespace EbookToolSuite.App.Views;

/// <summary>
/// 重复检测窗口（执行计划 T2-2）：列出可疑组、显示差异，
/// 用户可以「保留这本」（其余标为副本并从书库隐藏）或「忽略这组」。
/// </summary>
public partial class DuplicateScanWindow : Window
{
    private readonly DuplicateScanViewModel _vm;

    public DuplicateScanWindow(DuplicateScanViewModel vm)
    {
        _vm = vm;

        InitializeComponent();

        DataContext = vm;
        Loaded += OnLoaded;
    }

    /// <summary>冒烟用：关掉「打开就自动扫描」，免得和显式扫描并发打架。</summary>
    internal bool AutoScanOnLoad { get; set; } = true;

    /// <summary>当前列出的组数（冒烟用）。</summary>
    internal int GroupCount => _vm.Groups.Count;

    internal string StatusTextForSmoke => _vm.Status;

    /// <summary>按成员标题找组（冒烟用：不依赖组的排序）。</summary>
    internal int FindGroupIndexByMemberTitle(string title)
    {
        for (var i = 0; i < _vm.Groups.Count; i++)
        {
            if (_vm.Groups[i].Members.Any(m => m.Title == title))
            {
                return i;
            }
        }

        return -1;
    }

    internal int MemberCountOf(int index)
        => index >= 0 && index < _vm.Groups.Count ? _vm.Groups[index].Members.Count : -1;

    internal Guid MemberBookIdOf(int index, int member)
        => index >= 0 && index < _vm.Groups.Count && member >= 0 && member < _vm.Groups[index].Members.Count
            ? _vm.Groups[index].Members[member].BookId
            : Guid.Empty;

    /// <summary>组卡片在可视树里真实生成了几个（冒烟用：证明模板绑对了，不只是数据层有）。</summary>
    internal int RealizedCardCount()
    {
        GroupList.UpdateLayout();
        return GroupList.ItemContainerGenerator.Items.Count;
    }

    internal Task<int> ScanForSmokeAsync() => RunScanAsync();

    /// <summary>保留第 index 组里第 keepMember 本，其余标为副本。返回被标记的本数。</summary>
    internal async Task<int> MergeForSmokeAsync(int index, int keepMember)
    {
        if (index < 0 || index >= _vm.Groups.Count)
        {
            return -1;
        }

        var bookId = MemberBookIdOf(index, keepMember);
        if (bookId == Guid.Empty)
        {
            return -1;
        }

        return await _vm.MergeAsync(_vm.Groups[index], bookId, CancellationToken.None);
    }

    /// <summary>忽略第 index 组。</summary>
    internal async Task<bool> IgnoreForSmokeAsync(int index)
        => index >= 0 && index < _vm.Groups.Count
            && await _vm.IgnoreAsync(_vm.Groups[index], CancellationToken.None);

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        if (!AutoScanOnLoad)
        {
            return;
        }

        // 打开就自动扫一遍，省用户一次点击
        await RunScanAsync();
    }

    private async Task<int> RunScanAsync()
    {
        ScanButton.IsEnabled = false;

        try
        {
            return await _vm.ScanAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            return -1;
        }
        finally
        {
            ScanButton.IsEnabled = true;
        }
    }

    private async void OnScanClick(object sender, RoutedEventArgs e) => await RunScanAsync();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
