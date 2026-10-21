using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace EbookToolSuite.App.Converters;

/// <summary>
/// 布尔取反后转 Visibility：true 变成 Collapsed，false 变成 Visible。
/// </summary>
/// <remarks>
/// 书库页工具栏要在「普通模式按钮组」与「批量模式按钮组」之间切换，
/// 而 WPF 自带的 BooleanToVisibilityConverter 只会正向转，
/// 普通模式那一组得再取一次反。为这点小事引一整个 converter 库不值得。
/// </remarks>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Collapsed;
}