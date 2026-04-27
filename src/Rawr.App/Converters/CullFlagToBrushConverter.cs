using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Rawr.Core.Models;

namespace Rawr.App.Converters;

/// <summary>
/// Maps a CullFlag value to the brush used for the flag indicator strip
/// (green for Pick, red for Reject, transparent otherwise).
/// </summary>
public sealed class CullFlagToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not CullFlag flag)
            return Brushes.Transparent;

        return flag switch
        {
            CullFlag.Pick => (Brush)Application.Current.Resources["PickBrush"],
            CullFlag.Reject => (Brush)Application.Current.Resources["RejectBrush"],
            _ => Brushes.Transparent,
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
