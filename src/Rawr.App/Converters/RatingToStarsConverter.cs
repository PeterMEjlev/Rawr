using System.Globalization;
using System.Windows.Data;

namespace Rawr.App.Converters;

/// <summary>
/// Renders an int rating (0-5) as a string of filled stars, e.g. 3 → "★★★".
/// Empty when rating is 0.
/// </summary>
public sealed class RatingToStarsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var rating = value is int r ? r : 0;
        return rating <= 0 ? string.Empty : new string('★', Math.Clamp(rating, 0, 5));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
