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
using LibVLCSharp.Shared;
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
        bool IsGridExpanded = false,
        int ExpandedGridColumnCount = 6,
        double? SecondMonitorLeft = null,
        double? SecondMonitorTop = null,
        double? SecondMonitorWidth = null,
        double? SecondMonitorHeight = null);

    private bool _isPanning;
    private Point _panStart;
    private double _panStartTx;
    private double _panStartTy;
    private GridLength _savedFilmstripHeight = new GridLength(148);
    private GridLength _savedGridWidth = new GridLength(200);
    private PhotoItem? _prevSelectedPhoto;

    // Video playback state. The DispatcherTimer pulls _vlcPlayer.Time into the
    // slider while playing; the suppress flag prevents the timer-driven slider update
    // from being interpreted as a user scrub.
    private readonly DispatcherTimer _videoTick = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private LibVLC? _libVlc;
    private LibVLCSharp.Shared.MediaPlayer? _vlcPlayer;

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
    private Uri? _pendingVideoSource;
    private bool _suppressFilterToggleMouseUp;
    private bool _suppressTagsToggleMouseUp;
    private bool _suppressCopyToggleMouseUp;
    private bool _suppressViewToggleMouseUp;

    // Cached lookup of (Key, ModifierKeys) → (Action, Command, CommandParameter) for
    // fast shortcut matching in the fallback dead-key handler. Action is null for
    // macro entries since macros aren't part of the ShortcutRegistry.
    private Dictionary<(Key, ModifierKeys), (ShortcutAction? Action, ICommand Cmd, object? Param)>? _shortcutMap;

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

        _libVlc = new LibVLC();
        _vlcPlayer = new LibVLCSharp.Shared.MediaPlayer(_libVlc);
        _vlcPlayer.Playing += VlcPlayer_Playing;
        _vlcPlayer.LengthChanged += VlcPlayer_LengthChanged;
        _vlcPlayer.EndReached += VlcPlayer_EndReached;
        _vlcPlayer.EncounteredError += VlcPlayer_EncounteredError;

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

        // Fallback handler for dead-keys and IME-processed keys (common on non-US layouts)
        // that don't match via InputBindings. Resolves the underlying key and tries
        // matching shortcuts manually. Fires before InputBindings so we can prevent
        // double-execution if both match.
        PreviewKeyDown += OnWindowPreviewKeyDownResolveDeadKeys;

        ApplyShortcuts(AppSettings.Current);

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
                if (e.PropertyName == nameof(MainViewModel.ActiveGridColumnCount))
                    RecalcGridThumbnailSize();
                if (e.PropertyName == nameof(MainViewModel.ShowGrid) && DataContext is MainViewModel vmG)
                    ApplyGridVisibility(vmG.ShowGrid);
                if (e.PropertyName == nameof(MainViewModel.IsGridExpanded) && DataContext is MainViewModel vmE)
                    ApplyGridExpanded(vmE.IsGridExpanded);
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
        Loaded += (_, _) => _peek?.AttachView(PhotoInfoPanelControl.PixelPeekView);

        // VideoView must have its MediaPlayer set after the control is loaded.
        Loaded += (_, _) => AttachVlcPlayerToView();
        VideoPlayer.IsVisibleChanged += (_, _) =>
        {
            if (VideoPlayer.IsVisible)
            {
                AttachVlcPlayerToView(force: true);
                Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(PlayPendingVideoSource));
            }
        };

        Closing += (_, _) =>
        {
            SaveLayoutSettings();
            _peek?.Dispose();
            _peek = null;
            _vlcPlayer?.Stop();
            _vlcPlayer?.Dispose();
            _libVlc?.Dispose();
        };
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
        Loaded += async (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                var layout = await LoadLayoutSettingsAsync();
                _loadedLayout = layout;
                vm.GridColumnCount = Math.Clamp(layout.GridColumnCount, 1, 8);
                vm.ExpandedGridColumnCount = Math.Clamp(layout.ExpandedGridColumnCount, 1, 16);
                _savedFilmstripHeight = new GridLength(Math.Clamp(layout.FilmstripRowHeight, 80, 400));
                RootGrid.RowDefinitions[4].Height = _savedFilmstripHeight;
                vm.ShowGrid = layout.ShowGrid;
                vm.ShowFilmstrip = layout.ShowFilmstrip;
                vm.IsGridExpanded = layout.IsGridExpanded;
                ApplyGridVisibility(vm.ShowGrid);
                ApplyFilmstripVisibility(vm.ShowFilmstrip);
                ApplyGridExpanded(vm.IsGridExpanded);
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
                ? RootGrid.RowDefinitions[4].ActualHeight
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
                vm.IsGridExpanded,
                vm.ExpandedGridColumnCount,
                smLeft, smTop, smWidth, smHeight);
            File.WriteAllText(LayoutSettingsFile, JsonSerializer.Serialize(settings));
        }
        catch { /* non-critical */ }
    }

    // ── Panel visibility ──

    private void ApplyGridVisibility(bool show)
    {
        var vm = DataContext as MainViewModel;
        ApplyMainSplitLayout(show, vm?.IsGridExpanded ?? false);
    }

    // Expand mode collapses the preview pane so the grid can fill the full
    // horizontal area between the filmstrip and the metadata sidebar — useful
    // for pure-sorting passes where the user only needs thumbnails.
    private void ApplyGridExpanded(bool expanded)
    {
        var vm = DataContext as MainViewModel;
        ApplyMainSplitLayout(vm?.ShowGrid ?? true, expanded);
    }

    private void ApplyMainSplitLayout(bool showGrid, bool isExpanded)
    {
        var cols = MainSplitGrid.ColumnDefinitions;

        // Preserve the user-sized grid width when leaving the pixel-sized state.
        if (cols[0].Width.GridUnitType == GridUnitType.Pixel && cols[0].ActualWidth > 0)
            _savedGridWidth = new GridLength(cols[0].ActualWidth);

        if (!showGrid)
        {
            cols[0].MinWidth = 0;
            cols[0].MaxWidth = double.PositiveInfinity;
            cols[0].Width = new GridLength(0);
            cols[1].Width = new GridLength(0);
            cols[2].Width = new GridLength(1, GridUnitType.Star);
        }
        else if (isExpanded)
        {
            cols[0].MinWidth = 100;
            cols[0].MaxWidth = double.PositiveInfinity;
            cols[0].Width = new GridLength(1, GridUnitType.Star);
            cols[1].Width = new GridLength(0);
            cols[2].Width = new GridLength(0);
        }
        else
        {
            cols[0].MinWidth = 100;
            cols[0].MaxWidth = 500;
            cols[0].Width = _savedGridWidth;
            cols[1].Width = new GridLength(4);
            cols[2].Width = new GridLength(1, GridUnitType.Star);
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
            rows[3].Height = new GridLength(4);
            rows[4].MinHeight = 80;
            rows[4].Height = _savedFilmstripHeight;
        }
        else
        {
            var current = rows[4].ActualHeight;
            if (current > 0)
                _savedFilmstripHeight = new GridLength(current);
            rows[3].Height = new GridLength(0);
            rows[4].MinHeight = 0;
            rows[4].Height = new GridLength(0);
        }
    }

    // ── Grid panel ──

    private void GridView_SizeChanged(object sender, SizeChangedEventArgs e) => RecalcGridThumbnailSize();

    // GridThumbnailSize drives the fixed tile size used by the virtualizing grid.
    // Subtract 12 to reserve space for the slim scrollbar (10 px) plus rounding buffer,
    // and subtract 8 for the 2 px item margin + 2 px border on each side (FilmstripItemStyle).
    private void RecalcGridThumbnailSize()
    {
        if (DataContext is not MainViewModel vm) return;

        var available = GridView.ActualWidth - 12;
        if (available <= 0) return;
        vm.GridThumbnailSize = Math.Max(20, Math.Floor(available / vm.ActiveGridColumnCount) - 8);
    }

    private void GridView_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        if (DataContext is not MainViewModel vm) return;

        // Scroll up = zoom in = fewer columns; scroll down = zoom out = more columns.
        vm.ActiveGridColumnCount = Math.Clamp(vm.ActiveGridColumnCount + (e.Delta > 0 ? -1 : 1), 1, vm.MaxGridColumnCount);
        e.Handled = true;
        // RecalcGridThumbnailSize is called via the PropertyChanged → ActiveGridColumnCount handler.
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

        ScrollSpeed.ScrollHorizontal(sv, e);
        e.Handled = true;
    }

    private void Filmstrip_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox lb || lb.SelectedItem is null) return;
        lb.ScrollIntoView(lb.SelectedItem);
    }

    private void VerticalScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        ScrollSpeed.ScrollVertical(sv, e);
        e.Handled = true;
    }

    private void FilterToggleButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!FilterPopup.IsOpen) return;

        _suppressFilterToggleMouseUp = true;
        FilterPopup.IsOpen = false;
        FilterToggleButton.IsChecked = false;
        e.Handled = true;
    }

    private void FilterToggleButton_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_suppressFilterToggleMouseUp) return;

        _suppressFilterToggleMouseUp = false;
        FilterPopup.IsOpen = false;
        FilterToggleButton.IsChecked = false;
        e.Handled = true;
    }

    private void FilterPopup_Closed(object? sender, EventArgs e)
    {
        if (FilterToggleButton.IsMouseOver && Mouse.LeftButton == MouseButtonState.Pressed)
            _suppressFilterToggleMouseUp = true;
    }

    private void TagsToggleButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!TagsPopup.IsOpen) return;

        _suppressTagsToggleMouseUp = true;
        TagsPopup.IsOpen = false;
        TagsToggleButton.IsChecked = false;
        e.Handled = true;
    }

    private void TagsToggleButton_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_suppressTagsToggleMouseUp) return;

        _suppressTagsToggleMouseUp = false;
        TagsPopup.IsOpen = false;
        TagsToggleButton.IsChecked = false;
        e.Handled = true;
    }

    private void TagsPopup_Closed(object? sender, EventArgs e)
    {
        if (TagsToggleButton.IsMouseOver && Mouse.LeftButton == MouseButtonState.Pressed)
            _suppressTagsToggleMouseUp = true;
    }

    private void CopyToggleButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!CopyPopup.IsOpen) return;

        _suppressCopyToggleMouseUp = true;
        CopyPopup.IsOpen = false;
        CopyToggleButton.IsChecked = false;
        e.Handled = true;
    }

    private void CopyToggleButton_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_suppressCopyToggleMouseUp) return;

        _suppressCopyToggleMouseUp = false;
        CopyPopup.IsOpen = false;
        CopyToggleButton.IsChecked = false;
        e.Handled = true;
    }

    private void CopyPopup_Closed(object? sender, EventArgs e)
    {
        if (CopyToggleButton.IsMouseOver && Mouse.LeftButton == MouseButtonState.Pressed)
            _suppressCopyToggleMouseUp = true;
    }

    private void ViewToggleButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!ViewPopup.IsOpen) return;

        _suppressViewToggleMouseUp = true;
        ViewPopup.IsOpen = false;
        ViewToggleButton.IsChecked = false;
        e.Handled = true;
    }

    private void ViewToggleButton_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_suppressViewToggleMouseUp) return;

        _suppressViewToggleMouseUp = false;
        ViewPopup.IsOpen = false;
        ViewToggleButton.IsChecked = false;
        e.Handled = true;
    }

    private void ViewPopup_Closed(object? sender, EventArgs e)
    {
        if (ViewToggleButton.IsMouseOver && Mouse.LeftButton == MouseButtonState.Pressed)
            _suppressViewToggleMouseUp = true;
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        // Let text-input controls keep navigation keys for caret movement / selection.
        if (Keyboard.FocusedElement is TextBox or PasswordBox or RichTextBox or ComboBox) return;

        // Don't hijack arrow keys while a menu is open — let it navigate items.
        if (Keyboard.FocusedElement is MenuItem) return;

        if (DataContext is not MainViewModel vm) return;

        if (e.Key == Key.Space && vm.VideoSourceUri != null)
        {
            ToggleVideoPlayPause();
            e.Handled = true;
            return;
        }

        if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down or Key.Enter)) return;
        if (vm.FilteredPhotos.Count == 0) return;

        if (e.Key is Key.Enter)
        {
            if (vm.SelectedPhoto is { CollapsedBurstCount: > 0 } burst)
            {
                OpenBurstFocus(burst);
                e.Handled = true;
            }
            return;
        }

        if (e.Key is Key.Right or Key.Down)
            vm.NextPhotoCommand.Execute(null);
        else
            vm.PreviousPhotoCommand.Execute(null);
        e.Handled = true;
    }

    // Fallback handler that tries to match shortcuts via the resolved key, covering
    // cases InputBindings miss: dead-key/IME-processed keys (Key.DeadCharProcessed,
    // Key.ImeProcessed) on non-US layouts, and any other situation where e.Key is a
    // placeholder. We run this for every key down — if InputBindings would have
    // fired first (synchronous tunnelling order), e.Handled is already true and we
    // bail out, so no double-execution.
    private void OnWindowPreviewKeyDownResolveDeadKeys(object sender, KeyEventArgs e)
    {
        if (e.Handled) return;

        // Don't trigger shortcuts while typing in text-input controls (text-input guards
        // are applied in ShortcutBinder for InputBindings; apply same logic here).
        if (Keyboard.FocusedElement is TextBox or PasswordBox or RichTextBox) return;

        // Resolve the underlying key (unwraps System/Ime/DeadCharProcessed).
        var resolved = KeySpec.ResolveKey(e);
        if (resolved == Key.None || KeySpec.IsModifierKey(resolved)) return;

        // Diagnostic — only log when the resolved key differs from e.Key (i.e. WPF
        // gave us a placeholder), so the log doesn't fill up on normal keystrokes.
        if (resolved != e.Key)
            KeySpec.LogKeyDiagnostic("runtime", e);

        var mods = Keyboard.Modifiers;

        // Build the shortcut map on first use.
        _shortcutMap ??= BuildShortcutMap();

        if (_shortcutMap.TryGetValue((resolved, mods), out var match))
        {
            match.Cmd.Execute(match.Param);
            e.Handled = true;
        }
    }

    private Dictionary<(Key, ModifierKeys), (ShortcutAction? Action, ICommand Cmd, object? Param)> BuildShortcutMap()
    {
        var map = new Dictionary<(Key, ModifierKeys), (ShortcutAction? Action, ICommand Cmd, object? Param)>();
        var settings = AppSettings.Current;

        if (DataContext is not MainViewModel vm) return map;

        // Macros take priority over built-in shortcuts on the same combo, matching
        // ShortcutBinder.ApplyTo's collision policy.
        var macroKeys = new HashSet<(Key, ModifierKeys)>();
        foreach (var macro in settings.Macros)
        {
            if (!macro.HasAnyAction) continue;
            var spec = KeySpec.TryParse(macro.KeyBinding);
            if (spec is null) continue;
            if (!macroKeys.Add((spec.Key, spec.Modifiers))) continue;

            var capturedMacro = macro;
            ICommand cmd = new RelayCommand(() => vm.ExecuteMacro(capturedMacro));
            map[(spec.Key, spec.Modifiers)] = (null, cmd, null);
        }

        foreach (var action in ShortcutRegistry.All)
        {
            var (spec, _) = ShortcutBinder.ResolveBinding(settings, action);
            if (spec is null) continue;
            if (macroKeys.Contains((spec.Key, spec.Modifiers))) continue;

            var cmd = action.ResolveCommand(this);
            if (cmd is null) continue;

            map[(spec.Key, spec.Modifiers)] = (action, cmd, action.CommandParameter);
        }

        return map;
    }

    private void ApplyShortcuts(AppSettings settings)
    {
        ShortcutBinder.ApplyTo(this, settings);
        // Invalidate the dead-key fallback map so it rebuilds with the new bindings.
        _shortcutMap = null;
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

        var now = DateTime.UtcNow;
        var isDoubleClick = e.ClickCount >= 2
            || (_lastClickedPhoto == photo && (now - _lastClickTime) <= DoubleClickThreshold);

        if (isDoubleClick)
        {
            _lastClickedPhoto = null;

            if (DataContext is MainViewModel { IsGridExpanded: true } vm)
            {
                vm.SelectSinglePhoto(photo);
                vm.IsGridExpanded = false;
                e.Handled = true;
                return;
            }

            if (photo.CollapsedBurstCount > 0)
            {
                OpenBurstFocus(photo);
                e.Handled = true;
            }

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

        ApplyShortcuts(AppSettings.Current);

        if (DataContext is not MainViewModel vm) return;

        vm.NotifyDateFormatChanged();

        bool burstSettingsChanged =
            prev.BurstMaxGapSeconds != AppSettings.Current.BurstMaxGapSeconds ||
            prev.BurstSimilarityStrictness != AppSettings.Current.BurstSimilarityStrictness ||
            prev.BurstThumbnailMode != AppSettings.Current.BurstThumbnailMode ||
            prev.HdrDetectionEnabled != AppSettings.Current.HdrDetectionEnabled ||
            prev.HdrMinBracketSize != AppSettings.Current.HdrMinBracketSize ||
            Math.Abs(prev.HdrMinExposureSpread - AppSettings.Current.HdrMinExposureSpread) > 0.001f ||
            prev.PanoramaDetectionEnabled != AppSettings.Current.PanoramaDetectionEnabled ||
            prev.PanoramaMinChainSize != AppSettings.Current.PanoramaMinChainSize ||
            prev.PanoramaMaxGapSeconds != AppSettings.Current.PanoramaMaxGapSeconds ||
            prev.PanoramaMinOverlapPct != AppSettings.Current.PanoramaMinOverlapPct ||
            prev.PanoramaMaxOverlapPct != AppSettings.Current.PanoramaMaxOverlapPct ||
            prev.PanoramaDirectionToleranceDeg != AppSettings.Current.PanoramaDirectionToleranceDeg;

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
        _videoTick.Stop();
        _videoSliderIsDragging = false;

        var vm = DataContext as MainViewModel;
        if (vm?.VideoSourceUri == null)
        {
            _videoIsPlaying = false;
            SetPlayPauseGlyph(playing: false);
            _pendingVideoSource = null;
            _vlcPlayer?.Stop();
            _videoDuration = TimeSpan.Zero;
            _videoSuppressSliderEvent = true;
            VideoSlider.Maximum = 1;
            VideoSlider.Value = 0;
            _videoSuppressSliderEvent = false;
            VideoTimeText.Text = "0:00 / 0:00";
        }
        else if (_libVlc != null && _vlcPlayer != null)
        {
            _videoIsPlaying = true;
            SetPlayPauseGlyph(playing: true);
            _pendingVideoSource = vm.VideoSourceUri;
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(PlayPendingVideoSource));
        }
    }

    private void AttachVlcPlayerToView(bool force = false)
    {
        if (_vlcPlayer == null) return;
        if (force && ReferenceEquals(VideoPlayer.MediaPlayer, _vlcPlayer))
            VideoPlayer.MediaPlayer = null;
        if (!ReferenceEquals(VideoPlayer.MediaPlayer, _vlcPlayer))
            VideoPlayer.MediaPlayer = _vlcPlayer;
    }

    private void PlayPendingVideoSource()
    {
        var source = _pendingVideoSource;
        if (source == null || _libVlc == null || _vlcPlayer == null) return;
        if (DataContext is not MainViewModel vm || vm.VideoSourceUri != source) return;

        AttachVlcPlayerToView(force: true);
        VideoPlayer.UpdateLayout();

        using var media = new Media(_libVlc, source);
        _vlcPlayer.Mute = _videoIsMuted;
        _vlcPlayer.Play(media);
        _videoTick.Start();
    }

    // Fires on VLC background thread when playback starts (including on initial load).
    private void VlcPlayer_Playing(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (DataContext is not MainViewModel vm || vm.VideoSourceUri == null) return;
            if (_vlcPlayer != null) _vlcPlayer.Mute = _videoIsMuted;
            _videoIsPlaying = true;
            _videoTick.Start();
            SetPlayPauseGlyph(playing: true);
        });
    }

    // Fires on VLC background thread when the stream duration is known (may fire after Playing).
    private void VlcPlayer_LengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _videoDuration = TimeSpan.FromMilliseconds(e.Length);
            _videoSuppressSliderEvent = true;
            VideoSlider.Maximum = Math.Max(0.1, _videoDuration.TotalSeconds);
            VideoSlider.Value = 0;
            _videoSuppressSliderEvent = false;
            UpdateVideoTimeText(TimeSpan.Zero);
        });
    }

    // Fires on VLC background thread. Stop() must not be called directly from within a VLC event handler.
    private void VlcPlayer_EndReached(object? sender, EventArgs e)
    {
        Task.Run(() => _vlcPlayer?.Stop());
        Dispatcher.BeginInvoke(() =>
        {
            _videoIsPlaying = false;
            _videoTick.Stop();
            SetPlayPauseGlyph(playing: false);
            _videoSuppressSliderEvent = true;
            VideoSlider.Value = 0;
            _videoSuppressSliderEvent = false;
            UpdateVideoTimeText(TimeSpan.Zero);
        });
    }

    private void VlcPlayer_EncounteredError(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _videoTick.Stop();
            _videoIsPlaying = false;
            SetPlayPauseGlyph(playing: false);
            VideoTimeText.Text = "Failed to open video";
        });
    }

    private void VideoPlayPause_Click(object sender, RoutedEventArgs e) => ToggleVideoPlayPause();

    private void ToggleVideoPlayPause()
    {
        if (DataContext is not MainViewModel vm || vm.VideoSourceUri == null) return;
        if (_vlcPlayer == null || _libVlc == null) return;

        if (_videoIsPlaying)
        {
            _vlcPlayer.SetPause(true);
            _videoIsPlaying = false;
            _videoTick.Stop();
        }
        else
        {
            _videoIsPlaying = true; // set before Play so VlcPlayer_Playing won't auto-pause
            if (_vlcPlayer.State == VLCState.Paused)
            {
                _vlcPlayer.SetPause(false);
            }
            else
            {
                // Stopped or ended — reload from beginning
                AttachVlcPlayerToView();
                using var media = new Media(_libVlc, vm.VideoSourceUri);
                _vlcPlayer.Play(media);
            }
            _videoTick.Start();
        }
        SetPlayPauseGlyph(_videoIsPlaying);
    }

    private void VideoMute_Click(object sender, RoutedEventArgs e)
    {
        _videoIsMuted = !_videoIsMuted;
        if (_vlcPlayer != null) _vlcPlayer.Mute = _videoIsMuted;
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
        SeekToSlider();
    }

    private void SeekToSlider()
    {
        if (DataContext is not MainViewModel vm || vm.VideoSourceUri == null) return;
        if (_vlcPlayer == null) return;
        _vlcPlayer.Time = (long)(VideoSlider.Value * 1000);
        UpdateVideoTimeText(TimeSpan.FromSeconds(VideoSlider.Value));
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
        if (_videoSliderIsDragging || _vlcPlayer == null) return;
        var pos = TimeSpan.FromMilliseconds(Math.Max(0, _vlcPlayer.Time));
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
