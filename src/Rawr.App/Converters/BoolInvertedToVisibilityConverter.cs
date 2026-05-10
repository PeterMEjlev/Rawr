using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Rawr.App.Converters;

public sealed class BoolInvertedToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isFalse = value is bool b && !b;
        return isFalse ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
