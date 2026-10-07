using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Omega.Converters;

/// <summary>
/// bool → Visibility for x:Bind state surfaces (loading / empty /
/// error blocks). Registered once in App.xaml resources.
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is Visibility.Visible;
}

/// <summary>Inverse of <see cref="BoolToVisibilityConverter"/> (true → Collapsed).</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is Visibility.Collapsed;
}
