using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Rawr.App.Converters;

/// <summary>
/// Truthy → Visible, falsy → Collapsed. "Falsy" covers null, false, 0, and empty string.
/// Set Invert=true to swap the result.
/// </summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value switch
        {
            null => false,
            bool b => b,
            int i => i > 0,
            string s => !string.IsNullOrEmpty(s),
            _ => true,
        };
        if (Invert) visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
