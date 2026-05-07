using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Rawr.App.Controls;

/// <summary>
/// Inline pixel-peep view: a clipped square that shows a 1:1 (or zoomed) crop
/// of a source bitmap centred on a given image-pixel anchor. Sized by its
/// host (square layout slot is recommended); the controller fills it via
/// <see cref="SetSource"/> + <see cref="UpdateView"/>.
/// </summary>
public partial class PixelPeekView : UserControl
{
    // Last anchor + zoom seen, replayed if the layout slot resizes after the
    // controller pushed an update — otherwise the crop would drift on resize.
    private double _lastPxX;
    private double _lastPxY;
    private double _lastZoom = 1.0;
    private bool _hasUpdate;
    private bool _useLinear;

    public PixelPeekView()
    {
        InitializeComponent();
    }

    public void SetSource(BitmapSource? source)
    {
        PeekImage.Source = source;
        if (HintText != null)
            HintText.Visibility = source != null && _hasUpdate ? Visibility.Collapsed : Visibility.Visible;
    }

    public void UpdateView(double imagePixelX, double imagePixelY, double zoom, bool useLinear = false)
    {
        _lastPxX = imagePixelX;
        _lastPxY = imagePixelY;
        _lastZoom = zoom;
        _useLinear = useLinear;
        _hasUpdate = true;
        if (HintText != null && PeekImage.Source != null)
            HintText.Visibility = Visibility.Collapsed;
        Apply();
    }

    public void ClearAnchor()
    {
        _hasUpdate = false;
        if (HintText != null) HintText.Visibility = Visibility.Visible;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_hasUpdate) Apply();
    }

    private void Apply()
    {
        double cx = ActualWidth / 2.0;
        double cy = ActualHeight / 2.0;
        if (cx <= 0 || cy <= 0) return;

        // With Stretch=None, the bitmap renders at its natural DIP size
        // (PixelWidth × 96/DpiX). Bake DpiX/96 into the scale so 1 source
        // pixel maps to 1 DIP at zoom 1, regardless of the JPEG's DPI tag.
        double dpiAdjustX = 1.0, dpiAdjustY = 1.0;
        if (PeekImage.Source is BitmapSource src && src.Width > 0 && src.Height > 0)
        {
            dpiAdjustX = src.PixelWidth  / src.Width;
            dpiAdjustY = src.PixelHeight / src.Height;
        }

        RenderOptions.SetBitmapScalingMode(PeekImage,
            _useLinear ? BitmapScalingMode.Linear : BitmapScalingMode.NearestNeighbor);

        PeekScale.ScaleX = _lastZoom * dpiAdjustX;
        PeekScale.ScaleY = _lastZoom * dpiAdjustY;
        PeekTranslate.X = cx - _lastPxX * _lastZoom;
        PeekTranslate.Y = cy - _lastPxY * _lastZoom;
        ZoomLabel.Text = _lastZoom >= 1.0
            ? $"{_lastZoom:0.##}:1"
            : $"1:{(1.0 / _lastZoom):0.##}";
    }
}
