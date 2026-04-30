using System.Windows;
using System.Windows.Media;
using Rawr.Core.Models;

namespace Rawr.App.Controls;

public enum HistogramMode { Rgb, Combined, R, G, B }

public sealed class HistogramControl : FrameworkElement
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(HistogramData), typeof(HistogramControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
        nameof(Mode), typeof(HistogramMode), typeof(HistogramControl),
        new FrameworkPropertyMetadata(HistogramMode.Rgb, FrameworkPropertyMetadataOptions.AffectsRender));

    public HistogramData? Data
    {
        get => (HistogramData?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public HistogramMode Mode
    {
        get => (HistogramMode)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        double h = ActualHeight;

        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x18)), null, new Rect(0, 0, w, h));

        var data = Data;
        if (data == null || w <= 0 || h <= 0) return;

        switch (Mode)
        {
            case HistogramMode.R:
                DrawChannel(dc, data.R, Color.FromArgb(230, 255, 80, 80), w, h);
                break;
            case HistogramMode.G:
                DrawChannel(dc, data.G, Color.FromArgb(230, 80, 220, 80), w, h);
                break;
            case HistogramMode.B:
                DrawChannel(dc, data.B, Color.FromArgb(230, 80, 130, 255), w, h);
                break;
            case HistogramMode.Combined:
                DrawChannel(dc, data.Combined, Color.FromArgb(220, 220, 220, 220), w, h);
                break;
            default: // Rgb
                DrawChannel(dc, data.B, Color.FromArgb(200, 80, 130, 255), w, h);
                DrawChannel(dc, data.R, Color.FromArgb(200, 255, 80, 80), w, h);
                DrawChannel(dc, data.G, Color.FromArgb(200, 80, 220, 80), w, h);
                break;
        }
    }

    private static void DrawChannel(DrawingContext dc, int[] bins, Color color, double w, double h)
    {
        // Normalize against the inner range (bins 1–254) to prevent black/white spikes
        // from compressing the mid-tone detail.
        int max = 0;
        for (int i = 1; i < 255; i++)
            if (bins[i] > max) max = bins[i];
        if (max == 0)
            for (int i = 0; i < 256; i++)
                if (bins[i] > max) max = bins[i];
        if (max == 0) return;

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            double x0 = 0;
            double y0 = h - Math.Min(bins[0] / (double)max, 1.0) * h;
            ctx.BeginFigure(new Point(x0, y0), isFilled: false, isClosed: false);
            for (int i = 1; i < 256; i++)
            {
                double x = i * w / 255.0;
                double y = h - Math.Min(bins[i] / (double)max, 1.0) * h;
                ctx.LineTo(new Point(x, y), isStroked: true, isSmoothJoin: true);
            }
        }
        geo.Freeze();

        var pen = new Pen(new SolidColorBrush(color), 1.0) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }
}
