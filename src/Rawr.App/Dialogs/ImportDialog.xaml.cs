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
    private static readonly IPreviewExtractor _rawExtractor = CreateRawExtractor();
    private static readonly ShellThumbnailExtractor _shellExtractor = new();
    private const int ThumbnailPx = 256;

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
            // Block close while a copy is in flight; the Cancel button handles abort.
            if (_cts is { IsCancellationRequested: false } && ImportButton.IsEnabled == false)
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
        var snapshot = Files.ToList();

        _ = Task.Run(async () =>
        {
            using var sem = new SemaphoreSlim(AppSettings.CappedParallelism(Math.Max(2, Environment.ProcessorCount / 2)));
            var tasks = snapshot.Select(async file =>
            {
                if (ct.IsCancellationRequested) return;
                await sem.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (ct.IsCancellationRequested) return;
                    var bmp = LoadThumbnail(file);
                    if (bmp != null)
                    {
                        await dispatcher.InvokeAsync(() => file.Thumbnail = bmp);
                    }
                }
                catch { /* per-file failure is non-fatal */ }
                finally { sem.Release(); }
            });
            try { await Task.WhenAll(tasks); }
            catch (OperationCanceledException) { }
        }, ct);
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

    private static int ModeToIndex(ImportRouteMode m) => m switch
    {
        ImportRouteMode.Subfolder => 1,
        ImportRouteMode.Skip => 2,
        _ => 0,
    };

    private static ImportRouteMode IndexToMode(int i) => i switch
    {
        1 => ImportRouteMode.Subfolder,
        2 => ImportRouteMode.Skip,
        _ => ImportRouteMode.MainFolder,
    };

    private void InitRuleControls()
    {
        _suppressRuleSync = true;
        try
        {
            var s = AppSettings.Current;
            var raw = s.ImportRawRule ?? new ImportTypeRule { Subfolder = "RAW" };
            var jpg = s.ImportJpegRule ?? new ImportTypeRule { Subfolder = "JPEG" };
            var vid = s.ImportVideoRule ?? new ImportTypeRule { Subfolder = "Video" };

            RawModeCombo.SelectedIndex = ModeToIndex(raw.Mode);
            JpegModeCombo.SelectedIndex = ModeToIndex(jpg.Mode);
            VideoModeCombo.SelectedIndex = ModeToIndex(vid.Mode);

            RawSubfolderBox.Text = string.IsNullOrWhiteSpace(raw.Subfolder) ? "RAW" : raw.Subfolder;
            JpegSubfolderBox.Text = string.IsNullOrWhiteSpace(jpg.Subfolder) ? "JPEG" : jpg.Subfolder;
            VideoSubfolderBox.Text = string.IsNullOrWhiteSpace(vid.Subfolder) ? "Video" : vid.Subfolder;
        }
        finally { _suppressRuleSync = false; }
        UpdateSubfolderEnabled();
    }

    private void RuleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressRuleSync) return;
        if (RawModeCombo == null) return; // fired during XAML parse
        UpdateSubfolderEnabled();
        ApplyExclusions();
    }

    private void UpdateSubfolderEnabled()
    {
        if (RawSubfolderBox == null) return;
        RawSubfolderBox.IsEnabled = RawModeCombo.SelectedIndex == 1;
        JpegSubfolderBox.IsEnabled = JpegModeCombo.SelectedIndex == 1;
        VideoSubfolderBox.IsEnabled = VideoModeCombo.SelectedIndex == 1;
    }

    private (ImportRouteMode mode, string subfolder) RuleFor(MediaCategory cat) => cat switch
    {
        MediaCategory.Raw => (IndexToMode(RawModeCombo.SelectedIndex), RawSubfolderBox.Text),
        MediaCategory.Jpeg => (IndexToMode(JpegModeCombo.SelectedIndex), JpegSubfolderBox.Text),
        _ => (IndexToMode(VideoModeCombo.SelectedIndex), VideoSubfolderBox.Text),
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
        var rawMode = IndexToMode(RawModeCombo.SelectedIndex);
        var rawSub = SanitizeSegment(RawSubfolderBox.Text);
        var jpgMode = IndexToMode(JpegModeCombo.SelectedIndex);
        var jpgSub = SanitizeSegment(JpegSubfolderBox.Text);
        var vidMode = IndexToMode(VideoModeCombo.SelectedIndex);
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
            Mode = IndexToMode(RawModeCombo.SelectedIndex),
            Subfolder = SanitizeSegment(RawSubfolderBox.Text),
        };
        s.ImportJpegRule = new ImportTypeRule
        {
            Mode = IndexToMode(JpegModeCombo.SelectedIndex),
            Subfolder = SanitizeSegment(JpegSubfolderBox.Text),
        };
        s.ImportVideoRule = new ImportTypeRule
        {
            Mode = IndexToMode(VideoModeCombo.SelectedIndex),
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
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
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
