using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace Rawr.App.Controls;

/// <summary>
/// Drives a <see cref="PixelPeekView"/> embedded in some panel. Listens to the
/// preview's <see cref="Image.Source"/> for changes (so navigating between
/// photos updates the loupe automatically), maps clicks on the preview to an
/// image-pixel anchor (corrected for <c>Stretch=Uniform</c> letterboxing),
/// and exposes a wheel hook for in-place zoom.
///
/// Used by both the main window's right-hand panel and the burst-focus
/// viewer's metadata panel — same gestures, same view component.
/// </summary>
public sealed class PixelPeekController : IDisposable
{
    private readonly Border _previewHost;
    private readonly Image _previewImage;
    private readonly Func<Task>? _loadHighResAsync;
    private readonly DependencyPropertyDescriptor? _sourceDescriptor;

    private PixelPeekView? _view;
    private double _peekZoom = 1.0;
    private double _anchorNormX;
    private double _anchorNormY;
    private bool _hasAnchor;

    // Highest source resolution seen since the last anchor reset. Keeps the
    // visual zoom constant when the preview briefly drops to a low-res JPEG
    // during navigation: the on-screen frame stays the same instead of
    // flashing a less-zoomed view between burst frames.
    private double _referencePixelWidth;

    public PixelPeekController(Border previewHost, Image previewImage, Func<Task>? loadHighResAsync = null)
    {
        _previewHost = previewHost;
        _previewImage = previewImage;
        _loadHighResAsync = loadHighResAsync;

        // Auto-refresh whenever the preview's bitmap changes (new selection,
        // high-res swap-in, exposure render, …).
        _sourceDescriptor = DependencyPropertyDescriptor.FromProperty(Image.SourceProperty, typeof(Image));
        _sourceDescriptor?.AddValueChanged(_previewImage, OnPreviewSourceChanged);
    }

    /// <summary>Snapshot of the current peek state, used to carry the anchor
    /// across window boundaries (e.g. main window → burst-focus viewer) so
    /// the user keeps inspecting the same composition pixel.</summary>
    public readonly record struct State(double AnchorNormX, double AnchorNormY, bool HasAnchor, double Zoom);

    public State CaptureState() => new(_anchorNormX, _anchorNormY, _hasAnchor, _peekZoom);

    /// <summary>Restore a prior anchor + zoom captured via <see cref="CaptureState"/>.</summary>
    public void RestoreState(State state)
    {
        _anchorNormX = state.AnchorNormX;
        _anchorNormY = state.AnchorNormY;
        _hasAnchor = state.HasAnchor;
        _peekZoom = state.Zoom > 0 ? state.Zoom : 1.0;

        if (_view != null && _hasAnchor && _previewImage.Source is BitmapSource src)
        {
            TrackReference(src);
            _view.UpdateView(_anchorNormX * src.PixelWidth, _anchorNormY * src.PixelHeight, EffectiveZoom(src));
        }
    }

    /// <summary>Bind the controller to a panel-embedded view. Pass null to
    /// detach. Subscribes to the view's MouseWheel so the user can zoom the
    /// loupe by scrolling over the panel itself.</summary>
    public void AttachView(PixelPeekView? view)
    {
        if (_view != null)
            _view.MouseWheel -= OnViewMouseWheel;

        _view = view;
        if (_view != null)
        {
            _view.MouseWheel += OnViewMouseWheel;
            _view.SetSource(_previewImage.Source as BitmapSource);
            if (_hasAnchor && _previewImage.Source is BitmapSource src)
            {
                TrackReference(src);
                _view.UpdateView(_anchorNormX * src.PixelWidth, _anchorNormY * src.PixelHeight, EffectiveZoom(src));
            }
            // Kick a high-res decode so the embedded view shows real sensor
            // pixels even if the user just attached after a fresh selection.
            if (_loadHighResAsync != null) _ = _loadHighResAsync();
        }
    }

    private void OnViewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (ApplyWheelZoom(e.Delta)) e.Handled = true;
    }

    /// <summary>Set the inspection anchor from a click on the preview surface.</summary>
    public void SetAnchorFromCursor(Point cursorOverPreviewHost)
    {
        if (_previewImage.Source is not BitmapSource src) return;

        double elemW = _previewImage.ActualWidth;
        double elemH = _previewImage.ActualHeight;
        double imgW = src.PixelWidth;
        double imgH = src.PixelHeight;
        if (elemW <= 0 || elemH <= 0 || imgW <= 0 || imgH <= 0) return;

        double fit = Math.Min(elemW / imgW, elemH / imgH);
        double drawnW = imgW * fit;
        double drawnH = imgH * fit;
        if (drawnW <= 0 || drawnH <= 0) return;
        double offsetX = (elemW - drawnW) / 2.0;
        double offsetY = (elemH - drawnH) / 2.0;

        var fromHost = _previewHost.TranslatePoint(cursorOverPreviewHost, _previewImage);
        double localX = fromHost.X - offsetX;
        double localY = fromHost.Y - offsetY;

        double pxX = Math.Clamp(localX / fit, 0, imgW - 1);
        double pxY = Math.Clamp(localY / fit, 0, imgH - 1);
        _anchorNormX = pxX / imgW;
        _anchorNormY = pxY / imgH;
        _hasAnchor = true;

        TrackReference(src);
        _view?.UpdateView(pxX, pxY, EffectiveZoom(src));

        // Pull the full-res frame in so the loupe is at sensor pixels rather
        // than an upsampled preview.
        if (_loadHighResAsync != null) _ = _loadHighResAsync();
    }

    private bool ApplyWheelZoom(int delta)
    {
        if (_view == null || !_view.IsVisible || !_hasAnchor) return false;
        var step = delta > 0 ? 1.2 : 1.0 / 1.2;
        _peekZoom = Math.Clamp(_peekZoom * step, 0.5, 16.0);
        if (_previewImage.Source is BitmapSource src)
        {
            _view.UpdateView(_anchorNormX * src.PixelWidth, _anchorNormY * src.PixelHeight, EffectiveZoom(src));
        }
        return true;
    }

    public void Dispose()
    {
        if (_view != null)
            _view.MouseWheel -= OnViewMouseWheel;
        _sourceDescriptor?.RemoveValueChanged(_previewImage, OnPreviewSourceChanged);
    }

    private void OnPreviewSourceChanged(object? sender, EventArgs e)
    {
        var src = _previewImage.Source as BitmapSource;
        TrackReference(src);
        _view?.SetSource(src);
        if (src != null && _hasAnchor)
        {
            _view?.UpdateView(_anchorNormX * src.PixelWidth, _anchorNormY * src.PixelHeight, EffectiveZoom(src));
        }
    }

    private double EffectiveZoom(BitmapSource src)
    {
        if (_referencePixelWidth <= 0 || src.PixelWidth <= 0) return _peekZoom;
        return _peekZoom * (_referencePixelWidth / src.PixelWidth);
    }

    private void TrackReference(BitmapSource? src)
    {
        if (src == null) return;
        if (src.PixelWidth > _referencePixelWidth) _referencePixelWidth = src.PixelWidth;
    }
}
