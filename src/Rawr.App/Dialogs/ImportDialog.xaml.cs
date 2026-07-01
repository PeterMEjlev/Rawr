using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Rawr.App.Services;
using Rawr.Core.Models;
using Rawr.Core.Services;
using Rawr.Raw;

namespace Rawr.App.Dialogs;

public partial class ImportDialog : Window
{
    public enum MediaCategory { Raw, Jpeg, Video }

    public sealed class ImportFile : INotifyPropertyChanged
    {
        public string FullPath { get; }
        public string RelativePath { get; }
        public string FileName { get; }
        public long Size { get; }
        public DateTime Modified { get; }
        public string Kind { get; }
        public bool IsVideo { get; }
        public MediaCategory Category { get; }
        public string SizeText => FormatSize(Size);
        public string ModifiedText => Modified.ToString("yyyy-MM-dd HH:mm");

        private bool _isSelected = true;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                OnChanged();
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        // Set when the file's category is routed to "Skip". Forces it out of the
        // selection and disables/dims it so the user can see what won't import.
        private bool _isExcluded;
        public bool IsExcluded
        {
            get => _isExcluded;
            set
            {
                if (_isExcluded == value) return;
                _isExcluded = value;
                OnChanged();
                OnChanged(nameof(IsImportable));
            }
        }
        public bool IsImportable => !_isExcluded;

        private BitmapSource? _thumbnail;
        public BitmapSource? Thumbnail
        {
            get => _thumbnail;
            set
            {
                if (ReferenceEquals(_thumbnail, value)) return;
                _thumbnail = value;
                OnChanged();
            }
        }

        public event EventHandler? SelectionChanged;
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));

        public ImportFile(string fullPath, string root)
        {
            FullPath = fullPath;
            FileName = Path.GetFileName(fullPath);
            var rel = Path.GetRelativePath(root, fullPath);
            RelativePath = string.IsNullOrEmpty(rel) ? FileName : rel;
            try
            {
                var fi = new FileInfo(fullPath);
                Size = fi.Length;
                Modified = fi.LastWriteTime;
            }
            catch { Size = 0; Modified = DateTime.MinValue; }
            IsVideo = FolderScanner.IsVideo(fullPath);
            Category = IsVideo ? MediaCategory.Video
                     : FolderScanner.IsRaw(fullPath) ? MediaCategory.Raw
                     : MediaCategory.Jpeg;
            Kind = IsVideo ? "Video" : "Photo";
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            double v = bytes;
            string[] units = { "KB", "MB", "GB", "TB" };
            int i = -1;
            do { v /= 1024; i++; } while (v >= 1024 && i < units.Length - 1);
            return $"{v:0.#} {units[i]}";
        }
    }

    public ObservableCollection<ImportFile> Files { get; } = new();

    /// <summary>Drive letter of the source card (e.g. "E"), so caller can eject.</summary>
    public string? SourceDriveLetter { get; private set; }
    public string Destination => DestinationBox.Text.Trim();
    public bool EjectAfter => EjectAfterCheck.IsChecked == true;
    public bool ImportSucceeded { get; private set; }
    public ImportResult? Result { get; private set; }

    private string _root;
    private bool _suppressSelectAllSync;
    private bool _suppressRuleSync;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _thumbnailCts;
    private int _anchorIndex = -1;
    // True only while a copy is actually running. Used to block the window from
    // closing mid-copy without also blocking the normal close after a successful
    // import (the button stays disabled on success, so it can't be the guard).
    private bool _importInFlight;

    public static readonly DependencyProperty TileSizeProperty =
        DependencyProperty.Register(nameof(TileSize), typeof(double), typeof(ImportDialog),
            new PropertyMetadata(170.0));

    public double TileSize
    {
        get => (double)GetValue(TileSizeProperty);
        set => SetValue(TileSizeProperty, value);
    }

    private const double MinTileSize = 90;
    private const double MaxTileSize = 360;
    // Border Margin in the grid tile template (all four sides). Used to derive
    // the tile's outer size when mapping scroll offsets back to item indices.
    private const double TileMargin = 6;
    private static readonly IPreviewExtractor _rawExtractor = CreateRawExtractor();
    private static readonly ShellThumbnailExtractor _shellExtractor = new();
    private const int ThumbnailPx = 256;

    // Approximate item index at the centre of the grid viewport, updated on
    // scroll/zoom. The thumbnail loader reads this to prioritise tiles the user
    // is currently looking at. volatile: written on the UI thread, read on the
    // loader thread; int reads/writes are atomic and a slightly stale value only
    // means a marginally sub-optimal load order.
    private volatile int _viewportCenterIndex;

    private static IPreviewExtractor CreateRawExtractor()
    {
        try
        {
            var lr = new LibRawExtractor();
            if (lr.IsAvailable) return lr;
        }
        catch { /* fall through */ }
        try { return new WicExtractor(); }
        catch { return new ShellThumbnailExtractor(); }
    }

    public ImportDialog(MediaCardWatcher.MediaCard card, string defaultDestination, string? treeRootFolder = null)
    {
        InitializeComponent();
        WindowHelper.ApplyDarkTitleBar(this);

        SourceDriveLetter = card.DriveLetter;
        _root = card.DcimPath;

        SourceText.Text = $"{card.DriveLetter}:\\  ·  {card.VolumeLabel}  ·  DCIM";
        DestinationBox.Text = defaultDestination ?? "";

        FileList.ItemsSource = Files;
        FileGrid.ItemsSource = Files;
        _suppressSelectAllSync = true;
        SelectAllCheck.IsChecked = true;
        _suppressSelectAllSync = false;

        BuildDestinationTree(treeRootFolder);
        InitRuleControls();

        Loaded += async (_, _) => await PopulateAsync();
        Closing += (_, e) =>
        {
            // Block close only while a copy is genuinely running; the Stop button
            // aborts it. A completed import closes normally (DialogResult = true).
            if (_importInFlight)
            {
                e.Cancel = true;
                return;
            }
            _thumbnailCts?.Cancel();
        };
    }

    private async Task PopulateAsync()
    {
        StatusText.Text = "Scanning…";
        var root = _root;
        var files = await Task.Run(() =>
        {
            var list = FolderScanner.ScanRecursive(root);
            return list;
        });

        foreach (var f in files)
        {
            var item = new ImportFile(f, _root);
            item.SelectionChanged += (_, _) => UpdateSummary();
            Files.Add(item);
        }
        ApplyExclusions();
        StatusText.Text = "";

        StartThumbnailLoader();
    }

    private void StartThumbnailLoader()
    {
        _thumbnailCts?.Cancel();
        _thumbnailCts = new CancellationTokenSource();
        var ct = _thumbnailCts.Token;
        var dispatcher = Dispatcher;

        // Snapshot each file with its position in the grid (== its index in Files,
        // since the WrapPanel lays items out in collection order). We hand work out
        // nearest-to-viewport first rather than top-down, so the tiles the user is
        // looking at fill in before off-screen ones.
        var pending = Files.Select((file, index) => (file, index)).ToList();
        var parallelism = AppSettings.CappedParallelism(Math.Max(2, Environment.ProcessorCount / 2));

        _ = Task.Run(async () =>
        {
            using var sem = new SemaphoreSlim(parallelism);
            var inFlight = new List<Task>();
            try
            {
                while (pending.Count > 0 && !ct.IsCancellationRequested)
                {
                    await sem.WaitAsync(ct).ConfigureAwait(false);

                    // Re-read the viewport centre on every hand-off so a scroll mid-load
                    // immediately re-targets the queue at whatever is now on screen.
                    int center = _viewportCenterIndex;
                    int bestPos = 0, bestDist = int.MaxValue;
                    for (int i = 0; i < pending.Count; i++)
                    {
                        int d = Math.Abs(pending[i].index - center);
                        if (d < bestDist) { bestDist = d; bestPos = i; }
                    }
                    var file = pending[bestPos].file;
                    // Swap-remove: O(1), and remaining order is irrelevant (we rescan).
                    pending[bestPos] = pending[^1];
                    pending.RemoveAt(pending.Count - 1);

                    inFlight.Add(Task.Run(() =>
                    {
                        try
                        {
                            if (ct.IsCancellationRequested) return;
                            var bmp = LoadThumbnail(file);
                            if (bmp != null)
                                _ = dispatcher.InvokeAsync(() => file.Thumbnail = bmp);
                        }
                        catch { /* per-file failure is non-fatal */ }
                        finally { sem.Release(); }
                    }, ct));
                }
                await Task.WhenAll(inFlight);
            }
            catch (OperationCanceledException) { }
        }, ct);
    }

    // Maps the grid's current scroll position to an approximate item index at the
    // centre of the viewport, so the thumbnail loader can prioritise visible tiles.
    // Tiles are uniform (fixed TileSize + margins) and the WrapPanel is not
    // virtualizing, so the grid scrolls by pixel and the geometry is exact enough.
    private void FileGrid_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        int count = Files.Count;
        if (count == 0) return;
        double tileOuter = TileSize + TileMargin * 2;
        if (tileOuter <= 0 || e.ViewportWidth <= 0) return;

        int cols = Math.Max(1, (int)(e.ViewportWidth / tileOuter));
        double centerY = e.VerticalOffset + e.ViewportHeight / 2.0;
        int centerRow = Math.Max(0, (int)(centerY / tileOuter));
        int idx = centerRow * cols + cols / 2;
        _viewportCenterIndex = Math.Clamp(idx, 0, count - 1);
    }

    private static BitmapSource? LoadThumbnail(ImportFile file)
    {
        var ext = Path.GetExtension(file.FullPath).ToLowerInvariant();
        bool isRaw = FolderScanner.RawExtensions.Contains(ext);

        byte[]? bytes = null;
        if (isRaw)
        {
            try { bytes = _rawExtractor.ExtractThumbnail(file.FullPath); } catch { }
        }
        // Fall back to the Windows shell — handles JPEG, video, and most RAW
        // codecs the OS knows about (e.g. via the Raw Image Extension).
        if (bytes == null)
        {
            try { bytes = _shellExtractor.ExtractThumbnail(file.FullPath); } catch { }
        }
        if (bytes == null) return null;

        try
        {
            using var ms = new MemoryStream(bytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = ThumbnailPx;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    private void UpdateSummary()
    {
        int sel = 0;
        long bytes = 0;
        foreach (var f in Files)
        {
            if (!f.IsSelected) continue;
            sel++;
            bytes += f.Size;
        }
        SelectionSummary.Text = sel == 0
            ? $"0 of {Files.Count} selected"
            : $"{sel} of {Files.Count} selected · {FormatSize(bytes)}";
        ImportButton.IsEnabled = sel > 0;

        // Keep the select-all checkbox state coherent without re-firing its handler.
        _suppressSelectAllSync = true;
        SelectAllCheck.IsChecked = sel == 0 ? false : sel == Files.Count ? true : (bool?)null;
        _suppressSelectAllSync = false;
    }

    private void SelectAll_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressSelectAllSync) return;
        var target = SelectAllCheck.IsChecked == true;
        foreach (var f in Files)
        {
            if (f.IsExcluded) continue;
            f.IsSelected = target;
        }
        UpdateSummary();
    }

    private void Tile_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.FrameworkElement el || el.DataContext is not ImportFile f) return;
        if (f.IsExcluded) { e.Handled = true; return; }
        var clickedIndex = Files.IndexOf(f);
        if (clickedIndex < 0) return;

        var modifiers = System.Windows.Input.Keyboard.Modifiers;
        if (modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift) && _anchorIndex >= 0)
        {
            // Range select: set every item between anchor and click to selected.
            // Matches the main grid's shift-click semantics.
            int lo = Math.Min(_anchorIndex, clickedIndex);
            int hi = Math.Max(_anchorIndex, clickedIndex);
            for (int i = lo; i <= hi; i++)
            {
                if (!Files[i].IsExcluded) Files[i].IsSelected = true;
            }
        }
        else
        {
            f.IsSelected = !f.IsSelected;
        }
        _anchorIndex = clickedIndex;
        e.Handled = true;
    }

    private void FileGrid_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (!System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control)) return;
        // Step proportional to current size so zoom feels even across the range.
        var step = Math.Max(10, TileSize * 0.12);
        var next = TileSize + (e.Delta > 0 ? step : -step);
        TileSize = Math.Round(Math.Clamp(next, MinTileSize, MaxTileSize));
        e.Handled = true;
    }

    private void ViewMode_Changed(object sender, RoutedEventArgs e)
    {
        // RadioButtons fire before the XAML they reference is fully constructed
        // on first parse; guard against that.
        if (FileList == null || FileGrid == null) return;
        var grid = ViewModeGrid.IsChecked == true;
        FileList.Visibility = grid ? Visibility.Collapsed : Visibility.Visible;
        FileGrid.Visibility = grid ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Choose import destination",
            InitialDirectory = Directory.Exists(DestinationBox.Text) ? DestinationBox.Text : ""
        };
        if (dlg.ShowDialog(this) == true && !string.IsNullOrEmpty(dlg.FolderName))
            DestinationBox.Text = dlg.FolderName;
    }

    private async void BrowseSource_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Choose import source folder",
            InitialDirectory = Directory.Exists(_root) ? _root : ""
        };
        if (dlg.ShowDialog(this) != true || string.IsNullOrEmpty(dlg.FolderName)) return;

        _root = dlg.FolderName;
        SourceDriveLetter = null;
        SourceText.Text = dlg.FolderName;
        EjectAfterCheck.Visibility = Visibility.Collapsed;

        _thumbnailCts?.Cancel();
        Files.Clear();
        await PopulateAsync();
    }

    // ── Per-type organize rules ──

    // The mode combo holds only the two "organize on" choices; opting in/out is the
    // per-type checkbox. Combo index 0 = Subfolder, 1 = Skip.
    private static int OnModeToComboIndex(ImportRouteMode m) => m == ImportRouteMode.Skip ? 1 : 0;
    private static ImportRouteMode ComboIndexToOnMode(int i) => i == 1 ? ImportRouteMode.Skip : ImportRouteMode.Subfolder;

    // Effective routing for a type: MainFolder (flat) when its checkbox is off,
    // otherwise whatever the mode combo says. This is the single source of truth
    // for routing, exclusion, and persistence.
    private static ImportRouteMode EffectiveMode(CheckBox check, ComboBox combo) =>
        check.IsChecked == true ? ComboIndexToOnMode(combo.SelectedIndex) : ImportRouteMode.MainFolder;

    private void InitRuleControls()
    {
        _suppressRuleSync = true;
        try
        {
            var s = AppSettings.Current;
            var raw = s.ImportRawRule ?? new ImportTypeRule { Subfolder = "RAW" };
            var jpg = s.ImportJpegRule ?? new ImportTypeRule { Subfolder = "JPEG" };
            var vid = s.ImportVideoRule ?? new ImportTypeRule { Subfolder = "Video" };

            RawOrganizeCheck.IsChecked = raw.Mode != ImportRouteMode.MainFolder;
            JpegOrganizeCheck.IsChecked = jpg.Mode != ImportRouteMode.MainFolder;
            VideoOrganizeCheck.IsChecked = vid.Mode != ImportRouteMode.MainFolder;

            RawModeCombo.SelectedIndex = OnModeToComboIndex(raw.Mode);
            JpegModeCombo.SelectedIndex = OnModeToComboIndex(jpg.Mode);
            VideoModeCombo.SelectedIndex = OnModeToComboIndex(vid.Mode);

            RawSubfolderBox.Text = string.IsNullOrWhiteSpace(raw.Subfolder) ? "RAW" : raw.Subfolder;
            JpegSubfolderBox.Text = string.IsNullOrWhiteSpace(jpg.Subfolder) ? "JPEG" : jpg.Subfolder;
            VideoSubfolderBox.Text = string.IsNullOrWhiteSpace(vid.Subfolder) ? "Video" : vid.Subfolder;
        }
        finally { _suppressRuleSync = false; }
        UpdateOrganizeRowStates();
    }

    private void RuleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressRuleSync) return;
        if (RawModeCombo == null) return; // fired during XAML parse
        UpdateOrganizeRowStates();
        ApplyExclusions();
    }

    private void OrganizeCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressRuleSync) return;
        if (RawOrganizeCheck == null) return; // fired during XAML parse
        UpdateOrganizeRowStates();
        ApplyExclusions();
    }

    // Greys out (and disables) each type's mode/name controls until its checkbox
    // is ticked, so it's obvious at a glance which types are being organized. The
    // subfolder box only matters when routing into a subfolder, not for Skip.
    private void UpdateOrganizeRowStates()
    {
        if (RawOrganizeCheck == null || RawModeCombo == null) return;
        UpdateOrganizeRow(RawOrganizeCheck, RawModeCombo, RawSubfolderBox);
        UpdateOrganizeRow(JpegOrganizeCheck, JpegModeCombo, JpegSubfolderBox);
        UpdateOrganizeRow(VideoOrganizeCheck, VideoModeCombo, VideoSubfolderBox);
    }

    private static void UpdateOrganizeRow(CheckBox check, ComboBox combo, TextBox box)
    {
        bool on = check.IsChecked == true;
        combo.IsEnabled = on;
        combo.Opacity = on ? 1.0 : 0.45;
        bool needsName = on && combo.SelectedIndex == 0; // 0 = Subfolder
        box.IsEnabled = needsName;
        box.Opacity = needsName ? 1.0 : 0.45;
    }

    private (ImportRouteMode mode, string subfolder) RuleFor(MediaCategory cat) => cat switch
    {
        MediaCategory.Raw => (EffectiveMode(RawOrganizeCheck, RawModeCombo), RawSubfolderBox.Text),
        MediaCategory.Jpeg => (EffectiveMode(JpegOrganizeCheck, JpegModeCombo), JpegSubfolderBox.Text),
        _ => (EffectiveMode(VideoOrganizeCheck, VideoModeCombo), VideoSubfolderBox.Text),
    };

    // Marks files whose category is set to "Skip" as excluded (and clears their
    // selection so they can't be imported). Other files keep their selection.
    private void ApplyExclusions()
    {
        foreach (var f in Files)
        {
            var (mode, _) = RuleFor(f.Category);
            bool excluded = mode == ImportRouteMode.Skip;
            f.IsExcluded = excluded;
            if (excluded) f.IsSelected = false;
        }
        UpdateSummary();
    }

    private static string SanitizeSegment(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c.ToString(), "");
        return name.Trim().TrimEnd('.', ' ');
    }

    // Snapshots the current rules into a thread-safe closure so routing stays
    // consistent for the whole batch even though the copy runs off the UI
    // thread. Skip is handled by exclusion earlier, so it falls back to the
    // main folder here.
    private Func<string, string> BuildTargetResolver(string dest)
    {
        var rawMode = EffectiveMode(RawOrganizeCheck, RawModeCombo);
        var rawSub = SanitizeSegment(RawSubfolderBox.Text);
        var jpgMode = EffectiveMode(JpegOrganizeCheck, JpegModeCombo);
        var jpgSub = SanitizeSegment(JpegSubfolderBox.Text);
        var vidMode = EffectiveMode(VideoOrganizeCheck, VideoModeCombo);
        var vidSub = SanitizeSegment(VideoSubfolderBox.Text);

        return src =>
        {
            ImportRouteMode mode;
            string sub;
            if (FolderScanner.IsVideo(src)) { mode = vidMode; sub = vidSub; }
            else if (FolderScanner.IsRaw(src)) { mode = rawMode; sub = rawSub; }
            else { mode = jpgMode; sub = jpgSub; }

            if (mode == ImportRouteMode.Subfolder && sub.Length > 0)
                return Path.Combine(dest, sub);
            return dest;
        };
    }

    private void CommitRulesToSettings()
    {
        var s = AppSettings.Current;
        s.ImportRawRule = new ImportTypeRule
        {
            Mode = EffectiveMode(RawOrganizeCheck, RawModeCombo),
            Subfolder = SanitizeSegment(RawSubfolderBox.Text),
        };
        s.ImportJpegRule = new ImportTypeRule
        {
            Mode = EffectiveMode(JpegOrganizeCheck, JpegModeCombo),
            Subfolder = SanitizeSegment(JpegSubfolderBox.Text),
        };
        s.ImportVideoRule = new ImportTypeRule
        {
            Mode = EffectiveMode(VideoOrganizeCheck, VideoModeCombo),
            Subfolder = SanitizeSegment(VideoSubfolderBox.Text),
        };
    }

    // ── Destination folder tree ──

    private bool _suppressTreeSync;

    private void BuildDestinationTree(string? rootFolder)
    {
        DestinationTree.Items.Clear();
        if (string.IsNullOrEmpty(rootFolder) || !Directory.Exists(rootFolder))
        {
            EmptyTreeHint.Visibility = Visibility.Visible;
            DestinationTree.Visibility = Visibility.Collapsed;
            return;
        }

        var name = Path.GetFileName(rootFolder.TrimEnd(Path.DirectorySeparatorChar));
        if (string.IsNullOrEmpty(name)) name = rootFolder;
        var root = new FolderNode(name, rootFolder) { IsExpanded = true };
        DestinationTree.Items.Add(root);

        // Initial selection: if the existing destination is the tree root or a descendant,
        // highlight it. Otherwise leave nothing selected so a free-typed path doesn't get
        // overwritten by the tree.
        TrySelectMatchingNode(root, DestinationBox.Text);
    }

    private bool TrySelectMatchingNode(FolderNode node, string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var canonNode = NormalizePath(node.FullPath);
        var canonTarget = NormalizePath(path);
        if (string.Equals(canonNode, canonTarget, StringComparison.OrdinalIgnoreCase))
        {
            _suppressTreeSync = true;
            try { node.IsSelected = true; }
            finally { _suppressTreeSync = false; }
            return true;
        }
        if (canonTarget.StartsWith(canonNode + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            node.IsExpanded = true; // forces children to load
            foreach (var child in node.Children)
            {
                if (TrySelectMatchingNode(child, path)) return true;
            }
        }
        return false;
    }

    private static string NormalizePath(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private void DestinationTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_suppressTreeSync) return;
        if (e.NewValue is FolderNode node && !string.IsNullOrEmpty(node.FullPath))
        {
            DestinationBox.Text = node.FullPath;
        }
    }

    private void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        // Parent: the selected tree node, or the typed destination, or the tree root.
        string parent;
        FolderNode? parentNode = DestinationTree.SelectedItem as FolderNode;
        if (parentNode != null && Directory.Exists(parentNode.FullPath))
            parent = parentNode.FullPath;
        else if (!string.IsNullOrWhiteSpace(DestinationBox.Text) && Directory.Exists(DestinationBox.Text))
            parent = DestinationBox.Text;
        else if (DestinationTree.Items.Count > 0 && DestinationTree.Items[0] is FolderNode rootNode)
        {
            parent = rootNode.FullPath;
            parentNode = rootNode;
        }
        else
        {
            MessageBox.Show(this,
                "Select a folder in the tree (or use Browse) before creating a subfolder.",
                "New folder", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var defaultName = DateTime.Now.ToString("yyyy-MM-dd");
        var name = InputDialog.Show(this, "New folder", $"Create a new subfolder under:\n{parent}", defaultName);
        if (string.IsNullOrWhiteSpace(name)) return;

        // Strip characters that are invalid in a single segment.
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c.ToString(), "");
        name = name.Trim();
        if (string.IsNullOrEmpty(name)) return;

        var newPath = Path.Combine(parent, name);
        try { Directory.CreateDirectory(newPath); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't create folder:\n{ex.Message}",
                "New folder", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        DestinationBox.Text = newPath;
        if (parentNode != null)
        {
            parentNode.RefreshChildren();
            // Try to select the new node.
            foreach (var child in parentNode.Children)
            {
                if (string.Equals(NormalizePath(child.FullPath), NormalizePath(newPath), StringComparison.OrdinalIgnoreCase))
                {
                    child.IsSelected = true;
                    break;
                }
            }
        }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dest = Destination;
        if (string.IsNullOrWhiteSpace(dest))
        {
            MessageBox.Show(this, "Please choose a destination folder.", "Import", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try { Directory.CreateDirectory(dest); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't create destination:\n{ex.Message}", "Import", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var selected = Files.Where(f => f.IsSelected && !f.IsExcluded).Select(f => f.FullPath).ToList();
        if (selected.Count == 0) return;

        // Stop loading thumbnails so no lingering read handles keep the card
        // volume locked when we try to eject it after the copy.
        _thumbnailCts?.Cancel();

        _importInFlight = true;
        ImportButton.IsEnabled = false;
        DestinationBox.IsEnabled = false;
        SelectAllCheck.IsEnabled = false;
        EjectAfterCheck.IsEnabled = false;
        FileList.IsEnabled = false;
        OrganizeCard.IsEnabled = false;
        ProgressBar.Visibility = Visibility.Visible;
        CancelButton.Content = "Stop";

        _cts = new CancellationTokenSource();
        var progress = new Progress<ImportProgress>(p =>
        {
            ProgressBar.Value = p.FilesTotal == 0 ? 0 : (double)p.FilesCompleted / p.FilesTotal;
            StatusText.Text = string.IsNullOrEmpty(p.CurrentFile)
                ? $"Copied {p.FilesCompleted} / {p.FilesTotal}"
                : $"Copying ({p.FilesCompleted + 1} / {p.FilesTotal}): {p.CurrentFile}";
        });

        try
        {
            var resolver = BuildTargetResolver(dest);
            Result = await ImportService.CopyAsync(
                selected, dest, resolver, progress, _cts.Token);
            CommitRulesToSettings();
            ImportSucceeded = true;
            _importInFlight = false;
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            _importInFlight = false;
            StatusText.Text = "Cancelled.";
            ImportButton.IsEnabled = true;
            DestinationBox.IsEnabled = true;
            SelectAllCheck.IsEnabled = true;
            EjectAfterCheck.IsEnabled = true;
            FileList.IsEnabled = true;
            OrganizeCard.IsEnabled = true;
            CancelButton.Content = "Cancel";
            ProgressBar.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            _importInFlight = false;
            MessageBox.Show(this, $"Import failed:\n{ex.Message}", "Import", MessageBoxButton.OK, MessageBoxImage.Error);
            ImportButton.IsEnabled = true;
            DestinationBox.IsEnabled = true;
            SelectAllCheck.IsEnabled = true;
            EjectAfterCheck.IsEnabled = true;
            FileList.IsEnabled = true;
            OrganizeCard.IsEnabled = true;
            CancelButton.Content = "Cancel";
            ProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is { IsCancellationRequested: false })
        {
            _cts.Cancel();
            return;
        }
        DialogResult = false;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double v = bytes;
        string[] units = { "KB", "MB", "GB", "TB" };
        int i = -1;
        do { v /= 1024; i++; } while (v >= 1024 && i < units.Length - 1);
        return $"{v:0.#} {units[i]}";
    }
}
