using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using Rawr.App.ViewModels;
using Rawr.Core.Models;

namespace Rawr.App.Dialogs;

/// <summary>
/// Modal viewer for a single burst. Shows a large preview of the active frame
/// and a horizontal strip of every member of the burst. Edits made here mutate
/// the shared PhotoItem instances and persist via the parent MainViewModel,
/// so changes are reflected immediately in the main grid on close.
/// </summary>
public partial class BurstFocusWindow : Window, INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private PhotoItem? _currentPhoto;
    public PhotoItem? CurrentPhoto
    {
        get => _currentPhoto;
        private set { _currentPhoto = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentPhoto))); }
    }

    private const double MinZoom = 1.0;
    private const double MaxZoom = 64.0;
    private const double ZoomStep = 1.2;
    private const double DoubleClickZoom = 3.0;

    private readonly MainViewModel _vm;
    private readonly List<PhotoItem> _photos;
    private int _currentIndex = -1;
    private CancellationTokenSource? _previewCts;
    private bool _highResLoaded;
    private bool _isPanning;
    private Point _panStart;
    private double _panStartTx;
    private double _panStartTy;

    public IRelayCommand CloseCommand        { get; }
    public IRelayCommand NextCommand         { get; }
    public IRelayCommand PrevCommand         { get; }
    public IRelayCommand TogglePickCommand   { get; }
    public IRelayCommand ToggleRejectCommand { get; }
    public IRelayCommand UnflagCommand       { get; }
    public IRelayCommand<int> SetRatingCommand { get; }
    public IRelayCommand<ColorLabel> SetColorLabelCommand { get; }

    public BurstFocusWindow(MainViewModel vm, List<PhotoItem> photos, int startIndex)
    {
        _vm = vm;
        _photos = photos;

        CloseCommand        = new RelayCommand(Close);
        NextCommand         = new RelayCommand(() => MoveTo(_currentIndex + 1));
        PrevCommand         = new RelayCommand(() => MoveTo(_currentIndex - 1));
        TogglePickCommand   = new RelayCommand(() => MutateCurrent(p => p.Flag = p.Flag == CullFlag.Pick   ? CullFlag.Unflagged : CullFlag.Pick));
        ToggleRejectCommand = new RelayCommand(() => MutateCurrent(p => p.Flag = p.Flag == CullFlag.Reject ? CullFlag.Unflagged : CullFlag.Reject));
        UnflagCommand       = new RelayCommand(() => MutateCurrent(p => p.Flag = CullFlag.Unflagged));
        SetRatingCommand    = new RelayCommand<int>(r => MutateCurrent(p => p.Rating = Math.Clamp(r, 0, 5)));
        SetColorLabelCommand = new RelayCommand<ColorLabel>(l => MutateCurrent(p => p.ColorLabel = p.ColorLabel == l ? ColorLabel.None : l));

        InitializeComponent();
        DataContext = this;
        WindowHelper.ApplyDarkTitleBar(this);

        Strip.ItemsSource = _photos;
        HeaderText.Text = $"Burst — {_photos.Count} photos";

        Loaded += (_, _) => MoveTo(Math.Clamp(startIndex, 0, _photos.Count - 1));
        Closed += (_, _) => _previewCts?.Cancel();
    }

    private void MoveTo(int index)
    {
        if (index < 0 || index >= _photos.Count) return;
        _currentIndex = index;
        CurrentPhoto = _photos[index];
        Strip.SelectedIndex = index;
        Strip.ScrollIntoView(_photos[index]);
        ResetZoom();
        UpdateOverlays();
        _ = LoadPreviewAsync(_photos[index]);
    }

    private void MutateCurrent(Action<PhotoItem> mutate)
    {
        if (_currentIndex < 0 || _currentIndex >= _photos.Count) return;
        var photo = _photos[_currentIndex];
        mutate(photo);
        _vm.PersistPhoto(photo);
        UpdateOverlays();
    }

    private void UpdateOverlays()
    {
        if (_currentIndex < 0) return;
        var photo = _photos[_currentIndex];
        RatingText.Text = new string('★', photo.Rating);
        if (photo.Flag == CullFlag.Unflagged)
        {
            FlagBadge.Visibility = Visibility.Collapsed;
        }
        else
        {
            FlagBadge.Visibility = Visibility.Visible;
            FlagText.Text = photo.Flag == CullFlag.Pick ? "PICK" : "REJECT";
        }
        Title = $"Burst — {photo.FileName}  ({_currentIndex + 1}/{_photos.Count})";
    }

    private async Task LoadPreviewAsync(PhotoItem photo)
    {
        _previewCts?.Cancel();
        _previewCts = new CancellationTokenSource();
        var ct = _previewCts.Token;

        // Show whatever bytes are already resident first for instant feedback.
        var initial = photo.PreviewJpeg ?? photo.ThumbnailJpeg;
        if (initial != null)
            PreviewImageElement.Source = LoadBitmap(initial);

        if (photo.PreviewJpeg != null) return;

        try
        {
            var jpeg = await Task.Run(() => _vm.Extractor.ExtractPreview(photo.FilePath), ct);
            if (ct.IsCancellationRequested || jpeg == null) return;
            photo.PreviewJpeg = jpeg;
            if (_currentIndex >= 0 && _photos[_currentIndex] == photo)
                PreviewImageElement.Source = LoadBitmap(jpeg);
        }
        catch (OperationCanceledException) { }
    }

    private static BitmapSource? LoadBitmap(byte[] jpeg, int decodePixelWidth = 1920)
    {
        try
        {
            double rotation = 0.0;
            try
            {
                using var msMeta = new MemoryStream(jpeg);
                var metaDecoder = BitmapDecoder.Create(msMeta, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                var meta = metaDecoder.Frames[0].Metadata as BitmapMetadata;
                var raw = meta?.GetQuery("/app1/ifd/{ushort=274}");
                if (raw != null)
                {
                    rotation = Convert.ToInt32(raw) switch
                    {
                        3 => 180.0,
                        6 => 90.0,
                        8 => 270.0,
                        _ => 0.0
                    };
                }
            }
            catch { /* no EXIF — leave at 0 */ }

            var bi = new BitmapImage();
            bi.BeginInit();
            bi.StreamSource = new MemoryStream(jpeg);
            bi.CacheOption = BitmapCacheOption.OnLoad;
            if (decodePixelWidth > 0)
                bi.DecodePixelWidth = decodePixelWidth;
            bi.EndInit();
            bi.Freeze();

            if (rotation == 0.0) return bi;

            var rotated = new TransformedBitmap(bi, new System.Windows.Media.RotateTransform(rotation));
            rotated.Freeze();
            return rotated;
        }
        catch { return null; }
    }

    // ── Zoom & pan: mirrors the main preview's behaviour ──

    private void PreviewHost_MouseWheel(object sender, MouseWheelEventArgs e)
    {
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
            newScale = MinZoom;
            PreviewTranslate.X = 0;
            PreviewTranslate.Y = 0;
        }
        else
        {
            // Cursor-anchored zoom — keep the point under the cursor stable.
            var pt = e.GetPosition(PreviewImageElement);
            var ratio = newScale / oldScale;
            PreviewTranslate.X = pt.X * (1 - ratio) + PreviewTranslate.X * ratio;
            PreviewTranslate.Y = pt.Y * (1 - ratio) + PreviewTranslate.Y * ratio;
        }

        PreviewScale.ScaleX = PreviewScale.ScaleY = newScale;
        UpdateZoomIndicator(newScale);

        if (newScale > MinZoom + 1e-3) _ = LoadFullJpegIfNeededAsync();

        e.Handled = true;
    }

    private void PreviewHost_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement host) return;

        if (e.ClickCount == 2)
        {
            if (PreviewScale.ScaleX > MinZoom + 1e-3)
            {
                ResetZoom();
            }
            else
            {
                var pt = e.GetPosition(PreviewImageElement);
                var ratio = DoubleClickZoom / PreviewScale.ScaleX;
                PreviewTranslate.X = pt.X * (1 - ratio) + PreviewTranslate.X * ratio;
                PreviewTranslate.Y = pt.Y * (1 - ratio) + PreviewTranslate.Y * ratio;
                PreviewScale.ScaleX = PreviewScale.ScaleY = DoubleClickZoom;
                UpdateZoomIndicator(DoubleClickZoom);
                _ = LoadFullJpegIfNeededAsync();
            }
            e.Handled = true;
            return;
        }

        if (PreviewScale.ScaleX <= MinZoom + 1e-3) return;

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

    private void ResetZoom()
    {
        PreviewScale.ScaleX = PreviewScale.ScaleY = 1.0;
        PreviewTranslate.X = 0;
        PreviewTranslate.Y = 0;
        ZoomIndicator.Visibility = Visibility.Collapsed;
        _highResLoaded = false;
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

    private async Task LoadFullJpegIfNeededAsync()
    {
        if (_highResLoaded) return;
        if (_currentIndex < 0 || _currentIndex >= _photos.Count) return;

        var photo = _photos[_currentIndex];
        _highResLoaded = true;

        var ct = _previewCts?.Token ?? CancellationToken.None;
        try
        {
            var jpeg = photo.FullJpeg ?? await Task.Run(() => _vm.Extractor.ExtractFullJpeg(photo.FilePath), ct);
            if (ct.IsCancellationRequested || jpeg == null) return;
            if (_currentIndex < 0 || _photos[_currentIndex] != photo) return;

            photo.FullJpeg ??= jpeg;
            var bs = await Task.Run(() => LoadBitmap(jpeg, decodePixelWidth: 0), ct);
            if (ct.IsCancellationRequested || bs == null) return;
            if (_currentIndex < 0 || _photos[_currentIndex] != photo) return;

            PreviewImageElement.Source = bs;
        }
        catch (OperationCanceledException) { /* selection moved on */ }
        catch { _highResLoaded = false; /* let a later zoom retry */ }
    }

    private void Strip_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox lb) return;
        if (lb.SelectedIndex < 0 || lb.SelectedIndex == _currentIndex) return;
        MoveTo(lb.SelectedIndex);
    }

    private void Strip_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ListBox lb) return;
        var sv = FindScrollViewer(lb);
        if (sv == null) return;
        sv.ScrollToHorizontalOffset(sv.HorizontalOffset + e.Delta);
        e.Handled = true;
    }

    private static ScrollViewer? FindScrollViewer(System.Windows.DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var found = FindScrollViewer(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
            if (found != null) return found;
        }
        return null;
    }
}
