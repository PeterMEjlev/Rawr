using System.Collections;
using System.Globalization;
using System.Windows.Data;

namespace Rawr.App.Converters;

// MultiBinding converter that returns true when values[0] (an IEnumerable)
// contains values[1] (the candidate). Used by the Filter popup to highlight
// individual buttons against a HashSet of currently-selected values.
public sealed class SetContainsConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[1] is null) return false;
        if (values[0] is not IEnumerable col) return false;
        foreach (var item in col)
            if (Equals(item, values[1])) return true;
        return false;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
