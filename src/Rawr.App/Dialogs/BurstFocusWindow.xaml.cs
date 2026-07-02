using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
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

    // Subject-classifier debug HUD (Settings → General → Debug). Mirrors the main
    // preview's overlay for the burst frame currently in focus. Null = hidden.
    private string? _subjectDebugText;
    public string? SubjectDebugText
    {
        get => _subjectDebugText;
        private set
        {
            if (_subjectDebugText == value) return;
            _subjectDebugText = value;
            OnPropertyChanged(nameof(SubjectDebugText));
        }
    }

    private ImageSource? _faceDebugOverlay;
    public ImageSource? FaceDebugOverlay
    {
        get => _faceDebugOverlay;
        private set
        {
            if (_faceDebugOverlay == value) return;
            _faceDebugOverlay = value;
            OnPropertyChanged(nameof(FaceDebugOverlay));
        }
    }
    private CancellationTokenSource? _subjectDebugCts;

    private const double MinZoom = 1.0;
    // Backed by AppSettings (General → Zoom); defaults match the former constants.
    private static double MaxZoom => Math.Max(MinZoom, AppSettings.Current.MaxZoom);
    private static double ZoomStep => Math.Max(1.01, AppSettings.Current.ZoomStep);

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
    private CancellationTokenSource? _overlayCts;
    private CancellationTokenSource? _clippingCts;

    private BitmapSource? _focusPeakingOverlay;
    public BitmapSource? FocusPeakingOverlay
    {
        get => _focusPeakingOverlay;
        private set { _focusPeakingOverlay = value; OnPropertyChanged(nameof(FocusPeakingOverlay)); }
    }

    private bool _focusPeakingEnabled;
    public bool FocusPeakingEnabled
    {
        get => _focusPeakingEnabled;
        private set { _focusPeakingEnabled = value; OnPropertyChanged(nameof(FocusPeakingEnabled)); }
    }

    private BitmapSource? _clippingOverlay;
    public BitmapSource? ClippingOverlay
    {
        get => _clippingOverlay;
        private set { _clippingOverlay = value; OnPropertyChanged(nameof(ClippingOverlay)); }
    }

    private bool _clippingEnabled;
    public bool ClippingEnabled
    {
        get => _clippingEnabled;
        private set { _clippingEnabled = value; OnPropertyChanged(nameof(ClippingEnabled)); }
    }

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
    public IRelayCommand ToggleFocusPeakingCommand { get; }
    public IRelayCommand ToggleClippingCommand     { get; }
    public IRelayCommand CycleOverlayCommand       { get; }

    public IRelayCommand ToggleFullscreenCommand { get; }

    /// <summary>When true the burst viewer opens in the same chrome-free
    /// fullscreen mode the main window was in, so toggling fullscreen then
    /// diving into a burst stays fullscreen.</summary>
    public bool StartFullscreen { get; init; }

    private bool _isFullscreen;
    private WindowStyle _preFullscreenStyle;

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
        ToggleFullscreenCommand = new RelayCommand(() => ApplyFullscreen(!_isFullscreen));
        ToggleFocusPeakingCommand = new RelayCommand(ToggleFocusPeaking);
        ToggleClippingCommand     = new RelayCommand(ToggleClipping);
        CycleOverlayCommand       = new RelayCommand(CycleOverlay);

        InitializeComponent();
        Opacity = 0.0;
        DataContext = this;
        WindowHelper.ApplyDarkTitleBar(this);

        Strip.ItemsSource = _photos;
        HeaderText.Text = $"Burst - {_photos.Count} photos";

        _peek = new PixelPeekController(PreviewHost, PreviewImageElement,
            loadHighResAsync: LoadFullJpegIfNeededAsync);

        _hiResZoomTimer.Tick += async (_, _) =>
        {
            _hiResZoomTimer.Stop();
            if (NeedsHighResLoad())
                await LoadFullJpegIfNeededAsync();
        };

        RegisterShortcutBindings();
        PreviewKeyDown += OnPreviewKeyDown;
        ContentRendered += (_, _) => RevealAfterFirstRender();

        Loaded += (_, _) =>
        {
            _peek?.AttachView(PhotoInfoPanelControl.PixelPeekView);
            if (InitialPeekState is { HasAnchor: true } s) _peek?.RestoreState(s);
            if (StartFullscreen) ApplyFullscreen(true);
            MoveTo(Math.Clamp(startIndex, 0, _photos.Count - 1));
        };
        Closed += (_, _) =>
        {
            _hiResZoomTimer.Stop();
            _previewCts?.Cancel();
            _prefetchCts?.Cancel();
            _overlayCts?.Cancel();
            _clippingCts?.Cancel();
            LastPeekState = _peek?.CaptureState();
            _peek?.Dispose();
            _peek = null;
        };
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || Keyboard.Modifiers != ModifierKeys.None) return;

        // Let text-input / open dropdown controls keep the arrow keys for caret
        // movement and item navigation.
        if (Keyboard.FocusedElement is TextBox or PasswordBox or RichTextBox) return;
        if (Keyboard.FocusedElement is ComboBox) return;

        // Arrow navigation must work no matter which control has focus — the
        // filmstrip, the histogram, the peek view, etc. Handle it here in the
        // tunneling PreviewKeyDown (which fires at the window root) rather than
        // via bubbling InputBindings, so a focused child can't swallow the key
        // before it reaches the window. Mirrors the main window's arrow handling.
        switch (e.Key)
        {
            case Key.Right:
                MoveBy(1);
                e.Handled = true;
                break;
            case Key.Left:
                MoveBy(-1);
                e.Handled = true;
                break;
            case Key.Down:
                e.Handled = true;
                Close();
                break;
        }
    }

    private void RevealAfterFirstRender()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            if (IsVisible) Opacity = 1.0;
        }));
    }

    private void MoveTo(int index, bool keepZoom = false)
    {
        if (index < 0 || index >= _photos.Count) return;
        _currentIndex = index;
        CurrentPhoto = _photos[index];
        _ = RefreshSubjectDebugAsync(_photos[index]);
        Strip.SelectedIndex = index;
        CenterStripSelection();
        HistogramData = null;
        FocusPeakingOverlay = null;
        ClippingOverlay = null;
        FaceDebugOverlay = null;   // cleared now; RefreshSubjectDebugAsync repopulates it
        if (!keepZoom)
            ResetZoom();
        else
            _highResLoaded = false;
        UpdateOverlays();
        _ = ComputeHistogramAsync(_photos[index]);
        _ = LoadPreviewAsync(_photos[index]);
        if (FocusPeakingEnabled) _ = ComputeFocusPeakingAsync(_photos[index]);
        if (ClippingEnabled)     _ = ComputeClippingAsync(_photos[index]);
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
        ["ViewPhotoFullscreen"] = ToggleFullscreenCommand,
        ["ToggleFocusPeaking"] = ToggleFocusPeakingCommand,
        ["ToggleClipping"]     = ToggleClippingCommand,
        ["CycleOverlay"]       = CycleOverlayCommand,
        ["NextPhoto"]        = NextCommand,
        ["PreviousPhoto"]    = PrevCommand,
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
        Title = $"Burst - {photo.FileName}  ({_currentIndex + 1}/{_photos.Count})";
    }

    // Chrome-free view mirroring the main window's "F" fullscreen: drop the
    // header and the metadata/histogram panel so the frame fills the window,
    // but keep the filmstrip since it's how bursts are navigated visually.
    // The owner window is itself fullscreen over the taskbar, so we must cover
    // the full monitor too — otherwise its filmstrip peeks out under ours.
    private void ApplyFullscreen(bool fullscreen)
    {
        if (fullscreen == _isFullscreen) return;
        _isFullscreen = fullscreen;

        var hwnd = new WindowInteropHelper(this).Handle;

        if (fullscreen)
        {
            _preFullscreenStyle = WindowStyle;
            HeaderBar.Visibility = Visibility.Collapsed;
            PhotoInfoPanelControl.Visibility = Visibility.Collapsed;
            if (WindowStyle != WindowStyle.None) WindowStyle = WindowStyle.None;
            // The WM_GETMINMAXINFO hook (gated by _isFullscreen) now reports the
            // full monitor; force the OS to recompute the frame immediately.
            if (TryGetNearestMonitorInfo(hwnd, out var info))
                SetWindowToRect(hwnd, info.rcMonitor);
        }
        else
        {
            HeaderBar.Visibility = Visibility.Visible;
            PhotoInfoPanelControl.Visibility = Visibility.Visible;
            if (WindowStyle != _preFullscreenStyle) WindowStyle = _preFullscreenStyle;
            // Hook now defers to normal bounds — contract back off the taskbar.
            if (TryGetNearestMonitorInfo(hwnd, out var info))
                SetWindowToRect(hwnd, info.rcWork);
        }
    }

    // ── Fullscreen monitor-bounds interop (mirrors MainWindow) ──

    private const int WM_GETMINMAXINFO = 0x0024;
    private const int WM_ERASEBKGND = 0x0014;
    private const int MONITOR_DEFAULTTONEAREST = 2;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const int DarkPreviewColorRef = 0x000E0E0E;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr hwndInsertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr hDC, ref RECT lprc, IntPtr hbr);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(int colorRef);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd)?.AddHook(WindowWndProc);
    }

    // While fullscreen, report the full monitor as the maximized bounds so we
    // cover the taskbar like the owner does. Otherwise leave the message alone.
    private IntPtr WindowWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_ERASEBKGND)
        {
            PaintDarkNativeBackground(hwnd, wParam);
            handled = true;
            return new IntPtr(1);
        }

        if (msg != WM_GETMINMAXINFO || !_isFullscreen) return IntPtr.Zero;
        if (!TryGetNearestMonitorInfo(hwnd, out var info)) return IntPtr.Zero;

        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        var r = info.rcMonitor;
        mmi.ptMaxPosition.X = 0;
        mmi.ptMaxPosition.Y = 0;
        mmi.ptMaxSize.X = r.Right - r.Left;
        mmi.ptMaxSize.Y = r.Bottom - r.Top;
        Marshal.StructureToPtr(mmi, lParam, true);
        handled = true;
        return IntPtr.Zero;
    }

    private static void PaintDarkNativeBackground(IntPtr hwnd, IntPtr hdc)
    {
        if (hwnd == IntPtr.Zero || hdc == IntPtr.Zero) return;
        if (!GetClientRect(hwnd, out var rect)) return;

        var brush = CreateSolidBrush(DarkPreviewColorRef);
        if (brush == IntPtr.Zero) return;
        try { FillRect(hdc, ref rect, brush); }
        finally { DeleteObject(brush); }
    }

    private static bool TryGetNearestMonitorInfo(IntPtr hwnd, out MONITORINFO info)
    {
        info = default;
        if (hwnd == IntPtr.Zero) return false;

        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return false;

        info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        return GetMonitorInfo(monitor, ref info);
    }

    private static void SetWindowToRect(IntPtr hwnd, RECT rect)
    {
        if (hwnd == IntPtr.Zero) return;

        SetWindowPos(
            hwnd,
            IntPtr.Zero,
            rect.Left,
            rect.Top,
            rect.Right - rect.Left,
            rect.Bottom - rect.Top,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    private async Task RefreshSubjectDebugAsync(PhotoItem photo)
    {
        _subjectDebugCts?.Cancel();
        if (!AppSettings.Current.ShowSubjectClassifierScores)
        {
            SubjectDebugText = null;
            FaceDebugOverlay = null;
            return;
        }

        var cts = new CancellationTokenSource();
        _subjectDebugCts = cts;
        try
        {
            var d = await _vm.ComputeDebugAsync(photo, cts.Token);
            // Ignore if a newer frame superseded this one mid-inference.
            if (cts.IsCancellationRequested || _currentIndex < 0 || _photos[_currentIndex] != photo) return;
            SubjectDebugText = d.Text;
            // Build the WPF overlay on the UI thread; guard so a failure never hides text.
            try { FaceDebugOverlay = d.Faces != null ? MainViewModel.BuildFaceOverlay(d.Faces) : null; }
            catch { FaceDebugOverlay = null; }
        }
        catch (OperationCanceledException) { }
        catch { /* best-effort debug overlay */ }
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
            // Re-run focus peaking on the full-resolution JPEG so the overlay
            // matches the hi-res decode (same as the main window's refresh path).
            if (FocusPeakingEnabled) _ = ComputeFocusPeakingAsync(photo);
            // Clipping is keyed from the RAW decode, not the JPEG, so it doesn't
            // need a refresh here — it was already triggered by ComputeClippingAsync.
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

    // Shift the strip so the selected photo stays centered in the viewport
    // rather than drifting to the edge. Items are a uniform width, so the
    // per-item slot is ExtentWidth / count; the target is clamped so the
    // strip stops at the first/last item instead of scrolling past the ends.
    private void CenterStripSelection()
    {
        if (Strip.SelectedIndex < 0) return;

        bool TryCenter()
        {
            var sv = FindScrollViewer(Strip);
            if (sv == null) return false;

            int count = Strip.Items.Count;
            if (count == 0 || sv.ExtentWidth <= 0 || sv.ViewportWidth <= 0) return false;

            double slot = sv.ExtentWidth / count;
            double target = (Strip.SelectedIndex + 0.5) * slot - sv.ViewportWidth / 2.0;
            target = Math.Clamp(target, 0, sv.ScrollableWidth);
            sv.ScrollToHorizontalOffset(target);
            return true;
        }

        if (!TryCenter())
            Strip.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => TryCenter()));
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

    // ── Overlay toggles (focus peaking + clipping) ────────────────────────

    private void ToggleFocusPeaking()
    {
        if (FocusPeakingEnabled) DisableOverlays();
        else EnableFocusPeaking();
    }

    private void ToggleClipping()
    {
        if (ClippingEnabled) DisableOverlays();
        else EnableClipping();
    }

    // Mirrors the main window's CycleOverlay: off → focus peaking → clipping → off.
    private void CycleOverlay()
    {
        if (FocusPeakingEnabled) EnableClipping();
        else if (ClippingEnabled) DisableOverlays();
        else EnableFocusPeaking();
    }

    private void EnableFocusPeaking()
    {
        ClippingEnabled = false;
        ClippingOverlay = null;
        FocusPeakingEnabled = true;
        if (_currentIndex >= 0 && _currentIndex < _photos.Count)
            _ = ComputeFocusPeakingAsync(_photos[_currentIndex]);
    }

    private void EnableClipping()
    {
        FocusPeakingEnabled = false;
        FocusPeakingOverlay = null;
        ClippingEnabled = true;
        if (_currentIndex >= 0 && _currentIndex < _photos.Count)
            _ = ComputeClippingAsync(_photos[_currentIndex]);
    }

    private void DisableOverlays()
    {
        FocusPeakingEnabled = false;
        FocusPeakingOverlay = null;
        ClippingEnabled = false;
        ClippingOverlay = null;
    }

    private async Task ComputeFocusPeakingAsync(PhotoItem photo)
    {
        var jpeg = photo.FullJpeg ?? photo.PreviewJpeg;
        if (jpeg == null) return;

        _overlayCts?.Cancel();
        _overlayCts = new CancellationTokenSource();
        var ct = _overlayCts.Token;

        var strictness = AppSettings.Current.FocusPeakingThreshold;
        var options = AppSettings.Current.FocusPeaking;
        try
        {
            var overlay = await Task.Run(() => FocusPeakingComputer.Compute(jpeg, strictness, options), ct);
            if (!ct.IsCancellationRequested && IsCurrentPhoto(photo, _currentIndex) && FocusPeakingEnabled)
                FocusPeakingOverlay = overlay;
        }
        catch (OperationCanceledException) { }
    }

    private async Task ComputeClippingAsync(PhotoItem photo)
    {
        _clippingCts?.Cancel();
        _clippingCts = new CancellationTokenSource();
        var ct = _clippingCts.Token;

        try
        {
            var index = _currentIndex;
            var raw = await _vm.LoadLinearRawForPhotoAsync(photo, ct);
            if (ct.IsCancellationRequested || raw == null) return;
            if (!IsCurrentPhoto(photo, index) || !ClippingEnabled) return;

            var mode = AppSettings.Current.ClippingMode;
            var threshold = AppSettings.Current.ClippingThreshold;
            var overlay = await Task.Run(() => ClippingComputer.Compute(raw, mode, threshold), ct);
            if (!ct.IsCancellationRequested && IsCurrentPhoto(photo, index) && ClippingEnabled)
                ClippingOverlay = overlay;
        }
        catch (OperationCanceledException) { }
    }

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
