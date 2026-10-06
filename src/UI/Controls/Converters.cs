using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace GrepExplorer.UI.Controls;

/// <summary>bool → Visibility（true = Visible）。XAML 中以 StaticResource BoolToVis 引用。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

/// <summary>数量为 0 → Visible（用于"列表为空"的提示文案）。XAML 中以 StaticResource ZeroToVis 引用。</summary>
public sealed class ZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int n && n > 0 ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>bool → 不透明度（true = 0.45）：用于"当前输出模式下不生效"的块/控件。</summary>
public sealed class InactiveToOpacityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? 0.45 : 1.0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
