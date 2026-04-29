using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Rawr.App.Dialogs;
using Rawr.App.ViewModels;
using Rawr.Core.Models;

namespace Rawr.App;

public partial class MainWindow : Window
{
    private const double MinZoom = 1.0;
    private const double MaxZoom = 64.0;
    private const double ZoomStep = 1.2;
    private const double DoubleClickZoom = 3.0;

    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RAWR");
    private static readonly string LayoutSettingsFile = Path.Combine(SettingsDir, "layout.json");

    private record LayoutSettings(int GridColumnCount = 2, double FilmstripRowHeight = 148.0);

    private bool _isPanning;
    private Point _panStart;
    private double _panStartTx;
    private double _panStartTy;
    private UniformGrid? _gridItemsPanel;

    public MainWindow()
    {
        InitializeComponent();
        WindowHelper.ApplyDarkTitleBar(this);

        if (DataContext is INotifyPropertyChanged inpc)
        {
            inpc.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.SelectedPhoto))
                    ResetPreviewZoom();
                if (e.PropertyName == nameof(MainViewModel.GridColumnCount))
                    RecalcGridThumbnailSize();
            };
        }

        Closing += (_, _) => SaveLayoutSettings();
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
        Loaded += async (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                var layout = await LoadLayoutSettingsAsync();
                vm.GridColumnCount = Math.Clamp(layout.GridColumnCount, 1, 8);
                RootGrid.RowDefinitions[3].Height = new GridLength(
                    Math.Clamp(layout.FilmstripRowHeight, 80, 400));
                await vm.RestoreLastFolderAsync();
            }
            RecalcGridThumbnailSize();
            RecalcFilmstripItemWidth();
        };
    }

    // ── Layout persistence ──

    private async Task<LayoutSettings> LoadLayoutSettingsAsync()
    {
        try
        {
            if (!File.Exists(LayoutSettingsFile)) return new LayoutSettings();
            var json = await File.ReadAllTextAsync(LayoutSettingsFile);
            return JsonSerializer.Deserialize<LayoutSettings>(json) ?? new LayoutSettings();
        }
        catch { return new LayoutSettings(); }
    }

    private void SaveLayoutSettings()
    {
        try
        {
            if (DataContext is not MainViewModel vm) return;
            Directory.CreateDirectory(SettingsDir);
            var height = RootGrid.RowDefinitions[3].ActualHeight;
            var settings = new LayoutSettings(vm.GridColumnCount, height > 0 ? height : 148.0);
            File.WriteAllText(LayoutSettingsFile, JsonSerializer.Serialize(settings));
        }
        catch { /* non-critical */ }
    }

    // ── Grid panel ──

    private void GridView_SizeChanged(object sender, SizeChangedEventArgs e) => RecalcGridThumbnailSize();

    // GridThumbnailSize drives the square height of each cell.
    // UniformGrid owns the column count and divides available width automatically,
    // so we only need to set Columns and provide a matching height.
    // Subtract 12 to reserve space for the slim scrollbar (10 px) plus rounding buffer,
    // and subtract 4 more for the 2 px item margin on each side (FilmstripItemStyle).
    private void RecalcGridThumbnailSize()
    {
        if (DataContext is not MainViewModel vm) return;

        _gridItemsPanel ??= FindDescendant<UniformGrid>(GridView);
        if (_gridItemsPanel != null)
            _gridItemsPanel.Columns = vm.GridColumnCount;

        var available = GridView.ActualWidth - 12;
        if (available <= 0) return;
        vm.GridThumbnailSize = Math.Max(20, Math.Floor(available / vm.GridColumnCount) - 4);
    }

    private void GridView_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        if (DataContext is not MainViewModel vm) return;

        // Scroll up = zoom in = fewer columns; scroll down = zoom out = more columns.
        vm.GridColumnCount = Math.Clamp(vm.GridColumnCount + (e.Delta > 0 ? -1 : 1), 1, 8);
        e.Handled = true;
        // RecalcGridThumbnailSize is called via the PropertyChanged → GridColumnCount handler.
    }

    private void GridView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox lb || lb.SelectedItem is null) return;
        lb.ScrollIntoView(lb.SelectedItem);
    }

    // ── Filmstrip: size tracks height so items shrink when strip is made smaller ──

    private void Filmstrip_SizeChanged(object sender, SizeChangedEventArgs e) => RecalcFilmstripItemWidth();

    private void RecalcFilmstripItemWidth()
    {
        if (DataContext is not MainViewModel vm) return;
        var available = Filmstrip.ActualHeight - SystemParameters.HorizontalScrollBarHeight;
        vm.FilmstripItemWidth = Math.Max(60, Math.Floor(available));
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

    // ── Burst focus: double-click a collapsed-burst tile to open the focused viewer ──

    private DateTime _lastClickTime;
    private PhotoItem? _lastClickedPhoto;
    private static readonly TimeSpan DoubleClickThreshold = TimeSpan.FromMilliseconds(400);

    private void GridItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        => HandleTileClick(sender, e);

    private void FilmstripItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        => HandleTileClick(sender, e);

    private void HandleTileClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not PhotoItem photo) return;
        if (photo.CollapsedBurstCount <= 0) return; // not a collapsed burst rep

        var now = DateTime.UtcNow;
        if (_lastClickedPhoto == photo && (now - _lastClickTime) <= DoubleClickThreshold)
        {
            _lastClickedPhoto = null;
            OpenBurstFocus(photo);
            e.Handled = true;
            return;
        }
        _lastClickedPhoto = photo;
        _lastClickTime = now;
    }

    private void OpenBurstFocus(PhotoItem representative)
    {
        if (DataContext is not MainViewModel vm) return;
        var members = vm.GetBurstMembers(representative.GroupId);
        if (members.Count == 0) return;
        var startIdx = Math.Max(0, members.IndexOf(representative));
        var win = new BurstFocusWindow(vm, members, startIdx) { Owner = this };
        win.ShowDialog();
    }

    // ── Context menu: select item on right-click so ToggleGroupForSelected works on the right photo ──

    private void PhotoList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox) return;
        var source = e.OriginalSource as DependencyObject;
        while (source != null && source is not ListBoxItem)
            source = VisualTreeHelper.GetParent(source);
        if (source is ListBoxItem item)
            item.IsSelected = true;
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

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) return t;
            var found = FindDescendant<T>(child);
            if (found != null) return found;
        }
        return null;
    }
}
