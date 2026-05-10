using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using Rawr.App.Controls;
using Rawr.App.Services;
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
        private set
        {
            if (_currentPhoto == value) return;
            _currentPhoto = value;
            OnPropertyChanged(nameof(CurrentPhoto));
            OnPropertyChanged(nameof(CurrentPhotoCaptureDateFormatted));
        }
    }

    private HistogramData? _histogramData;
    public HistogramData? HistogramData
    {
        get => _histogramData;
        private set
        {
            if (_histogramData == value) return;
            _histogramData = value;
            OnPropertyChanged(nameof(HistogramData));
        }
    }

    private HistogramMode _histogramMode = HistogramMode.Rgb;
    public HistogramMode HistogramMode
    {
        get => _histogramMode;
        private set
        {
            if (_histogramMode == value) return;
            _histogramMode = value;
            OnPropertyChanged(nameof(HistogramMode));
        }
    }

    private SidePanelView _sidePanelView = SidePanelView.Histogram;
    public SidePanelView SidePanelView
    {
        get => _sidePanelView;
        private set
        {
            if (_sidePanelView == value) return;
            _sidePanelView = value;
            OnPropertyChanged(nameof(SidePanelView));
        }
    }

    public string CurrentPhotoCaptureDateFormatted =>
        CurrentPhoto?.Metadata?.CaptureTime is DateTime captureTime
            ? captureTime.ToString(AppSettings.Current.DateFormat)
            : "";

    private const double MinZoom = 1.0;
    private const double MaxZoom = 64.0;
    private const double ZoomStep = 1.2;

    private readonly MainViewModel _vm;
    private readonly List<PhotoItem> _photos;
    private int _currentIndex = -1;
    private CancellationTokenSource? _previewCts;
    private CancellationTokenSource? _prefetchCts;
    private readonly DispatcherTimer _hiResZoomTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
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
    public IRelayCommand<HistogramMode> SetHistogramModeCommand { get; }
    public IRelayCommand<SidePanelView> SetSidePanelViewCommand { get; }

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
        NextCommand         = new RelayCommand(() => MoveBy(1));
        PrevCommand         = new RelayCommand(() => MoveBy(-1));
        TogglePickCommand      = new RelayCommand(() => MutateCurrent(p => p.Flag = p.Flag == CullFlag.Pick   ? CullFlag.Unflagged : CullFlag.Pick));
        ToggleRejectCommand    = new RelayCommand(() => MutateCurrent(p => p.Flag = p.Flag == CullFlag.Reject ? CullFlag.Unflagged : CullFlag.Reject));
        UnflagCommand          = new RelayCommand(() => MutateCurrent(p => p.Flag = CullFlag.Unflagged));
        SetAsThumbnailCommand  = new RelayCommand(SetAsThumbnail);
        SetRatingCommand       = new RelayCommand<int>(r => MutateCurrent(p => p.Rating = Math.Clamp(r, 0, 5)));
        SetColorLabelCommand = new RelayCommand<ColorLabel>(l => MutateCurrent(p => p.ColorLabel = p.ColorLabel == l ? ColorLabel.None : l));
        SetHistogramModeCommand = new RelayCommand<HistogramMode>(mode => HistogramMode = mode);
        SetSidePanelViewCommand = new RelayCommand<SidePanelView>(view => SidePanelView = view);

        InitializeComponent();
        DataContext = this;
        WindowHelper.ApplyDarkTitleBar(this);

        Strip.ItemsSource = _photos;
        HeaderText.Text = $"Burst — {_photos.Count} photos";

        _peek = new PixelPeekController(PreviewHost, PreviewImageElement,
            loadHighResAsync: LoadFullJpegIfNeededAsync);

        _hiResZoomTimer.Tick += async (_, _) =>
        {
            _hiResZoomTimer.Stop();
            if (NeedsHighResLoad())
                await LoadFullJpegIfNeededAsync();
        };

        RegisterShortcutBindings();

        Loaded += (_, _) =>
        {
            _peek?.AttachView(PhotoInfoPanelControl.PixelPeekView);
            if (InitialPeekState is { HasAnchor: true } s) _peek?.RestoreState(s);
            MoveTo(Math.Clamp(startIndex, 0, _photos.Count - 1));
        };
        Closed += (_, _) =>
        {
            _hiResZoomTimer.Stop();
            _previewCts?.Cancel();
            _prefetchCts?.Cancel();
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
        HistogramData = null;
        if (!keepZoom)
            ResetZoom();
        else
            _highResLoaded = false;
        UpdateOverlays();
        _ = ComputeHistogramAsync(_photos[index]);
        _ = LoadPreviewAsync(_photos[index]);
        QueueHighResLoadIfNeeded();
        _ = PrefetchNeighborPreviewsAsync(index);
    }

    private void MoveBy(int delta)
    {
        if (_photos.Count == 0) return;
        var current = _currentIndex < 0 ? 0 : _currentIndex;
        var next = (current + delta + _photos.Count) % _photos.Count;
        MoveTo(next, keepZoom: true);
    }

    private void MutateCurrent(Action<PhotoItem> mutate)
    {
        if (_currentIndex < 0 || _currentIndex >= _photos.Count) return;
        var photo = _photos[_currentIndex];
        mutate(photo);
        _vm.PersistPhoto(photo);
        UpdateOverlays();
    }

    private void RegisterShortcutBindings()
    {
        // Apply user-customisable bindings programmatically so the burst viewer
        // honours overrides from Settings instead of using hardcoded XAML keys.
        // Macros take precedence over registry actions on the same combo, matching
        // ShortcutBinder.ApplyTo on the main window.
        var settings = AppSettings.Current;
        var claimed = new HashSet<(Key, ModifierKeys)>();

        foreach (var macro in settings.Macros)
        {
            if (!macro.HasAnyAction) continue;
            var spec = KeySpec.TryParse(macro.KeyBinding);
            if (spec is null) continue;
            if (!claimed.Add((spec.Key, spec.Modifiers))) continue;

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

        var dispatch = BuildBurstActionDispatch();
        foreach (var action in ShortcutRegistry.All)
        {
            if (!dispatch.TryGetValue(action.Id, out var cmd)) continue;
            var (spec, _) = ShortcutBinder.ResolveBinding(settings, action);
            if (spec is null) continue;
            if (!claimed.Add((spec.Key, spec.Modifiers))) continue;

            var kb = new KeyBinding
            {
                Command = cmd,
                Key = spec.Key,
                Modifiers = spec.Modifiers,
            };
            if (action.CommandParameter is not null)
                kb.CommandParameter = action.CommandParameter;
            InputBindings.Add(kb);
        }
    }

    // Maps registry action IDs to the burst viewer's per-frame commands. Only
    // includes actions that make sense here — main-window concepts like OpenTags,
    // burst-collapse, or filters are deliberately excluded.
    private Dictionary<string, ICommand> BuildBurstActionDispatch() => new()
    {
        ["TogglePick"]       = TogglePickCommand,
        ["ToggleReject"]     = ToggleRejectCommand,
        ["Unflag"]           = UnflagCommand,
        ["SetAsThumbnail"]   = SetAsThumbnailCommand,
        ["NextPhoto"]        = NextCommand,
        ["NextPhotoAlt"]     = NextCommand,
        ["PreviousPhoto"]    = PrevCommand,
        ["PreviousPhotoAlt"] = PrevCommand,
        ["Rating0"]          = SetRatingCommand,
        ["Rating1"]          = SetRatingCommand,
        ["Rating2"]          = SetRatingCommand,
        ["Rating3"]          = SetRatingCommand,
        ["Rating4"]          = SetRatingCommand,
        ["Rating5"]          = SetRatingCommand,
        ["ColorRed"]         = SetColorLabelCommand,
        ["ColorYellow"]      = SetColorLabelCommand,
        ["ColorGreen"]       = SetColorLabelCommand,
        ["ColorBlue"]        = SetColorLabelCommand,
    };

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
            var jpeg = await _vm.LoadPreviewJpegForPhotoAsync(photo, ct);
            if (ct.IsCancellationRequested || jpeg == null) return;
            if (_currentIndex >= 0 && _photos[_currentIndex] == photo)
            {
                PreviewImageElement.Source = LoadBitmap(jpeg);
                _ = ComputeHistogramAsync(photo);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private async Task PrefetchNeighborPreviewsAsync(int currentIndex)
    {
        _prefetchCts?.Cancel();
        _prefetchCts = new CancellationTokenSource();
        var ct = _prefetchCts.Token;

        var targets = new List<PhotoItem>(4);
        foreach (var offset in new[] { 1, -1, 2, -2 })
        {
            var i = currentIndex + offset;
            if (i < 0 || i >= _photos.Count) continue;
            targets.Add(_photos[i]);
        }
        if (targets.Count == 0) return;

        using var gate = new SemaphoreSlim(Math.Min(2, targets.Count));
        var tasks = new List<Task>(targets.Count);
        foreach (var photo in targets)
        {
            try
            {
                await gate.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            tasks.Add(Task.Run(async () =>
            {
                try { await _vm.LoadPreviewJpegForPhotoAsync(photo, ct); }
                catch (OperationCanceledException) { }
                catch { }
                finally { gate.Release(); }
            }));
        }

        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) { }
    }

    private async Task ComputeHistogramAsync(PhotoItem photo)
    {
        var index = _currentIndex;
        var jpeg = photo.FullJpeg ?? photo.PreviewJpeg ?? photo.ThumbnailJpeg;
        if (jpeg == null)
        {
            if (IsCurrentPhoto(photo, index))
                HistogramData = null;
            return;
        }

        try
        {
            var data = await Task.Run(() => HistogramComputer.Compute(jpeg));
            if (IsCurrentPhoto(photo, index))
                HistogramData = data;
        }
        catch
        {
            if (IsCurrentPhoto(photo, index))
                HistogramData = null;
        }
    }

    private bool IsCurrentPhoto(PhotoItem photo, int index) =>
        index >= 0
        && _currentIndex == index
        && _currentIndex < _photos.Count
        && ReferenceEquals(_photos[_currentIndex], photo);

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

        QueueHighResLoadIfNeeded();

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

    private void QueueHighResLoadIfNeeded()
    {
        if (NeedsHighResLoad())
        {
            _hiResZoomTimer.Stop();
            _hiResZoomTimer.Start();
        }
        else
        {
            _hiResZoomTimer.Stop();
        }
    }

    private bool NeedsHighResLoad() =>
        PreviewScale.ScaleX > MinZoom + 1e-3
        || _peek?.CaptureState().HasAnchor == true;

    private async Task LoadFullJpegIfNeededAsync()
    {
        if (_highResLoaded) return;
        if (_currentIndex < 0 || _currentIndex >= _photos.Count) return;

        var photo = _photos[_currentIndex];
        _highResLoaded = true;

        var ct = _previewCts?.Token ?? CancellationToken.None;
        try
        {
            var jpeg = await _vm.LoadFullJpegForPhotoAsync(photo, ct);
            if (ct.IsCancellationRequested || jpeg == null) return;
            if (_currentIndex < 0 || _photos[_currentIndex] != photo) return;

            _ = ComputeHistogramAsync(photo);
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
        ScrollSpeed.ScrollHorizontal(sv, e);
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

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
