using System.Windows;
using System.Windows.Media;

namespace Rawr.App.Services;

/// <summary>
/// Applies user-customisable colours onto the live application resource brushes
/// so the change propagates everywhere the brush is referenced (thumbnails,
/// preview, fullscreen preview) without a restart.
///
/// The brushes are declared in DarkTheme.xaml and referenced via
/// <c>DynamicResource</c>; we mutate the existing brush's <see cref="SolidColorBrush.Color"/>
/// in place (so both Static- and DynamicResource references update), falling back
/// to replacing the resource if the brush is somehow frozen.
/// </summary>
public static class ThemeColors
{
    // Neutral dark grey matching the other thumbnail badges (the collapsed-burst
    // count badge uses the same value). Kept here so AppSettings' default and the
    // theme's initial brush colour can't drift apart.
    public const string DefaultBurstLabelColor = "#CC1A1A1A";

    public const string BurstLabelBrushKey = "BurstLabelBrush";

    /// <summary>Parse an ARGB/RGB hex string ("#CC1A1A1A" / "#1A1A1A"); false on garbage.</summary>
    public static bool TryParseColor(string? hex, out Color color)
    {
        color = Colors.Transparent;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        try
        {
            if (ColorConverter.ConvertFromString(hex) is Color c)
            {
                color = c;
                return true;
            }
        }
        catch { /* invalid string → false */ }
        return false;
    }

    /// <summary>
    /// Push the burst-label colour from settings onto the live brush. Safe to call
    /// before any window exists (no-ops if the app / resource isn't ready).
    /// </summary>
    public static void ApplyBurstLabelColor(string? hex)
    {
        var app = Application.Current;
        if (app == null) return;
        if (!TryParseColor(hex, out var color) &&
            !TryParseColor(DefaultBurstLabelColor, out color))
            return;

        if (app.Resources[BurstLabelBrushKey] is SolidColorBrush brush && !brush.IsFrozen)
            brush.Color = color;
        else
            app.Resources[BurstLabelBrushKey] = new SolidColorBrush(color);
    }
}
