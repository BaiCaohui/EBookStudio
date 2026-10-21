using System.IO;
using System.Windows;
using System.Windows.Controls;
using EbookToolSuite.Core.Enums;
using EbookToolSuite.Core.Interfaces;
using EbookToolSuite.Core.Models;

namespace EbookToolSuite.App.Views;

/// <summary>
/// 批量转换：给「批量选择」里勾中的那些书一次性选好目标格式，然后全部排进转换队列。
///
/// 这里只选目标格式与模板：每本书的源格式由队列自己按偏好顺序挑（与单本转换一致），
/// 所以同一个目标格式对「EPUB 与 MOBI 都能转」的书也成立。
/// 已经有该格式、或排不进去的书由调用方逐本跳过并汇报，本窗口不逐本报错。
/// </summary>
public partial class BatchConvertWindow : Window
{
    private readonly IConversionService _conversion;
    private readonly List<TargetOption> _targets;

    /// <summary>源格式的偏好顺序，与 ConversionQueue 挑源文件的顺序一致。</summary>
    private static readonly BookFormatKind[] SourcePreference =
    [
        BookFormatKind.Epub,
        BookFormatKind.Txt,
        BookFormatKind.Fb2,
        BookFormatKind.Mobi,
        BookFormatKind.Azw3,
        BookFormatKind.Pdf,
        BookFormatKind.Cbz,
    ];

    /// <summary>下拉框里的排列顺序：EPUB 优先，其次是常用输出。</summary>
    private static readonly BookFormatKind[] OutputOrder =
    [
        BookFormatKind.Epub,
        BookFormatKind.Mobi,
        BookFormatKind.Pdf,
        BookFormatKind.Txt,
        BookFormatKind.Fb2,
    ];

    public BatchConvertWindow(IConversionService conversion, IReadOnlyList<Book> books)
    {
        ArgumentNullException.ThrowIfNull(conversion);
        ArgumentNullException.ThrowIfNull(books);

        _conversion = conversion;

        InitializeComponent();

        _targets = BuildTargets(books);

        HeaderText.Text = "已勾选 " + books.Count + " 本，选一个目标格式";
        HintText.Text = "已经有这个格式的书会自动跳过，最后会告诉你跳过了几本。"
            + "任务会排进转换队列，进度与结果在「转换队列」页查看。";

        TargetCombo.ItemsSource = _targets;
        if (_targets.Count > 0)
        {
            TargetCombo.SelectedIndex = 0;
        }
        else
        {
            HintText.Text = "勾选的这些书都没有可用的源文件，或者它们已经有能转出的全部格式了。";
            StartButton.IsEnabled = false;
        }

        TemplateCombo.SelectionChanged += (_, _) => UpdateNote();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>选定的目标格式（窗口以 OK 关闭时才有意义）。</summary>
    public BookFormatKind SelectedTarget { get; private set; }

    /// <summary>选定的模板 id；null 表示不套模板。</summary>
    public string? SelectedTemplateId { get; private set; }

    private sealed record TargetOption(BookFormatKind Format, string Display)
    {
        public override string ToString() => Display;
    }

    private sealed record TemplateOption(string? Id, string Display)
    {
        public override string ToString() => Display;
    }
    /// <summary>冒烟用：下拉框里有几个可选目标格式。</summary>

    public int TargetCount => _targets.Count;
    /// <summary>冒烟用：开始按钮是否可用。</summary>

    public bool StartEnabled => StartButton.IsEnabled;
    // ---------- 下拉框 ----------

    /// <summary>这批书里至少有一本能转出的目标格式（去重后按固定顺序排好）。</summary>
    private List<TargetOption> BuildTargets(IReadOnlyList<Book> books)
    {
        var seen = new List<BookFormatKind>();

        foreach (var book in books)
        {
            var source = SourcePreference
                .Select(pref => book.Formats.FirstOrDefault(
                    f => f.Format == pref && File.Exists(f.FilePath)))
                .FirstOrDefault(f => f is not null);

            if (source is null)
            {
                continue;
            }

            foreach (var target in _conversion.GetSupportedTargets(source.Format))
            {
                if (!seen.Contains(target))
                {
                    seen.Add(target);
                }
            }
        }

        return seen
            .OrderBy(RankOf)
            .Select(t => new TargetOption(t, DescribeTarget(t)))
            .ToList();
    }

    private static int RankOf(BookFormatKind format)
    {
        var index = Array.IndexOf(OutputOrder, format);
        return index < 0 ? int.MaxValue : index;
    }

    private void OnTargetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TargetCombo.SelectedItem is not TargetOption target)
        {
            return;
        }

        var options = new List<TemplateOption>
        {
            new(null, "不套模板（只用默认参数）"),
        };

        foreach (var template in _conversion.GetTemplates(target.Format))
        {
            options.Add(new TemplateOption(
                template.Id, template.DisplayName + "（" + template.Priority + "）"));
        }

        TemplateCombo.ItemsSource = options;
        TemplateCombo.SelectedIndex = options.Count > 1 ? 1 : 0;
        UpdateNote();
    }

    private void UpdateNote()
    {
        if (TargetCombo.SelectedItem is not TargetOption target
            || TemplateCombo.SelectedItem is not TemplateOption option)
        {
            NoteText.Text = string.Empty;
            return;
        }

        if (option.Id is null)
        {
            NoteText.Text = "不套模板：只使用 Config 里的默认档位，不加任何额外参数。";
            return;
        }

        var template = _conversion.GetTemplates(target.Format)
            .FirstOrDefault(t => string.Equals(t.Id, option.Id, StringComparison.OrdinalIgnoreCase));

        NoteText.Text = template is null
            ? string.Empty
            : template.Note + Environment.NewLine + "参数："
                + string.Join(' ', template.Arguments.Select(a => a.ToString()));
    }
    // ---------- 交互 ----------

    private void OnStartClick(object sender, RoutedEventArgs e)
    {
        if (TargetCombo.SelectedItem is not TargetOption target)
        {
            return;
        }

        SelectedTarget = target.Format;
        SelectedTemplateId = (TemplateCombo.SelectedItem as TemplateOption)?.Id;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>Esc 关窗、Enter 加入队列。</summary>
    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        if (e.Key == System.Windows.Input.Key.Enter && StartButton.IsEnabled)
        {
            OnStartClick(this, new RoutedEventArgs());
            e.Handled = true;
        }
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
}
