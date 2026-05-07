using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using Rawr.App.Controls;
using Rawr.App.Shortcuts;
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

    private readonly MainViewModel _vm;
    private readonly List<PhotoItem> _photos;
    private int _currentIndex = -1;
    private CancellationTokenSource? _previewCts;
    private CancellationTokenSource? _preloadCts;
    private bool _highResLoaded;
    private bool _isPanning;
    private Point _panStart;
    private double _panStartTx;
    private double _panStartTy;
    private PixelPeekController? _peek;

    public IRelayCommand CloseCommand           { get; }
    public IRelayCommand NextCommand            { get; }
    public IRelayCommand PrevCommand            { get; }
    public IRelayCommand TogglePickCommand      { get; }
    public IRelayCommand ToggleRejectCommand    { get; }
    public IRelayCommand UnflagCommand          { get; }
    public IRelayCommand SetAsThumbnailCommand  { get; }
    public IRelayCommand<int> SetRatingCommand  { get; }
    public IRelayCommand<ColorLabel> SetColorLabelCommand { get; }

    /// <summary>Optional initial peek anchor + zoom carried over from the
    /// caller so opening a burst doesn't lose the inspection point.</summary>
    public PixelPeekController.State? InitialPeekState { get; init; }

    /// <summary>Peek state snapshotted at close time for the caller to copy
    /// back so the main window's loupe stays in sync.</summary>
    public PixelPeekController.State? LastPeekState { get; private set; }

    public BurstFocusWindow(MainViewModel vm, List<PhotoItem> photos, int startIndex)
    {
        _vm = vm;
        _photos = photos;

        CloseCommand        = new RelayCommand(Close);
        NextCommand         = new RelayCommand(() => MoveTo(_currentIndex + 1, keepZoom: true));
        PrevCommand         = new RelayCommand(() => MoveTo(_currentIndex - 1, keepZoom: true));
        TogglePickCommand      = new RelayCommand(() => MutateCurrent(p => p.Flag = p.Flag == CullFlag.Pick   ? CullFlag.Unflagged : CullFlag.Pick));
        ToggleRejectCommand    = new RelayCommand(() => MutateCurrent(p => p.Flag = p.Flag == CullFlag.Reject ? CullFlag.Unflagged : CullFlag.Reject));
        UnflagCommand          = new RelayCommand(() => MutateCurrent(p => p.Flag = CullFlag.Unflagged));
        SetAsThumbnailCommand  = new RelayCommand(SetAsThumbnail);
        SetRatingCommand       = new RelayCommand<int>(r => MutateCurrent(p => p.Rating = Math.Clamp(r, 0, 5)));
        SetColorLabelCommand = new RelayCommand<ColorLabel>(l => MutateCurrent(p => p.ColorLabel = p.ColorLabel == l ? ColorLabel.None : l));

        InitializeComponent();
        DataContext = this;
        WindowHelper.ApplyDarkTitleBar(this);

        Strip.ItemsSource = _photos;
        HeaderText.Text = $"Burst — {_photos.Count} photos";

        _peek = new PixelPeekController(PreviewHost, PreviewImageElement,
            loadHighResAsync: LoadFullJpegIfNeededAsync);

        RegisterMacroBindings();

        Loaded += (_, _) =>
        {
            _peek?.AttachView(PixelPeekViewControl);
            if (InitialPeekState is { HasAnchor: true } s) _peek?.RestoreState(s);
            MoveTo(Math.Clamp(startIndex, 0, _photos.Count - 1));
            _ = PreloadAllFullJpegsAsync();
        };
        Closed += (_, _) =>
        {
            _previewCts?.Cancel();
            _preloadCts?.Cancel();
            LastPeekState = _peek?.CaptureState();
            _peek?.Dispose();
            _peek = null;
        };
    }

    private void MoveTo(int index, bool keepZoom = false)
    {
        if (index < 0 || index >= _photos.Count) return;
        _currentIndex = index;
        CurrentPhoto = _photos[index];
        Strip.SelectedIndex = index;
        Strip.ScrollIntoView(_photos[index]);
        if (!keepZoom)
            ResetZoom();
        else
            _highResLoaded = false;
        UpdateOverlays();
        _ = LoadPreviewAsync(_photos[index]);
        _ = LoadFullJpegIfNeededAsync();
    }

    private void MutateCurrent(Action<PhotoItem> mutate)
    {
        if (_currentIndex < 0 || _currentIndex >= _photos.Count) return;
        var photo = _photos[_currentIndex];
        mutate(photo);
        _vm.PersistPhoto(photo);
        UpdateOverlays();
    }

    private void RegisterMacroBindings()
    {
        // Mirror the main-window macro bindings here so the user's chord works even
        // when the burst viewer has the keyboard focus. Targets only the current
        // frame, not the main-window selection.
        foreach (var macro in AppSettings.Current.Macros)
        {
            if (!macro.HasAnyAction) continue;
            var spec = KeySpec.TryParse(macro.KeyBinding);
            if (spec is null) continue;

            var capturedMacro = macro;
            var cmd = new RelayCommand(() =>
            {
                if (_currentIndex < 0 || _currentIndex >= _photos.Count) return;
                _vm.ExecuteMacro(capturedMacro, new[] { _photos[_currentIndex] });
                UpdateOverlays();
            });
            InputBindings.Add(new KeyBinding
            {
                Command = cmd,
                Key = spec.Key,
                Modifiers = spec.Modifiers,
            });
        }
    }

    private void SetAsThumbnail()
    {
        if (_currentIndex < 0 || _currentIndex >= _photos.Count) return;
        var current = _photos[_currentIndex];
        var setTo = !current.IsBestInGroup;
        foreach (var p in _photos)
        {
            var desired = p == current && setTo;
            if (p.IsBestInGroup != desired)
            {
                p.IsBestInGroup = desired;
                _vm.PersistPhoto(p);
            }
        }
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
        // Wheel always zooms the main preview. To zoom the loupe, scroll
        // over the peek view itself in the metadata panel.
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

        // Shift + click re-anchors the pixel-peep view at any zoom level.
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && e.ClickCount == 1)
        {
            _peek?.SetAnchorFromCursor(e.GetPosition(host));
            e.Handled = true;
            return;
        }

        if (e.ClickCount == 2)
        {
            if (PreviewScale.ScaleX > MinZoom + 1e-3)
            {
                ResetZoom();
            }
            else
            {
                var dblClickZoom = AppSettings.Current.DoubleClickZoom;
                var pt = e.GetPosition(PreviewImageElement);
                var ratio = dblClickZoom / PreviewScale.ScaleX;
                PreviewTranslate.X = pt.X * (1 - ratio) + PreviewTranslate.X * ratio;
                PreviewTranslate.Y = pt.Y * (1 - ratio) + PreviewTranslate.Y * ratio;
                PreviewScale.ScaleX = PreviewScale.ScaleY = dblClickZoom;
                UpdateZoomIndicator(dblClickZoom);
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

    // Extract raw JPEG bytes for every burst photo, ordered nearest-to-current
    // first. Stores results in photo.FullJpeg so subsequent LoadFullJpegIfNeededAsync
    // calls skip extraction and go straight to the WPF decode.
    private async Task PreloadAllFullJpegsAsync()
    {
        _preloadCts?.Cancel();
        _preloadCts = new CancellationTokenSource();
        var ct = _preloadCts.Token;

        var ordered = _photos
            .Select((p, i) => (photo: p, dist: Math.Abs(i - _currentIndex)))
            .OrderBy(x => x.dist)
            .Select(x => x.photo);

        foreach (var photo in ordered)
        {
            if (ct.IsCancellationRequested) break;
            if (photo.FullJpeg != null) continue;
            try
            {
                var jpeg = await Task.Run(() => _vm.Extractor.ExtractFullJpeg(photo.FilePath), ct);
                if (jpeg != null) photo.FullJpeg ??= jpeg;
            }
            catch (OperationCanceledException) { break; }
            catch { }
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
        MoveTo(lb.SelectedIndex, keepZoom: true);
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
