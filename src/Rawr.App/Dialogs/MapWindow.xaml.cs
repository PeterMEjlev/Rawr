using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using CommunityToolkit.Mvvm.Input;
using GMap.NET;
using GMap.NET.MapProviders;
using GMap.NET.WindowsPresentation;
using Rawr.App.ViewModels;
using Rawr.Core.Models;

namespace Rawr.App.Dialogs;

/// <summary>
/// Modeless map view: plots one cluster per pixel-grid cell so densely packed
/// photos collapse into a count badge that explodes when you zoom in. Drag a
/// rectangle (after toggling the rect-button) to filter the main grid to the
/// selected region. Single-marker click jumps the main grid to that photo.
/// </summary>
public partial class MapWindow : Window
{
    /// <summary>Side length, in screen pixels, of the cluster grid cell.
    /// Larger = chunkier clusters; smaller = more individual dots visible.</summary>
    private const int ClusterCellPx = 60;

    /// <summary>Process-lifetime guard so we only set the static GMap globals
    /// (User-Agent, Referer, cache wipe) once.</summary>
    private static bool s_gmapInitialised;

    private readonly MainViewModel _vm;
    private readonly List<PhotoItem> _geoPhotos;

    public IRelayCommand CloseCommand { get; }

    private bool _isDraggingRect;
    private Point _rectStart;

    public MapWindow(MainViewModel vm, IEnumerable<PhotoItem> photos)
    {
        _vm = vm;
        _geoPhotos = photos
            .Where(p => p.Metadata?.GpsLatitude.HasValue == true && p.Metadata?.GpsLongitude.HasValue == true)
            .ToList();

        CloseCommand = new RelayCommand(Close);

        InitializeComponent();
        DataContext = this;
        WindowHelper.ApplyDarkTitleBar(this);

        ConfigureMap();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void ConfigureMap()
    {
        EnsureGMapConfigured();

        // Use an on-disk SQLite cache so previously panned tiles render offline.
        // GMap.NET's default cache lives under %LocalAppData%\GMap.NET\.
        GMaps.Instance.Mode = AccessMode.ServerAndCache;
        Map.MapProvider = GMapProviders.OpenStreetMap;
        Map.CanDragMap = true;
        Map.DragButton = MouseButton.Left;
        Map.IgnoreMarkerOnMouseWheel = true;

        // Re-cluster whenever the visible projection changes — the cell grid is
        // measured in screen pixels, so pan and zoom both invalidate it.
        Map.OnMapZoomChanged += RebuildMarkers;
        Map.OnMapDrag        += RebuildMarkers;

        MapHost.MouseLeftButtonDown += OnMapHostMouseDown;
        MapHost.MouseMove           += OnMapHostMouseMove;
        MapHost.MouseLeftButtonUp   += OnMapHostMouseUp;
    }

    /// <summary>
    /// Sets the global GMap.NET User-Agent and Referer (OpenStreetMap blocks
    /// requests using GMap.NET's default UA and serves "Access blocked"
    /// placeholder tiles), and one-time wipes the on-disk tile cache so any
    /// blocked tiles cached before the UA was set get re-fetched fresh.
    /// </summary>
    private static void EnsureGMapConfigured()
    {
        if (s_gmapInitialised) return;
        s_gmapInitialised = true;

        GMapProvider.UserAgent = "RAWR-Photo-Culling/1.0 (+https://github.com/PeterMEjlev/Rawr)";
        GMapProviders.OpenStreetMap.RefererUrl = "https://github.com/PeterMEjlev/Rawr/";

        // First-run-with-this-fix marker: if missing, wipe GMap's cache so any
        // previously-cached "Access blocked" tiles get evicted. Idempotent —
        // subsequent launches skip the wipe and reuse the cache normally.
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var rawrDir = System.IO.Path.Combine(localAppData, "RAWR");
            Directory.CreateDirectory(rawrDir);
            var marker = System.IO.Path.Combine(rawrDir, ".map-cache-cleared-v1");
            if (!File.Exists(marker))
            {
                var gmapCache = System.IO.Path.Combine(localAppData, "GMap.NET");
                if (Directory.Exists(gmapCache))
                    Directory.Delete(gmapCache, recursive: true);
                File.WriteAllText(marker, DateTime.UtcNow.ToString("O"));
            }
        }
        catch { /* best-effort; if the cache is locked the user can delete it manually */ }
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        StatusText.Text = $"{_geoPhotos.Count} of {_vm.AllPhotos.Count} photos have GPS";

        if (_geoPhotos.Count == 0)
        {
            EmptyHint.Visibility = Visibility.Visible;
            // Centre over Greenwich so the user sees something rather than the
            // ocean off Africa (GMap's default 0,0).
            Map.Position = new PointLatLng(51.4779, -0.0015);
            return;
        }

        FitToPhotos();
        RebuildMarkers();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Map.OnMapZoomChanged -= RebuildMarkers;
        Map.OnMapDrag        -= RebuildMarkers;
        Map.Markers.Clear();
        Map.Manager.CancelTileCaching();
    }

    /// <summary>
    /// Sets the map centre to the centroid of all geotagged photos and zooms to
    /// fit the bounding box (with a small margin). Falls back to a sensible
    /// default zoom for single-point shoots where the bbox is degenerate.
    /// </summary>
    private void FitToPhotos()
    {
        if (_geoPhotos.Count == 0) return;

        double minLat = double.MaxValue, maxLat = double.MinValue;
        double minLon = double.MaxValue, maxLon = double.MinValue;
        foreach (var p in _geoPhotos)
        {
            var lat = p.Metadata!.GpsLatitude!.Value;
            var lon = p.Metadata!.GpsLongitude!.Value;
            if (lat < minLat) minLat = lat;
            if (lat > maxLat) maxLat = lat;
            if (lon < minLon) minLon = lon;
            if (lon > maxLon) maxLon = lon;
        }

        Map.Position = new PointLatLng((minLat + maxLat) / 2, (minLon + maxLon) / 2);

        // Single point or tiny range: pick a comfortable city-level zoom.
        double latSpan = maxLat - minLat;
        double lonSpan = maxLon - minLon;
        if (latSpan < 1e-4 && lonSpan < 1e-4)
        {
            Map.Zoom = 14;
            return;
        }

        var rect = RectLatLng.FromLTRB(minLon, maxLat, maxLon, minLat);
        // 1.2x margin so dots aren't right at the canvas edge. Inflate takes
        // (lng, lat) — i.e. (width, height) — so the lng inflation has to come
        // from lonSpan, not latSpan. Reversing them inflates the wrong axis and
        // produces a lopsided fit (way too much vertical padding on wide-aspect
        // shoots, none on tall ones).
        rect.Inflate(lonSpan * 0.1, latSpan * 0.1);
        Map.SetZoomToFitRect(rect);
    }

    /// <summary>
    /// Re-buckets all geotagged photos into the current pixel grid and rebuilds
    /// the marker layer. Cells with one photo get a single-dot marker; cells
    /// with more get a count badge that zooms in on click.
    /// </summary>
    private void RebuildMarkers()
    {
        Map.Markers.Clear();
        if (_geoPhotos.Count == 0 || Map.ActualWidth <= 0) return;

        var cells = new Dictionary<(int x, int y), List<PhotoItem>>();
        foreach (var p in _geoPhotos)
        {
            var local = Map.FromLatLngToLocal(new PointLatLng(p.Metadata!.GpsLatitude!.Value, p.Metadata!.GpsLongitude!.Value));
            int cx = (int)Math.Floor((double)local.X / ClusterCellPx);
            int cy = (int)Math.Floor((double)local.Y / ClusterCellPx);
            var key = (cx, cy);
            if (!cells.TryGetValue(key, out var list)) cells[key] = list = new List<PhotoItem>();
            list.Add(p);
        }

        foreach (var bucket in cells.Values)
        {
            double avgLat = bucket.Average(p => p.Metadata!.GpsLatitude!.Value);
            double avgLon = bucket.Average(p => p.Metadata!.GpsLongitude!.Value);
            var marker = new GMapMarker(new PointLatLng(avgLat, avgLon))
            {
                Shape = bucket.Count == 1 ? BuildSingleShape(bucket[0]) : BuildClusterShape(bucket),
                Tag = bucket,
                Offset = new Point(-12, -12),
                ZIndex = bucket.Count == 1 ? 10 : 20,
            };
            Map.Markers.Add(marker);
        }
    }

    private FrameworkElement BuildSingleShape(PhotoItem photo)
    {
        var dot = new Ellipse
        {
            Width = 14, Height = 14,
            Fill = (Brush)FindResource("AccentBrush"),
            Stroke = Brushes.White,
            StrokeThickness = 1.5,
            Cursor = Cursors.Hand,
            ToolTip = photo.FileName,
            Tag = photo,
        };
        dot.MouseLeftButtonUp += OnSingleMarkerClick;
        return dot;
    }

    private FrameworkElement BuildClusterShape(List<PhotoItem> photos)
    {
        // Size scales with count so a 200-photo cluster reads bigger than a 3-photo one
        // without dwarfing the map.
        double size = Math.Min(48, 22 + Math.Log10(photos.Count) * 12);
        var grid = new Grid
        {
            Width = size, Height = size,
            Cursor = Cursors.Hand,
            Tag = photos,
        };
        grid.Children.Add(new Ellipse
        {
            Fill = (Brush)FindResource("AccentBrush"),
            Stroke = Brushes.White,
            StrokeThickness = 2,
            Opacity = 0.92,
        });
        grid.Children.Add(new TextBlock
        {
            Text = photos.Count.ToString(),
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            FontSize = photos.Count >= 1000 ? 10 : 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        });
        grid.MouseLeftButtonUp += OnClusterMarkerClick;
        // Centre the cluster shape on its lat/lon position.
        Canvas.SetLeft(grid, -size / 2 + 12); // counteract marker.Offset
        Canvas.SetTop(grid, -size / 2 + 12);
        return grid;
    }

    private void OnSingleMarkerClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is PhotoItem photo)
        {
            _vm.SelectedPhoto = photo;
            e.Handled = true;
        }
    }

    private void OnClusterMarkerClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is List<PhotoItem> photos && photos.Count > 0)
        {
            // Recentre on the cluster and step one zoom level closer; the cluster
            // will split on the next RebuildMarkers triggered by OnMapZoomChanged.
            double avgLat = photos.Average(p => p.Metadata!.GpsLatitude!.Value);
            double avgLon = photos.Average(p => p.Metadata!.GpsLongitude!.Value);
            Map.Position = new PointLatLng(avgLat, avgLon);
            Map.Zoom = Math.Min(Map.MaxZoom, Map.Zoom + 1);
            e.Handled = true;
        }
    }

    // ── Rectangle selection ──

    private void OnMapHostMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DrawRectToggle.IsChecked != true) return;
        // Suppress GMap's pan for this drag — we're using the left button to
        // draw a selection rect instead.
        Map.CanDragMap = false;
        _isDraggingRect = true;
        _rectStart = e.GetPosition(MapHost);
        Canvas.SetLeft(SelectionRect, _rectStart.X);
        Canvas.SetTop(SelectionRect, _rectStart.Y);
        SelectionRect.Width = 0;
        SelectionRect.Height = 0;
        SelectionRect.Visibility = Visibility.Visible;
        MapHost.CaptureMouse();
        e.Handled = true;
    }

    private void OnMapHostMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingRect) return;
        var current = e.GetPosition(MapHost);
        var x = Math.Min(_rectStart.X, current.X);
        var y = Math.Min(_rectStart.Y, current.Y);
        var w = Math.Abs(current.X - _rectStart.X);
        var h = Math.Abs(current.Y - _rectStart.Y);
        Canvas.SetLeft(SelectionRect, x);
        Canvas.SetTop(SelectionRect, y);
        SelectionRect.Width = w;
        SelectionRect.Height = h;
    }

    private void OnMapHostMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingRect) return;
        _isDraggingRect = false;
        MapHost.ReleaseMouseCapture();
        Map.CanDragMap = true;

        var end = e.GetPosition(MapHost);
        SelectionRect.Visibility = Visibility.Collapsed;
        DrawRectToggle.IsChecked = false;

        // Discard noise drags: a stray click shouldn't filter to a 2-pixel box.
        if (Math.Abs(end.X - _rectStart.X) < 6 || Math.Abs(end.Y - _rectStart.Y) < 6) return;

        var corner1 = Map.FromLocalToLatLng((int)_rectStart.X, (int)_rectStart.Y);
        var corner2 = Map.FromLocalToLatLng((int)end.X, (int)end.Y);
        _vm.SetRegionFilter(corner1.Lat, corner1.Lng, corner2.Lat, corner2.Lng);
        Close();
    }

    private void OnClearRegionFilter(object sender, RoutedEventArgs e)
    {
        if (_vm.ClearRegionFilterCommand.CanExecute(null))
            _vm.ClearRegionFilterCommand.Execute(null);
    }

    private void OnFitToPhotos(object sender, RoutedEventArgs e)
    {
        FitToPhotos();
        RebuildMarkers();
    }
}
