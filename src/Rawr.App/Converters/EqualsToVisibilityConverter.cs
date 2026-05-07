using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Rawr.App.Converters;

/// <summary>
/// Returns <see cref="Visibility.Visible"/> when the bound value equals the
/// converter parameter, otherwise <see cref="Visibility.Collapsed"/>. Used to
/// swap sibling panels by binding their Visibility to the same enum-typed
/// state property with different parameters.
/// </summary>
public sealed class EqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Equals(value, parameter) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
