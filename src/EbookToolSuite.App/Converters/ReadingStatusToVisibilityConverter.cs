using System.Globalization;
using System.Windows;
using System.Windows.Data;
using EbookToolSuite.Core.Enums;

namespace EbookToolSuite.App.Converters;

/// <summary>
/// 当前阅读状态和 ConverterParameter（Unread / Reading / Finished / Abandoned）一致时返回 Visible。
/// </summary>
/// <remarks>
/// 用在卡片右键菜单「标记为」的四个子项上，给当前那一项点一个对勾。
/// 故意**不用** MenuItem.IsCheckable：那样点击会把 IsChecked 自己翻面，
/// 而「点当前状态」这条路径被 MarkStatusAsync 早退（不落库也不发通知），
/// 对勾就会停在错误的一侧。这里只做只读显示，交互一律走 Click。
/// </remarks>
public sealed class ReadingStatusToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not ReadingStatus current || parameter is not string name)
        {
            return Visibility.Collapsed;
        }

        return Enum.TryParse<ReadingStatus>(name, ignoreCase: true, out var wanted) && current == wanted
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}