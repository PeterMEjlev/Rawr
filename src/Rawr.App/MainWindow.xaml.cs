using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using Rawr.App.Controls;
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

    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RAWR");
    private static readonly string LayoutSettingsFile = Path.Combine(SettingsDir, "layout.json");

    private record LayoutSettings(
        int GridColumnCount = 2,
        double FilmstripRowHeight = 148.0,
        bool ShowGrid = true,
        bool ShowFilmstrip = true,
        bool ShowSecondMonitor = false,
        double? SecondMonitorLeft = null,
        double? SecondMonitorTop = null,
        double? SecondMonitorWidth = null,
        double? SecondMonitorHeight = null);

    private bool _isPanning;
    private Point _panStart;
    private double _panStartTx;
    private double _panStartTy;
    private WrapPanel? _gridItemsPanel;
    private GridLength _savedFilmstripHeight = new GridLength(148);
    private GridLength _savedGridWidth = new GridLength(200);
    private PhotoItem? _prevSelectedPhoto;

    // Video playback state. The DispatcherTimer pulls VideoPlayer.Position into the
    // slider while playing; the suppress flag prevents the timer-driven slider update
    // from being interpreted as a user scrub.
    private readonly DispatcherTimer _videoTick = new() { Interval = TimeSpan.FromMilliseconds(250) };

    // Debounces the high-res preview load triggered by the zoom wheel. Swapping in a
    // full-resolution bitmap mid-scroll stalls the render thread when WPF uploads the
    // texture; deferring the load until the user pauses keeps rapid zoom smooth.
    private readonly DispatcherTimer _hiResZoomTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };

    // Pixel-peep loupe. Toggled by single-clicking the unzoomed preview; the
    // controller handles the peek window lifecycle, source tracking and
    // cursor → image-pixel mapping.
    private PixelPeekController? _peek;

    // Second-monitor preview window. Lifetime is driven by MainViewModel.ShowSecondMonitor;
    // closing the window directly flips the flag back off so the View-menu checkbox stays in sync.
    private SecondMonitorWindow? _secondMonitor;
    private LayoutSettings? _loadedLayout;

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

        // Arrow keys must always navigate between photos, regardless of which
        // panel currently has focus (sidebar buttons, folder tree, etc.).
        // Without this, container ScrollViewers consume arrow keys for scrolling
        // before the window-level KeyBindings see them.
        PreviewKeyDown += OnWindowPreviewKeyDown;

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
                if (e.PropertyName == nameof(MainViewModel.ShowSecondMonitor) && DataContext is MainViewModel vmS)
                    ApplySecondMonitorVisibility(vmS.ShowSecondMonitor);
                if (e.PropertyName == nameof(MainViewModel.VideoSourceUri))
                    OnVideoSourceChanged();
            };
        }

        _videoTick.Tick += VideoTick_OnTick;

        _hiResZoomTimer.Tick += async (_, _) =>
        {
            _hiResZoomTimer.Stop();
            if (PreviewScale.ScaleX > MinZoom + 1e-3 && DataContext is MainViewModel vm)
            {
                await vm.LoadHighResPreviewAsync();
                // Source dimensions just changed — re-evaluate the scaling mode
                // now that the full-res bitmap is in place.
                UpdatePreviewScalingMode(PreviewScale.ScaleX);
            }
        };

        _peek = new PixelPeekController(
            PreviewHost, PreviewImageElement,
            loadHighResAsync: () => (DataContext as MainViewModel)?.LoadHighResPreviewAsync() ?? Task.CompletedTask);

        // The peek view is in the right-hand panel — attach once it's loaded.
        Loaded += (_, _) => _peek?.AttachView(PixelPeekViewControl);

        Closing += (_, _) => { SaveLayoutSettings(); _peek?.Dispose(); _peek = null; };
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
        Loaded += async (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                var layout = await LoadLayoutSettingsAsync();
                _loadedLayout = layout;
                vm.GridColumnCount = Math.Clamp(layout.GridColumnCount, 1, 8);
                _savedFilmstripHeight = new GridLength(Math.Clamp(layout.FilmstripRowHeight, 80, 400));
                RootGrid.RowDefinitions[3].Height = _savedFilmstripHeight;
                vm.ShowGrid = layout.ShowGrid;
                vm.ShowFilmstrip = layout.ShowFilmstrip;
                ApplyGridVisibility(vm.ShowGrid);
                ApplyFilmstripVisibility(vm.ShowFilmstrip);
                vm.ShowSecondMonitor = layout.ShowSecondMonitor;
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

            // Carry forward the second-monitor bounds so the next session restores
            // to the same display. Prefer the live window's bounds; otherwise reuse
            // whatever was loaded.
            double? smLeft = _loadedLayout?.SecondMonitorLeft;
            double? smTop = _loadedLayout?.SecondMonitorTop;
            double? smWidth = _loadedLayout?.SecondMonitorWidth;
            double? smHeight = _loadedLayout?.SecondMonitorHeight;
            if (_secondMonitor is { IsLoaded: true } w)
            {
                smLeft = w.Left;
                smTop = w.Top;
                smWidth = w.Width;
                smHeight = w.Height;
            }

            var settings = new LayoutSettings(
                vm.GridColumnCount,
                height > 0 ? height : 148.0,
                vm.ShowGrid,
                vm.ShowFilmstrip,
                vm.ShowSecondMonitor,
                smLeft, smTop, smWidth, smHeight);
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

    private void ApplySecondMonitorVisibility(bool show)
    {
        if (show)
        {
            if (_secondMonitor is { IsLoaded: true })
            {
                _secondMonitor.Activate();
                return;
            }

            if (DataContext is not MainViewModel vm) return;

            var win = new SecondMonitorWindow(vm) { Owner = this };

            // Prefer last-saved bounds when present; otherwise centre a default-size window
            // on a non-primary monitor so the user can maximize / drag from there.
            if (_loadedLayout is { SecondMonitorWidth: > 0, SecondMonitorHeight: > 0 } l
                && l.SecondMonitorLeft is double ll
                && l.SecondMonitorTop is double tt
                && l.SecondMonitorWidth is double ww
                && l.SecondMonitorHeight is double hh)
            {
                win.WindowStartupLocation = WindowStartupLocation.Manual;
                win.Left = ll;
                win.Top = tt;
                win.Width = ww;
                win.Height = hh;
            }
            else if (WindowHelper.PickSecondaryMonitor() is { } target)
            {
                win.WindowStartupLocation = WindowStartupLocation.Manual;
                var w = Math.Min(win.Width, target.Width * 0.8);
                var h = Math.Min(win.Height, target.Height * 0.8);
                win.Width = w;
                win.Height = h;
                win.Left = target.Left + (target.Width - w) / 2;
                win.Top = target.Top + (target.Height - h) / 2;
            }

            // User-driven close (e.g. ESC) needs to flip the menu checkbox off.
            // Re-entrancy guard: ApplySecondMonitorVisibility is the only writer when
            // _secondMonitor != null, so resetting the flag here doesn't loop.
            win.Closing += (_, _) =>
            {
                // Capture the final bounds so SaveLayoutSettings on app exit still
                // writes them, even though _secondMonitor is about to be cleared.
                if (win.IsLoaded)
                {
                    _loadedLayout = (_loadedLayout ?? new LayoutSettings()) with
                    {
                        SecondMonitorLeft = win.Left,
                        SecondMonitorTop = win.Top,
                        SecondMonitorWidth = win.Width,
                        SecondMonitorHeight = win.Height,
                    };
                }
            };
            win.Closed += (_, _) =>
            {
                _secondMonitor = null;
                if (DataContext is MainViewModel vm2 && vm2.ShowSecondMonitor)
                    vm2.ShowSecondMonitor = false;
            };

            _secondMonitor = win;
            win.Show();
        }
        else
        {
            if (_secondMonitor is { } w)
            {
                _secondMonitor = null;
                w.Close();
            }
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

    // GridThumbnailSize drives both the width and height of each thumbnail cell.
    // WrapPanel arranges items at their natural size; the column count is implied
    // by item width fitting into the available row width.
    // Subtract 12 to reserve space for the slim scrollbar (10 px) plus rounding buffer,
    // and subtract 8 for the 2 px item margin + 2 px border on each side (FilmstripItemStyle).
    private void RecalcGridThumbnailSize()
    {
        if (DataContext is not MainViewModel vm) return;

        _gridItemsPanel ??= FindDescendant<WrapPanel>(GridView);

        var available = GridView.ActualWidth - 12;
        if (available <= 0) return;
        vm.GridThumbnailSize = Math.Max(20, Math.Floor(available / vm.GridColumnCount) - 8);
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

    // ── Folder tree: single-click to navigate ──
    //
    // Uses SelectedItemChanged (not a MouseBinding) so keyboard nav works too.
    // Bails when the new selection already matches CurrentFolder so programmatic
    // selection (e.g. when SetTreeRoot marks the root selected after load) does
    // not trigger a redundant reload.
    private async void FolderTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not FolderNode node) return;
        if (node.IsPlaceholder || string.IsNullOrEmpty(node.FullPath)) return;
        if (DataContext is not MainViewModel vm) return;
        if (string.Equals(vm.CurrentFolder, node.FullPath, StringComparison.OrdinalIgnoreCase)) return;
        await vm.LoadFolderAsync(node.FullPath);
    }

    // Right-click on a tree row should focus that row before its context menu opens
    // — otherwise the menu's commands would target whatever was previously selected.
    private void FolderTreeView_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var hit = e.OriginalSource as DependencyObject;
        while (hit != null && hit is not TreeViewItem)
            hit = VisualTreeHelper.GetParent(hit);
        if (hit is TreeViewItem item)
            item.IsSelected = true;
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

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down)) return;

        // Let text-input controls keep arrow keys for caret movement / selection.
        if (Keyboard.FocusedElement is TextBox or PasswordBox or RichTextBox or ComboBox) return;

        // Don't hijack arrow keys while a menu is open — let it navigate items.
        if (Keyboard.FocusedElement is MenuItem) return;

        if (DataContext is not MainViewModel vm) return;
        if (vm.FilteredPhotos.Count == 0) return;

        if (e.Key is Key.Right or Key.Down)
            vm.NextPhotoCommand.Execute(null);
        else
            vm.PreviousPhotoCommand.Execute(null);
        e.Handled = true;
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

    // Take over arrow navigation entirely so ListBox's own arrow-key handler never
    // gets a turn. ListBox at the boundary consumes the first press as an internal
    // focus move (without changing selection), forcing a second press to wrap —
    // routing every press through Next/PreviousPhotoCommand makes the wrap happen
    // on the first press regardless of ListBox internal focus state.
    private void Filmstrip_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        if (DataContext is not MainViewModel vm) return;
        if (vm.FilteredPhotos.Count == 0) return;

        if (e.Key is Key.Right or Key.Down)
        {
            vm.NextPhotoCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key is Key.Left or Key.Up)
        {
            vm.PreviousPhotoCommand.Execute(null);
            e.Handled = true;
        }
    }

    // ── Preview: wheel zooms around the cursor; left-drag pans when zoomed ──

    private void PreviewHost_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not FrameworkElement host) return;
        if ((DataContext as MainViewModel)?.VideoSourceUri != null) return; // no zoom for videos

        // While the loupe is open the wheel adjusts its zoom rather than the
        // main preview; otherwise a mid-peep wheel-spin would zoom out the
        // background and break the 1:1 reference.
        // Wheel always zooms the main preview here. To zoom the loupe, scroll
        // over the peek view itself in the side panel.
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
        UpdatePreviewScalingMode(newScale);

        // Defer the full-resolution upgrade until the wheel stops — see _hiResZoomTimer.
        if (newScale > MinZoom + 1e-3)
        {
            _hiResZoomTimer.Stop();
            _hiResZoomTimer.Start();
        }
        else
        {
            _hiResZoomTimer.Stop();
        }

        e.Handled = true;
    }

    private void PreviewHost_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement host) return;
        if ((DataContext as MainViewModel)?.VideoSourceUri != null) return; // no zoom/pan for videos

        // Shift + click: re-anchor the pixel-peep view to the clicked pixel.
        // Works at any zoom level — TranslatePoint already inverts the
        // preview's RenderTransform so the math holds when zoomed in.
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && e.ClickCount == 1)
        {
            _peek?.SetAnchorFromCursor(e.GetPosition(host));
            if (DataContext is MainViewModel vmShift)
                vmShift.SidePanelView = SidePanelView.PixelPeek;
            e.Handled = true;
            return;
        }

        if (e.ClickCount == 2)
        {
            if (PreviewScale.ScaleX > MinZoom + 1e-3)
            {
                ResetPreviewZoom();
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
                UpdatePreviewScalingMode(dblClickZoom);
                if (DataContext is MainViewModel vm)
                {
                    _ = vm.LoadHighResPreviewAsync().ContinueWith(
                        _ => UpdatePreviewScalingMode(PreviewScale.ScaleX),
                        TaskScheduler.FromCurrentSynchronizationContext());
                }
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
        _hiResZoomTimer.Stop();
        PreviewScale.ScaleX = PreviewScale.ScaleY = 1.0;
        PreviewTranslate.X = 0;
        PreviewTranslate.Y = 0;
        ZoomIndicator.Visibility = Visibility.Collapsed;
        UpdatePreviewScalingMode(1.0);
    }

    // Pick a scaling filter based on whether each source pixel will end up smaller
    // (downscale → Linear smooths nicely) or larger (upscale → NearestNeighbor shows
    // actual sensor pixels instead of bilinear smudge). Compared against the source
    // PixelWidth so the threshold is correct regardless of whether we're showing the
    // screen-size preview or the full-resolution bitmap.
    private void UpdatePreviewScalingMode(double scale)
    {
        var mode = BitmapScalingMode.Linear;
        if (PreviewImageElement.Source is BitmapSource src && src.PixelWidth > 0)
        {
            double displayedWidth = PreviewImageElement.ActualWidth * scale;
            if (displayedWidth > src.PixelWidth)
                mode = BitmapScalingMode.NearestNeighbor;
        }

        RenderOptions.SetBitmapScalingMode(PreviewImageElement, mode);
        if (FocusPeakingOverlayImage != null)
            RenderOptions.SetBitmapScalingMode(FocusPeakingOverlayImage, mode);
        if (ClippingOverlayImage != null)
            RenderOptions.SetBitmapScalingMode(ClippingOverlayImage, mode);
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

    private void OnOpenMap(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var win = new MapWindow(vm, vm.AllPhotos) { Owner = this };
        win.Show();
    }

    private void OpenBurstFocus(PhotoItem representative)
    {
        if (DataContext is not MainViewModel vm) return;
        var members = vm.GetBurstMembers(representative.GroupId);
        if (members.Count == 0) return;
        var startIdx = Math.Max(0, members.IndexOf(representative));
        var win = new BurstFocusWindow(vm, members, startIdx)
        {
            Owner = this,
            // Carry the current peek anchor in so the burst viewer keeps
            // inspecting the same composition pixel — comparing focus across
            // burst frames is the whole point of the loupe here.
            InitialPeekState = _peek?.CaptureState(),
        };
        win.ShowDialog();
        // Bring any anchor / zoom changes from the burst viewer back so the
        // main panel reflects the user's last-inspected point.
        if (win.LastPeekState is { } returned) _peek?.RestoreState(returned);
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

        if (prev.ClippingMode != AppSettings.Current.ClippingMode
            || prev.ClippingThreshold != AppSettings.Current.ClippingThreshold)
            vm.RefreshClipping();

        // The per-pixel threshold also feeds the sidebar Exposure buckets — when it
        // changes, the cached percentages on each photo were computed against the old
        // value and need a re-scan. The area threshold is just a gate on those values,
        // so a plain ApplyFilter is enough to redraw the buckets without recomputing.
        if (prev.ClippingThreshold != AppSettings.Current.ClippingThreshold)
            _ = vm.RecomputeClippingStatsAsync();
        else if (prev.ClippedAreaThreshold != AppSettings.Current.ClippedAreaThreshold)
            vm.ApplyFilter();
    }

    // ── Click handling: Ctrl/Shift modifiers build the multi-selection set ──
    //
    // PreviewMouseLeftButtonDown runs before the ListBox claims the click, so we can
    // intercept Ctrl+click (toggle photo in selection set) and Shift+click (range from
    // anchor) and route them through the VM. Plain click falls through to ListBox's
    // built-in selection change → OnSelectedPhotoChanged → ReconcileSingleSelection
    // collapses any prior multi-selection back to the new anchor.

    private void PhotoList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox) return;
        if (DataContext is not MainViewModel vm) return;

        var hit = e.OriginalSource as DependencyObject;
        while (hit != null && hit is not ListBoxItem)
            hit = VisualTreeHelper.GetParent(hit);
        if (hit is not ListBoxItem item) return;
        if (item.DataContext is not PhotoItem photo) return;

        var modifiers = Keyboard.Modifiers;
        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            vm.SelectRangeTo(photo);
            // ListBox would otherwise do its own range selection (anchor → click)
            // using ListBoxItem.IsSelected and clobber our PhotoItem.IsSelected
            // bookkeeping; suppress the default so only our path runs.
            e.Handled = true;
        }
        else if (modifiers.HasFlag(ModifierKeys.Control))
        {
            vm.TogglePhotoSelection(photo);
            e.Handled = true;
        }
        else
        {
            // Plain click. Call SelectSinglePhoto explicitly so re-clicking the
            // current anchor still collapses any prior multi-selection (the
            // ListBox's own selection change wouldn't fire for a re-click).
            vm.SelectSinglePhoto(photo);
            // Don't mark Handled — let ListBox finish its focus / drag-detect
            // logic. SelectSinglePhoto already set SelectedIndex to this photo,
            // so the ListBox's own selection change is a no-op.
        }
    }

    // ── Context menu: select item on right-click so bulk ops hit the right photos ──

    private void PhotoList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox) return;
        if (DataContext is not MainViewModel vm) return;
        var source = e.OriginalSource as DependencyObject;
        while (source != null && source is not ListBoxItem)
            source = VisualTreeHelper.GetParent(source);
        if (source is not ListBoxItem item) return;
        if (item.DataContext is not PhotoItem photo) return;

        if (photo.IsSelected && vm.SelectedPhotosCount > 1)
        {
            // Right-click inside a multi-selection should preserve the set —
            // bulk ops triggered from the context menu need every photo. Just
            // re-aim the anchor so SelectedPhotoTagAssignments reflects the
            // right-clicked photo's checks (tag-toggle direction, etc.).
            vm.MoveAnchorTo(photo);
        }
        else
        {
            // Right-click outside the selection collapses to the click target,
            // matching Explorer's behaviour.
            vm.SelectSinglePhoto(photo);
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
