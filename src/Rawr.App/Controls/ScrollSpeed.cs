using System.Windows.Controls;
using System.Windows.Input;

namespace Rawr.App.Controls;

public static class ScrollSpeed
{
    public const int MinPercent = 25;
    public const int MaxPercent = 200;
    public const int DefaultPercent = 100;

    private const double GridWheelPixels = 36.0;
    private const double VerticalWheelPixels = 30.0;
    private const double HorizontalWheelPixels = 120.0;

    public static double GridWheelStep => GridWheelPixels * Multiplier;

    public static void ScrollVertical(ScrollViewer scrollViewer, MouseWheelEventArgs e) =>
        ScrollVertical(scrollViewer, e, VerticalWheelPixels);

    public static void ScrollHorizontal(ScrollViewer scrollViewer, MouseWheelEventArgs e) =>
        scrollViewer.ScrollToHorizontalOffset(scrollViewer.HorizontalOffset + WheelPixels(e, HorizontalWheelPixels));

    private static void ScrollVertical(ScrollViewer scrollViewer, MouseWheelEventArgs e, double basePixels) =>
        scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - WheelPixels(e, basePixels));

    private static double WheelPixels(MouseWheelEventArgs e, double basePixels) =>
        e.Delta / 120.0 * basePixels * Multiplier;

    private static double Multiplier =>
        Math.Clamp(AppSettings.Current.ScrollSpeedPercent, MinPercent, MaxPercent) / 100.0;
}
