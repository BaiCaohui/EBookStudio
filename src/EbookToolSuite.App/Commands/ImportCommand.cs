using System.Windows;
using EbookToolSuite.App.Views;
using EbookToolSuite.Core.Abstractions;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Errors;
using EbookToolSuite.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace EbookToolSuite.App.Commands;

/// <summary>
/// 批量导入。菜单里的「文件 - 导入文件 / 导入文件夹」与书库空状态里的按钮**共用这一份**
/// （设计文档 9.9.17 明确要求，避免两处行为不一致）。
/// </summary>
/// <remarks>
/// T2-1 起走 ImportQueue：多选文件、递归扫文件夹、预检去重、
/// 进度可暂停可取消、结果写进 import_batch。
/// 旧的"一次一本 + 弹一个汇总 MessageBox"已删除：200 本时那种做法既看不见进度也点不完弹窗。
/// </remarks>
public sealed class ImportCommand
{
    private const string EbookFilter =
        "EPUB 电子书 (*.epub)|*.epub"
        + "|所有支持的电子书 (*.epub;*.mobi;*.azw;*.azw3;*.fb2;*.pdf;*.txt;*.cbz;*.cbr)"
        + "|*.epub;*.mobi;*.azw;*.azw3;*.fb2;*.pdf;*.txt;*.cbz;*.cbr"
        + "|所有文件 (*.*)|*.*";

    private readonly IImportQueue _queue;
    private readonly ILogger<ImportCommand> _log;

    /// <summary>上一批的窗口。队列是单例，旧窗口必须先关掉，否则它会收到下一批的事件。</summary>
    private ImportQueueWindow? _openWindow;

    public ImportCommand(IImportQueue queue, ILogger<ImportCommand> log)
    {
        _queue = queue;
        _log = log;
    }

    /// <summary>正在导入。用于按钮置灰与防重入。</summary>
    public bool IsRunning { get; private set; }

    /// <summary>至少成功导入了一本时触发。书库页靠它刷新网格（菜单与页内两个入口共用）。</summary>
    public event EventHandler? Completed;

    /// <summary>选一个或多个文件。</summary>
    public async Task RunAsync(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        if (IsRunning)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "选择要导入的电子书",
            Filter = EbookFilter,
            // 默认选第 2 项「所有支持的电子书」：停在只有 .epub 的第 1 项会让人以为 MOBI/AZW3 等导不进来。
            FilterIndex = 2,
            Multiselect = true,
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(owner) != true)
        {
            return;
        }

        await RunCoreAsync(owner, dialog.FileNames, DescribeFiles(dialog.FileNames)).ConfigureAwait(true);
    }

    /// <summary>选一个文件夹，递归扫描里面的电子书。</summary>
    public async Task RunFolderAsync(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        if (IsRunning)
        {
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "选择要导入的文件夹（含子文件夹）",
            Multiselect = false,
        };

        if (dialog.ShowDialog(owner) != true)
        {
            return;
        }

        await RunCoreAsync(owner, new[] { dialog.FolderName }, dialog.FolderName).ConfigureAwait(true);
    }

    /// <summary>
    /// 直接导入手上这批路径（T7-3 拖放用）：不弹文件选择框，其余走同一条预检 + 队列。
    /// </summary>
    public async Task RunPathsAsync(Window owner, IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(paths);

        if (IsRunning || paths.Count == 0)
        {
            return;
        }

        await RunCoreAsync(owner, paths, DescribeFiles(paths)).ConfigureAwait(true);
    }

    private async Task RunCoreAsync(Window owner, IReadOnlyList<string> paths, string sourceLabel)
    {
        // 队列是单例：上一批的窗口还开着的话，它会继续订阅同一批事件并显示错的数据。
        // 此时上一批必然已经结束（IsRunning 在批次结束时才复位），关掉它是安全的。
        _openWindow?.Close();

        var window = new ImportQueueWindow(_queue) { Owner = owner };
        _openWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_openWindow, window))
            {
                _openWindow = null;
            }
        };

        IsRunning = true;
        window.Show();

        try
        {
            var options = new ImportQueueOptions
            {
                Recursive = true,
                SourceLabel = sourceLabel,
            };

            // 预检（扫描 + 算指纹 + 查重 + 弹重复对话框）发生在 StartAsync 里面：
            // 窗口先显示"正在扫描"，拿到批次再把每一行灌进列表。
            var batch = await _queue.StartAsync(paths, options, window.Token).ConfigureAwait(true);
            window.Attach(batch);

            await _queue.WaitForCompletionAsync(window.Token).ConfigureAwait(true);
        }
        catch (ArgumentException ex)
        {
            // 一个能导入的文件都没扫到：留在窗口里说明原因，让用户自己关。
            _log.LogInformation("这一批没有可导入的文件：{Message}", ex.Message);
            window.ShowStartupFailure(ex.SafeMessage());
        }
        catch (OperationCanceledException)
        {
            // 用户关窗或取消：正常路径，窗口自己会收尾。
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "批量导入失败");
            window.ShowStartupFailure("导入没能开始：" + ex.SafeMessage());
            AppDialog.Show(owner, ex.SafeMessage(), "导入失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsRunning = false;
        }

        // 只要有成功导入的，就通知书库页刷新（两处入口共用这一个事件）。
        if (_queue.Jobs.Count(j => j.Outcome == ImportOutcome.Imported) > 0)
        {
            Completed?.Invoke(this, EventArgs.Empty);
        }
    }

    private static string DescribeFiles(IReadOnlyList<string> files)
        => files.Count == 1 ? files[0] : files.Count + " 个文件";
}
