using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Rawr.App.ViewModels;

namespace Rawr.App;

public partial class MainWindow : Window
{
    private const double MinZoom = 1.0;
    private const double MaxZoom = 8.0;
    private const double ZoomStep = 1.2;
    private const double DoubleClickZoom = 3.0;

    private bool _isPanning;
    private Point _panStart;
    private double _panStartTx;
    private double _panStartTy;

    public MainWindow()
    {
        InitializeComponent();

        // Reset zoom whenever the user moves to a new photo so each shot
        // opens fitted to the preview area.
        if (DataContext is INotifyPropertyChanged inpc)
        {
            inpc.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.SelectedPhoto))
                    ResetPreviewZoom();
            };
        }

        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
        Loaded += async (_, _) =>
        {
            if (DataContext is MainViewModel vm)
                await vm.RestoreLastFolderAsync();
        };
    }

    // ── Filmstrip: wheel scrolls horizontally ──

    private void Filmstrip_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ListBox lb) return;
        var sv = FindScrollViewer(lb);
        if (sv == null) return;

        sv.ScrollToHorizontalOffset(sv.HorizontalOffset + e.Delta);
        e.Handled = true;
    }

    private void Filmstrip_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox lb || lb.SelectedItem is null) return;
        lb.ScrollIntoView(lb.SelectedItem);
    }

    // ── Preview: wheel zooms around the cursor; left-drag pans when zoomed ──

    private void PreviewHost_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not FrameworkElement host) return;

        var oldScale = PreviewScale.ScaleX;
        var step = e.Delta > 0 ? ZoomStep : 1.0 / ZoomStep;
        var newScale = Math.Clamp(oldScale * step, MinZoom, MaxZoom);

        if (Math.Abs(newScale - oldScale) < 1e-6)
        {
            e.Handled = true;
            return;
        }

        if (newScale <= MinZoom + 1e-3)
        {
            // Snap back to fit-to-screen so the image stays centred.
            newScale = MinZoom;
            PreviewTranslate.X = 0;
            PreviewTranslate.Y = 0;
        }
        else
        {
            // Cursor-anchored zoom: keep the point under the cursor stable.
            // Cursor must be measured in the Image element's coord space (matching
            // RenderTransformOrigin="0,0"), not the Border's, otherwise the Margin
            // offset compounds across zoom steps and the image drifts away.
            var pt = e.GetPosition(PreviewImageElement);
            var ratio = newScale / oldScale;
            PreviewTranslate.X = pt.X * (1 - ratio) + PreviewTranslate.X * ratio;
            PreviewTranslate.Y = pt.Y * (1 - ratio) + PreviewTranslate.Y * ratio;
        }

        PreviewScale.ScaleX = PreviewScale.ScaleY = newScale;
        UpdateZoomIndicator(newScale);

        // First time the user zooms in, upgrade to full-resolution source.
        if (newScale > MinZoom + 1e-3 && DataContext is MainViewModel vm)
            _ = vm.LoadHighResPreviewAsync();

        e.Handled = true;
    }

    private void PreviewHost_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement host) return;

        if (e.ClickCount == 2)
        {
            if (PreviewScale.ScaleX > MinZoom + 1e-3)
            {
                ResetPreviewZoom();
            }
            else
            {
                var pt = e.GetPosition(PreviewImageElement);
                var ratio = DoubleClickZoom / PreviewScale.ScaleX;
                PreviewTranslate.X = pt.X * (1 - ratio) + PreviewTranslate.X * ratio;
                PreviewTranslate.Y = pt.Y * (1 - ratio) + PreviewTranslate.Y * ratio;
                PreviewScale.ScaleX = PreviewScale.ScaleY = DoubleClickZoom;
                UpdateZoomIndicator(DoubleClickZoom);
                if (DataContext is MainViewModel vm)
                    _ = vm.LoadHighResPreviewAsync();
            }
            e.Handled = true;
            return;
        }

        if (PreviewScale.ScaleX <= MinZoom + 1e-3) return; // nothing to pan

        _isPanning = true;
        _panStart = e.GetPosition(host);
        _panStartTx = PreviewTranslate.X;
        _panStartTy = PreviewTranslate.Y;
        host.CaptureMouse();
        host.Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private void PreviewHost_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPanning || sender is not FrameworkElement host) return;
        var pos = e.GetPosition(host);
        PreviewTranslate.X = _panStartTx + (pos.X - _panStart.X);
        PreviewTranslate.Y = _panStartTy + (pos.Y - _panStart.Y);
    }

    private void PreviewHost_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isPanning || sender is not FrameworkElement host) return;
        _isPanning = false;
        host.ReleaseMouseCapture();
        host.Cursor = Cursors.Arrow;
        e.Handled = true;
    }

    private void ResetPreviewZoom()
    {
        PreviewScale.ScaleX = PreviewScale.ScaleY = 1.0;
        PreviewTranslate.X = 0;
        PreviewTranslate.Y = 0;
        ZoomIndicator.Visibility = Visibility.Collapsed;
    }

    private void UpdateZoomIndicator(double scale)
    {
        if (scale <= MinZoom + 1e-3)
        {
            ZoomIndicator.Visibility = Visibility.Collapsed;
        }
        else
        {
            ZoomIndicatorText.Text = $"{scale:0.##}×";
            ZoomIndicator.Visibility = Visibility.Visible;
        }
    }

    // ── Helpers ──

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found != null) return found;
        }
        return null;
    }
}
