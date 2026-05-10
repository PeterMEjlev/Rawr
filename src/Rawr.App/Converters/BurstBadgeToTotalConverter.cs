using System.Globalization;
using System.Windows.Data;

namespace Rawr.App.Converters;

// "2/5" → "5". Empty / null in returns empty string out so we never paint
// a stray "0" for non-burst photos.
public sealed class BurstBadgeToTotalConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string s && !string.IsNullOrEmpty(s))
        {
            var idx = s.IndexOf('/');
            if (idx >= 0 && idx < s.Length - 1) return s[(idx + 1)..];
        }
        return string.Empty;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
