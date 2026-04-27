using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Rawr.Core.Models;

namespace Rawr.App.Converters;

/// <summary>
/// Maps a ColorLabel enum to the brush used for the label dot in the filmstrip.
/// </summary>
public sealed class ColorLabelToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Red = Freeze(new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35)));
    private static readonly SolidColorBrush Yellow = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07)));
    private static readonly SolidColorBrush Green = Freeze(new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)));
    private static readonly SolidColorBrush Blue = Freeze(new SolidColorBrush(Color.FromRgb(0x21, 0x96, 0xF3)));
    private static readonly SolidColorBrush Purple = Freeze(new SolidColorBrush(Color.FromRgb(0x9C, 0x27, 0xB0)));

    private static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            ColorLabel.Red => Red,
            ColorLabel.Yellow => Yellow,
            ColorLabel.Green => Green,
            ColorLabel.Blue => Blue,
            ColorLabel.Purple => Purple,
            _ => (object)Brushes.Transparent,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
