using System.IO;
using System.Windows;
using System.Windows.Controls;
using EbookToolSuite.App.Settings;
using EbookToolSuite.App.ViewModels;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Errors;
using EbookToolSuite.Core.Exceptions;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;
using EbookToolSuite.Engines.Calibre;
using EbookToolSuite.Services.Conversion;
using Serilog;

namespace EbookToolSuite.App.Views;

/// <summary>
/// 转换窗口：选源格式 / 目标格式 / 模板 / 高级参数，然后【排进转换队列】。
///
/// 2026-10-04 起这个窗口不再自己跑转换（用户拍板）：所有转换一律走队列，
/// 「转一次等一次」这条路径彻底消失，进度与结果统一在「转换队列」页看。
/// IConversionService 还留着，是因为下拉框要用它的支持矩阵与模板清单。
/// </summary>
public partial class ConvertWindow : Window
{
    private readonly IConversionService _conversion;
    private readonly IConversionQueue _queue;
    private readonly ConversionTemplateStore _templates;
    private readonly IToolLocator _tools;
    private readonly IConfigStore? _config;
    private readonly Book _book;

    private bool _busy;
    private bool _calibreAvailable = true;

    public ConvertWindow(
        IConversionService conversion,
        IConversionQueue queue,
        ConversionTemplateStore templates,
        IToolLocator tools,
        Book book,
        IConfigStore? config = null)
    {
        _conversion = conversion;
        _queue = queue;
        _templates = templates;
        _tools = tools;
        _book = book;
        _config = config;

        InitializeComponent();

        Title = "转换格式 - " + book.Title;
        BookTitleText.Text = book.Title;
        BookFormatsText.Text = BuildFormatsLine(book);

        Loaded += OnLoaded;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private sealed record SourceOption(BookFormatKind Format, string FilePath, string Display)
    {
        public override string ToString() => Display;
    }

    private sealed record TargetOption(BookFormatKind Format, string Display, bool KeepsFormat = false)
    {
        public override string ToString() => Display;
    }

    private sealed record TemplateOption(string? Id, string Display)
    {
        public override string ToString() => Display;
    }

    // ---------- 下拉框填充 ----------

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        /* T7-2 第 10 项：外部工具被删 -> 功能置灰 + 说明 + 指引。
           CalibreConvert 即使文件不存在也会返回"期望路径"，正好用来告诉用户应该放哪。 */
        _calibreAvailable = File.Exists(_tools.CalibreConvert);

        if (!_calibreAvailable)
        {
            HintText.Text = "未找到 Calibre，转换功能不可用。"
                + Environment.NewLine
                + "应该在：" + _tools.CalibreConvert
                + Environment.NewLine
                + "把 Calibre 放回 Tools/Calibre 目录（或按「工具准备说明」重新安装），"
                + "然后点主窗口右上角的工具指示灯重新检测。";

            StartButton.IsEnabled = false;
            SourceCombo.IsEnabled = false;
            TargetCombo.IsEnabled = false;
            TemplateCombo.IsEnabled = false;
            AdvancedBox.IsEnabled = false;
            SaveAsTemplateButton.IsEnabled = false;
            return;
        }

        var sources = _book.Formats
            .Where(f => File.Exists(f.FilePath))
            .Where(f => _conversion.GetSupportedTargets(f.Format).Count > 0)
            .Select(f => new SourceOption(
                f.Format,
                f.FilePath,
                f.Format.ToStorageString() + "  |  " + BookCardViewModel.FormatSize(f.FileSize)))
            .ToList();

        SourceCombo.ItemsSource = sources;
        if (sources.Count > 0)
        {
            SourceCombo.SelectedIndex = 0;
        }
        else
        {
            HintText.Text = "这本书没有可用的源文件（文件可能被移动或删除了）。";
            StartButton.IsEnabled = false;
        }
    }

    private void RefreshTargets()
    {
        if (SourceCombo.SelectedItem is not SourceOption source)
        {
            return;
        }

        var existing = _book.Formats.Select(f => f.Format).ToHashSet();
        var targets = _conversion.GetSupportedTargets(source.Format)
            .Where(t => !existing.Contains(t))
            .Select(t => new TargetOption(t, DescribeTarget(t)))
            .ToList();

        // 「不换格式，只优化」（polish）单独放一项：产物就地替换源文件，所以
        // 不受「这本书已经有这个格式了」那条限制（那条对 polish 必然为真）。
        if (_conversion.GetFormatKeepingTemplates(source.Format).Count > 0)
        {
            targets.Add(new TargetOption(
                source.Format,
                "不换格式，只优化（" + source.Format.ToStorageString() + "）",
                KeepsFormat: true));
        }

        TargetCombo.ItemsSource = targets;
        if (targets.Count > 0)
        {
            // T7-4：设置页里选的默认输出格式优先；读不到 / 这本转不出来时保持原来的"第一个"。
            TargetCombo.SelectedIndex = FindPreferredTargetIndex(targets);
            StartButton.IsEnabled = _calibreAvailable;
        }
        else
        {
            HintText.Text = "这本书已经有它所有能转出的格式了。要重新转换请先移除旧格式。";
            StartButton.IsEnabled = false;
        }
    }

    /// <summary>
    /// 默认目标格式在本次可选目标里的下标；没有配置、配置值不认识、或者这本转不出该格式时返回 0
    /// （即"第一个"，与 T7-4 之前的行为一致）。
    /// </summary>
    private int FindPreferredTargetIndex(List<TargetOption> targets)
    {
        var stored = _config?.GetString(AppSettingsKeys.ConversionDefaultTarget, null);

        if (string.IsNullOrWhiteSpace(stored))
        {
            return 0;
        }

        var preferred = BookFormatKinds.FromStorageString(stored);

        if (preferred == BookFormatKind.Unknown)
        {
            return 0;
        }

        for (var i = 0; i < targets.Count; i++)
        {
            // 「不换格式，只优化」的 KeepsFormat 项不算"目标格式"，不能被默认值选中。
            if (!targets[i].KeepsFormat && targets[i].Format == preferred)
            {
                return i;
            }
        }

        return 0;
    }

    private void RefreshTemplates()
    {
        if (TargetCombo.SelectedItem is not TargetOption target)
        {
            return;
        }

        var options = new List<TemplateOption>();

        // 「不换格式，只优化」必须选一个具体模板：polish 不带参数就什么都没做，
        // 所以这一组里不提供「不套模板」。
        if (!target.KeepsFormat)
        {
            options.Add(new TemplateOption(null, "不套模板（只用默认参数）"));
        }

        // 选了「不换格式，只优化」时，模板清单换成 polish 专用的那组。
        var templates = target.KeepsFormat && SourceCombo.SelectedItem is SourceOption polishSource
            ? _conversion.GetFormatKeepingTemplates(polishSource.Format)
            : _conversion.GetTemplates(target.Format);

        foreach (var template in templates)
        {
            options.Add(new TemplateOption(
                template.Id, template.DisplayName + "（" + template.Priority + "）"));
        }

        TemplateCombo.ItemsSource = options;
        TemplateCombo.SelectedIndex = options.Count > 1 ? 1 : 0;
        UpdateOutputPath();
    }

    // ---------- 交互 ----------

    private void OnSourceChanged(object sender, SelectionChangedEventArgs e) => RefreshTargets();

    private void OnTargetChanged(object sender, SelectionChangedEventArgs e) => RefreshTemplates();

    private void OnTemplateChanged(object sender, SelectionChangedEventArgs e)
    {
        TemplateNoteText.Text = ResolveTemplateNote();
        UpdateOutputPath();
        UpdateTemplateButtons();
    }

    /// <summary>「删除模板」只在内置模板**之外**的模板被选中时可用（内置的删不掉）。</summary>
    private void UpdateTemplateButtons()
    {
        var id = (TemplateCombo.SelectedItem as TemplateOption)?.Id;
        DeleteTemplateButton.IsEnabled = id is not null
            && _templates.UserTemplates.Any(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    private string ResolveTemplateNote()
    {
        if (TemplateCombo.SelectedItem is not TemplateOption option || option.Id is null)
        {
            return "不套模板：只使用 Config 里的默认档位，不加任何额外参数。";
        }

        var template = _templates.Find(option.Id);

        if (template is null)
        {
            return string.Empty;
        }

        var args = string.Join(' ', template.Arguments.Select(a => a.ToString()));
        return template.Note + Environment.NewLine + "参数：" + args;
    }

    /// <summary>输出位置：与源文件同目录（正式区里这本书自己的目录）。</summary>
    private void UpdateOutputPath()
    {
        if (SourceCombo.SelectedItem is not SourceOption source
            || TargetCombo.SelectedItem is not TargetOption target)
        {
            OutputPathText.Text = string.Empty;
            return;
        }

        var directory = Path.GetDirectoryName(source.FilePath) ?? "(未知)";
        OutputPathText.Text = directory
            + Environment.NewLine
            + "文件名与源文件同名，扩展名改为 " + target.Format.ToExtension()
            + "；重名会自动加序号，不会覆盖已有文件。";
    }

    /// <summary>加入队列。成功就关窗；失败把原因写在提示条上（不弹第三个窗口）。</summary>
    private async void OnStartClick(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        if (SourceCombo.SelectedItem is not SourceOption source
            || TargetCombo.SelectedItem is not TargetOption target)
        {
            return;
        }

        IReadOnlyList<ConversionArgument> overrides;
        try
        {
            overrides = CalibreAdvancedArgs.Parse(AdvancedBox.Text);
        }
        catch (AppException ex)
        {
            AppDialog.Show(this, ex.UserMessage, "参数有问题",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var request = new ConversionRequest
        {
            BookId = _book.Id,
            TargetFormat = target.Format,
            SourceFormat = source.Format,
            TemplateId = (TemplateCombo.SelectedItem as TemplateOption)?.Id,
            Overrides = overrides,
        };

        SetBusy(true);
        try
        {
            await _queue.EnqueueAsync(request).ConfigureAwait(true);
            DialogResult = true;   // 关窗，调用方据此刷新界面
        }
        catch (AppException ex)
        {
            HintText.Text = ex.UserMessage + "（" + ex.Code + "）";
            Log.Warning(ex, "加入转换队列失败 {Code}", ex.Code);
        }
        catch (Exception ex)
        {
            HintText.Text = "加入队列失败：" + ex.SafeMessage();
            Log.Error(ex, "加入转换队列时发生未预期的异常");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private void SetBusy(bool busy)
    {
        _busy = busy;
        StartButton.IsEnabled = !busy;
        CancelButton.IsEnabled = !busy;
        SourceCombo.IsEnabled = !busy;
        TargetCombo.IsEnabled = !busy;
        TemplateCombo.IsEnabled = !busy;
        AdvancedBox.IsEnabled = !busy;
        SaveAsTemplateButton.IsEnabled = !busy;
        if (busy)
        {
            DeleteTemplateButton.IsEnabled = false;
        }
        else
        {
            UpdateTemplateButtons();
        }
    }

    /// <summary>Esc 关窗、Enter 加入队列（焦点在多行参数框里时 Enter 仍然换行，不抢）。</summary>
    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            if (!_busy)
            {
                Close();
            }

            e.Handled = true;
            return;
        }

        if (e.Key == System.Windows.Input.Key.Enter
            && e.OriginalSource is not TextBox
            && StartButton.IsEnabled)
        {
            OnStartClick(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private static string BuildFormatsLine(Book book)
    {
        if (book.Formats.Count == 0)
        {
            return "这本书还没有登记任何格式文件。";
        }

        return "现有格式：" + string.Join("、", book.Formats
            .Select(f => f.Format.ToStorageString()
                + (File.Exists(f.FilePath) ? string.Empty : "（文件缺失）")));
    }

    private static string DescribeTarget(BookFormatKind format) => format switch
    {
        BookFormatKind.Epub => "EPUB  推荐：安卓手机/平板与电脑屏通吃，可重排、可调字号",
        BookFormatKind.Mobi => "MOBI  备选：部分安卓阅读器可读；本机没有 Kindle 设备",
        BookFormatKind.Pdf => "PDF  固定版式：电脑屏阅读与打印",
        BookFormatKind.Txt => "TXT  纯文本：只留文字，适合归档与全文搜索",
        BookFormatKind.Fb2 => "FB2",
        _ => format.ToStorageString(),
    };

    /// <summary>
    /// 把「当前模板 + 高级参数框里的临时覆盖」合并成一份用户模板存起来。
    /// 用户改过的参数因此能复用（执行计划 T2-5 判据 2）。
    /// </summary>
    private async void OnSaveAsTemplateClick(object sender, RoutedEventArgs e)
    {
        if (_busy || TargetCombo.SelectedItem is not TargetOption target)
        {
            return;
        }

        IReadOnlyList<ConversionArgument> overrides;
        try
        {
            overrides = CalibreAdvancedArgs.Parse(AdvancedBox.Text);
        }
        catch (AppException ex)
        {
            AppDialog.Show(this, ex.UserMessage, "参数有问题",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var baseTemplate = (TemplateCombo.SelectedItem as TemplateOption)?.Id is { } baseId
            ? _templates.Find(baseId)
            : null;

        var name = PromptForName(this, baseTemplate?.DisplayName ?? "我的模板");
        if (name is null)
        {
            return;
        }

        var sourceFormat = (SourceCombo.SelectedItem as SourceOption)?.Format;

        var template = new ConversionTemplate
        {
            Id = "user-" + Guid.NewGuid().ToString("N")[..12],
            DisplayName = name,
            TargetFormat = target.KeepsFormat ? null : target.Format,
            Engine = target.KeepsFormat ? ConversionEngine.Polish : ConversionEngine.Calibre,
            ApplicableFormats = target.KeepsFormat && sourceFormat is { } sf
                ? new[] { sf }
                : Array.Empty<BookFormatKind>(),
            Note = baseTemplate?.Note,
            IsBuiltIn = false,
            Arguments = CalibreArgumentBuilder.Merge(
                baseTemplate?.Arguments ?? Array.Empty<ConversionArgument>(),
                overrides),
        };

        SetBusy(true);
        try
        {
            await _templates.SaveUserAsync(template).ConfigureAwait(true);
            RefreshTemplates();

            // 选中刚存的那份，让用户立刻看到它生效了。
            if (TemplateCombo.ItemsSource is List<TemplateOption> options)
            {
                var index = options.FindIndex(
                    o => string.Equals(o.Id, template.Id, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    TemplateCombo.SelectedIndex = index;
                }
            }

            HintText.Text = "已保存模板「" + name + "」，下次可以直接在模板下拉里选它。";
        }
        catch (Exception ex)
        {
            HintText.Text = "保存模板失败：" + ex.SafeMessage();
            Log.Error(ex, "保存用户转换模板失败");
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>删掉选中的用户模板。内置模板删不掉（它们不在库里）。</summary>
    private async void OnDeleteTemplateClick(object sender, RoutedEventArgs e)
    {
        if (_busy || (TemplateCombo.SelectedItem as TemplateOption)?.Id is not { } id)
        {
            return;
        }

        var isUserTemplate = _templates.UserTemplates.Any(
            t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
        if (!isUserTemplate)
        {
            HintText.Text = "内置模板不能删。想改就「另存为模板」存一份自己的。";
            return;
        }

        var answer = AppDialog.Show(
            this,
            "删掉模板「" + id + "」？内置模板不受影响，已经排队的任务也不受影响。",
            "删除模板",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Cancel);

        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        SetBusy(true);
        try
        {
            await _templates.DeleteUserAsync(id).ConfigureAwait(true);
            RefreshTemplates();
            HintText.Text = "已删除模板。";
        }
        catch (Exception ex)
        {
            HintText.Text = "删除模板失败：" + ex.SafeMessage();
            Log.Error(ex, "删除用户转换模板失败");
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>最小的「输入名字」对话框。为一个输入框单开一套 XAML 不划算。</summary>
    private static string? PromptForName(Window owner, string initial)
    {
        var box = new TextBox
        {
            Text = initial,
            Margin = new Thickness(16, 0, 16, 0),
            MinWidth = 320,
        };

        var okButton = new Button
        {
            Content = "保存",
            IsDefault = true,
            MinWidth = 72,
            Padding = new Thickness(12, 4, 12, 4),
        };

        var cancelButton = new Button
        {
            Content = "取消",
            IsCancel = true,
            MinWidth = 72,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(12, 4, 12, 4),
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(16, 0, 16, 16),
        };
        buttons.Children.Add(okButton);
        buttons.Children.Add(cancelButton);

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "给这份模板起个名字：",
            Margin = new Thickness(16, 16, 16, 8),
        });
        panel.Children.Add(box);
        panel.Children.Add(buttons);

        var dialog = new Window
        {
            Title = "另存为模板",
            Owner = owner,
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        okButton.Click += (_, _) => dialog.DialogResult = true;

        // 选中初始名字：用户直接打字就覆盖掉。
        dialog.Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };

        return dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(box.Text)
            ? box.Text.Trim()
            : null;
    }
}
