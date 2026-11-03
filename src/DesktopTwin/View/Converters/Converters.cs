using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DesktopTwin.View.Converters;

/// <summary>
/// 把 <see cref="bool"/> 反向转换为 <see cref="Visibility"/>：true 折叠，false 显示。
/// </summary>
public sealed class BooleanToVisibilityRevertConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is Visibility.Collapsed;
    }
}

/// <summary>
/// 把 <see cref="bool"/> 取反。
/// </summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is not true;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is not true;
    }
}
