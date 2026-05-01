using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using Rawr.App.Dialogs;
using Rawr.App.Shortcuts;
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

    private record LayoutSettings(int GridColumnCount = 2, double FilmstripRowHeight = 148.0, bool ShowGrid = true, bool ShowFilmstrip = true);

    private bool _isPanning;
    private Point _panStart;
    private double _panStartTx;
    private double _panStartTy;
    private UniformGrid? _gridItemsPanel;
    private GridLength _savedFilmstripHeight = new GridLength(148);
    private GridLength _savedGridWidth = new GridLength(200);
    private PhotoItem? _prevSelectedPhoto;

    // Video playback state. The DispatcherTimer pulls VideoPlayer.Position into the
    // slider while playing; the suppress flag prevents the timer-driven slider update
    // from being interpreted as a user scrub.
    private readonly DispatcherTimer _videoTick = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _videoIsPlaying;
    private bool _videoSliderIsDragging;
    private bool _videoSuppressSliderEvent;
    private TimeSpan _videoDuration;
    private bool _videoIsMuted;

    /// <summary>Toggles the tags popup. Bound by default to 'T' via the shortcut registry.</summary>
    public ICommand OpenTagsCommand { get; }

    public MainWindow()
    {
        OpenTagsCommand = new RelayCommand(() =>
        {
            if (TagsPopup is not null) TagsPopup.IsOpen = !TagsPopup.IsOpen;
        });

        // Load persisted settings before InputBindings are applied so user-customised
        // keyboard shortcuts are in place by the time the window is shown.
        AppSettings.Current = AppSettings.Load();

        InitializeComponent();
        WindowHelper.ApplyDarkTitleBar(this);

        // Text-input controls take precedence over window shortcuts: while a TextBox
        // has keyboard focus, we suspend InputBindings so keys like P/T/G/Ctrl+C type
        // or do clipboard ops on the field instead of firing the shortcut. They're
        // restored on focus loss.
        AddHandler(GotKeyboardFocusEvent,
                   new KeyboardFocusChangedEventHandler(OnAnyGotKeyboardFocus),
                   handledEventsToo: true);

        ShortcutBinder.ApplyTo(this, AppSettings.Current);

        if (DataContext is INotifyPropertyChanged inpc)
        {
            inpc.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.SelectedPhoto))
                {
                    var newPhoto = (DataContext as MainViewModel)?.SelectedPhoto;
                    var sameGroup = newPhoto != null
                        && newPhoto.GroupId > 0
                        && _prevSelectedPhoto?.GroupId == newPhoto.GroupId;
                    _prevSelectedPhoto = newPhoto;
                    if (!sameGroup)
                        ResetPreviewZoom();
                }
                if (e.PropertyName == nameof(MainViewModel.GridColumnCount))
                    RecalcGridThumbnailSize();
                if (e.PropertyName == nameof(MainViewModel.ShowGrid) && DataContext is MainViewModel vmG)
                    ApplyGridVisibility(vmG.ShowGrid);
                if (e.PropertyName == nameof(MainViewModel.ShowFilmstrip) && DataContext is MainViewModel vmF)
                    ApplyFilmstripVisibility(vmF.ShowFilmstrip);
                if (e.PropertyName == nameof(MainViewModel.VideoSourceUri))
                    OnVideoSourceChanged();
            };
        }

        _videoTick.Tick += VideoTick_OnTick;

        Closing += (_, _) => SaveLayoutSettings();
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
        Loaded += async (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                var layout = await LoadLayoutSettingsAsync();
                vm.GridColumnCount = Math.Clamp(layout.GridColumnCount, 1, 8);
                _savedFilmstripHeight = new GridLength(Math.Clamp(layout.FilmstripRowHeight, 80, 400));
                RootGrid.RowDefinitions[3].Height = _savedFilmstripHeight;
                vm.ShowGrid = layout.ShowGrid;
                vm.ShowFilmstrip = layout.ShowFilmstrip;
                ApplyGridVisibility(vm.ShowGrid);
                ApplyFilmstripVisibility(vm.ShowFilmstrip);
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
            var height = vm.ShowFilmstrip
                ? RootGrid.RowDefinitions[3].ActualHeight
                : _savedFilmstripHeight.Value;
            var settings = new LayoutSettings(vm.GridColumnCount, height > 0 ? height : 148.0, vm.ShowGrid, vm.ShowFilmstrip);
            File.WriteAllText(LayoutSettingsFile, JsonSerializer.Serialize(settings));
        }
        catch { /* non-critical */ }
    }

    // ── Panel visibility ──

    private void ApplyGridVisibility(bool show)
    {
        var cols = MainSplitGrid.ColumnDefinitions;
        if (show)
        {
            cols[0].MinWidth = 100;
            cols[0].Width = _savedGridWidth;
            cols[1].Width = new GridLength(4);
        }
        else
        {
            if (cols[0].ActualWidth > 0)
                _savedGridWidth = new GridLength(cols[0].ActualWidth);
            cols[0].MinWidth = 0;
            cols[0].Width = new GridLength(0);
            cols[1].Width = new GridLength(0);
        }
    }

    private void ApplyFilmstripVisibility(bool show)
    {
        var rows = RootGrid.RowDefinitions;
        if (show)
        {
            rows[2].Height = new GridLength(4);
            rows[3].MinHeight = 80;
            rows[3].Height = _savedFilmstripHeight;
        }
        else
        {
            var current = rows[3].ActualHeight;
            if (current > 0)
                _savedFilmstripHeight = new GridLength(current);
            rows[2].Height = new GridLength(0);
            rows[3].MinHeight = 0;
            rows[3].Height = new GridLength(0);
        }
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

    private List<InputBinding>? _suspendedInputBindings;

    private void OnAnyGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        var isText = e.NewFocus is TextBox or PasswordBox or RichTextBox;
        if (isText && _suspendedInputBindings is null)
        {
            _suspendedInputBindings = new List<InputBinding>(InputBindings.Count);
            foreach (InputBinding ib in InputBindings) _suspendedInputBindings.Add(ib);
            InputBindings.Clear();
        }
        else if (!isText && _suspendedInputBindings is not null)
        {
            foreach (var ib in _suspendedInputBindings) InputBindings.Add(ib);
            _suspendedInputBindings = null;
        }
    }

    // ListBox swallows Left/Right at the ends without moving selection, blocking the
    // window-level NextPhoto/PreviousPhoto KeyBindings from firing — wrap manually.
    private void Filmstrip_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        if (sender is not ListBox lb || DataContext is not MainViewModel vm) return;
        if (vm.FilteredPhotos.Count == 0) return;

        if (e.Key is Key.Right or Key.Down)
        {
            if (lb.SelectedIndex == vm.FilteredPhotos.Count - 1)
            {
                vm.NextPhotoCommand.Execute(null);
                e.Handled = true;
            }
        }
        else if (e.Key is Key.Left or Key.Up)
        {
            if (lb.SelectedIndex <= 0)
            {
                vm.PreviousPhotoCommand.Execute(null);
                e.Handled = true;
            }
        }
    }

    // ── Preview: wheel zooms around the cursor; left-drag pans when zoomed ──

    private void PreviewHost_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not FrameworkElement host) return;
        if ((DataContext as MainViewModel)?.VideoSourceUri != null) return; // no zoom for videos

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
        if ((DataContext as MainViewModel)?.VideoSourceUri != null) return; // no zoom/pan for videos

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
        vm.ApplyFilter();
    }

    // ── Settings ──

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Dialogs.SettingsWindow(AppSettings.Current) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result == null) return;

        var prev = AppSettings.Current;
        AppSettings.Current = dlg.Result;
        AppSettings.Current.Save();

        ShortcutBinder.ApplyTo(this, AppSettings.Current);

        if (DataContext is not MainViewModel vm) return;

        vm.NotifyDateFormatChanged();

        bool burstSettingsChanged =
            prev.BurstMaxGapSeconds != AppSettings.Current.BurstMaxGapSeconds ||
            prev.BurstSimilarityStrictness != AppSettings.Current.BurstSimilarityStrictness ||
            prev.BurstThumbnailMode != AppSettings.Current.BurstThumbnailMode;

        if (burstSettingsChanged)
            vm.ApplyBurstSettings();

        if (prev.FocusPeakingThreshold != AppSettings.Current.FocusPeakingThreshold)
            vm.RefreshFocusPeaking();
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

    // ── Video playback ──

    private void OnVideoSourceChanged()
    {
        // The Source binding has just been updated (or cleared). MediaOpened will
        // populate the slider when the new file is ready; in the meantime, stop the
        // tick timer and reset the player UI so we don't carry leftover state from
        // the prior video.
        _videoTick.Stop();
        _videoIsPlaying = false;
        _videoSliderIsDragging = false;
        SetPlayPauseGlyph(playing: false);

        var vm = DataContext as MainViewModel;
        if (vm?.VideoSourceUri == null)
        {
            // Selection moved off video: explicitly stop so the file handle is freed
            // even when the binding alone wouldn't have triggered teardown.
            VideoPlayer.Stop();
            VideoPlayer.Close();
            _videoDuration = TimeSpan.Zero;
            _videoSuppressSliderEvent = true;
            VideoSlider.Maximum = 1;
            VideoSlider.Value = 0;
            _videoSuppressSliderEvent = false;
            VideoTimeText.Text = "0:00 / 0:00";
        }
    }

    private void VideoPlayer_MediaOpened(object sender, RoutedEventArgs e)
    {
        _videoDuration = VideoPlayer.NaturalDuration.HasTimeSpan
            ? VideoPlayer.NaturalDuration.TimeSpan
            : TimeSpan.Zero;

        _videoSuppressSliderEvent = true;
        VideoSlider.Maximum = Math.Max(0.1, _videoDuration.TotalSeconds);
        VideoSlider.Value = 0;
        _videoSuppressSliderEvent = false;
        UpdateVideoTimeText(TimeSpan.Zero);

        VideoPlayer.IsMuted = _videoIsMuted;

        // Render the first frame without auto-playing audio. Play() then Pause() forces
        // the decoder to produce a frame; ScrubbingEnabled keeps it visible while paused.
        VideoPlayer.Play();
        VideoPlayer.Pause();
        _videoIsPlaying = false;
        SetPlayPauseGlyph(playing: false);
    }

    private void VideoPlayer_MediaEnded(object sender, RoutedEventArgs e)
    {
        VideoPlayer.Pause();
        VideoPlayer.Position = TimeSpan.Zero;
        _videoIsPlaying = false;
        _videoTick.Stop();
        SetPlayPauseGlyph(playing: false);
        _videoSuppressSliderEvent = true;
        VideoSlider.Value = 0;
        _videoSuppressSliderEvent = false;
        UpdateVideoTimeText(TimeSpan.Zero);
    }

    private void VideoPlayer_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        _videoTick.Stop();
        _videoIsPlaying = false;
        SetPlayPauseGlyph(playing: false);
        VideoTimeText.Text = "Failed to open video";
    }

    private void VideoPlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || vm.VideoSourceUri == null) return;

        if (_videoIsPlaying)
        {
            VideoPlayer.Pause();
            _videoIsPlaying = false;
            _videoTick.Stop();
        }
        else
        {
            VideoPlayer.Play();
            _videoIsPlaying = true;
            _videoTick.Start();
        }
        SetPlayPauseGlyph(_videoIsPlaying);
    }

    private void VideoMute_Click(object sender, RoutedEventArgs e)
    {
        _videoIsMuted = !_videoIsMuted;
        VideoPlayer.IsMuted = _videoIsMuted;
        VideoMuteButton.Content = _videoIsMuted ? "🔇" : "🔊";
    }

    private void VideoSlider_DragStarted(object sender, DragStartedEventArgs e) => _videoSliderIsDragging = true;

    private void VideoSlider_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _videoSliderIsDragging = false;
        SeekToSlider();
    }

    private void VideoSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_videoSuppressSliderEvent) return;
        // While the user is mid-drag we still seek so the frame updates live (ScrubbingEnabled);
        // value-changes from clicks on the track also fall through here.
        SeekToSlider();
    }

    private void SeekToSlider()
    {
        if (DataContext is not MainViewModel vm || vm.VideoSourceUri == null) return;
        VideoPlayer.Position = TimeSpan.FromSeconds(VideoSlider.Value);
        UpdateVideoTimeText(VideoPlayer.Position);
    }

    private void ExposureSlider_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var dep = e.OriginalSource as DependencyObject;
        while (dep != null && dep is not Thumb)
            dep = VisualTreeHelper.GetParent(dep);
        if (dep is Thumb && DataContext is MainViewModel vm)
        {
            vm.ExposureCompensation = 0.0;
            e.Handled = true;
        }
    }

    private void VideoTick_OnTick(object? sender, EventArgs e)
    {
        if (_videoSliderIsDragging) return;
        var pos = VideoPlayer.Position;
        _videoSuppressSliderEvent = true;
        VideoSlider.Value = pos.TotalSeconds;
        _videoSuppressSliderEvent = false;
        UpdateVideoTimeText(pos);
    }

    private void UpdateVideoTimeText(TimeSpan position) =>
        VideoTimeText.Text = $"{Format(position)} / {Format(_videoDuration)}";

    private static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}" : $"{t.Minutes}:{t.Seconds:D2}";

    private void SetPlayPauseGlyph(bool playing) =>
        VideoPlayPauseButton.Content = playing ? "⏸" : "▶";
}
