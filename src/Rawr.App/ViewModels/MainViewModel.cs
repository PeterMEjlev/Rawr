using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32;
using Rawr.App.Controls;
using Rawr.App.Dialogs;
using Rawr.App.Services;
using Rawr.Core.Data;
using Rawr.Core.Models;
using Rawr.Core.Services;
using Rawr.Raw;

namespace Rawr.App.ViewModels;

public enum SortField { FileName, Rating, CaptureDate, ColorLabel, Flag, Burst, ImageType }
public enum RatingFilterMode { Any, Exact, AtLeast, LessThan }
public enum BurstFilterMode { Any, OnlyInBursts, OnlySingles }
public enum ImageTypeFilterMode { Any, RawOnly, JpegOnly, VideoOnly }
public enum ExposureFilterMode { Any, ClippedHighlights, CrushedShadows }
public enum FaceFilterMode { Any, ClosedEyes }
public enum SidePanelView { Histogram, PixelPeek }
public enum CopySource { SelectedPhotos, CurrentView, CustomFilter }

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IPreviewExtractor _extractor;
    private readonly LibRawExtractor? _libRaw;
    private readonly WicExtractor _wicExtractor = new();
    private readonly ShellThumbnailExtractor _videoExtractor = new();
    private PreviewCache? _cache;
    private CullingDatabase? _db;
    private XmpSidecarWriter? _xmpWriter;
    private CancellationTokenSource? _indexCts;
    private CancellationTokenSource? _previewCts;
    private bool _highResPreviewLoaded;
    private PhotoItem? _metadataSubscription;

    // Per-folder "resume where I left off" — persisted to <folder>/.rawr/session.json.
    // Suppressed while a folder is being loaded so the reset/restore sequence doesn't
    // overwrite the file with transient null state.
    private string? _sessionFolder;
    private bool _suppressSessionSave;

    // Photos within this radius of the current selection keep their PreviewJpeg /
    // FullJpeg bytes in memory for instant browsing. Photos outside the window are
    // evicted on selection change to keep memory bounded.
    private const int KeepRadius = 2;

    // ── Observable state ──

    [ObservableProperty] private string _currentFolder = "";
    [ObservableProperty] private string _statusText = "Open a folder to begin (Ctrl+O)";
    [ObservableProperty] private BitmapSource? _previewImage;

    // Set when the selected item is a video. The MediaElement in the preview pane
    // binds to this; null hides the player and shows the still-image preview path.
    [ObservableProperty] private Uri? _videoSourceUri;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedPhotoCaptureDateFormatted))]
    private PhotoItem? _selectedPhoto;
    [ObservableProperty] private int _selectedIndex = -1;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _filterDescription = "All";
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _visibleCount;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GridFilenameVisibility))]
    private double _gridThumbnailSize = 90.0; // derived in code-behind from GridColumnCount

    public Visibility GridFilenameVisibility => GridThumbnailSize >= 60 ? Visibility.Visible : Visibility.Collapsed;

    [ObservableProperty] private int _gridColumnCount = 2;
    [ObservableProperty] private double _filmstripItemWidth = 140.0; // derived in code-behind from filmstrip height
    [ObservableProperty] private bool _showGrid = true;
    [ObservableProperty] private bool _showFilmstrip = true;
    [ObservableProperty] private bool _showSecondMonitor;

    public ObservableCollection<FolderNode> FolderTreeRoots { get; } = [];
    [ObservableProperty] private HistogramData? _histogramData;
    [ObservableProperty] private HistogramMode _histogramMode = HistogramMode.Rgb;

    // The right-hand panel's first card swaps between the histogram view and
    // the pixel-peep loupe; both share a single slot to avoid stealing screen
    // estate when neither is in active use.
    [ObservableProperty] private SidePanelView _sidePanelView = SidePanelView.Histogram;
    [ObservableProperty] private bool _focusPeakingEnabled;
    [ObservableProperty] private BitmapSource? _focusPeakingOverlay;
    [ObservableProperty] private bool _clippingEnabled;
    [ObservableProperty] private BitmapSource? _clippingOverlay;
    [ObservableProperty] private double _exposureCompensation = 0.0;
    [ObservableProperty] private bool _isLinearRawReady;
    [ObservableProperty] private string _exposureSourceLabel = "EV";

    public double ExposureSelectionStart => Math.Min(0.0, ExposureCompensation);
    public double ExposureSelectionEnd   => Math.Max(0.0, ExposureCompensation);

    private BitmapSource? _basePreviewImage;
    private LinearRawImage? _baseRawImage;
    private CancellationTokenSource? _exposureCts;
    private CancellationTokenSource? _rawDecodeCts;

    // Filter state
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilters))]
    [NotifyPropertyChangedFor(nameof(ActiveRatingValue))]
    [NotifyPropertyChangedFor(nameof(RatingModeLabel))]
    private RatingFilterMode _ratingFilterMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveRatingValue))]
    private int _ratingFilterValue;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RatingModeLabel))]
    private RatingFilterMode _ratingCycleMode = RatingFilterMode.Exact;

    public int ActiveRatingValue => RatingFilterMode == RatingFilterMode.Any ? -1 : RatingFilterValue;

    public string RatingModeLabel => RatingCycleMode switch
    {
        RatingFilterMode.AtLeast  => "≥",
        RatingFilterMode.LessThan => "<",
        _                         => "="
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilters))]
    private CullFlag? _flagFilter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilters))]
    private ColorLabel? _colorLabelFilter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilters))]
    private BurstFilterMode _burstFilter = BurstFilterMode.Any;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilters))]
    private ImageTypeFilterMode _imageTypeFilter = ImageTypeFilterMode.Any;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilters))]
    private ExposureFilterMode _exposureFilter = ExposureFilterMode.Any;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilters))]
    private FaceFilterMode _faceFilter = FaceFilterMode.Any;

    // Time-of-day filter: include photos whose CaptureTime falls between
    // [TimeOfDayStartMinutes, TimeOfDayEndMinutes) modulo 24h. The filter is
    // active whenever the range isn't the full day (0..1440). Both values are
    // stored as minutes since midnight (0–1440 inclusive).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilters))]
    [NotifyPropertyChangedFor(nameof(TimeOfDayStartText))]
    [NotifyPropertyChangedFor(nameof(IsTimeOfDayFilterActive))]
    private int _timeOfDayStartMinutes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilters))]
    [NotifyPropertyChangedFor(nameof(TimeOfDayEndText))]
    [NotifyPropertyChangedFor(nameof(IsTimeOfDayFilterActive))]
    private int _timeOfDayEndMinutes = 1440;

    public bool IsTimeOfDayFilterActive => TimeOfDayStartMinutes != 0 || TimeOfDayEndMinutes != 1440;

    // Geographic bounding box filter set by the Map view's rectangle selection.
    // All four bounds are set together as a single unit; null means "no region filter".
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilters))]
    [NotifyPropertyChangedFor(nameof(IsRegionFilterActive))]
    private double? _regionFilterMinLat;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilters))]
    [NotifyPropertyChangedFor(nameof(IsRegionFilterActive))]
    private double? _regionFilterMaxLat;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilters))]
    [NotifyPropertyChangedFor(nameof(IsRegionFilterActive))]
    private double? _regionFilterMinLon;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilters))]
    [NotifyPropertyChangedFor(nameof(IsRegionFilterActive))]
    private double? _regionFilterMaxLon;

    public bool IsRegionFilterActive => RegionFilterMinLat.HasValue;

    public string TimeOfDayStartText
    {
        get => FormatMinutes(TimeOfDayStartMinutes);
        set
        {
            if (TryParseMinutes(value, out var m)) TimeOfDayStartMinutes = Math.Clamp(m, 0, 1440);
            // Always re-notify so an unparseable string in the TextBox snaps back
            // to the last valid value rather than lingering as garbage on screen.
            OnPropertyChanged(nameof(TimeOfDayStartText));
        }
    }

    public string TimeOfDayEndText
    {
        get => FormatMinutes(TimeOfDayEndMinutes);
        set
        {
            if (TryParseMinutes(value, out var m)) TimeOfDayEndMinutes = Math.Clamp(m, 0, 1440);
            OnPropertyChanged(nameof(TimeOfDayEndText));
        }
    }

    private static string FormatMinutes(int totalMinutes)
    {
        var clamped = Math.Clamp(totalMinutes, 0, 1440);
        var h = clamped / 60;
        var m = clamped % 60;
        // 24:00 is preserved as the explicit "end of day" marker so the upper
        // handle reads as the user expects rather than wrapping back to 00:00.
        return $"{h:D2}:{m:D2}";
    }

    private static bool TryParseMinutes(string? text, out int minutes)
    {
        minutes = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Trim().Split(':');
        if (parts.Length == 1)
        {
            if (int.TryParse(parts[0], out var hOnly) && hOnly >= 0 && hOnly <= 24)
            {
                minutes = hOnly * 60;
                return true;
            }
            return false;
        }
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m)) return false;
        if (h < 0 || h > 24 || m < 0 || m > 59) return false;
        minutes = h * 60 + m;
        return true;
    }

    // Per-criterion polarity. When true, the criterion's predicate is negated
    // (e.g. "NOT Rejected" instead of "Rejected"). Has no effect when the
    // associated filter is in its "Any" state.
    [ObservableProperty] private bool _ratingFilterExclude;
    [ObservableProperty] private bool _flagFilterExclude;
    [ObservableProperty] private bool _colorLabelFilterExclude;
    [ObservableProperty] private bool _tagFilterExclude;
    [ObservableProperty] private bool _burstFilterExclude;
    [ObservableProperty] private bool _imageTypeFilterExclude;
    [ObservableProperty] private bool _exposureFilterExclude;
    [ObservableProperty] private bool _faceFilterExclude;
    [ObservableProperty] private bool _timeOfDayFilterExclude;
    [ObservableProperty] private bool _regionFilterExclude;

    partial void OnRatingFilterExcludeChanged(bool value)     { if (RatingFilterMode != RatingFilterMode.Any) ApplyFilter(); }
    partial void OnFlagFilterExcludeChanged(bool value)       { if (FlagFilter.HasValue)                     ApplyFilter(); }
    partial void OnColorLabelFilterExcludeChanged(bool value) { if (ColorLabelFilter.HasValue)               ApplyFilter(); }
    partial void OnTagFilterExcludeChanged(bool value)        { if (TagFilter != null)                       ApplyFilter(); }
    partial void OnBurstFilterExcludeChanged(bool value)      { if (BurstFilter != BurstFilterMode.Any)      ApplyFilter(); }
    partial void OnImageTypeFilterExcludeChanged(bool value)  { if (ImageTypeFilter != ImageTypeFilterMode.Any) ApplyFilter(); }
    partial void OnExposureFilterExcludeChanged(bool value)   { if (ExposureFilter != ExposureFilterMode.Any) ApplyFilter(); }
    partial void OnFaceFilterExcludeChanged(bool value)       { if (FaceFilter != FaceFilterMode.Any)        ApplyFilter(); }
    partial void OnTimeOfDayFilterExcludeChanged(bool value)  { if (IsTimeOfDayFilterActive)                 ApplyFilter(); }
    partial void OnRegionFilterExcludeChanged(bool value)     { if (IsRegionFilterActive)                    ApplyFilter(); }

    // While the user is dragging a slider thumb we still update the start/end
    // minute values continuously (so the labels and text boxes track the
    // motion), but skip the expensive ApplyFilter pass. The filter runs once
    // when IsTimeOfDaySliderDragging flips back to false.
    [ObservableProperty] private bool _isTimeOfDaySliderDragging;

    partial void OnTimeOfDayStartMinutesChanged(int value) { if (!IsTimeOfDaySliderDragging) ApplyFilter(); }
    partial void OnTimeOfDayEndMinutesChanged(int value)   { if (!IsTimeOfDaySliderDragging) ApplyFilter(); }
    partial void OnIsTimeOfDaySliderDraggingChanged(bool value) { if (!value) ApplyFilter(); }

    public bool HasActiveFilters => RatingFilterMode != RatingFilterMode.Any || FlagFilter.HasValue || ColorLabelFilter.HasValue || TagFilter != null || BurstFilter != BurstFilterMode.Any || ImageTypeFilter != ImageTypeFilterMode.Any || ExposureFilter != ExposureFilterMode.Any || FaceFilter != FaceFilterMode.Any || IsTimeOfDayFilterActive || IsRegionFilterActive;

    [ObservableProperty] private int _burstCount;

    // When true, FilteredPhotos shows one representative tile per burst (the
    // chronologically first matching frame); when false, every burst member is shown.
    [ObservableProperty] private bool _burstCollapsed = true;
    partial void OnBurstCollapsedChanged(bool value) => ApplyFilter();

    // ── Tags ──

    [ObservableProperty] private ObservableCollection<PhotoTag> _tags = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilters))]
    [NotifyPropertyChangedFor(nameof(SelectedPhotoTagAssignments))]
    private PhotoTag? _tagFilter;

    public IEnumerable<TagAssignmentItem> SelectedPhotoTagAssignments =>
        Tags.Select(t => new TagAssignmentItem(t, SelectedPhoto?.TagIds.Contains(t.Id) ?? false));

    public record TagAssignmentItem(PhotoTag Tag, bool IsAssigned);

    // Copy criteria state (independent of filter). Defaults to SelectedPhotos so the
    // currently highlighted photo (or multi-selection) is the source out-of-the-box —
    // the path most users want, especially after Ctrl/Shift-clicking to build a set.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CopyTargetCount))]
    private CopySource _copyMode = CopySource.SelectedPhotos;

    [RelayCommand] private void UseCopySelectedPhotos() => CopyMode = CopySource.SelectedPhotos;
    [RelayCommand] private void UseCopyCurrentView()    => CopyMode = CopySource.CurrentView;
    [RelayCommand] private void UseCopyCustomFilter()   => CopyMode = CopySource.CustomFilter;

    public int CopyTargetCount => CopyMode switch
    {
        CopySource.SelectedPhotos => SelectedPhotos.Count,
        CopySource.CurrentView    => FilteredPhotos.Count,
        _                          => 0
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CopyActiveRatingValue))]
    [NotifyPropertyChangedFor(nameof(CopyRatingModeLabel))]
    private RatingFilterMode _copyRatingFilterMode = RatingFilterMode.Any;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CopyActiveRatingValue))]
    private int _copyRatingFilterValue;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CopyRatingModeLabel))]
    private RatingFilterMode _copyRatingCycleMode = RatingFilterMode.Exact;

    [ObservableProperty] private CullFlag? _copyFlagFilter = CullFlag.Pick;
    [ObservableProperty] private ColorLabel? _copyColorLabelFilter;
    [ObservableProperty] private bool _copyRenameEnabled;
    [ObservableProperty] private string _copyCustomBaseName = string.Empty;

    // Quality slider: 0 = smallest JPEG (Email), CopyQualityPresets.FullIndex = original RAW.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CopyQualityPreset))]
    [NotifyPropertyChangedFor(nameof(CopyQualityLabel))]
    [NotifyPropertyChangedFor(nameof(CopyQualityDescription))]
    private int _copyQualityIndex = CopyQualityPresets.FullIndex;

    public CopyQualityPreset CopyQualityPreset =>
        CopyQualityPresets.All[Math.Clamp(CopyQualityIndex, 0, CopyQualityPresets.All.Length - 1)];
    public string CopyQualityLabel => CopyQualityPreset.Label;
    public string CopyQualityDescription => CopyQualityPreset.Description;

    public int CopyActiveRatingValue => CopyRatingFilterMode == RatingFilterMode.Any ? -1 : CopyRatingFilterValue;

    public string CopyRatingModeLabel => CopyRatingCycleMode switch
    {
        RatingFilterMode.AtLeast  => "≥",
        RatingFilterMode.LessThan => "<",
        _                         => "="
    };

    // Sort state
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortDirectionLabel))]
    private SortField _sortField = SortField.FileName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortDirectionLabel))]
    private bool _sortDescending;

    public string SortDirectionLabel => SortDescending ? "↓" : "↑";

    partial void OnSortFieldChanged(SortField value) => ApplyFilter();
    partial void OnSortDescendingChanged(bool value) => ApplyFilter();

    public ObservableCollection<PhotoItem> AllPhotos { get; } = [];
    public ObservableCollection<PhotoItem> FilteredPhotos { get; } = [];

    // Multi-selection set that bulk operations (rate/flag/colour/tag/copy/delete) act on.
    // SelectedPhoto is the *anchor* — the focused tile that drives preview, EXIF, and
    // arrow-key navigation; it is always also a member of SelectedPhotos when non-null.
    // _selectionAnchor stays at the last non-shift click target so subsequent shift-clicks
    // form a range from that anchor, matching Windows Explorer / Lightroom semantics.
    public ObservableCollection<PhotoItem> SelectedPhotos { get; } = [];
    private PhotoItem? _selectionAnchor;
    private bool _suspendSelectionReconcile;

    public int SelectedPhotosCount => SelectedPhotos.Count;

    public EditHistory History { get; } = new();

    public bool CanUndo => History.CanUndo;
    public bool CanRedo => History.CanRedo;
    public string UndoTooltip => History.CanUndo
        ? $"Undo: {History.UndoDescription} (Ctrl+Z)"
        : "Nothing to undo (Ctrl+Z)";
    public string RedoTooltip => History.CanRedo
        ? $"Redo: {History.RedoDescription} (Ctrl+Y)"
        : "Nothing to redo (Ctrl+Y)";

    public string ExtractorName { get; }

    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RAWR");
    private static readonly string LastFolderFile = Path.Combine(SettingsDir, "lastfolder.txt");

    public MainViewModel()
    {
        // Try LibRaw first, fall back to WIC
        var libraw = new LibRawExtractor();
        _extractor = libraw.IsAvailable ? libraw : new WicExtractor();
        _libRaw = libraw.IsAvailable ? libraw : null;
        ExtractorName = libraw.IsAvailable ? "LibRaw" : "WIC";

        History.Changed += (_, _) =>
        {
            UndoCommand.NotifyCanExecuteChanged();
            RedoCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
            OnPropertyChanged(nameof(UndoTooltip));
            OnPropertyChanged(nameof(RedoTooltip));
        };
    }

    public async Task RestoreLastFolderAsync()
    {
        try
        {
            if (!File.Exists(LastFolderFile)) return;
            var folder = (await File.ReadAllTextAsync(LastFolderFile)).Trim();
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                await OpenRootFolderAsync(folder);
        }
        catch { /* non-critical */ }
    }

    // ── Folder operations ──

    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select folder containing RAW photos"
        };

        if (dialog.ShowDialog() != true || string.IsNullOrEmpty(dialog.FolderName))
            return;

        await OpenRootFolderAsync(dialog.FolderName);
    }

    /// <summary>
    /// Establishes <paramref name="folderPath"/> as the new sidebar tree root and
    /// loads its photos. The tree is rebuilt; subfolders show up as children that
    /// the user can click to navigate into. Used by Ctrl+O and last-folder restore;
    /// in-tree navigation should call <see cref="LoadFolderAsync"/> directly so the
    /// root remains pinned to the originally-opened folder.
    /// </summary>
    public async Task OpenRootFolderAsync(string folderPath)
    {
        SetTreeRoot(folderPath);
        await LoadFolderAsync(folderPath);
        // Select the root after the load so the tree-selection handler — which
        // bails when the selected node already matches CurrentFolder — doesn't
        // kick off a redundant second load.
        if (FolderTreeRoots.Count > 0)
            FolderTreeRoots[0].IsSelected = true;
    }

    private void SetTreeRoot(string folderPath)
    {
        FolderTreeRoots.Clear();
        if (!Directory.Exists(folderPath)) return;

        var trimmed = folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        if (string.IsNullOrEmpty(name)) name = folderPath; // drive root: "C:\"

        var root = new FolderNode(name, folderPath);
        FolderTreeRoots.Add(root);
        root.IsExpanded = true; // lazy-loads children synchronously
    }

    [RelayCommand]
    private void CreateNewFolder(FolderNode? target)
    {
        // From the tree's "+" header button no node is supplied, so fall back to
        // CurrentFolder. From the per-row context menu we get the right-clicked node.
        var parent = target?.FullPath ?? CurrentFolder;
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
        {
            MessageBox.Show(
                "Open a folder first to create a new subfolder.",
                "No folder open",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var name = InputDialog.Show(Application.Current.MainWindow, "New Folder", "Folder name:");
        if (string.IsNullOrWhiteSpace(name)) return;

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            MessageBox.Show(
                "Folder name contains invalid characters.",
                "Invalid name",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var newPath = Path.Combine(parent, name);
        if (Directory.Exists(newPath))
        {
            MessageBox.Show(
                $"A folder named \"{name}\" already exists here.",
                "Already exists",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            Directory.CreateDirectory(newPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not create folder:\n{ex.Message}",
                "Create folder failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        var parentNode = FindNodeByPath(parent);
        parentNode?.RefreshChildren();
    }

    [RelayCommand]
    private async Task RenameFolderAsync(FolderNode? node)
    {
        if (node == null || node.IsPlaceholder || string.IsNullOrEmpty(node.FullPath)) return;
        if (!Directory.Exists(node.FullPath))
        {
            MessageBox.Show("Folder no longer exists.", "Rename failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var newName = InputDialog.Show(Application.Current.MainWindow, "Rename Folder", "New name:", node.Name);
        if (string.IsNullOrWhiteSpace(newName) || newName == node.Name) return;

        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            MessageBox.Show("Folder name contains invalid characters.", "Invalid name", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var parentPath = Path.GetDirectoryName(node.FullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(parentPath))
        {
            MessageBox.Show("Cannot rename a drive root.", "Rename failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var newPath = Path.Combine(parentPath, newName);
        if (Directory.Exists(newPath))
        {
            MessageBox.Show($"A folder named \"{newName}\" already exists here.", "Already exists", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // If the renamed folder (or one of its ancestors) holds the active DB / cache,
        // close it first or Directory.Move will fail with "in use".
        var renamingActive = IsSameOrAncestorOfCurrent(node.FullPath);
        if (renamingActive)
        {
            _db?.Dispose();
            _db = null;
            _cache = null;
        }

        try
        {
            Directory.Move(node.FullPath, newPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not rename folder:\n{ex.Message}", "Rename failed", MessageBoxButton.OK, MessageBoxImage.Error);
            // Best effort: if we closed the DB but the move failed, reopen it on the original path.
            if (renamingActive && Directory.Exists(node.FullPath))
                await LoadFolderAsync(node.FullPath);
            return;
        }

        // Rebuild the tree from the (possibly renamed) root and reopen if needed.
        var rootPath = FolderTreeRoots.Count > 0 ? FolderTreeRoots[0].FullPath : null;
        if (rootPath != null && IsSameOrAncestor(node.FullPath, rootPath))
        {
            // Renamed the tree root itself — its FullPath changed.
            rootPath = newPath;
        }

        if (renamingActive)
        {
            // Map the old current path into the new namespace.
            var newCurrent = newPath + CurrentFolder.Substring(node.FullPath.Length);
            await OpenRootFolderAsync(rootPath ?? newCurrent);
            if (!string.Equals(newCurrent, rootPath, StringComparison.OrdinalIgnoreCase))
                await LoadFolderAsync(newCurrent);
        }
        else if (rootPath != null)
        {
            // Just refresh the parent in the tree so the rename is reflected.
            FindNodeByPath(parentPath)?.RefreshChildren();
        }
    }

    [RelayCommand]
    private async Task DeleteFolderAsync(FolderNode? node)
    {
        if (node == null || node.IsPlaceholder || string.IsNullOrEmpty(node.FullPath)) return;
        if (!Directory.Exists(node.FullPath))
        {
            // Already gone — just clean up the tree.
            FindNodeByPath(Path.GetDirectoryName(node.FullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? "")?.RefreshChildren();
            return;
        }

        var confirm = MessageBox.Show(
            $"Move \"{node.Name}\" to the Recycle Bin?\n\n{node.FullPath}",
            "Delete Folder",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        var deletingActive = IsSameOrAncestorOfCurrent(node.FullPath);
        if (deletingActive)
        {
            _db?.Dispose();
            _db = null;
            _cache = null;
            AllPhotos.Clear();
            FilteredPhotos.Clear();
            Tags.Clear();
            PreviewImage = null;
            VideoSourceUri = null;
            SelectedPhoto = null;
            SelectedIndex = -1;
            CurrentFolder = "";
        }

        try
        {
            FileSystem.DeleteDirectory(node.FullPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not delete folder:\n{ex.Message}", "Delete failed", MessageBoxButton.OK, MessageBoxImage.Error);
            // Reopen the active folder if we closed it preemptively.
            if (deletingActive && Directory.Exists(node.FullPath))
                await LoadFolderAsync(node.FullPath);
            return;
        }

        var parentPath = Path.GetDirectoryName(node.FullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var rootPath = FolderTreeRoots.Count > 0 ? FolderTreeRoots[0].FullPath : null;

        if (rootPath != null && IsSameOrAncestor(node.FullPath, rootPath))
        {
            // The tree root itself was deleted. Drop the tree; user must Ctrl+O again.
            FolderTreeRoots.Clear();
            StatusText = "Folder deleted. Open a folder to begin (Ctrl+O)";
            return;
        }

        if (!string.IsNullOrEmpty(parentPath))
            FindNodeByPath(parentPath)?.RefreshChildren();
    }

    [RelayCommand]
    private void CopyFolderPath(FolderNode? node)
    {
        if (node == null || node.IsPlaceholder || string.IsNullOrEmpty(node.FullPath)) return;
        try { Clipboard.SetText(node.FullPath); }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not copy to clipboard:\n{ex.Message}", "Copy failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void OpenFolderInExplorer(FolderNode? node)
    {
        if (node == null || node.IsPlaceholder || string.IsNullOrEmpty(node.FullPath)) return;
        if (!Directory.Exists(node.FullPath))
        {
            MessageBox.Show("Folder no longer exists.", "Open failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = node.FullPath,
                UseShellExecute = true,
                Verb = "open"
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open folder:\n{ex.Message}", "Open failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool IsSameOrAncestorOfCurrent(string path) =>
        !string.IsNullOrEmpty(CurrentFolder) && IsSameOrAncestor(path, CurrentFolder);

    private static bool IsSameOrAncestor(string ancestor, string descendant)
    {
        var a = ancestor.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var d = descendant.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(a, d, StringComparison.OrdinalIgnoreCase)) return true;
        return d.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private FolderNode? FindNodeByPath(string path)
    {
        foreach (var root in FolderTreeRoots)
        {
            var hit = FindNode(root, path);
            if (hit != null) return hit;
        }
        return null;

        static FolderNode? FindNode(FolderNode node, string path)
        {
            if (string.Equals(node.FullPath, path, StringComparison.OrdinalIgnoreCase)) return node;
            foreach (var child in node.Children)
            {
                if (child.IsPlaceholder) continue;
                var hit = FindNode(child, path);
                if (hit != null) return hit;
            }
            return null;
        }
    }

    public async Task LoadFolderAsync(string folderPath)
    {
        // Cancel any in-progress indexing
        _indexCts?.Cancel();
        _indexCts = new CancellationTokenSource();
        var ct = _indexCts.Token;

        // Suppress per-folder session writes while we tear down the previous folder
        // and rebuild this one — otherwise transient state (cleared filters, null
        // selection) would clobber the saved session before we get a chance to
        // restore it. Any final save happens via SaveSessionIfNeeded after restore.
        _suppressSessionSave = true;
        _sessionFolder = null;

        IsLoading = true;
        CurrentFolder = folderPath;
        StatusText = "Scanning folder...";

        // Dispose previous session. Drain any queued XMP writes for the *previous*
        // folder first so we don't lose edits from a debounce window straddling a
        // folder switch — bounded so a slow disk can't stall the UI indefinitely.
        if (_xmpWriter != null)
        {
            _xmpWriter.Flush(TimeSpan.FromSeconds(2));
            _xmpWriter.Dispose();
            _xmpWriter = null;
        }
        _db?.Dispose();

        AllPhotos.Clear();
        FilteredPhotos.Clear();
        Tags.Clear();
        TagFilter = null;
        PreviewImage = null;
        VideoSourceUri = null;
        SelectedPhoto = null;
        SelectedIndex = -1;
        // History references PhotoItem instances that won't survive a folder switch.
        History.Clear();

        BurstCollapsed = AppSettings.Current.CollapseBurstsOnOpen;
        SortField = AppSettings.Current.DefaultSortField;

        // Scan for RAW files
        var files = await Task.Run(() => FolderScanner.Scan(folderPath), ct);
        TotalCount = files.Count;

        if (files.Count == 0)
        {
            StatusText = "No supported image files found in this folder.";
            IsLoading = false;
            return;
        }

        StatusText = $"Found {files.Count} image files. Loading...";

        // Open database and preview cache
        _db = CullingDatabase.Open(folderPath);
        _xmpWriter = new XmpSidecarWriter();
        _cache = new PreviewCache(folderPath);
        var savedState = _db.LoadAll();
        var dbPath = Path.Combine(folderPath, ".rawr", "culling.db");
        DateTime dbMtime = File.Exists(dbPath) ? File.GetLastWriteTimeUtc(dbPath) : DateTime.MinValue;

        // Create PhotoItem for each file, restoring saved culling state
        foreach (var filePath in files)
        {
            var photo = new PhotoItem { FilePath = filePath };
            var fileName = photo.FileName;

            if (savedState.TryGetValue(fileName, out var state))
            {
                photo.Rating = state.Rating;
                photo.Flag = state.Flag;
                photo.ColorLabel = state.ColorLabel;
                photo.GroupId = state.GroupId;
                photo.IsBestInGroup = state.IsBestInGroup;
                photo.Phash = state.Phash;
                photo.HighlightClippedPct = state.HighlightClippedPct;
                photo.ShadowClippedPct = state.ShadowClippedPct;
                photo.FaceCount = state.FaceCount;
                photo.ClosedEyeCount = state.ClosedEyeCount;
                photo.MinEyeOpenScore = state.MinEyeOpenScore;
            }

            AllPhotos.Add(photo);
        }

        // Load tags and photo-tag assignments
        foreach (var t in _db.LoadGroups())
            Tags.Add(t);
        var allPhotoTags = _db.LoadAllPhotoGroups();
        foreach (var photo in AllPhotos)
        {
            if (allPhotoTags.TryGetValue(photo.FileName, out var tagIds))
            {
                foreach (var id in tagIds)
                    photo.TagIds.Add(id);
            }
            UpdateTagDisplay(photo);
        }

        // Merge externally-edited XMP sidecars. A sidecar is "external" when it
        // was modified after the SQLite file (e.g. the user edited the rating in
        // Lightroom and saved metadata back), or when there's no DB row at all
        // for this photo (folder copied between machines / catalogs).
        // Read sidecars off-thread (just file I/O + XML parse), then apply to
        // the observable PhotoItems on the UI thread.
        // 5-second grace window: writes that RAWR itself made just before this
        // session started will land *after* the SQLite mtime; without the grace
        // we'd re-merge our own data on every folder open. Harmless but wasteful.
        var grace = TimeSpan.FromSeconds(5);
        var photosToScan = AllPhotos.ToList();
        var pendingMerges = await Task.Run(() =>
        {
            var list = new List<(PhotoItem photo, XmpData data)>();
            foreach (var photo in photosToScan)
            {
                if (ct.IsCancellationRequested) break;
                if (photo.IsVideo) continue;
                var sidecarPath = XmpSidecar.SidecarPathFor(photo.FilePath);
                if (!File.Exists(sidecarPath)) continue;
                var sidecarMtime = File.GetLastWriteTimeUtc(sidecarPath);
                bool noDbRow = !savedState.ContainsKey(photo.FileName);
                if (!noDbRow && sidecarMtime <= dbMtime + grace) continue;
                var data = XmpSidecar.TryRead(photo.FilePath);
                if (data != null) list.Add((photo, data));
            }
            return list;
        }, ct);

        if (pendingMerges.Count > 0)
            ApplyXmpMerges(pendingMerges);

        // Restore per-folder session (filters, sort, burst-collapse) before the
        // first ApplyFilter so the rebuilt FilteredPhotos already reflects the
        // user's previous view of this folder. Falls back to the defaults set
        // earlier if there's no session file.
        var session = FolderSession.TryLoad(folderPath);
        if (session != null)
            ApplySessionState(session);

        ApplyFilter();

        // Resume on the same photo as last time, if it's still in the filtered
        // view. If filters now hide it, fall back to the first visible photo.
        var resumedIndex = -1;
        if (!string.IsNullOrEmpty(session?.LastSelectedFile))
        {
            for (int i = 0; i < FilteredPhotos.Count; i++)
            {
                if (string.Equals(FilteredPhotos[i].FileName, session.LastSelectedFile, StringComparison.OrdinalIgnoreCase))
                {
                    resumedIndex = i;
                    break;
                }
            }
        }

        if (resumedIndex >= 0)
        {
            SelectedIndex = resumedIndex;
            SelectedPhoto = FilteredPhotos[resumedIndex];
        }
        else if (FilteredPhotos.Count > 0)
        {
            SelectedIndex = 0;
            SelectedPhoto = FilteredPhotos[0];
        }

        // Restore complete — re-enable session writes, anchored to this folder.
        _sessionFolder = folderPath;
        _suppressSessionSave = false;

        StatusText = $"Loaded {files.Count} photos. Generating previews...";

        // Background: generate thumbnails progressively
        await GeneratePreviewsAsync(ct);

        if (!ct.IsCancellationRequested)
        {
            var burstSuffix = BurstCount > 0 ? $"  ({BurstCount} burst{(BurstCount == 1 ? "" : "s")})" : "";
            StatusText = $"{files.Count} photos ready{burstSuffix}. [{_extractor.GetType().Name}]";
            IsLoading = false;
            try
            {
                Directory.CreateDirectory(SettingsDir);
                await File.WriteAllTextAsync(LastFolderFile, folderPath, ct);
            }
            catch { /* non-critical */ }
        }
    }

    private async Task GeneratePreviewsAsync(CancellationToken ct)
    {
        // First pass: load cached thumbnails on the UI thread (instant)
        var toExtract = new List<PhotoItem>();
        foreach (var photo in AllPhotos)
        {
            if (ct.IsCancellationRequested) return;
            var cached = _cache!.LoadThumbnail(photo.FileName);
            if (cached != null)
                photo.ThumbnailJpeg = cached;
            else
                toExtract.Add(photo);
        }

        // Second pass: extract missing thumbnails + metadata for all photos in parallel.
        // Extraction is CPU+IO bound and per-call independent, so it parallelises cleanly.
        // Cap at ProcessorCount/2 to leave headroom for the UI thread + decode.
        int done = 0;
        int total = AllPhotos.Count;
        int parallelism = Math.Max(2, Math.Min(8, Environment.ProcessorCount / 2));
        var needsThumb = new HashSet<PhotoItem>(toExtract);

        await Task.Run(() =>
        {
            var po = new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct };
            try
            {
                Parallel.ForEach(AllPhotos, po, photo =>
                {
                    byte[]? thumbBytes = null;
                    if (needsThumb.Contains(photo))
                    {
                        var jpeg = ExtractorFor(photo).ExtractThumbnail(photo.FilePath);
                        if (jpeg != null)
                        {
                            var thumb = ProcessJpegForCache(jpeg, ThumbnailDecodeWidth) ?? jpeg;
                            _cache!.SaveThumbnail(photo.FileName, thumb);
                            thumbBytes = thumb;
                            Application.Current.Dispatcher.Invoke(() => photo.ThumbnailJpeg = thumb);
                        }
                    }
                    else
                    {
                        thumbBytes = photo.ThumbnailJpeg; // loaded from disk cache in pass 1
                    }

                    var metadata = ExtractorFor(photo).ExtractMetadata(photo.FilePath);
                    if (metadata != null)
                        Application.Current.Dispatcher.Invoke(() => photo.Metadata = metadata);

                    // Compute the perceptual hash from the thumbnail once and reuse on every
                    // subsequent open via the SQLite cache. Used by BurstDetector below.
                    if (photo.Phash == null && thumbBytes != null)
                        photo.Phash = Rawr.App.Services.PerceptualHash.Compute(thumbBytes);

                    // Same lifecycle for clipping percentages — feeds the sidebar Exposure
                    // buckets. Recompute when the per-pixel threshold changes between sessions.
                    if (thumbBytes != null && (photo.HighlightClippedPct == null || photo.ShadowClippedPct == null))
                    {
                        try
                        {
                            var stats = Rawr.App.Services.ClippingStatsComputer.Compute(thumbBytes, AppSettings.Current.ClippingThreshold);
                            photo.HighlightClippedPct = stats.HighlightPct;
                            photo.ShadowClippedPct = stats.ShadowPct;
                        }
                        catch { /* malformed thumbnail; leave nulls — bucket will skip this photo */ }
                    }

                    var d = Interlocked.Increment(ref done);
                    if (d % 10 == 0)
                    {
                        var snapshot = d;
                        Application.Current.Dispatcher.BeginInvoke(() =>
                            StatusText = $"Generating previews... {snapshot}/{total}");
                    }
                });
            }
            catch (OperationCanceledException) { /* folder switched mid-scan */ }
        }, ct);

        if (ct.IsCancellationRequested) return;

        // Once metadata is in for every photo, group consecutive shots into bursts.
        // BurstDetector mutates GroupId/BurstBadge on the UI thread (the properties are observable),
        // so run it on the dispatcher.
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var (loose, strict) = BurstDetector.ThresholdsFromStrictness(AppSettings.Current.BurstSimilarityStrictness);
            BurstCount = BurstDetector.Detect(AllPhotos,
                TimeSpan.FromSeconds(AppSettings.Current.BurstMaxGapSeconds),
                looseHammingThreshold: loose,
                strictHammingThreshold: strict);
        });

        // Persist burst assignments and freshly-computed perceptual hashes so the
        // next session reuses them without re-decoding every thumbnail.
        if (_db != null)
        {
            try { await Task.Run(() => _db.SaveBatch(AllPhotos), ct); }
            catch (OperationCanceledException) { }
        }

        if (BurstFilter != BurstFilterMode.Any || SortField == SortField.Burst)
            ApplyFilter();
        else
            // The parallel pass populated clipping percentages and tag counts that
            // weren't known when ApplyFilter ran at folder-open time. Refresh just
            // the sidebar bucket counts so they tick up to match without rebuilding
            // the (potentially large) filtered list.
            await Application.Current.Dispatcher.InvokeAsync(RefreshFilterBuckets);
    }

    // ── Navigation ──

    partial void OnSelectedIndexChanged(int value)
    {
        if (value < 0 || value >= FilteredPhotos.Count) return;

        SelectedPhoto = FilteredPhotos[value];
        _highResPreviewLoaded = false;

        // Cancel any in-flight preview/prefetch work for the previous selection so
        // its decoded BitmapSource doesn't race ahead and overwrite the new one.
        _previewCts?.Cancel();
        _previewCts = new CancellationTokenSource();
        var ct = _previewCts.Token;

        _exposureCts?.Cancel();
        _rawDecodeCts?.Cancel();
        _basePreviewImage = null;
        _baseRawImage = null;
        IsLinearRawReady = false;
        ExposureSourceLabel = "EV";
        _exposureCompensation = 0.0;
        OnPropertyChanged(nameof(ExposureCompensation));
        OnPropertyChanged(nameof(ExposureSelectionStart));
        OnPropertyChanged(nameof(ExposureSelectionEnd));

        HistogramData = null;
        FocusPeakingOverlay = null;
        ClippingOverlay = null;
        EvictFarPhotos(value);
        _ = LoadPreviewForSelectedAsync(ct);
        _ = PrefetchNeighborsAsync(value, ct);
        UpdateStatus();
    }

    partial void OnSelectedPhotoChanged(PhotoItem? value)
    {
        OnPropertyChanged(nameof(SelectedPhotoTagAssignments));

        if (_metadataSubscription != null)
            _metadataSubscription.PropertyChanged -= OnSelectedPhotoPropertyChanged;
        _metadataSubscription = value;
        if (value != null)
            value.PropertyChanged += OnSelectedPhotoPropertyChanged;

        // Default path: any anchor change collapses the multi-selection back to just
        // the new anchor (plain click, arrow keys, undo/redo, filter restore). The
        // Ctrl/Shift-click selection methods set _suspendSelectionReconcile while
        // they manage SelectedPhotos themselves so this collapse doesn't fire.
        if (!_suspendSelectionReconcile)
            ReconcileSingleSelection(value);

        // Persist last-selected so reopening this folder jumps straight back here.
        // Skip when value is null — that's almost always the transient clear during
        // folder load or filter rebuild, not a real "user deselected everything".
        if (value != null) SaveSessionIfNeeded();
    }

    private void OnSelectedPhotoPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PhotoItem.Metadata))
            OnPropertyChanged(nameof(SelectedPhotoCaptureDateFormatted));
    }

    // ── Multi-selection ──
    //
    // Three callers — plain click, Ctrl+click, Shift+click — produce different selection
    // changes; everything else (arrow keys, filter restore, undo target, …) flows
    // through SelectedPhoto and lands in ReconcileSingleSelection which collapses to
    // a single-anchor selection. AddToSelection / RemoveFromSelection are the only
    // places that mutate PhotoItem.IsSelected, and they always expand burst
    // representatives to cover their full burst (see user requirement #6).

    private void ReconcileSingleSelection(PhotoItem? anchor)
    {
        ClearAllSelection();
        if (anchor != null)
            AddToSelection(anchor);
        _selectionAnchor = anchor;
        OnPropertyChanged(nameof(SelectedPhotosCount));
        OnPropertyChanged(nameof(CopyTargetCount));
    }

    private void ClearAllSelection()
    {
        foreach (var p in SelectedPhotos)
            p.IsSelected = false;
        SelectedPhotos.Clear();
    }

    private void AddToSelection(PhotoItem photo)
    {
        if (!photo.IsSelected)
        {
            photo.IsSelected = true;
            SelectedPhotos.Add(photo);
        }
        // A collapsed-burst representative stands in for every member of its burst —
        // selecting it must select the whole stack so bulk ops touch every frame,
        // not just the visible representative.
        if (photo.CollapsedBurstCount > 0)
        {
            foreach (var member in GetBurstMembers(photo.GroupId))
            {
                if (!member.IsSelected)
                {
                    member.IsSelected = true;
                    SelectedPhotos.Add(member);
                }
            }
        }
    }

    private void RemoveFromSelection(PhotoItem photo)
    {
        if (photo.IsSelected)
        {
            photo.IsSelected = false;
            SelectedPhotos.Remove(photo);
        }
        if (photo.CollapsedBurstCount > 0)
        {
            foreach (var member in GetBurstMembers(photo.GroupId))
            {
                if (member.IsSelected)
                {
                    member.IsSelected = false;
                    SelectedPhotos.Remove(member);
                }
            }
        }
    }

    private void SetAnchorWithoutReconcile(PhotoItem photo)
    {
        var idx = FilteredPhotos.IndexOf(photo);
        _suspendSelectionReconcile = true;
        try
        {
            // Setting SelectedIndex updates SelectedPhoto via OnSelectedIndexChanged.
            // Suspending reconcile keeps the multi-selection set intact.
            if (idx >= 0)
                SelectedIndex = idx;
            else
                SelectedPhoto = photo;
        }
        finally { _suspendSelectionReconcile = false; }
    }

    public void SelectSinglePhoto(PhotoItem photo)
    {
        var idx = FilteredPhotos.IndexOf(photo);
        if (idx < 0) return;
        if (SelectedIndex == idx)
        {
            // Re-click on the existing anchor — SelectedIndex is unchanged, so
            // OnSelectedIndexChanged won't fire and the multi-selection wouldn't
            // collapse on its own. Reconcile explicitly so plain-click always
            // means "single-selection of the click target".
            ReconcileSingleSelection(photo);
        }
        else
        {
            // Default reconcile path: setting SelectedIndex flows through
            // OnSelectedPhotoChanged → ReconcileSingleSelection which clears + adds.
            SelectedIndex = idx;
        }
        _selectionAnchor = photo;
    }

    public void TogglePhotoSelection(PhotoItem photo)
    {
        if (photo.IsSelected)
        {
            // Never let the user empty the selection — there is always at least
            // one anchor (user requirement #4).
            if (SelectedPhotos.Count <= 1) return;
            RemoveFromSelection(photo);

            // Anchor moves to the clicked photo so a follow-up Shift+click ranges
            // from here, even though the photo is no longer selected.
            _selectionAnchor = photo;

            // SelectedPhoto must point at a still-selected photo so the preview
            // pane keeps showing one of the user's actual selections.
            if (ReferenceEquals(SelectedPhoto, photo))
            {
                var fallback = SelectedPhotos.FirstOrDefault();
                if (fallback != null) SetAnchorWithoutReconcile(fallback);
            }
        }
        else
        {
            AddToSelection(photo);
            _selectionAnchor = photo;
            SetAnchorWithoutReconcile(photo);
        }
        OnPropertyChanged(nameof(SelectedPhotosCount));
        OnPropertyChanged(nameof(CopyTargetCount));
    }

    public void SelectRangeTo(PhotoItem target)
    {
        var anchor = _selectionAnchor ?? SelectedPhoto;
        if (anchor == null) { SelectSinglePhoto(target); return; }

        var anchorIdx = FilteredPhotos.IndexOf(anchor);
        var targetIdx = FilteredPhotos.IndexOf(target);
        if (anchorIdx < 0 || targetIdx < 0) { SelectSinglePhoto(target); return; }

        var lo = Math.Min(anchorIdx, targetIdx);
        var hi = Math.Max(anchorIdx, targetIdx);

        // A range click replaces the prior selection but keeps the anchor where it
        // was — that's how Explorer & Lightroom let you re-aim a Shift+click without
        // re-anchoring after each one.
        ClearAllSelection();
        for (int i = lo; i <= hi; i++)
            AddToSelection(FilteredPhotos[i]);

        SetAnchorWithoutReconcile(target);
        OnPropertyChanged(nameof(SelectedPhotosCount));
        OnPropertyChanged(nameof(CopyTargetCount));
    }

    [RelayCommand]
    private void SelectAllVisible()
    {
        if (FilteredPhotos.Count == 0) return;
        ClearAllSelection();
        foreach (var p in FilteredPhotos)
            AddToSelection(p);
        // Anchor and SelectedPhoto stay where they were; both remain in the set.
        if (SelectedPhoto != null && !SelectedPhoto.IsSelected)
            AddToSelection(SelectedPhoto);
        OnPropertyChanged(nameof(SelectedPhotosCount));
        OnPropertyChanged(nameof(CopyTargetCount));
    }

    [RelayCommand]
    private void ClearMultiSelection()
    {
        // Esc collapses the multi-selection back to just the anchor — there is
        // always exactly one selected photo (user requirement #4).
        ReconcileSingleSelection(SelectedPhoto);
    }

    public void MoveAnchorTo(PhotoItem photo)
    {
        // Right-click inside a multi-selection should re-aim the anchor without
        // tearing the set apart, so context-menu actions hit the right photo for
        // their "decision" (e.g. tag-toggle direction) but still apply to all.
        SetAnchorWithoutReconcile(photo);
        _selectionAnchor = photo;
    }

    public string SelectedPhotoCaptureDateFormatted =>
        SelectedPhoto?.Metadata?.CaptureTime.HasValue == true
            ? SelectedPhoto.Metadata.CaptureTime.Value.ToString(AppSettings.Current.DateFormat)
            : "—";

    public void NotifyDateFormatChanged() =>
        OnPropertyChanged(nameof(SelectedPhotoCaptureDateFormatted));

    [RelayCommand]
    private void SetHistogramMode(HistogramMode mode) => HistogramMode = mode;

    [RelayCommand]
    private void SetSidePanelView(SidePanelView view) => SidePanelView = view;

    [RelayCommand]
    private void ToggleFocusPeaking()
    {
        if (FocusPeakingEnabled) DisableOverlays();
        else EnableFocusPeaking();
    }

    [RelayCommand]
    private void ToggleClipping()
    {
        if (ClippingEnabled) DisableOverlays();
        else EnableClipping();
    }

    // Cycles the preview overlay through Off → Focus Peaking → Clipping → Off.
    // Bound to 'O' by default.
    [RelayCommand]
    private void CycleOverlay()
    {
        if (FocusPeakingEnabled) EnableClipping();
        else if (ClippingEnabled) DisableOverlays();
        else EnableFocusPeaking();
    }

    private void EnableFocusPeaking()
    {
        if (ClippingEnabled)
        {
            ClippingEnabled = false;
            ClippingOverlay = null;
        }
        FocusPeakingEnabled = true;
        var photo = SelectedPhoto;
        if (photo == null) return;
        var ct = _previewCts?.Token ?? CancellationToken.None;
        _ = ComputeFocusPeakingAsync(photo, ct);
    }

    private void EnableClipping()
    {
        if (FocusPeakingEnabled)
        {
            FocusPeakingEnabled = false;
            FocusPeakingOverlay = null;
        }
        ClippingEnabled = true;
        var photo = SelectedPhoto;
        if (photo == null) return;
        var raw = _baseRawImage;
        if (raw == null) return; // RAW decode not ready yet — overlay paints when LoadLinearRawAsync finishes.
        var ct = _previewCts?.Token ?? CancellationToken.None;
        _ = ComputeClippingAsync(photo, raw, ct);
    }

    private void DisableOverlays()
    {
        FocusPeakingEnabled = false;
        FocusPeakingOverlay = null;
        ClippingEnabled = false;
        ClippingOverlay = null;
    }

    public void RefreshFocusPeaking()
    {
        if (!FocusPeakingEnabled) return;
        var photo = SelectedPhoto;
        if (photo == null) return;
        var ct = _previewCts?.Token ?? CancellationToken.None;
        _ = ComputeFocusPeakingAsync(photo, ct);
    }

    private void SetBasePreview(BitmapSource bitmap)
    {
        _basePreviewImage = bitmap;
        // The linear RAW path is the only one that doesn't band under ±EV, so it
        // wins on screen by default. But the cached RAW is downsampled to
        // LinearRawPreviewWidth — when a higher-resolution JPEG bitmap arrives (zoom
        // time, after ExtractFullJpeg pulls the sensor-sized embedded preview) and
        // the user isn't applying EV, prefer it so pixel-peeping shows real detail.
        if (_baseRawImage != null && !PreferJpegOverRaw(bitmap)) return;
        PreviewImage = ExposureCompensation == 0.0 ? bitmap : ExposureProcessor.Apply(bitmap, ExposureCompensation);
    }

    private bool PreferJpegOverRaw(BitmapSource jpegBitmap)
    {
        var raw = _baseRawImage;
        if (raw == null) return true;
        if (ExposureCompensation != 0.0) return false;
        return jpegBitmap.PixelWidth > raw.Width;
    }

    [RelayCommand] private void IncreaseExposure() =>
        ExposureCompensation = Math.Round(Math.Clamp(ExposureCompensation + 0.2, -5.0, 5.0), 10);

    [RelayCommand] private void DecreaseExposure() =>
        ExposureCompensation = Math.Round(Math.Clamp(ExposureCompensation - 0.2, -5.0, 5.0), 10);

    partial void OnExposureCompensationChanged(double value)
    {
        OnPropertyChanged(nameof(ExposureSelectionStart));
        OnPropertyChanged(nameof(ExposureSelectionEnd));
        var photo = SelectedPhoto;
        if (photo == null) return;
        _exposureCts?.Cancel();
        _exposureCts = new CancellationTokenSource();
        _ = ApplyExposureAsync(photo, value, _exposureCts.Token);
    }

    private async Task ApplyExposureAsync(PhotoItem photo, double ev, CancellationToken ct)
    {
        // At EV=0 we pay no precision cost going through the JPEG, so if a higher-res
        // JPEG bitmap is cached (typical after zoom-time ExtractFullJpeg), use it
        // directly — the RAW path's cached buffer is downsampled to LinearRawPreviewWidth.
        if (ev == 0.0 && _basePreviewImage != null
            && (_baseRawImage == null || _basePreviewImage.PixelWidth > _baseRawImage.Width))
        {
            if (SelectedPhoto == photo) PreviewImage = _basePreviewImage;
            return;
        }

        // Otherwise prefer the linear RAW path — that's the only one that reflects
        // true sensor highlights/shadows under non-zero EV.
        var raw = _baseRawImage;
        if (raw != null)
        {
            try
            {
                var rendered = await Task.Run(() => ExposureProcessor.Render(raw, ev, ct), ct);
                if (ct.IsCancellationRequested || SelectedPhoto != photo) return;
                PreviewImage = rendered;
            }
            catch (OperationCanceledException) { /* superseded by a newer slider value */ }
            return;
        }

        var baseImage = _basePreviewImage;
        if (baseImage == null) return;
        try
        {
            BitmapSource adjusted = ev == 0.0
                ? baseImage
                : await Task.Run(() => ExposureProcessor.Apply(baseImage, ev), ct);
            if (ct.IsCancellationRequested || SelectedPhoto != photo) return;
            PreviewImage = adjusted;
        }
        catch (OperationCanceledException) { /* superseded */ }
    }

    /// <summary>
    /// Decode the full RAW sensor data for the given photo in the background and
    /// promote the slider to the linear RAW path once it's ready. Slow (~300ms-2s
    /// per photo) but only runs for the selected photo, not on bulk navigation.
    /// </summary>
    private async Task LoadLinearRawAsync(PhotoItem photo, CancellationToken ct)
    {
        if (_libRaw == null || !photo.IsRaw || photo.IsVideo) return;
        try
        {
            var raw = await Task.Run(() => LoadOrDecodeLinearRaw(photo, ct), ct);
            if (ct.IsCancellationRequested || SelectedPhoto != photo) return;
            if (raw == null)
            {
                // Surface the failure so users don't silently keep operating on JPEG.
                ExposureSourceLabel = "EV (JPG — RAW decode failed)";
                return;
            }

            _baseRawImage = raw;
            IsLinearRawReady = true;
            ExposureSourceLabel = "EV (RAW)";
            // Clipping detection runs on the linear RAW; if the user already toggled it
            // on while we were decoding, paint the overlay now that sensor data is here.
            if (ClippingEnabled) _ = ComputeClippingAsync(photo, raw, ct);
            // Replace the JPEG-based histogram (already shown) with one computed from
            // the linear sensor data — the JPEG histogram understates highlight clip.
            _ = ComputeHistogramAsync(photo, ct);
            // Re-render the current preview through the linear pipeline so the user
            // sees the more accurate rendition even at EV=0, and so subsequent slider
            // moves operate on real sensor data. Skip the swap if a higher-res JPEG
            // is already on screen at EV=0 — clobbering it would visibly drop detail
            // for someone pixel-peeping at zoom.
            var rendered = await Task.Run(() => ExposureProcessor.Render(raw, ExposureCompensation, ct), ct);
            if (!ct.IsCancellationRequested && SelectedPhoto == photo)
            {
                bool keepJpeg = ExposureCompensation == 0.0
                    && _basePreviewImage != null
                    && _basePreviewImage.PixelWidth > raw.Width;
                if (!keepJpeg) PreviewImage = rendered;
            }
        }
        catch (OperationCanceledException) { /* selection moved on */ }
        catch
        {
            ExposureSourceLabel = "EV (JPG — RAW decode failed)";
        }
    }

    /// <summary>
    /// Disk-cache-aware linear RAW load. The decode itself is the slow part
    /// (~1-3s for cRAW unpack + dcraw_process); the downsampled buffer is ~50MB
    /// at LinearRawPreviewWidth and reads back from disk in ~30ms. So once a
    /// photo has been visited once, subsequent loads (re-selecting, app restart,
    /// neighbour prefetch) skip LibRaw entirely.
    /// </summary>
    private LinearRawImage? LoadOrDecodeLinearRaw(PhotoItem photo, CancellationToken ct)
    {
        var cache = _cache;
        if (cache != null)
        {
            var hit = cache.LoadLinearRaw(photo.FileName, photo.FilePath);
            if (hit != null)
                return new LinearRawImage(hit.Width, hit.Height, hit.Pixels);
        }

        ct.ThrowIfCancellationRequested();
        var full = _libRaw!.ExtractLinearRgb(photo.FilePath);
        if (full == null) return null;

        // Box-average down to ~preview resolution. Dither needs to live at roughly
        // display pixel density to survive WPF's scaling — at full sensor res the
        // per-pixel noise gets averaged out during the ~3x downscale and banding
        // reappears. Also slashes memory/CPU.
        var down = full.Downsample(LinearRawPreviewWidth);
        if (down == null) return null;

        // Persist for future loads. Failures are silently ignored — losing the
        // cache means the next visit re-decodes, not that anything is broken.
        try { cache?.SaveLinearRaw(photo.FileName, photo.FilePath, down.Width, down.Height, down.Pixels); }
        catch { }
        return down;
    }

    private async Task ComputeFocusPeakingAsync(PhotoItem photo, CancellationToken ct)
    {
        var jpeg = photo.FullJpeg ?? photo.PreviewJpeg;
        if (jpeg == null) return;
        var threshold = AppSettings.Current.FocusPeakingThreshold;
        var overlay = await Task.Run(() => FocusPeakingComputer.Compute(jpeg, threshold), ct);
        if (!ct.IsCancellationRequested && SelectedPhoto == photo && FocusPeakingEnabled)
            FocusPeakingOverlay = overlay;
    }

    private async Task ComputeClippingAsync(PhotoItem photo, LinearRawImage raw, CancellationToken ct)
    {
        var mode = AppSettings.Current.ClippingMode;
        var threshold = AppSettings.Current.ClippingThreshold;
        var overlay = await Task.Run(() => ClippingComputer.Compute(raw, mode, threshold), ct);
        if (!ct.IsCancellationRequested && SelectedPhoto == photo && ClippingEnabled)
            ClippingOverlay = overlay;
    }

    public void RefreshClipping()
    {
        if (!ClippingEnabled) return;
        var photo = SelectedPhoto;
        if (photo == null) return;
        var raw = _baseRawImage;
        if (raw == null) return;
        var ct = _previewCts?.Token ?? CancellationToken.None;
        _ = ComputeClippingAsync(photo, raw, ct);
    }

    /// <summary>
    /// Re-scans every photo's cached thumbnail to refresh the highlight/shadow
    /// percentages used by the sidebar Exposure buckets. Call after the per-pixel
    /// ClippingThreshold changes — the cached values were computed against the
    /// old threshold and would otherwise misclassify photos.
    /// </summary>
    public async Task RecomputeClippingStatsAsync()
    {
        if (AllPhotos.Count == 0) return;

        var threshold = AppSettings.Current.ClippingThreshold;
        var snapshot = AllPhotos.Where(p => p.ThumbnailJpeg != null).ToList();
        int parallelism = Math.Max(2, Math.Min(8, Environment.ProcessorCount / 2));

        await Task.Run(() =>
        {
            var po = new ParallelOptions { MaxDegreeOfParallelism = parallelism };
            Parallel.ForEach(snapshot, po, photo =>
            {
                var bytes = photo.ThumbnailJpeg;
                if (bytes == null) return;
                try
                {
                    var stats = Services.ClippingStatsComputer.Compute(bytes, threshold);
                    photo.HighlightClippedPct = stats.HighlightPct;
                    photo.ShadowClippedPct = stats.ShadowPct;
                }
                catch { /* leave stale value rather than null out a previously valid one */ }
            });
        });

        if (_db != null)
        {
            try { await Task.Run(() => _db.SaveBatch(AllPhotos)); } catch { }
        }

        // The active filter may now include/exclude a different set of photos.
        ApplyFilter();
    }

    private async Task ComputeHistogramAsync(PhotoItem photo, CancellationToken ct)
    {
        // Prefer the linear-RAW histogram when sensor data is in hand: the JPEG
        // path bakes in the camera's tone curve and lies about clipping headroom.
        var raw = _baseRawImage;
        if (raw != null)
        {
            var rawHist = await Task.Run(() => HistogramComputer.Compute(raw), ct);
            if (!ct.IsCancellationRequested && SelectedPhoto == photo)
                HistogramData = rawHist;
            return;
        }

        var jpeg = photo.FullJpeg ?? photo.PreviewJpeg;
        if (jpeg == null) return;
        var histData = await Task.Run(() => HistogramComputer.Compute(jpeg), ct);
        if (!ct.IsCancellationRequested && SelectedPhoto == photo)
            HistogramData = histData;
    }

    [RelayCommand]
    private void NextPhoto()
    {
        if (FilteredPhotos.Count == 0) return;
        SelectedIndex = (SelectedIndex + 1) % FilteredPhotos.Count;
    }

    [RelayCommand]
    private void PreviousPhoto()
    {
        if (FilteredPhotos.Count == 0) return;
        SelectedIndex = (SelectedIndex - 1 + FilteredPhotos.Count) % FilteredPhotos.Count;
    }

    private async Task LoadPreviewForSelectedAsync(CancellationToken ct)
    {
        var photo = SelectedPhoto;
        if (photo == null) return;

        if (photo.IsVideo)
        {
            // Hand the file to the MediaElement; clear any still-image preview so the
            // image control doesn't peek through behind the player.
            PreviewImage = null;
            VideoSourceUri = new Uri(photo.FilePath);
            return;
        }

        // Switching from a video back to a photo: release the player's file handle.
        if (VideoSourceUri != null) VideoSourceUri = null;

        try
        {
            // Fast path: a previous decode already wrote the linear-RAW buffer to
            // disk, so we can skip the JPEG-first paint entirely. Reading + sRGB-
            // encoding the cached buffer is fast enough (~30-80ms) that we don't
            // need a placeholder at all — leaving the previous photo's RAW render
            // on screen for that interval looks far smoother than flashing the
            // small thumbnail or a black gap. PreviewImage is only replaced once
            // LoadLinearRawAsync finishes rendering the new buffer.
            if (photo.IsRaw && _libRaw != null && _cache != null
                && _cache.HasLinearRaw(photo.FileName))
            {
                StartRawDecode(photo);
                _ = LoadPreviewJpegInBackgroundAsync(photo, ct);
                return;
            }

            // Already-resident bytes (set by an earlier prefetch) — skip the disk read.
            var cached = photo.PreviewJpeg ?? _cache?.LoadPreview(photo.FileName);
            if (cached != null)
            {
                var bs = await Task.Run(() => LoadBitmapFromJpeg(cached), ct);
                if (ct.IsCancellationRequested || SelectedPhoto != photo) return;
                photo.PreviewJpeg = cached;
                if (bs != null) SetBasePreview(bs);
                _ = ComputeHistogramAsync(photo, ct);
                if (FocusPeakingEnabled) _ = ComputeFocusPeakingAsync(photo, ct);
                _ = PreloadFullJpegAsync(photo, ct);
                StartRawDecode(photo);
                return;
            }

            // Show the small thumbnail as a placeholder while the medium preview is being extracted.
            if (photo.ThumbnailJpeg != null)
            {
                var thumbBs = await Task.Run(() => LoadBitmapFromJpeg(photo.ThumbnailJpeg), ct);
                if (!ct.IsCancellationRequested && SelectedPhoto == photo)
                    PreviewImage = thumbBs;
            }
            else
            {
                PreviewImage = null;
            }

            var jpeg = await Task.Run(() => ExtractorFor(photo).ExtractPreview(photo.FilePath), ct);
            if (ct.IsCancellationRequested || jpeg == null || SelectedPhoto != photo) return;

            // Shrink to screen-sized JPEG with orientation baked in for fast subsequent loads.
            var processed = await Task.Run(() => ProcessJpegForCache(jpeg, PreviewDecodeWidth) ?? jpeg, ct);
            if (ct.IsCancellationRequested || SelectedPhoto != photo) return;

            _cache?.SavePreview(photo.FileName, processed);
            photo.PreviewJpeg = processed;

            var fullBs = await Task.Run(() => LoadBitmapFromJpeg(processed), ct);
            if (ct.IsCancellationRequested || SelectedPhoto != photo) return;

            if (fullBs != null) SetBasePreview(fullBs);
            _ = ComputeHistogramAsync(photo, ct);
            if (FocusPeakingEnabled) _ = ComputeFocusPeakingAsync(photo, ct);
            _ = PreloadFullJpegAsync(photo, ct);
            StartRawDecode(photo);
        }
        catch (OperationCanceledException) { /* selection moved on */ }
    }

    private void StartRawDecode(PhotoItem photo)
    {
        if (!photo.IsRaw || photo.IsVideo) return;
        _rawDecodeCts?.Cancel();
        _rawDecodeCts = new CancellationTokenSource();
        _ = LoadLinearRawAsync(photo, _rawDecodeCts.Token);
    }

    /// <summary>
    /// On the linear-RAW fast path we skip the JPG-first display, but we still
    /// want PreviewJpeg cached on the PhotoItem so focus-peaking, the JPEG-fallback
    /// histogram path, and zoom-to-FullJpeg don't have to wait. Just populates
    /// the bytes — never touches PreviewImage, so the RAW render on screen stays
    /// untouched.
    /// </summary>
    private async Task LoadPreviewJpegInBackgroundAsync(PhotoItem photo, CancellationToken ct)
    {
        if (photo.IsVideo || photo.PreviewJpeg != null) return;
        try
        {
            var cached = _cache?.LoadPreview(photo.FileName);
            if (cached != null)
            {
                photo.PreviewJpeg = cached;
            }
            else
            {
                var jpeg = await Task.Run(() => ExtractorFor(photo).ExtractPreview(photo.FilePath), ct);
                if (ct.IsCancellationRequested || jpeg == null) return;
                var processed = await Task.Run(() => ProcessJpegForCache(jpeg, PreviewDecodeWidth) ?? jpeg, ct);
                if (ct.IsCancellationRequested) return;
                _cache?.SavePreview(photo.FileName, processed);
                photo.PreviewJpeg = processed;
            }

            // Now that PreviewJpeg bytes exist, fire the work that was deferred
            // when we took the fast path — but only if the user actually needs
            // it (focus peaking on, or no histogram yet from the RAW path).
            if (!ct.IsCancellationRequested && SelectedPhoto == photo)
            {
                if (FocusPeakingEnabled) _ = ComputeFocusPeakingAsync(photo, ct);
                _ = PreloadFullJpegAsync(photo, ct);
            }
        }
        catch (OperationCanceledException) { /* selection moved on */ }
        catch { /* JPEG fallback isn't critical — RAW is already on screen */ }
    }

    public async Task LoadHighResPreviewAsync()
    {
        if (_highResPreviewLoaded) return;
        var photo = SelectedPhoto;
        if (photo == null || photo.IsVideo) return;

        _highResPreviewLoaded = true; // guard against duplicate concurrent calls
        var ct = _previewCts?.Token ?? CancellationToken.None;

        try
        {
            // Reuse pre-extracted bytes if PreloadFullJpegAsync already finished.
            var jpeg = photo.FullJpeg ?? await Task.Run(() => ExtractorFor(photo).ExtractFullJpeg(photo.FilePath), ct);
            if (ct.IsCancellationRequested || jpeg == null || SelectedPhoto != photo) return;

            photo.FullJpeg ??= jpeg;

            var bs = await Task.Run(() => LoadBitmapFromJpeg(jpeg, decodePixelWidth: 0), ct);
            if (!ct.IsCancellationRequested && SelectedPhoto == photo)
            {
                if (bs != null) SetBasePreview(bs);
                else PreviewImage = null;
            }
        }
        catch (OperationCanceledException) { /* selection moved on */ }
    }

    /// <summary>
    /// Background-extract the full sensor-resolution JPEG bytes for the current photo
    /// so that a subsequent zoom can decode them immediately without disk I/O.
    /// </summary>
    private async Task PreloadFullJpegAsync(PhotoItem photo, CancellationToken ct)
    {
        if (photo.IsVideo || photo.FullJpeg != null) return;
        try
        {
            var jpeg = await Task.Run(() => ExtractorFor(photo).ExtractFullJpeg(photo.FilePath), ct);
            if (!ct.IsCancellationRequested && jpeg != null)
            {
                photo.FullJpeg = jpeg;
                _ = ComputeHistogramAsync(photo, ct);
            }
        }
        catch (OperationCanceledException) { /* selection moved on */ }
        catch { /* extraction failed — fall back to on-demand on zoom */ }
    }

    /// <summary>
    /// Warm the disk/memory preview cache for photos adjacent to the current selection
    /// so Next/Previous feels instant. Two layers:
    ///   1. JPEG preview — cached in memory on PhotoItem so the swap is instant.
    ///   2. Linear RAW — written to disk only; the buffer is ~50MB so we don't hold
    ///      it in RAM, but persisting it means the next selection skips LibRaw.
    ///
    /// Each photo's RAW decode takes ~1-3s and is single-threaded inside LibRaw,
    /// but multiple LibRaw handles run independently — so we fan out across cores
    /// rather than serialising. ProcessorCount/2 leaves headroom for the UI thread,
    /// the active-photo decode, and the JPEG codec.
    /// </summary>
    private async Task PrefetchNeighborsAsync(int currentIndex, CancellationToken ct)
    {
        // Process in alternating order so the immediate neighbours land first.
        var offsets = new[] { 1, -1, 2, -2 };
        var targets = new List<PhotoItem>(offsets.Length);
        foreach (var offset in offsets)
        {
            var i = currentIndex + offset;
            if (i < 0 || i >= FilteredPhotos.Count) continue;
            var p = FilteredPhotos[i];
            if (!p.IsVideo) targets.Add(p);
        }
        if (targets.Count == 0) return;

        int parallelism = Math.Max(1, Math.Min(targets.Count, Math.Max(2, Environment.ProcessorCount / 2)));
        using var gate = new SemaphoreSlim(parallelism);

        var tasks = new List<Task>(targets.Count);
        foreach (var photo in targets)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            tasks.Add(Task.Run(() =>
            {
                try { PrefetchPhoto(photo, ct); }
                catch (OperationCanceledException) { /* selection moved on */ }
                catch { /* one neighbour failing should not block the others */ }
                finally { gate.Release(); }
            }, ct));
        }

        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* selection moved on */ }
    }

    /// <summary>
    /// Warm both the JPEG preview cache and the linear-RAW disk cache for a single
    /// photo. Safe to call concurrently for different photos. Synchronous — caller
    /// wraps in Task.Run so multiple photos can decode in parallel.
    /// </summary>
    private void PrefetchPhoto(PhotoItem photo, CancellationToken ct)
    {
        if (photo.PreviewJpeg == null)
        {
            var cached = _cache?.LoadPreview(photo.FileName);
            if (cached != null)
            {
                photo.PreviewJpeg = cached;
            }
            else
            {
                var jpeg = ExtractorFor(photo).ExtractPreview(photo.FilePath);
                ct.ThrowIfCancellationRequested();
                if (jpeg != null)
                {
                    var processed = ProcessJpegForCache(jpeg, PreviewDecodeWidth) ?? jpeg;
                    ct.ThrowIfCancellationRequested();
                    _cache?.SavePreview(photo.FileName, processed);
                    photo.PreviewJpeg = processed;
                }
            }
        }

        // Warm the linear-RAW disk cache so the slow unpack+process runs while
        // the user is looking at the current photo, not when they hit Next. Skip
        // if a valid cache file already exists.
        if (_libRaw != null && photo.IsRaw && _cache != null
            && _cache.LoadLinearRaw(photo.FileName, photo.FilePath) == null)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var full = _libRaw.ExtractLinearRgb(photo.FilePath);
                var down = full?.Downsample(LinearRawPreviewWidth);
                if (down != null)
                    _cache.SaveLinearRaw(photo.FileName, photo.FilePath, down.Width, down.Height, down.Pixels);
            }
            catch { /* prefetch is best-effort; the on-demand path will retry */ }
        }
    }

    [ObservableProperty] private bool _isCachingAllRaws;
    [ObservableProperty] private int _cacheAllProgress;
    [ObservableProperty] private int _cacheAllTotal;
    private CancellationTokenSource? _cacheAllCts;

    public string CacheAllButtonLabel => IsCachingAllRaws
        ? $"⏹ Cancel  ({CacheAllProgress}/{CacheAllTotal})"
        : "⚡ Cache RAWs";

    partial void OnIsCachingAllRawsChanged(bool value) => OnPropertyChanged(nameof(CacheAllButtonLabel));
    partial void OnCacheAllProgressChanged(int value) => OnPropertyChanged(nameof(CacheAllButtonLabel));
    partial void OnCacheAllTotalChanged(int value) => OnPropertyChanged(nameof(CacheAllButtonLabel));

    /// <summary>
    /// User-triggered: walk every RAW in the current folder and write its
    /// downsampled linear RGB buffer to the disk cache, fanned out across cores.
    /// After this completes, every selection in the folder skips the slow
    /// libraw_unpack + dcraw_process and reads ~30ms from disk instead. Safe to
    /// re-run — already-cached files are skipped.
    /// </summary>
    [RelayCommand]
    private async Task CacheAllRawsAsync()
    {
        if (IsCachingAllRaws)
        {
            _cacheAllCts?.Cancel();
            return;
        }

        if (_libRaw == null || _cache == null || AllPhotos.Count == 0) return;

        var todo = AllPhotos.Where(p => p.IsRaw && !p.IsVideo).ToList();
        if (todo.Count == 0) return;

        _cacheAllCts = new CancellationTokenSource();
        var ct = _cacheAllCts.Token;
        IsCachingAllRaws = true;
        CacheAllTotal = todo.Count;
        CacheAllProgress = 0;
        StatusText = $"Caching RAWs… 0/{todo.Count}";

        // One less than ProcessorCount so the UI thread + on-demand decode stay
        // responsive while the bulk job runs.
        int parallelism = Math.Max(2, Math.Min(8, Environment.ProcessorCount - 1));

        try
        {
            int done = 0;
            await Task.Run(() =>
            {
                var po = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = parallelism };
                Parallel.ForEach(todo, po, photo =>
                {
                    if (ct.IsCancellationRequested) return;
                    try
                    {
                        if (_cache.LoadLinearRaw(photo.FileName, photo.FilePath) == null)
                        {
                            var full = _libRaw.ExtractLinearRgb(photo.FilePath);
                            var down = full?.Downsample(LinearRawPreviewWidth);
                            if (down != null)
                                _cache.SaveLinearRaw(photo.FileName, photo.FilePath, down.Width, down.Height, down.Pixels);
                        }
                    }
                    catch { /* one file failing should not block the rest */ }
                    finally
                    {
                        int n = Interlocked.Increment(ref done);
                        // Throttle UI updates — touching CacheAllProgress every
                        // photo on a 1000-file folder swamps the dispatcher.
                        if (n % 4 == 0 || n == todo.Count)
                        {
                            Application.Current?.Dispatcher.InvokeAsync(() =>
                            {
                                CacheAllProgress = n;
                                StatusText = $"Caching RAWs… {n}/{todo.Count}";
                            });
                        }
                    }
                });
            }, ct);
        }
        catch (OperationCanceledException) { /* user cancelled */ }
        finally
        {
            IsCachingAllRaws = false;
            StatusText = ct.IsCancellationRequested
                ? $"Cache cancelled at {CacheAllProgress}/{CacheAllTotal}."
                : $"Cached {CacheAllProgress} RAW previews.";
            _cacheAllCts?.Dispose();
            _cacheAllCts = null;
        }
    }

    // ── Face / closed-eye analysis ──

    [ObservableProperty] private bool _isAnalyzingFaces;
    [ObservableProperty] private int _analyzeFacesProgress;
    [ObservableProperty] private int _analyzeFacesTotal;
    private CancellationTokenSource? _analyzeFacesCts;
    private FaceAnalyzer? _faceAnalyzer;

    public string AnalyzeFacesButtonLabel => IsAnalyzingFaces
        ? $"⏹ Cancel  ({AnalyzeFacesProgress}/{AnalyzeFacesTotal})"
        : "👁 Detect Closed Eyes";

    partial void OnIsAnalyzingFacesChanged(bool value) => OnPropertyChanged(nameof(AnalyzeFacesButtonLabel));
    partial void OnAnalyzeFacesProgressChanged(int value) => OnPropertyChanged(nameof(AnalyzeFacesButtonLabel));
    partial void OnAnalyzeFacesTotalChanged(int value) => OnPropertyChanged(nameof(AnalyzeFacesButtonLabel));

    /// <summary>
    /// User-triggered: walk every photo without prior face/eye analysis and run
    /// the ONNX face detector + eye-state classifier on its cached preview JPEG.
    /// Click again to cancel. Already-analysed photos are skipped (clear the
    /// SQLite columns to force re-analysis). The pipeline reads the same
    /// _preview.jpg the main viewer uses, so this never re-decodes RAWs.
    /// </summary>
    [RelayCommand]
    private async Task AnalyzeFacesAsync()
    {
        if (IsAnalyzingFaces)
        {
            _analyzeFacesCts?.Cancel();
            return;
        }

        if (_cache == null || AllPhotos.Count == 0) return;

        _faceAnalyzer ??= new FaceAnalyzer();
        _faceAnalyzer.Initialize();
        if (!_faceAnalyzer.IsAvailable)
        {
            StatusText = $"Face analysis unavailable: {_faceAnalyzer.UnavailableReason}";
            return;
        }

        // Skip-already-done: only analyse photos with a missing FaceCount. Clear
        // the SQLite columns to force re-analysis from scratch.
        var todo = AllPhotos.Where(p => !p.IsVideo && p.FaceCount == null).ToList();
        if (todo.Count == 0)
        {
            StatusText = "All photos already analysed.";
            return;
        }

        _analyzeFacesCts = new CancellationTokenSource();
        var ct = _analyzeFacesCts.Token;
        IsAnalyzingFaces = true;
        AnalyzeFacesTotal = todo.Count;
        AnalyzeFacesProgress = 0;
        StatusText = $"Analysing faces… 0/{todo.Count}";

        // Convert the user-facing 0–100 threshold into a 0–1 probability in one
        // place; ONNX session is reentrant so we can fan out across cores.
        float threshold = AppSettings.Current.ClosedEyeThreshold / 100f;
        int parallelism = Math.Max(2, Math.Min(8, Environment.ProcessorCount - 1));

        try
        {
            int done = 0;
            await Task.Run(() =>
            {
                var po = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = parallelism };
                Parallel.ForEach(todo, po, photo =>
                {
                    if (ct.IsCancellationRequested) return;
                    try
                    {
                        // Prefer the cached preview JPEG (fast, on-disk). Fall
                        // back to the in-memory thumbnail if the preview hasn't
                        // been extracted yet — the analyser handles either size.
                        byte[]? jpeg = _cache.LoadPreview(photo.FileName)
                                    ?? _cache.LoadThumbnail(photo.FileName)
                                    ?? photo.ThumbnailJpeg;
                        if (jpeg == null) return;

                        var result = _faceAnalyzer.Analyze(jpeg, threshold);
                        if (result == null) return;

                        Application.Current?.Dispatcher.Invoke(() =>
                        {
                            photo.FaceCount = result.FaceCount;
                            photo.ClosedEyeCount = result.ClosedEyeCount;
                            photo.MinEyeOpenScore = result.MinEyeOpenScore;
                        });
                    }
                    catch { /* one bad photo shouldn't block the rest */ }
                    finally
                    {
                        int n = Interlocked.Increment(ref done);
                        if (n % 4 == 0 || n == todo.Count)
                        {
                            Application.Current?.Dispatcher.InvokeAsync(() =>
                            {
                                AnalyzeFacesProgress = n;
                                StatusText = $"Analysing faces… {n}/{todo.Count}";
                            });
                        }
                    }
                });
            }, ct);
        }
        catch (OperationCanceledException) { /* user cancelled */ }
        finally
        {
            // Persist results so a re-open of the folder reuses them.
            if (_db != null)
            {
                try { await Task.Run(() => _db.SaveBatch(AllPhotos)); }
                catch { /* persistence is best-effort; results live in memory */ }
            }

            IsAnalyzingFaces = false;
            StatusText = ct.IsCancellationRequested
                ? $"Face analysis cancelled at {AnalyzeFacesProgress}/{AnalyzeFacesTotal}."
                : $"Analysed {AnalyzeFacesProgress} photos.";
            _analyzeFacesCts?.Dispose();
            _analyzeFacesCts = null;

            // Bucket count + chip state may have changed for many photos.
            await Application.Current.Dispatcher.InvokeAsync(RefreshFilterBuckets);
            if (FaceFilter == FaceFilterMode.ClosedEyes)
                await Application.Current.Dispatcher.InvokeAsync(ApplyFilter);
        }
    }

    /// <summary>
    /// Drop PreviewJpeg/FullJpeg bytes for photos far from the current selection so
    /// memory stays bounded as the user browses. ThumbnailJpeg is kept (small, drives the grid).
    /// </summary>
    private void EvictFarPhotos(int currentIndex)
    {
        for (int i = 0; i < FilteredPhotos.Count; i++)
        {
            if (Math.Abs(i - currentIndex) <= KeepRadius) continue;
            var photo = FilteredPhotos[i];
            if (photo.PreviewJpeg != null) photo.PreviewJpeg = null;
            if (photo.FullJpeg != null) photo.FullJpeg = null;
        }
    }

    // Default screen-size decode for the main preview. LibRaw always extracts the
    // full sensor-sized JPEG (~6000x4000); decoding at this width uses the JPEG
    // codec's fast 1/2/1/4/1/8 native scaling, which is far faster than full decode.
    private const int PreviewDecodeWidth = 1920;

    // Target width for the cached linear-RAW preview buffer. ~2x display width gives
    // headroom for moderate zoom while keeping dither at display-relevant frequency.
    private const int LinearRawPreviewWidth = 2400;
    private const int ThumbnailDecodeWidth = 320;

    /// <summary>
    /// Downscale a JPEG to <paramref name="maxWidth"/> and bake any EXIF orientation into
    /// the pixel data. Output JPEG has no orientation tag, so consumers can render directly.
    /// Used at cache-write time so on-disk thumbnails/previews are small and fast to load.
    /// </summary>
    private static byte[]? ProcessJpegForCache(byte[] jpeg, int maxWidth)
    {
        try
        {
            double rotation = 0.0;
            try
            {
                using var msMeta = new MemoryStream(jpeg);
                var metaDecoder = BitmapDecoder.Create(msMeta, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                rotation = ReadExifRotation(metaDecoder.Frames[0].Metadata as BitmapMetadata);
            }
            catch { /* no EXIF */ }

            var bi = new BitmapImage();
            bi.BeginInit();
            bi.StreamSource = new MemoryStream(jpeg);
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.DecodePixelWidth = maxWidth;
            bi.EndInit();
            bi.Freeze();

            BitmapSource source = bi;
            if (rotation != 0.0)
            {
                var rotated = new TransformedBitmap(bi, new RotateTransform(rotation));
                rotated.Freeze();
                source = rotated;
            }

            var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var outMs = new MemoryStream();
            encoder.Save(outMs);
            return outMs.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource? LoadBitmapFromJpeg(byte[] jpeg, int decodePixelWidth = PreviewDecodeWidth)
    {
        try
        {
            // Read EXIF orientation from headers — cheap, no pixel decode.
            double rotation = 0.0;
            try
            {
                using var msMeta = new MemoryStream(jpeg);
                var metaDecoder = BitmapDecoder.Create(msMeta, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                rotation = ReadExifRotation(metaDecoder.Frames[0].Metadata as BitmapMetadata);
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

            var rotated = new TransformedBitmap(bi, new RotateTransform(rotation));
            rotated.Freeze();
            return rotated;
        }
        catch
        {
            return null;
        }
    }

    private static double ReadExifRotation(BitmapMetadata? metadata)
    {
        try
        {
            var raw = metadata?.GetQuery("/app1/ifd/{ushort=274}");
            if (raw == null) return 0.0;
            // GetQuery may return ushort/uint/int depending on codec — coerce defensively.
            int orientation = Convert.ToInt32(raw);
            return orientation switch
            {
                3 => 180.0,
                6 => 90.0,
                8 => 270.0,
                _ => 0.0
            };
        }
        catch { return 0.0; }
    }

    // ── Undo / Redo ──

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        var op = History.Undo();
        if (op != null) NavigateToHistoryTarget(op.Photo);
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        var op = History.Redo();
        if (op != null) NavigateToHistoryTarget(op.Photo);
    }

    private void NavigateToHistoryTarget(PhotoItem photo)
    {
        // Only re-select if the photo is currently visible; if a filter has hidden it
        // since the edit was recorded, the value still updates but selection stays put.
        if (FilteredPhotos.Contains(photo))
            SelectedPhoto = photo;
        UpdateStatus();
    }

    // ── Rating ──

    [RelayCommand]
    private void SetRating(int rating)
    {
        if (SelectedPhoto == null) return;
        var anchor = SelectedPhoto;
        var clamped = Math.Clamp(rating, 0, 5);
        // Toggle: pressing the same star already on the anchor clears the rating —
        // when applied across a multi-selection the anchor's prior state decides
        // the new value for every photo in the set, even if some had a different
        // rating to start with. Predictable beats clever.
        var newRating = anchor.Rating == clamped ? 0 : clamped;

        ApplyBulkRatingEdit(SelectedPhotosSnapshot(), newRating);
    }

    private List<PhotoItem> SelectedPhotosSnapshot() =>
        SelectedPhotos.Count == 0 && SelectedPhoto != null
            ? [SelectedPhoto]
            : SelectedPhotos.ToList();

    private void ApplyBulkRatingEdit(IList<PhotoItem> photos, int newRating)
    {
        var changes = photos
            .Select(p => (photo: p, oldRating: p.Rating))
            .Where(t => t.oldRating != newRating)
            .ToList();
        if (changes.Count == 0) return;
        var changedPhotos = changes.Select(c => c.photo).ToList();

        void ApplyAll()
        {
            foreach (var (p, _) in changes) p.Rating = newRating;
            SavePhotoBatch(changedPhotos);
        }
        void RevertAll()
        {
            foreach (var (p, old) in changes) p.Rating = old;
            SavePhotoBatch(changedPhotos);
        }

        ApplyAll();
        var label = changes.Count == 1
            ? $"Rating {changes[0].oldRating} → {newRating}"
            : $"Rating → {newRating} ({changes.Count} photos)";
        History.Record(new EditOp(label, SelectedPhoto ?? changes[0].photo, ApplyAll, RevertAll));
    }

    private void ApplyRatingEdit(PhotoItem photo, int rating)
    {
        photo.Rating = rating;
        SavePhoto(photo);
    }

    // ── Flagging ──

    [RelayCommand]
    private void TogglePick()
    {
        if (SelectedPhoto == null) return;
        var newFlag = SelectedPhoto.Flag == CullFlag.Pick ? CullFlag.Unflagged : CullFlag.Pick;
        ApplyBulkFlagEdit(SelectedPhotosSnapshot(), newFlag);
    }

    [RelayCommand]
    private void ToggleReject()
    {
        if (SelectedPhoto == null) return;
        var newFlag = SelectedPhoto.Flag == CullFlag.Reject ? CullFlag.Unflagged : CullFlag.Reject;
        ApplyBulkFlagEdit(SelectedPhotosSnapshot(), newFlag);
    }

    [RelayCommand]
    private void Unflag()
    {
        if (SelectedPhoto == null) return;
        ApplyBulkFlagEdit(SelectedPhotosSnapshot(), CullFlag.Unflagged);
    }

    private void ApplyBulkFlagEdit(IList<PhotoItem> photos, CullFlag newFlag)
    {
        var changes = photos
            .Select(p => (photo: p, oldFlag: p.Flag))
            .Where(t => t.oldFlag != newFlag)
            .ToList();
        if (changes.Count == 0) return;
        var changedPhotos = changes.Select(c => c.photo).ToList();

        void ApplyAll()
        {
            foreach (var (p, _) in changes) p.Flag = newFlag;
            SavePhotoBatch(changedPhotos);
        }
        void RevertAll()
        {
            foreach (var (p, old) in changes) p.Flag = old;
            SavePhotoBatch(changedPhotos);
        }

        ApplyAll();
        var label = changes.Count == 1
            ? $"Flag {changes[0].oldFlag} → {newFlag}"
            : $"Flag → {newFlag} ({changes.Count} photos)";
        History.Record(new EditOp(label, SelectedPhoto ?? changes[0].photo, ApplyAll, RevertAll));
    }

    private void ApplyFlagEdit(PhotoItem photo, CullFlag flag)
    {
        photo.Flag = flag;
        SavePhoto(photo);
    }

    // ── Color labels ──

    [RelayCommand]
    private void SetColorLabel(ColorLabel label)
    {
        if (SelectedPhoto == null) return;
        var newLabel = SelectedPhoto.ColorLabel == label ? ColorLabel.None : label;
        ApplyBulkColorLabelEdit(SelectedPhotosSnapshot(), newLabel);
    }

    private void ApplyBulkColorLabelEdit(IList<PhotoItem> photos, ColorLabel newLabel)
    {
        var changes = photos
            .Select(p => (photo: p, oldLabel: p.ColorLabel))
            .Where(t => t.oldLabel != newLabel)
            .ToList();
        if (changes.Count == 0) return;
        var changedPhotos = changes.Select(c => c.photo).ToList();

        void ApplyAll()
        {
            foreach (var (p, _) in changes) p.ColorLabel = newLabel;
            SavePhotoBatch(changedPhotos);
        }
        void RevertAll()
        {
            foreach (var (p, old) in changes) p.ColorLabel = old;
            SavePhotoBatch(changedPhotos);
        }

        ApplyAll();
        var label = changes.Count == 1
            ? $"Color {changes[0].oldLabel} → {newLabel}"
            : $"Color → {newLabel} ({changes.Count} photos)";
        History.Record(new EditOp(label, SelectedPhoto ?? changes[0].photo, ApplyAll, RevertAll));
    }

    private void ApplyColorLabelEdit(PhotoItem photo, ColorLabel label)
    {
        photo.ColorLabel = label;
        SavePhoto(photo);
    }

    // ── Filtering ──

    [RelayCommand]
    private void ClearRatingFilter()
    {
        RatingFilterMode = RatingFilterMode.Any;
        RatingFilterExclude = false;
        ApplyFilter();
    }

    [RelayCommand]
    private void CycleRatingMode()
    {
        RatingCycleMode = RatingCycleMode switch
        {
            RatingFilterMode.Exact    => RatingFilterMode.AtLeast,
            RatingFilterMode.AtLeast  => RatingFilterMode.LessThan,
            _                         => RatingFilterMode.Exact
        };
        if (RatingFilterMode != RatingFilterMode.Any)
        {
            RatingFilterMode = RatingCycleMode;
            ApplyFilter();
        }
    }

    [RelayCommand]
    private void SetRatingValue(int value)
    {
        if (RatingFilterMode == RatingCycleMode && RatingFilterValue == value)
        {
            RatingFilterMode = RatingFilterMode.Any;
            RatingFilterExclude = false;
        }
        else
        {
            RatingFilterMode = RatingCycleMode;
            RatingFilterValue = value;
        }
        ApplyFilter();
    }

    // ── Copy criteria ──

    [RelayCommand]
    private void ClearCopyRatingFilter() => CopyRatingFilterMode = RatingFilterMode.Any;

    [RelayCommand]
    private void CycleCopyRatingMode()
    {
        CopyRatingCycleMode = CopyRatingCycleMode switch
        {
            RatingFilterMode.Exact    => RatingFilterMode.AtLeast,
            RatingFilterMode.AtLeast  => RatingFilterMode.LessThan,
            _                         => RatingFilterMode.Exact
        };
        if (CopyRatingFilterMode != RatingFilterMode.Any)
            CopyRatingFilterMode = CopyRatingCycleMode;
    }

    [RelayCommand]
    private void SetCopyRatingValue(int value)
    {
        if (CopyRatingFilterMode == CopyRatingCycleMode && CopyRatingFilterValue == value)
            CopyRatingFilterMode = RatingFilterMode.Any;
        else
        {
            CopyRatingFilterMode = CopyRatingCycleMode;
            CopyRatingFilterValue = value;
        }
    }

    [RelayCommand]
    private void SetCopyFlagFilter(CullFlag flag) => CopyFlagFilter = CopyFlagFilter == flag ? null : flag;

    [RelayCommand]
    private void ClearCopyFlagFilter() => CopyFlagFilter = null;

    [RelayCommand]
    private void SetCopyColorLabelFilter(ColorLabel label) => CopyColorLabelFilter = CopyColorLabelFilter == label ? null : label;

    [RelayCommand]
    private void ClearCopyColorLabelFilter() => CopyColorLabelFilter = null;

    // ── Flag filter ──

    [RelayCommand]
    private void SetFlagFilter(CullFlag flag)
    {
        FlagFilter = FlagFilter == flag ? null : flag;
        if (!FlagFilter.HasValue) FlagFilterExclude = false;
        ApplyFilter();
    }

    [RelayCommand]
    private void ClearFlagFilter()
    {
        FlagFilter = null;
        FlagFilterExclude = false;
        ApplyFilter();
    }

    [RelayCommand]
    private void SetColorLabelFilter(ColorLabel label)
    {
        ColorLabelFilter = ColorLabelFilter == label ? null : label;
        if (!ColorLabelFilter.HasValue) ColorLabelFilterExclude = false;
        ApplyFilter();
    }

    [RelayCommand]
    private void ClearColorLabelFilter()
    {
        ColorLabelFilter = null;
        ColorLabelFilterExclude = false;
        ApplyFilter();
    }

    // ── Tag commands ──

    [RelayCommand]
    private void SetTagFilter(PhotoTag tag)
    {
        TagFilter = TagFilter?.Id == tag.Id ? null : tag;
        if (TagFilter == null) TagFilterExclude = false;
        ApplyFilter();
    }

    [RelayCommand]
    private void ClearTagFilter()
    {
        TagFilter = null;
        TagFilterExclude = false;
        ApplyFilter();
    }

    [RelayCommand]
    private void CreateTag()
    {
        if (_db == null)
        {
            MessageBox.Show(
                "Open a folder first to start creating tags.",
                "No folder open",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }
        var name = InputDialog.Show(Application.Current.MainWindow, "New Tag", "Tag name:");
        if (string.IsNullOrWhiteSpace(name)) return;
        var tag = _db.CreateGroup(name);
        Tags.Add(tag);
    }

    [RelayCommand]
    private void RenameTag(PhotoTag tag)
    {
        if (_db == null) return;
        var name = InputDialog.Show(Application.Current.MainWindow, "Rename Tag", "New name:", tag.Name);
        if (string.IsNullOrWhiteSpace(name) || name == tag.Name) return;
        _db.RenameGroup(tag.Id, name);
        tag.Name = name;
        var idx = Tags.IndexOf(tag);
        if (idx >= 0)
        {
            Tags.RemoveAt(idx);
            Tags.Insert(idx, tag);
        }
        if (TagFilter?.Id == tag.Id)
        {
            TagFilter = tag;
            UpdateFilterDescription();
        }
        foreach (var photo in AllPhotos.Where(p => p.TagIds.Contains(tag.Id)))
            UpdateTagDisplay(photo);
    }

    [RelayCommand]
    private void DeleteTag(PhotoTag tag)
    {
        if (_db == null) return;
        _db.DeleteGroup(tag.Id);
        foreach (var photo in AllPhotos.Where(p => p.TagIds.Contains(tag.Id)))
        {
            photo.TagIds.Remove(tag.Id);
            UpdateTagDisplay(photo);
        }
        Tags.Remove(tag);
        if (TagFilter?.Id == tag.Id)
        {
            TagFilter = null;
            ApplyFilter();
        }
        OnPropertyChanged(nameof(SelectedPhotoTagAssignments));
    }

    [RelayCommand]
    private void ToggleTagForSelected(PhotoTag tag)
    {
        if (SelectedPhoto == null || _db == null) return;
        // Anchor's prior assignment decides direction; the same op is applied to
        // every selected photo, even those that already match the target state
        // (those become no-ops in ApplyTagEdit). Single compound undo entry.
        var assignToAll = !SelectedPhoto.TagIds.Contains(tag.Id);

        var photos = SelectedPhotosSnapshot();
        var changedPhotos = photos
            .Where(p => p.TagIds.Contains(tag.Id) != assignToAll)
            .ToList();
        if (changedPhotos.Count == 0) return;

        void ApplyAll()
        {
            // One SQLite transaction for the whole bulk op — otherwise each
            // photo's AssignGroup/UnassignGroup fsyncs separately and the UI
            // visibly stalls on 20+ photos.
            if (_db != null)
                _db.WithTransaction(() =>
                {
                    foreach (var p in changedPhotos) ApplyTagEdit(p, tag, assignToAll);
                });
            else
                foreach (var p in changedPhotos) ApplyTagEdit(p, tag, assignToAll);
            if (TagFilter != null) ApplyFilter();
        }
        void RevertAll()
        {
            if (_db != null)
                _db.WithTransaction(() =>
                {
                    foreach (var p in changedPhotos) ApplyTagEdit(p, tag, !assignToAll);
                });
            else
                foreach (var p in changedPhotos) ApplyTagEdit(p, tag, !assignToAll);
            if (TagFilter != null) ApplyFilter();
        }

        ApplyAll();
        var verb = assignToAll ? "Add" : "Remove";
        var label = changedPhotos.Count == 1
            ? $"{verb} tag “{tag.Name}”"
            : $"{verb} tag “{tag.Name}” ({changedPhotos.Count} photos)";
        History.Record(new EditOp(label, SelectedPhoto, ApplyAll, RevertAll));
    }

    private void ApplyTagEdit(PhotoItem photo, PhotoTag tag, bool assign)
    {
        if (_db == null) return;
        if (assign)
        {
            if (photo.TagIds.Add(tag.Id))
                _db.AssignGroup(photo.FileName, tag.Id);
        }
        else
        {
            if (photo.TagIds.Remove(tag.Id))
                _db.UnassignGroup(photo.FileName, tag.Id);
        }
        UpdateTagDisplay(photo);
        ScheduleXmpWrite(photo);
        if (ReferenceEquals(photo, SelectedPhoto))
            OnPropertyChanged(nameof(SelectedPhotoTagAssignments));
        // Refilter is the caller's responsibility — bulk tag ops batch one ApplyFilter
        // at the end of the loop to avoid clearing the multi-selection mid-flight.
    }

    [RelayCommand]
    private void AssignTagByIndex(int index)
    {
        if (index < 0 || index >= Tags.Count) return;
        ToggleTagForSelected(Tags[index]);
    }

    private void UpdateTagDisplay(PhotoItem photo)
    {
        photo.TagDisplay = photo.TagIds.Count == 0
            ? ""
            : string.Join("\n", photo.TagIds
                .Select(id => Tags.FirstOrDefault(t => t.Id == id)?.Name)
                .Where(n => n != null));
    }

    [RelayCommand]
    private void ClearFilters()
    {
        RatingFilterMode = RatingFilterMode.Any;
        FlagFilter = null;
        ColorLabelFilter = null;
        TagFilter = null;
        BurstFilter = BurstFilterMode.Any;
        ImageTypeFilter = ImageTypeFilterMode.Any;
        ExposureFilter = ExposureFilterMode.Any;
        FaceFilter = FaceFilterMode.Any;
        TimeOfDayStartMinutes = 0;
        TimeOfDayEndMinutes = 1440;
        RatingFilterExclude = false;
        FlagFilterExclude = false;
        ColorLabelFilterExclude = false;
        TagFilterExclude = false;
        BurstFilterExclude = false;
        ImageTypeFilterExclude = false;
        ExposureFilterExclude = false;
        FaceFilterExclude = false;
        TimeOfDayFilterExclude = false;
        RegionFilterMinLat = null;
        RegionFilterMaxLat = null;
        RegionFilterMinLon = null;
        RegionFilterMaxLon = null;
        RegionFilterExclude = false;
        ApplyFilter();
    }

    [RelayCommand]
    private void ClearTimeOfDayFilter()
    {
        TimeOfDayStartMinutes = 0;
        TimeOfDayEndMinutes = 1440;
        TimeOfDayFilterExclude = false;
    }

    /// <summary>
    /// Sets all four geographic bounds at once and re-applies the filter. Called
    /// from the Map window when the user finishes drawing a selection rectangle.
    /// </summary>
    public void SetRegionFilter(double minLat, double minLon, double maxLat, double maxLon)
    {
        RegionFilterMinLat = Math.Min(minLat, maxLat);
        RegionFilterMaxLat = Math.Max(minLat, maxLat);
        RegionFilterMinLon = Math.Min(minLon, maxLon);
        RegionFilterMaxLon = Math.Max(minLon, maxLon);
        ApplyFilter();
    }

    [RelayCommand]
    private void ClearRegionFilter()
    {
        RegionFilterMinLat = null;
        RegionFilterMaxLat = null;
        RegionFilterMinLon = null;
        RegionFilterMaxLon = null;
        RegionFilterExclude = false;
        ApplyFilter();
    }

    // ── Burst filter ──

    [RelayCommand]
    private void SetBurstFilter(BurstFilterMode mode)
    {
        BurstFilter = BurstFilter == mode ? BurstFilterMode.Any : mode;
        if (BurstFilter == BurstFilterMode.Any) BurstFilterExclude = false;
        ApplyFilter();
    }

    [RelayCommand]
    private void ClearBurstFilter()
    {
        BurstFilter = BurstFilterMode.Any;
        BurstFilterExclude = false;
        ApplyFilter();
    }

    // ── Image type filter ──

    [RelayCommand]
    private void SetImageTypeFilter(ImageTypeFilterMode mode)
    {
        ImageTypeFilter = ImageTypeFilter == mode ? ImageTypeFilterMode.Any : mode;
        if (ImageTypeFilter == ImageTypeFilterMode.Any) ImageTypeFilterExclude = false;
        ApplyFilter();
    }

    [RelayCommand]
    private void ClearImageTypeFilter()
    {
        ImageTypeFilter = ImageTypeFilterMode.Any;
        ImageTypeFilterExclude = false;
        ApplyFilter();
    }

    // ── Exposure filter (filter popup; combines with other criteria) ──

    [RelayCommand]
    private void SetExposureFilter(ExposureFilterMode mode)
    {
        ExposureFilter = ExposureFilter == mode ? ExposureFilterMode.Any : mode;
        if (ExposureFilter == ExposureFilterMode.Any) ExposureFilterExclude = false;
        ApplyFilter();
    }

    [RelayCommand]
    private void ClearExposureFilter()
    {
        ExposureFilter = ExposureFilterMode.Any;
        ExposureFilterExclude = false;
        ApplyFilter();
    }

    [RelayCommand]
    private void ToggleBurstCollapse() => BurstCollapsed = !BurstCollapsed;

    /// <summary>Returns every PhotoItem in the burst, ordered by capture time.</summary>
    public List<PhotoItem> GetBurstMembers(int groupId) =>
        AllPhotos
            .Where(p => p.GroupId == groupId)
            .OrderBy(p => p.Metadata?.CaptureTime ?? DateTime.MinValue)
            .ThenBy(p => p.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Picks the burst member most likely to be the user's favourite.
    /// Priority: rated+picked > highest rated > any pick > first chronologically.
    /// </summary>
    private static PhotoItem SelectBurstRepresentative(List<PhotoItem> members)
    {
        if (AppSettings.Current.BurstThumbnailMode == BurstThumbnailMode.FirstChronological)
            return members[0];

        // HighestRated: rated+picked > highest rated > any pick > first chronologically
        var ratedPick = members
            .Where(p => p.Rating > 0 && p.Flag == CullFlag.Pick)
            .OrderByDescending(p => p.Rating)
            .FirstOrDefault();
        if (ratedPick != null) return ratedPick;

        var topRated = members
            .Where(p => p.Rating > 0)
            .OrderByDescending(p => p.Rating)
            .FirstOrDefault();
        if (topRated != null) return topRated;

        var picked = members.FirstOrDefault(p => p.Flag == CullFlag.Pick);
        if (picked != null) return picked;

        return members[0];
    }

    public IPreviewExtractor Extractor => _extractor;

    public void PersistPhoto(PhotoItem photo)
    {
        _db?.Save(photo);
        ScheduleXmpWrite(photo);
    }

    private void ScheduleXmpWrite(PhotoItem photo)
    {
        if (_xmpWriter == null || photo.IsVideo) return;
        // Tag IDs → names lookup is rebuilt per call. Tag count is small (typically
        // single digits) so the cost is negligible compared to the write itself.
        var tagNames = Tags.ToDictionary(t => t.Id, t => t.Name);
        _xmpWriter.Schedule(photo.FilePath, XmpSidecar.Snapshot(photo, tagNames));
    }

    /// <summary>
    /// Apply a batch of parsed sidecar reads to their target photos. Mutates
    /// observable PhotoItem properties so it must run on the UI thread; reuses
    /// or creates tags as needed and persists every touched row to SQLite.
    /// </summary>
    private void ApplyXmpMerges(List<(PhotoItem photo, XmpData data)> merges)
    {
        if (_db == null) return;
        foreach (var (photo, data) in merges)
        {
            if (data.Rating.HasValue)
            {
                if (data.Rating.Value == -1)
                {
                    photo.Flag = CullFlag.Reject;
                }
                else if (data.Rating.Value >= 0 && data.Rating.Value <= 5)
                {
                    photo.Rating = data.Rating.Value;
                }
            }
            photo.ColorLabel = data.Label switch
            {
                "Red"    => ColorLabel.Red,
                "Yellow" => ColorLabel.Yellow,
                "Green"  => ColorLabel.Green,
                "Blue"   => ColorLabel.Blue,
                "Purple" => ColorLabel.Purple,
                _        => photo.ColorLabel,
            };

            foreach (var keyword in data.Keywords)
            {
                if (keyword == XmpSidecar.PickKeyword)   { photo.Flag = CullFlag.Pick;   continue; }
                if (keyword == XmpSidecar.RejectKeyword) { photo.Flag = CullFlag.Reject; continue; }

                var tag = Tags.FirstOrDefault(t => string.Equals(t.Name, keyword, StringComparison.OrdinalIgnoreCase));
                if (tag == null)
                {
                    tag = _db.CreateGroup(keyword);
                    Tags.Add(tag);
                }
                if (photo.TagIds.Add(tag.Id))
                    _db.AssignGroup(photo.FileName, tag.Id);
            }
            UpdateTagDisplay(photo);
            _db.Save(photo);
        }
    }

    /// <summary>
    /// Force an immediate XMP sidecar write for every photo in the current
    /// folder, bypassing the debounce queue. Used for the toolbar
    /// "Sync metadata to XMP" command — typical case is the first export of an
    /// already-culled folder, so users don't have to touch every photo to get
    /// sidecars written.
    /// </summary>
    [RelayCommand]
    private async Task SyncAllXmpAsync()
    {
        if (AllPhotos.Count == 0)
        {
            StatusText = "No photos in this folder.";
            return;
        }

        var tagNames = Tags.ToDictionary(t => t.Id, t => t.Name);
        var snapshots = AllPhotos
            .Where(p => !p.IsVideo)
            .Select(p => (path: p.FilePath, data: XmpSidecar.Snapshot(p, tagNames)))
            .ToList();
        StatusText = $"Writing XMP for {snapshots.Count} photos...";

        int written = 0;
        await Task.Run(() =>
        {
            foreach (var (path, data) in snapshots)
            {
                try { XmpSidecar.Write(path, data); written++; }
                catch { /* skip files we can't write next to (read-only media, locked, …) */ }
            }
        });

        StatusText = $"Wrote {written}/{snapshots.Count} XMP sidecars.";
    }

    /// <summary>
    /// Re-runs burst detection with the current AppSettings and refreshes the view.
    /// Call after AppSettings.Current has been updated.
    /// </summary>
    public void ApplyBurstSettings()
    {
        if (AllPhotos.Count == 0) return;
        var (loose, strict) = BurstDetector.ThresholdsFromStrictness(AppSettings.Current.BurstSimilarityStrictness);
        BurstCount = BurstDetector.Detect(AllPhotos,
            TimeSpan.FromSeconds(AppSettings.Current.BurstMaxGapSeconds),
            looseHammingThreshold: loose,
            strictHammingThreshold: strict);
        ApplyFilter();
    }

    [RelayCommand]
    private void NextBurst()
    {
        if (FilteredPhotos.Count == 0) return;
        var start = SelectedIndex < 0 ? 0 : SelectedIndex;
        var startGroup = FilteredPhotos[start].GroupId;
        for (int step = 1; step <= FilteredPhotos.Count; step++)
        {
            int i = (start + step) % FilteredPhotos.Count;
            var g = FilteredPhotos[i].GroupId;
            if (g > 0 && g != startGroup) { SelectedIndex = i; return; }
        }
    }

    [RelayCommand]
    private void PreviousBurst()
    {
        if (FilteredPhotos.Count == 0) return;
        var start = SelectedIndex < 0 ? 0 : SelectedIndex;
        var startGroup = FilteredPhotos[start].GroupId;
        // Walk backward to find the first photo of the previous burst.
        int prev = -1;
        for (int step = 1; step <= FilteredPhotos.Count; step++)
        {
            int i = (start - step + FilteredPhotos.Count) % FilteredPhotos.Count;
            var g = FilteredPhotos[i].GroupId;
            if (g > 0 && g != startGroup) { prev = i; break; }
        }
        if (prev < 0) return;
        // Walk back further while still inside that same burst, to land on its first frame.
        var targetGroup = FilteredPhotos[prev].GroupId;
        while (true)
        {
            int j = (prev - 1 + FilteredPhotos.Count) % FilteredPhotos.Count;
            if (j == start || FilteredPhotos[j].GroupId != targetGroup) break;
            prev = j;
        }
        SelectedIndex = prev;
    }

    // ── Sorting ──

    [RelayCommand]
    private void ToggleSortDirection() => SortDescending = !SortDescending;

    private IPreviewExtractor ExtractorFor(PhotoItem photo)
    {
        if (photo.IsVideo) return _videoExtractor;
        return photo.IsRaw ? _extractor : _wicExtractor;
    }

    private IEnumerable<PhotoItem> ApplySorting(IEnumerable<PhotoItem> items) => SortField switch
    {
        SortField.Rating => SortDescending
            ? items.OrderByDescending(p => p.Rating)
            : items.OrderBy(p => p.Rating),
        SortField.CaptureDate => SortDescending
            ? items.OrderByDescending(p => p.Metadata?.CaptureTime ?? DateTime.MinValue)
            : items.OrderBy(p => p.Metadata?.CaptureTime ?? DateTime.MaxValue),
        SortField.ColorLabel => SortDescending
            ? items.OrderByDescending(p => (int)p.ColorLabel)
            : items.OrderBy(p => (int)p.ColorLabel),
        SortField.Flag => SortDescending
            ? items.OrderByDescending(p => (int)p.Flag)
            : items.OrderBy(p => (int)p.Flag),
        SortField.Burst => SortDescending
            // Bursts first (descending by group id, then by capture time inside each).
            ? items.OrderByDescending(p => p.GroupId)
                   .ThenBy(p => p.Metadata?.CaptureTime ?? DateTime.MinValue)
                   .ThenBy(p => p.FileName, StringComparer.OrdinalIgnoreCase)
            // Singles first (group id 0), then bursts grouped by id, capture time inside.
            : items.OrderBy(p => p.GroupId == 0 ? 0 : 1)
                   .ThenBy(p => p.GroupId)
                   .ThenBy(p => p.Metadata?.CaptureTime ?? DateTime.MinValue)
                   .ThenBy(p => p.FileName, StringComparer.OrdinalIgnoreCase),
        SortField.ImageType => SortDescending
            // Video → JPG → RAW
            ? items.OrderByDescending(p => p.IsVideo ? 2 : (p.IsRaw ? 0 : 1))
                   .ThenBy(p => p.FileName, StringComparer.OrdinalIgnoreCase)
            // RAW → JPG → Video
            : items.OrderBy(p => p.IsVideo ? 2 : (p.IsRaw ? 0 : 1))
                   .ThenBy(p => p.FileName, StringComparer.OrdinalIgnoreCase),
        _ => SortDescending
            ? items.OrderByDescending(p => p.FileName, StringComparer.OrdinalIgnoreCase)
            : items.OrderBy(p => p.FileName, StringComparer.OrdinalIgnoreCase)
    };

    public void ApplyFilter()
    {
        var previousSelection = SelectedPhoto;
        // Filter or burst-collapse change wipes any multi-selection — RestoreSelection
        // at the end re-adds the (possibly remapped) anchor via the reconcile path.
        ClearAllSelection();
        FilteredPhotos.Clear();

        IEnumerable<PhotoItem> visible = AllPhotos;

        Func<PhotoItem, bool>? ratingPred = RatingFilterMode switch
        {
            RatingFilterMode.Exact    => p => p.Rating == RatingFilterValue,
            RatingFilterMode.AtLeast  => p => p.Rating >= RatingFilterValue,
            RatingFilterMode.LessThan => p => p.Rating <  RatingFilterValue,
            _                         => null
        };
        if (ratingPred != null)
            visible = RatingFilterExclude ? visible.Where(p => !ratingPred(p)) : visible.Where(ratingPred);

        if (FlagFilter.HasValue)
        {
            var f = FlagFilter.Value;
            visible = FlagFilterExclude ? visible.Where(p => p.Flag != f) : visible.Where(p => p.Flag == f);
        }
        if (ColorLabelFilter.HasValue)
        {
            var c = ColorLabelFilter.Value;
            visible = ColorLabelFilterExclude ? visible.Where(p => p.ColorLabel != c) : visible.Where(p => p.ColorLabel == c);
        }
        if (TagFilter != null)
        {
            var tagId = TagFilter.Id;
            visible = TagFilterExclude ? visible.Where(p => !p.TagIds.Contains(tagId)) : visible.Where(p => p.TagIds.Contains(tagId));
        }

        Func<PhotoItem, bool>? typePred = ImageTypeFilter switch
        {
            ImageTypeFilterMode.RawOnly   => p => p.IsRaw,
            ImageTypeFilterMode.JpegOnly  => p => !p.IsRaw && !p.IsVideo,
            ImageTypeFilterMode.VideoOnly => p => p.IsVideo,
            _                             => null
        };
        if (typePred != null)
            visible = ImageTypeFilterExclude ? visible.Where(p => !typePred(p)) : visible.Where(typePred);

        Func<PhotoItem, bool>? burstPred = BurstFilter switch
        {
            BurstFilterMode.OnlyInBursts => p => p.GroupId >  0,
            BurstFilterMode.OnlySingles  => p => p.GroupId == 0,
            _                            => null
        };
        if (burstPred != null)
            visible = BurstFilterExclude ? visible.Where(p => !burstPred(p)) : visible.Where(burstPred);

        if (ExposureFilter != ExposureFilterMode.Any)
        {
            float gate = AppSettings.Current.ClippedAreaThreshold;
            Func<PhotoItem, bool> exposurePred = ExposureFilter == ExposureFilterMode.ClippedHighlights
                ? (p => p.HighlightClippedPct.HasValue && p.HighlightClippedPct.Value >= gate)
                : (p => p.ShadowClippedPct.HasValue    && p.ShadowClippedPct.Value    >= gate);
            visible = ExposureFilterExclude ? visible.Where(p => !exposurePred(p)) : visible.Where(exposurePred);
        }

        if (FaceFilter == FaceFilterMode.ClosedEyes)
        {
            // Same convention as Exposure: only photos where the analysis has run
            // (ClosedEyeCount has a value) are eligible. Photos that haven't been
            // analysed yet stay out of both sides of the include/exclude split so
            // the filter doesn't lie about them.
            Func<PhotoItem, bool> facePred = p => p.ClosedEyeCount.HasValue && p.ClosedEyeCount.Value > 0;
            Func<PhotoItem, bool> analysed = p => p.ClosedEyeCount.HasValue;
            visible = FaceFilterExclude
                ? visible.Where(p => analysed(p) && !facePred(p))
                : visible.Where(facePred);
        }

        if (IsTimeOfDayFilterActive)
        {
            int start = TimeOfDayStartMinutes;
            int end   = TimeOfDayEndMinutes;
            // Photos without a CaptureTime stay out of both sides of the
            // include/exclude split, matching the Face/Exposure convention so
            // un-analysable photos don't lie either way.
            Func<PhotoItem, bool> hasTime = p => p.Metadata?.CaptureTime.HasValue == true;
            Func<PhotoItem, bool> inWindow = p =>
            {
                var t = p.Metadata!.CaptureTime!.Value;
                int mins = t.Hour * 60 + t.Minute;
                // start <= end is the simple "same day" window; start > end means
                // the window straddles midnight (e.g. 22:00 → 04:00 = night).
                return start <= end
                    ? mins >= start && mins < end
                    : mins >= start || mins < end;
            };
            visible = TimeOfDayFilterExclude
                ? visible.Where(p => hasTime(p) && !inWindow(p))
                : visible.Where(p => hasTime(p) && inWindow(p));
        }

        if (IsRegionFilterActive)
        {
            double minLat = RegionFilterMinLat!.Value;
            double maxLat = RegionFilterMaxLat!.Value;
            double minLon = RegionFilterMinLon!.Value;
            double maxLon = RegionFilterMaxLon!.Value;
            // Photos without GPS coordinates stay out of both sides of the
            // include/exclude split — same convention as Time-of-day / Face.
            Func<PhotoItem, bool> hasGps = p => p.Metadata?.GpsLatitude.HasValue == true && p.Metadata?.GpsLongitude.HasValue == true;
            Func<PhotoItem, bool> inBox = p =>
            {
                double lat = p.Metadata!.GpsLatitude!.Value;
                double lon = p.Metadata!.GpsLongitude!.Value;
                return lat >= minLat && lat <= maxLat && lon >= minLon && lon <= maxLon;
            };
            visible = RegionFilterExclude
                ? visible.Where(p => hasGps(p) && !inBox(p))
                : visible.Where(p => hasGps(p) && inBox(p));
        }

        var sorted = ApplySorting(visible).ToList();

        // Reset any prior collapse markers — collapse is purely a presentation
        // pass derived from the current filter, never persisted.
        foreach (var p in AllPhotos)
            if (p.CollapsedBurstCount != 0) p.CollapsedBurstCount = 0;

        if (BurstCollapsed)
        {
            // Per burst: keep the chronologically first matching photo as the
            // representative (its CollapsedBurstCount = matching count for that
            // burst). Hide the other matching members.
            var membersByGroup = sorted
                .Where(p => p.GroupId > 0)
                .GroupBy(p => p.GroupId)
                .ToDictionary(g => g.Key, g => g
                    .OrderBy(p => p.Metadata?.CaptureTime ?? DateTime.MinValue)
                    .ThenBy(p => p.FileName, StringComparer.OrdinalIgnoreCase)
                    .ToList());

            var seenGroups = new HashSet<int>();
            foreach (var photo in sorted)
            {
                if (photo.GroupId == 0)
                {
                    FilteredPhotos.Add(photo);
                    continue;
                }
                if (!seenGroups.Add(photo.GroupId)) continue; // already represented
                var members = membersByGroup[photo.GroupId];
                var rep = SelectBurstRepresentative(members);
                rep.CollapsedBurstCount = members.Count;
                FilteredPhotos.Add(rep);
            }
        }
        else
        {
            foreach (var photo in sorted)
                FilteredPhotos.Add(photo);
        }

        VisibleCount = FilteredPhotos.Count;
        UpdateFilterDescription();

        RestoreSelection(previousSelection);
        RefreshFilterBuckets();
        OnPropertyChanged(nameof(CopyTargetCount));
        SaveSessionIfNeeded();
    }

    // ── Per-folder session persistence ──

    private FolderSession CaptureSessionState() => new()
    {
        LastSelectedFile = SelectedPhoto?.FileName,
        RatingFilterMode = RatingFilterMode,
        RatingFilterValue = RatingFilterValue,
        RatingCycleMode = RatingCycleMode,
        FlagFilter = FlagFilter,
        ColorLabelFilter = ColorLabelFilter,
        BurstFilter = BurstFilter,
        ImageTypeFilter = ImageTypeFilter,
        ExposureFilter = ExposureFilter,
        FaceFilter = FaceFilter,
        TagFilterId = TagFilter?.Id,
        RatingFilterExclude = RatingFilterExclude,
        FlagFilterExclude = FlagFilterExclude,
        ColorLabelFilterExclude = ColorLabelFilterExclude,
        TagFilterExclude = TagFilterExclude,
        BurstFilterExclude = BurstFilterExclude,
        ImageTypeFilterExclude = ImageTypeFilterExclude,
        ExposureFilterExclude = ExposureFilterExclude,
        FaceFilterExclude = FaceFilterExclude,
        TimeOfDayStartMinutes = TimeOfDayStartMinutes,
        TimeOfDayEndMinutes = TimeOfDayEndMinutes,
        TimeOfDayFilterExclude = TimeOfDayFilterExclude,
        BurstCollapsed = BurstCollapsed,
        SortField = SortField,
        SortDescending = SortDescending,
    };

    private void ApplySessionState(FolderSession s)
    {
        // Filters
        RatingFilterMode = s.RatingFilterMode;
        RatingFilterValue = s.RatingFilterValue;
        RatingCycleMode = s.RatingCycleMode == RatingFilterMode.Any ? RatingFilterMode.Exact : s.RatingCycleMode;
        FlagFilter = s.FlagFilter;
        ColorLabelFilter = s.ColorLabelFilter;
        BurstFilter = s.BurstFilter;
        ImageTypeFilter = s.ImageTypeFilter;
        ExposureFilter = s.ExposureFilter;
        FaceFilter = s.FaceFilter;
        TagFilter = s.TagFilterId.HasValue
            ? Tags.FirstOrDefault(t => t.Id == s.TagFilterId.Value)
            : null;

        RatingFilterExclude = s.RatingFilterExclude;
        FlagFilterExclude = s.FlagFilterExclude;
        ColorLabelFilterExclude = s.ColorLabelFilterExclude;
        TagFilterExclude = s.TagFilterExclude;
        BurstFilterExclude = s.BurstFilterExclude;
        ImageTypeFilterExclude = s.ImageTypeFilterExclude;
        ExposureFilterExclude = s.ExposureFilterExclude;
        FaceFilterExclude = s.FaceFilterExclude;

        TimeOfDayStartMinutes = Math.Clamp(s.TimeOfDayStartMinutes, 0, 1440);
        TimeOfDayEndMinutes = Math.Clamp(s.TimeOfDayEndMinutes <= 0 ? 1440 : s.TimeOfDayEndMinutes, 0, 1440);
        TimeOfDayFilterExclude = s.TimeOfDayFilterExclude;

        if (s.BurstCollapsed.HasValue) BurstCollapsed = s.BurstCollapsed.Value;
        if (s.SortField.HasValue) SortField = s.SortField.Value;
        SortDescending = s.SortDescending;
    }

    private void SaveSessionIfNeeded()
    {
        if (_suppressSessionSave) return;
        if (string.IsNullOrEmpty(_sessionFolder)) return;
        if (!Directory.Exists(_sessionFolder)) return;
        CaptureSessionState().Save(_sessionFolder);
    }

    private void RestoreSelection(PhotoItem? previousSelection)
    {
        if (previousSelection != null)
        {
            var idx = FilteredPhotos.IndexOf(previousSelection);
            if (idx >= 0)
            {
                SelectedIndex = idx;
                return;
            }
            // Hidden because we just collapsed a burst the user was inside —
            // map them to that burst's representative so focus stays put.
            if (previousSelection.GroupId > 0)
            {
                for (int i = 0; i < FilteredPhotos.Count; i++)
                {
                    if (FilteredPhotos[i].GroupId == previousSelection.GroupId)
                    {
                        SelectedIndex = i;
                        return;
                    }
                }
            }
        }

        if (FilteredPhotos.Count > 0)
        {
            SelectedIndex = 0;
        }
        else
        {
            SelectedIndex = -1;
            SelectedPhoto = null;
            PreviewImage = null;
            VideoSourceUri = null;
            // SelectedIndex = -1 short-circuits its setter, so the per-photo
            // cleanup that normally clears these doesn't run — wipe them here
            // so a leftover overlay isn't painted over an empty preview.
            ClippingOverlay = null;
            FocusPeakingOverlay = null;
            HistogramData = null;
        }
    }

    private void UpdateFilterDescription()
    {
        var parts = new List<string>();
        static string Tag(bool exclude, string s) => exclude ? "NOT " + s : s;

        var ratingDesc = RatingFilterMode switch
        {
            RatingFilterMode.Exact    => RatingFilterValue == 0 ? "No stars" : $"={RatingFilterValue}★",
            RatingFilterMode.AtLeast  => $"≥{RatingFilterValue}★",
            RatingFilterMode.LessThan => $"<{RatingFilterValue}★",
            _                         => null
        };
        if (ratingDesc != null) parts.Add(Tag(RatingFilterExclude, ratingDesc));
        if (FlagFilter.HasValue)
            parts.Add(Tag(FlagFilterExclude, FlagFilter.Value.ToString()));
        if (ColorLabelFilter.HasValue)
            parts.Add(Tag(ColorLabelFilterExclude, ColorLabelFilter.Value.ToString()));
        if (TagFilter != null)
            parts.Add(Tag(TagFilterExclude, TagFilter.Name));
        if (BurstFilter == BurstFilterMode.OnlyInBursts) parts.Add(Tag(BurstFilterExclude, "Bursts"));
        else if (BurstFilter == BurstFilterMode.OnlySingles) parts.Add(Tag(BurstFilterExclude, "Singles"));
        if (ImageTypeFilter == ImageTypeFilterMode.RawOnly) parts.Add(Tag(ImageTypeFilterExclude, "RAW"));
        else if (ImageTypeFilter == ImageTypeFilterMode.JpegOnly) parts.Add(Tag(ImageTypeFilterExclude, "JPG"));
        else if (ImageTypeFilter == ImageTypeFilterMode.VideoOnly) parts.Add(Tag(ImageTypeFilterExclude, "Video"));
        if (ExposureFilter == ExposureFilterMode.ClippedHighlights) parts.Add(Tag(ExposureFilterExclude, "Clipped highlights"));
        else if (ExposureFilter == ExposureFilterMode.CrushedShadows) parts.Add(Tag(ExposureFilterExclude, "Crushed shadows"));
        if (FaceFilter == FaceFilterMode.ClosedEyes) parts.Add(Tag(FaceFilterExclude, "Closed eyes"));
        if (IsTimeOfDayFilterActive) parts.Add(Tag(TimeOfDayFilterExclude, $"{FormatMinutes(TimeOfDayStartMinutes)}–{FormatMinutes(TimeOfDayEndMinutes)}"));

        FilterDescription = parts.Count > 0 ? string.Join(", ", parts) : "All";
    }

    // ── File operations ──

    [RelayCommand]
    private async Task CopyPickedAsync()
    {
        List<PhotoItem> photos = CopyMode switch
        {
            CopySource.SelectedPhotos => SelectedPhotosSnapshot(),
            CopySource.CurrentView    => FilteredPhotos.ToList(),
            _                          => BuildCopyCustomFilter().ToList(),
        };
        if (photos.Count == 0)
        {
            StatusText = "No photos match the copy criteria.";
            return;
        }

        var dialog = new OpenFolderDialog { Title = "Select destination folder" };
        if (dialog.ShowDialog() != true) return;

        string? baseName = CopyRenameEnabled && !string.IsNullOrWhiteSpace(CopyCustomBaseName)
            ? CopyCustomBaseName.Trim()
            : null;

        var preset = CopyQualityPreset;
        Directory.CreateDirectory(dialog.FolderName);
        StatusText = $"Copying {photos.Count} photos ({preset.Label})...";

        var exporter = new PhotoExporter(_extractor);
        int digits = photos.Count.ToString().Length;
        int copied = 0;
        for (int i = 0; i < photos.Count; i++)
        {
            var photo = photos[i];
            StatusText = $"Copying {i + 1}/{photos.Count} ({preset.Label}): {photo.FileName}";
            try
            {
                if (await exporter.ExportAsync(photo, dialog.FolderName, preset, baseName, i + 1, digits))
                    copied++;
            }
            catch { /* skip files that fail to decode/copy */ }
        }
        StatusText = $"Copied {copied}/{photos.Count} photos to {dialog.FolderName} ({preset.Label})";
    }

    private IEnumerable<PhotoItem> BuildCopyCustomFilter()
    {
        IEnumerable<PhotoItem> candidates = AllPhotos;
        candidates = CopyRatingFilterMode switch
        {
            RatingFilterMode.Exact    => candidates.Where(p => p.Rating == CopyRatingFilterValue),
            RatingFilterMode.AtLeast  => candidates.Where(p => p.Rating >= CopyRatingFilterValue),
            RatingFilterMode.LessThan => candidates.Where(p => p.Rating <  CopyRatingFilterValue),
            _                         => candidates
        };
        if (CopyFlagFilter.HasValue)
            candidates = candidates.Where(p => p.Flag == CopyFlagFilter.Value);
        if (CopyColorLabelFilter.HasValue)
            candidates = candidates.Where(p => p.ColorLabel == CopyColorLabelFilter.Value);
        return candidates;
    }

    // ── Delete ──

    [RelayCommand]
    private void DeletePhoto()
    {
        if (SelectedPhoto == null) return;
        var photos = SelectedPhotosSnapshot();
        if (photos.Count == 0) return;

        var prompt = photos.Count == 1
            ? $"Move \"{photos[0].FileName}\" to the Recycle Bin?"
            : $"Move {photos.Count} photos to the Recycle Bin?";
        var title = photos.Count == 1 ? "Delete Photo" : "Delete Photos";
        var result = MessageBox.Show(prompt, title, MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        int deleted = 0;
        var failed = new List<string>();
        foreach (var photo in photos)
        {
            try
            {
                FileSystem.DeleteFile(photo.FilePath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                _db?.DeletePhoto(photo.FileName);
                AllPhotos.Remove(photo);
                deleted++;
            }
            catch (Exception ex)
            {
                failed.Add($"{photo.FileName}: {ex.Message}");
            }
        }

        TotalCount = AllPhotos.Count;
        ApplyFilter();
        StatusText = photos.Count == 1
            ? $"Moved \"{photos[0].FileName}\" to the Recycle Bin."
            : $"Moved {deleted}/{photos.Count} photos to the Recycle Bin.";

        if (failed.Count > 0)
        {
            MessageBox.Show(
                "Some photos could not be deleted:\n\n" + string.Join("\n", failed.Take(8)),
                "Delete Errors",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void DeleteAllRejected()
    {
        var rejected = AllPhotos.Where(p => p.Flag == CullFlag.Reject).ToList();
        if (rejected.Count == 0)
        {
            MessageBox.Show("No rejected photos found.", "Delete Rejected",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(
            $"Move {rejected.Count} rejected photo(s) to the Recycle Bin?",
            "Delete All Rejected",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        int deleted = 0;
        foreach (var photo in rejected)
        {
            try
            {
                FileSystem.DeleteFile(photo.FilePath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                _db?.DeletePhoto(photo.FileName);
                AllPhotos.Remove(photo);
                deleted++;
            }
            catch { /* skip files that can't be deleted */ }
        }

        TotalCount = AllPhotos.Count;
        ApplyFilter();
        StatusText = $"Moved {deleted} rejected photo(s) to the Recycle Bin.";
    }

    // ── Quick advance: rate/flag then move to next ──

    [RelayCommand]
    private void PickAndAdvance()
    {
        if (SelectedPhoto == null) return;
        ApplyBulkFlagEdit(SelectedPhotosSnapshot(), CullFlag.Pick);
        NextPhoto();
    }

    [RelayCommand]
    private void RejectAndAdvance()
    {
        if (SelectedPhoto == null) return;
        ApplyBulkFlagEdit(SelectedPhotosSnapshot(), CullFlag.Reject);
        NextPhoto();
    }

    // ── Helpers ──

    private void SavePhoto(PhotoItem photo)
    {
        _db?.Save(photo);
        ScheduleXmpWrite(photo);
    }

    // Bulk-edit fast path: every per-photo Save() opens its own SQLite transaction,
    // so each one fsyncs separately — 20 photos meant 20 fsyncs and a visible UI
    // stall for what should be a metadata flick. SaveBatch wraps the whole set in
    // one transaction so it's effectively a single disk hit.
    private void SavePhotoBatch(IList<PhotoItem> photos)
    {
        if (photos.Count == 0) return;
        _db?.SaveBatch(photos);
        foreach (var p in photos) ScheduleXmpWrite(p);
    }

    private void UpdateStatus()
    {
        if (SelectedPhoto == null) return;
        var pos = SelectedIndex + 1;
        var total = FilteredPhotos.Count;
        var flag = SelectedPhoto.Flag switch
        {
            CullFlag.Pick => " [PICK]",
            CullFlag.Reject => " [REJECT]",
            _ => ""
        };
        var stars = SelectedPhoto.Rating > 0 ? $" {new string('★', SelectedPhoto.Rating)}" : "";
        StatusText = $"{pos}/{total}  {SelectedPhoto.FileName}{stars}{flag}  Filter: {FilterDescription}";
    }

    // ── Sidebar filter buckets ──
    // Counts reflect AllPhotos (unfiltered totals) and refresh inside ApplyFilter().

    public int Rating5Count        => AllPhotos.Count(p => p.Rating == 5);
    public int Rating4Count        => AllPhotos.Count(p => p.Rating == 4);
    public int Rating3Count        => AllPhotos.Count(p => p.Rating == 3);
    public int Rating2Count        => AllPhotos.Count(p => p.Rating == 2);
    public int Rating1Count        => AllPhotos.Count(p => p.Rating == 1);
    public int RatingUnratedCount  => AllPhotos.Count(p => p.Rating == 0);

    public int LabelRedCount    => AllPhotos.Count(p => p.ColorLabel == ColorLabel.Red);
    public int LabelYellowCount => AllPhotos.Count(p => p.ColorLabel == ColorLabel.Yellow);
    public int LabelGreenCount  => AllPhotos.Count(p => p.ColorLabel == ColorLabel.Green);
    public int LabelBlueCount   => AllPhotos.Count(p => p.ColorLabel == ColorLabel.Blue);
    public int LabelPurpleCount => AllPhotos.Count(p => p.ColorLabel == ColorLabel.Purple);

    public int FlagPickCount      => AllPhotos.Count(p => p.Flag == CullFlag.Pick);
    public int FlagRejectCount    => AllPhotos.Count(p => p.Flag == CullFlag.Reject);
    public int FlagUnflaggedCount => AllPhotos.Count(p => p.Flag == CullFlag.Unflagged);

    public int ClippedHighlightsCount => AllPhotos.Count(p => p.HighlightClippedPct.HasValue && p.HighlightClippedPct.Value >= AppSettings.Current.ClippedAreaThreshold);
    public int CrushedShadowsCount    => AllPhotos.Count(p => p.ShadowClippedPct.HasValue    && p.ShadowClippedPct.Value    >= AppSettings.Current.ClippedAreaThreshold);

    // Counts photos where the analysis pass detected at least one closed eye.
    // Photos that haven't been analysed yet (ClosedEyeCount == null) are not
    // counted, mirroring the Exposure buckets — the chip only shows what we
    // actually know about.
    public int ClosedEyesCount => AllPhotos.Count(p => p.ClosedEyeCount.HasValue && p.ClosedEyeCount.Value > 0);

    public bool IsRating5Active       => RatingFilterMode == RatingFilterMode.Exact && RatingFilterValue == 5;
    public bool IsRating4Active       => RatingFilterMode == RatingFilterMode.Exact && RatingFilterValue == 4;
    public bool IsRating3Active       => RatingFilterMode == RatingFilterMode.Exact && RatingFilterValue == 3;
    public bool IsRating2Active       => RatingFilterMode == RatingFilterMode.Exact && RatingFilterValue == 2;
    public bool IsRating1Active       => RatingFilterMode == RatingFilterMode.Exact && RatingFilterValue == 1;
    public bool IsRatingUnratedActive => RatingFilterMode == RatingFilterMode.Exact && RatingFilterValue == 0;

    public bool IsLabelRedActive    => ColorLabelFilter == ColorLabel.Red;
    public bool IsLabelYellowActive => ColorLabelFilter == ColorLabel.Yellow;
    public bool IsLabelGreenActive  => ColorLabelFilter == ColorLabel.Green;
    public bool IsLabelBlueActive   => ColorLabelFilter == ColorLabel.Blue;
    public bool IsLabelPurpleActive => ColorLabelFilter == ColorLabel.Purple;

    public bool IsFlagPickActive      => FlagFilter == CullFlag.Pick;
    public bool IsFlagRejectActive    => FlagFilter == CullFlag.Reject;
    public bool IsFlagUnflaggedActive => FlagFilter == CullFlag.Unflagged;

    public bool IsExposureClippedHighlightsActive => ExposureFilter == ExposureFilterMode.ClippedHighlights;
    public bool IsExposureCrushedShadowsActive    => ExposureFilter == ExposureFilterMode.CrushedShadows;

    public bool IsFaceClosedEyesActive => FaceFilter == FaceFilterMode.ClosedEyes;

    [RelayCommand]
    private void SetRatingBucket(int rating)
    {
        var isSame = RatingFilterMode == RatingFilterMode.Exact && RatingFilterValue == rating;
        ClearOtherSidebarFilters(SidebarFilterKind.Rating);
        if (isSame)
        {
            RatingFilterMode = RatingFilterMode.Any;
            RatingFilterValue = 0;
            RatingFilterExclude = false;
        }
        else
        {
            RatingFilterValue = rating;
            RatingFilterMode = RatingFilterMode.Exact;
        }
        ApplyFilter();
    }

    [RelayCommand]
    private void SetSidebarColorLabel(ColorLabel label)
    {
        var isSame = ColorLabelFilter == label;
        ClearOtherSidebarFilters(SidebarFilterKind.Color);
        ColorLabelFilter = isSame ? null : label;
        if (!ColorLabelFilter.HasValue) ColorLabelFilterExclude = false;
        ApplyFilter();
    }

    [RelayCommand]
    private void SetSidebarTag(PhotoTag tag)
    {
        var isSame = TagFilter?.Id == tag.Id;
        ClearOtherSidebarFilters(SidebarFilterKind.Tag);
        TagFilter = isSame ? null : tag;
        if (TagFilter == null) TagFilterExclude = false;
        ApplyFilter();
    }

    [RelayCommand]
    private void SetSidebarFlag(CullFlag flag)
    {
        var isSame = FlagFilter == flag;
        ClearOtherSidebarFilters(SidebarFilterKind.Flag);
        FlagFilter = isSame ? null : flag;
        if (!FlagFilter.HasValue) FlagFilterExclude = false;
        ApplyFilter();
    }

    [RelayCommand]
    private void SetSidebarExposure(ExposureFilterMode mode)
    {
        var isSame = ExposureFilter == mode;
        ClearOtherSidebarFilters(SidebarFilterKind.Exposure);
        ExposureFilter = isSame ? ExposureFilterMode.Any : mode;
        if (ExposureFilter == ExposureFilterMode.Any) ExposureFilterExclude = false;
        ApplyFilter();
    }

    [RelayCommand]
    private void SetSidebarFace(FaceFilterMode mode)
    {
        var isSame = FaceFilter == mode;
        ClearOtherSidebarFilters(SidebarFilterKind.Face);
        FaceFilter = isSame ? FaceFilterMode.Any : mode;
        if (FaceFilter == FaceFilterMode.Any) FaceFilterExclude = false;
        ApplyFilter();
    }

    private enum SidebarFilterKind { Rating, Color, Tag, Flag, Exposure, Face }

    private void ClearOtherSidebarFilters(SidebarFilterKind keep)
    {
        if (keep != SidebarFilterKind.Rating)
        {
            RatingFilterMode = RatingFilterMode.Any;
            RatingFilterValue = 0;
            RatingFilterExclude = false;
        }
        if (keep != SidebarFilterKind.Color)
        {
            ColorLabelFilter = null;
            ColorLabelFilterExclude = false;
        }
        if (keep != SidebarFilterKind.Tag)
        {
            TagFilter = null;
            TagFilterExclude = false;
        }
        if (keep != SidebarFilterKind.Flag)
        {
            FlagFilter = null;
            FlagFilterExclude = false;
        }
        if (keep != SidebarFilterKind.Exposure)
        {
            ExposureFilter = ExposureFilterMode.Any;
            ExposureFilterExclude = false;
        }
        if (keep != SidebarFilterKind.Face)
        {
            FaceFilter = FaceFilterMode.Any;
            FaceFilterExclude = false;
        }
        BurstFilter = BurstFilterMode.Any;
        BurstFilterExclude = false;
        ImageTypeFilter = ImageTypeFilterMode.Any;
        ImageTypeFilterExclude = false;
    }

    private void RefreshFilterBuckets()
    {
        OnPropertyChanged(nameof(Rating5Count));
        OnPropertyChanged(nameof(Rating4Count));
        OnPropertyChanged(nameof(Rating3Count));
        OnPropertyChanged(nameof(Rating2Count));
        OnPropertyChanged(nameof(Rating1Count));
        OnPropertyChanged(nameof(RatingUnratedCount));
        OnPropertyChanged(nameof(LabelRedCount));
        OnPropertyChanged(nameof(LabelYellowCount));
        OnPropertyChanged(nameof(LabelGreenCount));
        OnPropertyChanged(nameof(LabelBlueCount));
        OnPropertyChanged(nameof(LabelPurpleCount));
        OnPropertyChanged(nameof(FlagPickCount));
        OnPropertyChanged(nameof(FlagRejectCount));
        OnPropertyChanged(nameof(FlagUnflaggedCount));
        OnPropertyChanged(nameof(ClippedHighlightsCount));
        OnPropertyChanged(nameof(CrushedShadowsCount));
        OnPropertyChanged(nameof(ClosedEyesCount));

        OnPropertyChanged(nameof(IsRating5Active));
        OnPropertyChanged(nameof(IsRating4Active));
        OnPropertyChanged(nameof(IsRating3Active));
        OnPropertyChanged(nameof(IsRating2Active));
        OnPropertyChanged(nameof(IsRating1Active));
        OnPropertyChanged(nameof(IsRatingUnratedActive));
        OnPropertyChanged(nameof(IsLabelRedActive));
        OnPropertyChanged(nameof(IsLabelYellowActive));
        OnPropertyChanged(nameof(IsLabelGreenActive));
        OnPropertyChanged(nameof(IsLabelBlueActive));
        OnPropertyChanged(nameof(IsLabelPurpleActive));
        OnPropertyChanged(nameof(IsFlagPickActive));
        OnPropertyChanged(nameof(IsFlagRejectActive));
        OnPropertyChanged(nameof(IsFlagUnflaggedActive));
        OnPropertyChanged(nameof(IsExposureClippedHighlightsActive));
        OnPropertyChanged(nameof(IsExposureCrushedShadowsActive));
        OnPropertyChanged(nameof(IsFaceClosedEyesActive));

        // Tag counts are stored on the PhotoTag itself so each row can bind directly.
        // Single pass over all photos beats Count(...) per tag when there are many of either.
        if (Tags.Count > 0)
        {
            var tagCounts = new Dictionary<int, int>(Tags.Count);
            foreach (var tag in Tags) tagCounts[tag.Id] = 0;
            foreach (var photo in AllPhotos)
                foreach (var id in photo.TagIds)
                    if (tagCounts.ContainsKey(id)) tagCounts[id]++;
            foreach (var tag in Tags)
                tag.Count = tagCounts[tag.Id];
        }
    }

    public void Dispose()
    {
        // Flush the per-folder "resume where I left off" state on shutdown so
        // edits that happened after the last ApplyFilter / selection change
        // (none today, but cheap insurance against future code paths) make it
        // to disk.
        SaveSessionIfNeeded();

        _indexCts?.Cancel();
        _indexCts?.Dispose();
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _analyzeFacesCts?.Cancel();
        _analyzeFacesCts?.Dispose();
        _faceAnalyzer?.Dispose();
        // Drain any debounced XMP writes that haven't fired yet so an immediate
        // app exit doesn't lose recent rating/flag/label edits. Bounded so a
        // wedged disk can't keep the process alive.
        _xmpWriter?.Flush(TimeSpan.FromSeconds(2));
        _xmpWriter?.Dispose();
        _db?.Dispose();
    }
}
