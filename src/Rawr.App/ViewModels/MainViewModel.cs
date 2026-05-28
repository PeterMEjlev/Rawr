using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rawr.App.Collections;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32;
using Rawr.App.Controls;
using Rawr.App.Dialogs;
using Rawr.App.Services;
using Rawr.App.Shortcuts;
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

    // ── Recursive view (Phase 1) ──
    // Per-subfolder DB + cache contexts. In single-folder mode this contains
    // exactly one entry (CurrentFolder); in recursive mode one per subfolder that
    // contributed at least one photo. Keyed by full folder path, case-insensitive
    // because Windows paths are case-insensitive.
    private sealed record FolderContext(string FolderPath, CullingDatabase Db, PreviewCache Cache);
    private readonly Dictionary<string, FolderContext> _contexts =
        new(StringComparer.OrdinalIgnoreCase);

    private static string OwningFolderOf(PhotoItem photo) =>
        Path.GetDirectoryName(photo.FilePath) ?? string.Empty;

    private CullingDatabase DbFor(PhotoItem photo)
    {
        var folder = OwningFolderOf(photo);
        return _contexts.TryGetValue(folder, out var ctx) ? ctx.Db : _db!;
    }

    private PreviewCache CacheFor(PhotoItem photo)
    {
        var folder = OwningFolderOf(photo);
        return _contexts.TryGetValue(folder, out var ctx) ? ctx.Cache : _cache!;
    }

    // Counts linear-RAW saves so the on-demand decode path can prune only every
    // Nth write instead of enumerating the cache dir on every navigation.
    private int _linearRawSavesSincePrune;

    // Configured linear-RAW disk budget in bytes. 0 (or negative) disables
    // pruning — PreviewCache.PruneLinearRaw treats that as a no-op.
    private static long LinearRawCacheBudgetBytes()
    {
        long mb = AppSettings.Current.LinearRawCacheBudgetMb;
        return mb <= 0 ? 0 : mb * 1024L * 1024L;
    }

    // Evict least-recently-used *_linearraw.bin across every open folder cache so
    // the on-disk total stays within budget. One directory enumeration per cache,
    // best-effort. Snapshots the context map because folder opens mutate it.
    private void PruneLinearRawCaches()
    {
        long budget = LinearRawCacheBudgetBytes();
        if (budget <= 0) return;

        var seen = new HashSet<PreviewCache>();
        if (_cache != null && seen.Add(_cache))
            _cache.PruneLinearRaw(budget);
        foreach (var ctx in _contexts.Values.ToArray())
            if (seen.Add(ctx.Cache))
                ctx.Cache.PruneLinearRaw(budget);
    }

    /// <summary>
    /// Persist every photo to its owning subfolder's CullingDatabase, grouped so
    /// each subfolder DB sees a single transaction (matches the speed profile of
    /// the original single-folder SaveBatch).
    /// </summary>
    private void SaveAllPhotosPerOwningDb(IEnumerable<PhotoItem> photos)
    {
        foreach (var grp in photos.GroupBy(OwningFolderOf, StringComparer.OrdinalIgnoreCase))
        {
            if (!_contexts.TryGetValue(grp.Key, out var ctx))
            {
                // Defensive: photo doesn't have a context (shouldn't happen) —
                // fall back to the primary DB if available, otherwise skip.
                if (_db == null) continue;
                _db.SaveBatch(grp);
            }
            else
            {
                ctx.Db.SaveBatch(grp);
            }
        }
    }
    private CancellationTokenSource? _indexCts;
    private CancellationTokenSource? _previewCts;
    private bool _highResPreviewLoaded;
    private PhotoItem? _metadataSubscription;

    // The in-flight LoadFolderAsync, so a subsequent call can cancel-then-await
    // before tearing down DB contexts. Without this, GeneratePreviewsAsync's
    // background SaveBatch (on a threadpool thread) can race the dispose loop at
    // line ~1238 and hit ObjectDisposedException on a sqlite3_stmt.
    private Task _activeLoadTask = Task.CompletedTask;

    // Per-folder "resume where I left off" — persisted to <folder>/.rawr/session.json.
    // Suppressed while a folder is being loaded so the reset/restore sequence doesn't
    // overwrite the file with transient null state.
    private string? _sessionFolder;
    private bool _suppressSessionSave;
    private CancellationTokenSource? _sessionSaveCts;
    private CancellationTokenSource? _rawPrefetchCts;
    private CancellationTokenSource? _videoProxyPrefetchCts;

    // Photos within this radius of the current selection keep their PreviewJpeg /
    // FullJpeg bytes in memory for instant browsing. Photos outside the window are
    // evicted on selection change to keep memory bounded.
    private const int KeepRadius = 2;
    private readonly HashSet<PhotoItem> _retainedPreviewPhotos = [];
    private const int SessionSaveDebounceMs = 600;
    private const int CachedRawDecodeSettleDelayMs = 45;
    private const int RawDecodeSettleDelayMs = 180;
    private const int FullJpegPreloadSettleDelayMs = 350;
    private const int RawPrefetchSettleDelayMs = 650;
    private const int VideoProxyPrefetchSettleDelayMs = 700;

    private sealed record FolderCatalog(
        CullingDatabase Database,
        PreviewCache Cache,
        Dictionary<string, PhotoState> SavedState,
        List<PhotoTag> Tags,
        List<PhotoItem> Photos,
        DateTime DatabaseModifiedUtc);

    private sealed record PreviewUpdate(PhotoItem Photo, byte[]? ThumbnailJpeg, PhotoMetadata? Metadata);

    // ── Observable state ──

    [ObservableProperty] private string _currentFolder = "";
    [ObservableProperty] private string _statusText = "Open a folder to begin (Ctrl+O)";

    // Sticky global toolbar toggle: when true, every folder open shows photos
    // from the whole subtree. The choice is persisted in AppSettings so it
    // survives folder switches and app restarts. Tag *editing* is disabled
    // while recursive (display still works) — Phase 2 will lift that.
    [ObservableProperty] private bool _isRecursiveView = AppSettings.Current.IncludeSubfolders;
    // True iff the currently open folder has subfolders that contain media,
    // i.e. the toggle is meaningful. Drives the enabled/disabled state of the
    // toolbar button (it stays visible either way).
    [ObservableProperty] private bool _hasSubfolderMedia;
    public bool IsTagEditingEnabled => !IsRecursiveView;
    partial void OnIsRecursiveViewChanged(bool value)
    {
        OnPropertyChanged(nameof(IsTagEditingEnabled));
        // Skip persistence + reload when LoadFolderAsync itself adjusted the
        // flag (e.g. forcing off because the new folder has no subfolders) —
        // the persisted preference should reflect the user's intent, not the
        // current folder's capability.
        if (_suppressRecursiveReload) return;

        // Persist the new value as the global default so opening any folder
        // afterwards starts in this mode.
        AppSettings.Current.IncludeSubfolders = value;
        AppSettings.Current.Save();
        // Reload the current folder so the photo list reflects the new mode.
        if (!string.IsNullOrEmpty(CurrentFolder) && !IsLoading)
        {
            _ = LoadFolderAsync(CurrentFolder);
        }
    }
    // Suppresses the auto-reload in OnIsRecursiveViewChanged while LoadFolderAsync
    // itself adjusts the value (e.g. forcing off when the new folder has no
    // subfolders so a recursive scan is meaningless).
    private bool _suppressRecursiveReload;
    [ObservableProperty] private BitmapSource? _previewImage;

    // Set when the selected item is a video. The MediaElement in the preview pane
    // binds to this; null hides the player and shows the still-image preview path.
    [ObservableProperty] private Uri? _videoSourceUri;

    // True while a smooth-preview proxy is being generated for the selected video.
    // The preview pane keeps the still JPEG up and shows a "Preparing…" overlay
    // instead of playing the high-bitrate source (which decodes in software for
    // HEVC 4:2:2 / Level 6.2 and stutters badly).
    [ObservableProperty] private bool _isPreparingVideoProxy;
    // Fraction in [0, 1] when ffmpeg reports out_time and we know the source
    // duration; -1 while the encode is starting (no progress yet) so the UI can
    // show an indeterminate state instead of a stale percentage.
    [ObservableProperty] private double _videoProxyProgress = -1;
    [ObservableProperty] private string _videoProxyProgressText = "Preparing smooth preview…";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedPhotoCaptureDateFormatted))]
    [NotifyPropertyChangedFor(nameof(FullscreenPreviewSourceLabel))]
    private PhotoItem? _selectedPhoto;
    [ObservableProperty] private int _selectedIndex = -1;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _filterDescription = "All";
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _visibleCount;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GridFilenameVisibility))]
    [NotifyPropertyChangedFor(nameof(GridCellHeight))]
    [NotifyPropertyChangedFor(nameof(GridItemWidth))]
    [NotifyPropertyChangedFor(nameof(GridItemHeight))]
    private double _gridThumbnailSize = 90.0; // derived in code-behind from GridColumnCount

    public Visibility GridFilenameVisibility => GridThumbnailSize >= 60 ? Visibility.Visible : Visibility.Collapsed;
    public double GridCellHeight => GridThumbnailSize + (GridThumbnailSize >= 60 ? 16.0 : 0.0);
    public double GridItemWidth => GridThumbnailSize + 8.0;
    public double GridItemHeight => GridCellHeight + 8.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveGridColumnCount))]
    private int _gridColumnCount = 2;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveGridColumnCount))]
    private int _expandedGridColumnCount = 6;
    [ObservableProperty] private double _filmstripItemWidth = 140.0; // derived in code-behind from filmstrip height
    [ObservableProperty] private bool _showGrid = true;
    [ObservableProperty] private bool _showFilmstrip = true;
    [ObservableProperty] private bool _showSecondMonitor;
    [ObservableProperty] private bool _isPhotoFullscreen;

    // LOG profile applied to the currently selected video. Defaults to None for
    // smooth culling playback; users can opt into a profile from the dropdown when
    // color correction is worth the extra video-filter cost.
    [ObservableProperty] private LogProfile _selectedLogProfile = LogProfile.None;
    public sealed record LogProfileItem(LogProfile Profile, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }
    public IReadOnlyList<LogProfileItem> AvailableLogProfiles { get; } =
        Enum.GetValues<LogProfile>().Select(p => new LogProfileItem(p, LogProfileDetector.DisplayName(p))).ToArray();
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveGridColumnCount))]
    [NotifyPropertyChangedFor(nameof(MaxGridColumnCount))]
    private bool _isGridExpanded;

    // Single value the GRID slider binds to; routes to whichever underlying field
    // matches the current mode so that normal- and expanded-mode column counts
    // stay independent.
    public int ActiveGridColumnCount
    {
        get => IsGridExpanded ? ExpandedGridColumnCount : GridColumnCount;
        set
        {
            if (IsGridExpanded) ExpandedGridColumnCount = value;
            else GridColumnCount = value;
        }
    }

    // Expanded mode has far more horizontal space, so it gets a higher cap to
    // let users squeeze in smaller thumbnails for fast triage.
    public int MaxGridColumnCount => IsGridExpanded ? 16 : 8;
    public string GridExpandedToggleTooltip => IsGridExpanded
        ? $"Restore preview pane ({ToggleGridExpandedShortcutDisplay})"
        : $"Expand grid - hide preview pane ({ToggleGridExpandedShortcutDisplay})";
    public string FullGridToggleTooltip =>
        $"Toggle full grid view - hide the preview pane and expand the thumbnail grid ({ToggleGridExpandedShortcutDisplay})";

    private static string ToggleGridExpandedShortcutDisplay => ShortcutDisplay("ToggleGridExpanded");
    partial void OnIsGridExpandedChanged(bool value) =>
        OnPropertyChanged(nameof(GridExpandedToggleTooltip));

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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FullscreenPreviewSourceLabel))]
    private string _exposureSourceLabel = "EV";

    public double ExposureSelectionStart => Math.Min(0.0, ExposureCompensation);
    public double ExposureSelectionEnd   => Math.Max(0.0, ExposureCompensation);
    public string FullscreenPreviewSourceLabel
    {
        get
        {
            if (SelectedPhoto == null || SelectedPhoto.IsVideo) return "";

            var label = ExposureSourceLabel.ToUpperInvariant();
            var source = label switch
            {
                var s when s.Contains("JPG LARGE") => "JPG (LARGE)",
                var s when s.Contains("JPG SMALL") => "JPG (SMALL)",
                var s when s.Contains("JPG THUMB") => "JPG (THUMB)",
                var s when s.Contains("RAW") => "RAW",
                _ => "",
            };

            if (source.Length == 0) return "";
            if (label.Contains("RAW UNAVAILABLE")) return source + " - RAW UNAVAILABLE";
            if (label.Contains("RAW DECODE FAILED")) return source + " - RAW DECODE FAILED";
            return source;
        }
    }

    private BitmapSource? _basePreviewImage;
    private LinearRawImage? _baseRawImage;
    // Full-resolution linear RAW for the currently selected photo, populated by
    // the zoom-time decode. Stays alongside the downsampled _baseRawImage so the
    // EV slider can render fast against the small buffer during drags, then
    // upgrade to full-res once the drag settles (see ApplyExposureAsync). Cleared
    // on selection change so we don't keep ~180 MB of RAW pixels per photo.
    private LinearRawImage? _fullRawImage;
    private CancellationTokenSource? _exposureCts;
    private CancellationTokenSource? _rawDecodeCts;

    // Why the on-screen preview is JPG-derived even though the user expects RAW.
    // Surfaced as a suffix on ExposureSourceLabel so a silently-broken RAW pipeline
    // (LibRaw missing, per-file decode failure) is visible without a debugger
    // attached. Sticky across slider moves — only OnSelectedIndexChanged resets it.
    private enum RawDecodeStatus
    {
        Pending,            // decode in flight or not yet started
        Available,          // _baseRawImage populated
        NotApplicable,      // file isn't RAW (or is a video)
        LibRawUnavailable,  // _libRaw == null at startup
        DecodeFailed,       // ExtractLinearRgb returned null / threw
    }
    private RawDecodeStatus _rawDecodeStatus = RawDecodeStatus.Pending;

    // Filter state
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilters))]
    [NotifyPropertyChangedFor(nameof(ActiveRatingValue))]
    [NotifyPropertyChangedFor(nameof(RatingFilterActiveValues))]
    [NotifyPropertyChangedFor(nameof(RatingModeLabel))]
    private RatingFilterMode _ratingFilterMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveRatingValue))]
    [NotifyPropertyChangedFor(nameof(RatingFilterActiveValues))]
    private int _ratingFilterValue;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RatingModeLabel))]
    private RatingFilterMode _ratingCycleMode = RatingFilterMode.Exact;

    // Additional star values selected via shift-click in the Filter popup.
    // Only meaningful in Exact mode; cleared when switching to AtLeast/LessThan.
    // RatingFilterValue remains the "anchor" so existing sidebar/copy code keeps working.
    private readonly HashSet<int> _ratingFilterExtraValues = new();
    public IReadOnlyCollection<int> RatingFilterExtraValues => _ratingFilterExtraValues;

    public int ActiveRatingValue => RatingFilterMode == RatingFilterMode.Any ? -1 : RatingFilterValue;

    // Union of the anchor + shift-click extras, used by the Filter popup buttons to
    // highlight every value currently in the active set.
    public IReadOnlyCollection<int> RatingFilterActiveValues
    {
        get
        {
            if (RatingFilterMode == RatingFilterMode.Any) return Array.Empty<int>();
            if (RatingFilterMode != RatingFilterMode.Exact || _ratingFilterExtraValues.Count == 0)
                return new[] { RatingFilterValue };
            var set = new HashSet<int>(_ratingFilterExtraValues) { RatingFilterValue };
            return set;
        }
    }

    public string RatingModeLabel => RatingCycleMode switch
    {
        RatingFilterMode.AtLeast  => "≥",
        RatingFilterMode.LessThan => "<",
        _                         => "="
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilters))]
    [NotifyPropertyChangedFor(nameof(FlagFilterActiveValues))]
    private CullFlag? _flagFilter;

    private readonly HashSet<CullFlag> _flagFilterExtraValues = new();
    public IReadOnlyCollection<CullFlag> FlagFilterExtraValues => _flagFilterExtraValues;
    public IReadOnlyCollection<CullFlag> FlagFilterActiveValues
    {
        get
        {
            if (!FlagFilter.HasValue) return Array.Empty<CullFlag>();
            if (_flagFilterExtraValues.Count == 0) return new[] { FlagFilter.Value };
            var set = new HashSet<CullFlag>(_flagFilterExtraValues) { FlagFilter.Value };
            return set;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilters))]
    [NotifyPropertyChangedFor(nameof(ColorLabelFilterActiveValues))]
    private ColorLabel? _colorLabelFilter;

    private readonly HashSet<ColorLabel> _colorLabelFilterExtraValues = new();
    public IReadOnlyCollection<ColorLabel> ColorLabelFilterExtraValues => _colorLabelFilterExtraValues;
    public IReadOnlyCollection<ColorLabel> ColorLabelFilterActiveValues
    {
        get
        {
            if (!ColorLabelFilter.HasValue) return Array.Empty<ColorLabel>();
            if (_colorLabelFilterExtraValues.Count == 0) return new[] { ColorLabelFilter.Value };
            var set = new HashSet<ColorLabel>(_colorLabelFilterExtraValues) { ColorLabelFilter.Value };
            return set;
        }
    }

    // Cameras (CameraFormatted strings) currently selected. Unlike the other filters
    // this one has no "anchor" — empty set means inactive. Photos with empty EXIF
    // camera info match the sentinel UnknownCameraKey.
    public const string UnknownCameraKey = "(Unknown)";
    private readonly HashSet<string> _cameraFilters = new(StringComparer.Ordinal);
    public IReadOnlyCollection<string> CameraFilters => _cameraFilters;
    public bool IsCameraFilterActive => _cameraFilters.Count > 0;

    // Cameras present in the currently-loaded photos. Repopulated from AllPhotos
    // whenever metadata changes; bound to the Filter popup.
    public ObservableCollection<string> AvailableCameras { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilters))]
    private BurstFilterMode _burstFilter = BurstFilterMode.Any;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilters))]
    [NotifyPropertyChangedFor(nameof(ImageTypeFilterActiveValues))]
    private ImageTypeFilterMode _imageTypeFilter = ImageTypeFilterMode.Any;

    private readonly HashSet<ImageTypeFilterMode> _imageTypeFilterExtraValues = new();
    public IReadOnlyCollection<ImageTypeFilterMode> ImageTypeFilterExtraValues => _imageTypeFilterExtraValues;
    public IReadOnlyCollection<ImageTypeFilterMode> ImageTypeFilterActiveValues
    {
        get
        {
            if (ImageTypeFilter == ImageTypeFilterMode.Any) return Array.Empty<ImageTypeFilterMode>();
            if (_imageTypeFilterExtraValues.Count == 0) return new[] { ImageTypeFilter };
            var set = new HashSet<ImageTypeFilterMode>(_imageTypeFilterExtraValues) { ImageTypeFilter };
            return set;
        }
    }

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

    public bool HasActiveFilters => RatingFilterMode != RatingFilterMode.Any || FlagFilter.HasValue || ColorLabelFilter.HasValue || TagFilter != null || BurstFilter != BurstFilterMode.Any || ImageTypeFilter != ImageTypeFilterMode.Any || ExposureFilter != ExposureFilterMode.Any || FaceFilter != FaceFilterMode.Any || IsTimeOfDayFilterActive || IsRegionFilterActive || IsCameraFilterActive;

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
    [NotifyPropertyChangedFor(nameof(TagFilterActiveIds))]
    private PhotoTag? _tagFilter;

    // Additional tag IDs selected via shift-click. Combined with TagFilter for the filter
    // predicate and for highlighting active buttons in the popup.
    private readonly HashSet<int> _tagFilterExtraIds = new();
    public IReadOnlyCollection<int> TagFilterExtraIds => _tagFilterExtraIds;
    public IReadOnlyCollection<int> TagFilterActiveIds
    {
        get
        {
            if (TagFilter == null) return Array.Empty<int>();
            if (_tagFilterExtraIds.Count == 0) return new[] { TagFilter.Id };
            var set = new HashSet<int>(_tagFilterExtraIds) { TagFilter.Id };
            return set;
        }
    }

    public IEnumerable<TagAssignmentItem> SelectedPhotoTagAssignments =>
        Tags.Where(t => !t.IsSystem)
            .Select(t => new TagAssignmentItem(t, SelectedPhoto?.TagIds.Contains(t.Id) ?? false));

    public record TagAssignmentItem(PhotoTag Tag, bool IsAssigned);

    private sealed record AssignedMetadataSnapshot(
        PhotoItem Photo,
        int Rating,
        CullFlag Flag,
        ColorLabel ColorLabel,
        int[] TagIds);

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

    public ObservableRangeCollection<PhotoItem> AllPhotos { get; } = [];
    public ObservableRangeCollection<PhotoItem> FilteredPhotos { get; } = [];

    // Mixed list that the grid view binds to: same photos as FilteredPhotos, plus
    // DateHeaderItem rows inserted at calendar-day boundaries when sorted by capture
    // time. The filmstrip and all index-based code still operate on FilteredPhotos
    // — this collection exists solely so the grid can render full-width separators.
    public ObservableRangeCollection<object> GridItems { get; } = [];

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
            foreach (var c in _contexts.Values) c.Db.Dispose();
            _contexts.Clear();
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
            foreach (var c in _contexts.Values) c.Db.Dispose();
            _contexts.Clear();
            _db = null;
            _cache = null;
            AllPhotos.Clear();
            FilteredPhotos.Clear();
            GridItems.Clear();
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

    private sealed record FolderCatalogMulti(
        List<FolderContext> Contexts,
        List<PhotoItem> Photos,
        List<PhotoTag> MergedTags,
        Dictionary<string, Dictionary<string, PhotoState>> SavedStateByFolder,
        Dictionary<string, DateTime> DbMtimeByFolder);

    /// <summary>
    /// Open a single folder's CullingDatabase + PreviewCache and load the saved
    /// state for the files that live directly inside it. Used as the per-subfolder
    /// building block by both the single-folder and recursive loaders.
    /// </summary>
    private static (FolderContext Ctx, Dictionary<string, PhotoState> SavedState,
                    List<PhotoTag> Tags, Dictionary<string, HashSet<int>> PhotoTags,
                    DateTime DbMtime)
        OpenContextAndState(string folderPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var db = CullingDatabase.Open(folderPath);
        try
        {
            var cache = new PreviewCache(folderPath);
            // Reclaim obsolete-version .bin first (e.g. ~2x-oversized pre-Downsample-
            // fix v2 buffers) — unconditional, independent of the budget — then
            // bound cross-session growth to the configured budget, all before the
            // cache gets used (and grown) again this session.
            cache.PruneStaleLinearRaw();
            cache.PruneLinearRaw(LinearRawCacheBudgetBytes());
            var savedState = db.LoadAll();
            var tags = db.LoadGroups();
            var photoTags = db.LoadAllPhotoGroups();
            var dbPath = Path.Combine(folderPath, ".rawr", "culling.db");
            var dbMtime = File.Exists(dbPath) ? File.GetLastWriteTimeUtc(dbPath) : DateTime.MinValue;
            var ctx = new FolderContext(folderPath, db, cache);
            db = null!;
            return (ctx, savedState, tags, photoTags, dbMtime);
        }
        finally
        {
            db?.Dispose();
        }
    }

    /// <summary>
    /// Apply a per-folder PhotoState to a PhotoItem.
    /// </summary>
    private static void ApplyPhotoState(PhotoItem photo, PhotoState state)
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

    /// <summary>
    /// Single-folder catalog load. Preserved as the simple path; the recursive
    /// loader below builds on the same per-folder primitives.
    /// </summary>
    private static FolderCatalog LoadFolderCatalog(string folderPath, IReadOnlyList<string> files, CancellationToken ct)
    {
        FolderContext? owned = null;
        try
        {
            var (ctx, savedState, tags, allPhotoTags, dbMtime) = OpenContextAndState(folderPath, ct);
            owned = ctx;
            var tagsById = tags.ToDictionary(t => t.Id);
            var photos = new List<PhotoItem>(files.Count);

            foreach (var filePath in files)
            {
                ct.ThrowIfCancellationRequested();

                var photo = new PhotoItem { FilePath = filePath };
                var fileName = photo.FileName;

                if (savedState.TryGetValue(fileName, out var state))
                    ApplyPhotoState(photo, state);

                if (allPhotoTags.TryGetValue(fileName, out var tagIds))
                {
                    foreach (var id in tagIds)
                        photo.TagIds.Add(id);
                }
                UpdateTagDisplay(photo, tagsById);

                photos.Add(photo);
            }

            var catalog = new FolderCatalog(ctx.Db, ctx.Cache, savedState, tags, photos, dbMtime);
            owned = null;
            return catalog;
        }
        finally
        {
            owned?.Db.Dispose();
        }
    }

    /// <summary>
    /// Recursive catalog load. <paramref name="files"/> contains every supported
    /// file under <paramref name="topFolderPath"/>; we group them by their owning
    /// directory, open one CullingDatabase + PreviewCache per directory, and
    /// produce a merged tag list (deduped by name) with synthetic display IDs so
    /// the UI can render tags across subfolder boundaries.
    /// </summary>
    private static FolderCatalogMulti LoadFolderCatalogRecursive(string topFolderPath, IReadOnlyList<string> files, CancellationToken ct)
    {
        var byFolder = files.GroupBy(f => Path.GetDirectoryName(f) ?? topFolderPath,
                                     StringComparer.OrdinalIgnoreCase)
                            .ToList();

        var contexts = new List<FolderContext>();
        var savedStateByFolder = new Dictionary<string, Dictionary<string, PhotoState>>(StringComparer.OrdinalIgnoreCase);
        var dbMtimeByFolder = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        // Local DB rows per folder (so we can translate per-subfolder tag IDs to
        // the merged display IDs below).
        var photoTagsByFolder = new Dictionary<string, Dictionary<string, HashSet<int>>>(StringComparer.OrdinalIgnoreCase);
        // Per-folder map of local tag ID → tag name. Combined with the global
        // name → display ID map this lets us translate local→display in one step.
        var localTagNamesByFolder = new Dictionary<string, Dictionary<int, string>>(StringComparer.OrdinalIgnoreCase);

        // Merge tags by name. The first occurrence (top folder if present, then
        // subfolders in scan order) wins for IsSystem/Color, with system tags
        // taking precedence over user tags of the same name.
        var mergedByName = new Dictionary<string, PhotoTag>(StringComparer.OrdinalIgnoreCase);
        // Synthetic display IDs start just past the highest natural ID we see.
        int nextDisplayId = 1;

        try
        {
            // Open the top folder first so its tags get the canonical IDs / colours
            // when names collide.
            var foldersInOrder = new List<string> { topFolderPath };
            foreach (var grp in byFolder)
            {
                if (!string.Equals(grp.Key, topFolderPath, StringComparison.OrdinalIgnoreCase))
                    foldersInOrder.Add(grp.Key);
            }

            // De-dup while preserving order.
            foldersInOrder = foldersInOrder
                .Where((f, i) => foldersInOrder.FindIndex(x => string.Equals(x, f, StringComparison.OrdinalIgnoreCase)) == i)
                .ToList();

            // Only open contexts for folders that actually contributed at least one
            // file *or* the top folder (so subsequent "create tag" operations have
            // somewhere to land).
            var foldersWithFiles = new HashSet<string>(byFolder.Select(g => g.Key), StringComparer.OrdinalIgnoreCase);

            foreach (var folder in foldersInOrder)
            {
                ct.ThrowIfCancellationRequested();
                if (!foldersWithFiles.Contains(folder) && !string.Equals(folder, topFolderPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                var (ctx, savedState, tags, photoTags, dbMtime) = OpenContextAndState(folder, ct);
                contexts.Add(ctx);
                savedStateByFolder[folder] = savedState;
                photoTagsByFolder[folder] = photoTags;
                dbMtimeByFolder[folder] = dbMtime;

                var localNames = new Dictionary<int, string>();
                foreach (var t in tags)
                {
                    localNames[t.Id] = t.Name;
                    if (!mergedByName.TryGetValue(t.Name, out var existing))
                    {
                        // Keep the original ID if it doesn't clash with the running
                        // synthetic counter; otherwise mint a fresh display ID.
                        int displayId = t.Id;
                        if (displayId < nextDisplayId) displayId = nextDisplayId;
                        nextDisplayId = Math.Max(nextDisplayId, displayId + 1);
                        mergedByName[t.Name] = new PhotoTag
                        {
                            Id = displayId,
                            Name = t.Name,
                            IsSystem = t.IsSystem,
                            Color = t.Color,
                        };
                    }
                    else if (t.IsSystem && !existing.IsSystem)
                    {
                        // System metadata wins over a stray user-named copy.
                        mergedByName[t.Name] = new PhotoTag
                        {
                            Id = existing.Id,
                            Name = existing.Name,
                            IsSystem = true,
                            Color = t.Color ?? existing.Color,
                        };
                    }
                }
                localTagNamesByFolder[folder] = localNames;
            }

            // Build photos in original (file-sorted) order so the UI's filmstrip
            // shows them contiguously by path.
            var photos = new List<PhotoItem>(files.Count);
            var mergedTags = mergedByName.Values.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var tagsById = mergedTags.ToDictionary(t => t.Id);

            foreach (var filePath in files)
            {
                ct.ThrowIfCancellationRequested();
                var folder = Path.GetDirectoryName(filePath) ?? topFolderPath;
                var photo = new PhotoItem { FilePath = filePath };
                var fileName = photo.FileName;

                if (savedStateByFolder.TryGetValue(folder, out var fs) && fs.TryGetValue(fileName, out var state))
                    ApplyPhotoState(photo, state);

                if (photoTagsByFolder.TryGetValue(folder, out var fpt) && fpt.TryGetValue(fileName, out var localIds))
                {
                    var localNames = localTagNamesByFolder[folder];
                    foreach (var lid in localIds)
                    {
                        if (!localNames.TryGetValue(lid, out var name)) continue;
                        if (mergedByName.TryGetValue(name, out var disp))
                            photo.TagIds.Add(disp.Id);
                    }
                }
                UpdateTagDisplay(photo, tagsById);

                photos.Add(photo);
            }

            var result = new FolderCatalogMulti(contexts, photos, mergedTags, savedStateByFolder, dbMtimeByFolder);
            contexts = null!; // ownership transferred
            return result;
        }
        finally
        {
            if (contexts != null)
            {
                foreach (var c in contexts) c.Db.Dispose();
            }
        }
    }

    public Task LoadFolderAsync(string folderPath)
    {
        // Cancel the previous load *before* we await its task, so its
        // GeneratePreviewsAsync chain (and the background SaveBatch inside it)
        // unwinds promptly. Then await it to completion so its DB contexts are no
        // longer in use by the time the new load disposes them.
        _indexCts?.Cancel();
        var previousLoad = _activeLoadTask;
        var task = LoadFolderCoreAsync(folderPath, previousLoad);
        _activeLoadTask = task;
        return task;
    }

    private async Task LoadFolderCoreAsync(string folderPath, Task previousLoad)
    {
        // Wait for any prior load to settle. Exceptions from the previous load
        // (including the OperationCanceledException we just triggered) belong to
        // its caller, not us — swallow them here.
        try { await previousLoad.ConfigureAwait(true); }
        catch { /* previous load's exceptions surface via its own await */ }

        FlushSessionSave();

        // Cancel any in-progress indexing
        _previewCts?.Cancel();
        _rawDecodeCts?.Cancel();
        _rawPrefetchCts?.Cancel();
        _videoProxyPrefetchCts?.Cancel();
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

        bool hadSubfolderMedia = await Task.Run(() => FolderScanner.HasMediaInSubfolders(folderPath), ct);
        if (ct.IsCancellationRequested) return;
        HasSubfolderMedia = hadSubfolderMedia;

        // IsRecursiveView is a sticky global preference — the user's last choice
        // applies to every folder. Reconcile it on every load against (a) the
        // persisted preference and (b) whether this folder actually has any
        // subfolders to recurse into. The persisted preference is left intact
        // either way (OnIsRecursiveViewChanged skips its save when
        // _suppressRecursiveReload is set), so navigating from a subfolder-less
        // folder back to one with subfolders restores the preference.
        bool effectiveRecursive = AppSettings.Current.IncludeSubfolders && hadSubfolderMedia;
        if (effectiveRecursive != IsRecursiveView)
        {
            _suppressRecursiveReload = true;
            try { IsRecursiveView = effectiveRecursive; }
            finally { _suppressRecursiveReload = false; }
        }

        // Dispose previous session. Drain any queued XMP writes for the *previous*
        // folder first so we don't lose edits from a debounce window straddling a
        // folder switch — bounded so a slow disk can't stall the UI indefinitely.
        if (_xmpWriter != null)
        {
            _xmpWriter.Flush(TimeSpan.FromSeconds(2));
            _xmpWriter.Dispose();
            _xmpWriter = null;
        }
        // Dispose every subfolder context from the previous folder. _db points
        // into _contexts so the loop has already disposed it.
        foreach (var c in _contexts.Values) c.Db.Dispose();
        _contexts.Clear();
        _db = null;
        _cache = null;

        AllPhotos.Clear();
        FilteredPhotos.Clear();
        GridItems.Clear();
        Tags.Clear();
        TagFilter = null;
        PreviewImage = null;
        VideoSourceUri = null;
        SelectedPhoto = null;
        SelectedIndex = -1;
        ClearRetainedPreviewPhotos();
        // History references PhotoItem instances that won't survive a folder switch.
        History.Clear();

        BurstCollapsed = AppSettings.Current.CollapseBurstsOnOpen;
        SortField = AppSettings.Current.DefaultSortField;

        // Scan (recursive or single-folder per the toggle).
        var files = await Task.Run(
            () => IsRecursiveView ? FolderScanner.ScanRecursive(folderPath) : FolderScanner.Scan(folderPath),
            ct);
        TotalCount = files.Count;

        if (files.Count == 0)
        {
            StatusText = IsRecursiveView
                ? "No supported image files found in this folder tree."
                : "No supported image files found in this folder.";
            IsLoading = false;
            return;
        }

        StatusText = $"Found {files.Count} image files. Loading catalog...";

        List<PhotoItem> catalogPhotos;
        List<PhotoTag> catalogTags;
        Dictionary<string, PhotoState> savedState;  // primary folder's saved state (used for XMP-merge gating)
        DateTime dbMtime;
        Dictionary<string, Dictionary<string, PhotoState>>? savedStateByFolder = null;
        Dictionary<string, DateTime>? dbMtimeByFolder = null;

        if (IsRecursiveView)
        {
            var multi = await Task.Run(() => LoadFolderCatalogRecursive(folderPath, files, ct), ct);
            if (ct.IsCancellationRequested)
            {
                foreach (var c in multi.Contexts) c.Db.Dispose();
                return;
            }
            foreach (var c in multi.Contexts) _contexts[c.FolderPath] = c;
            // Primary context = the top folder if it has its own context, else the first one.
            var primary = _contexts.TryGetValue(folderPath, out var p) ? p : multi.Contexts[0];
            _db = primary.Db;
            _cache = primary.Cache;
            catalogPhotos = multi.Photos;
            catalogTags = multi.MergedTags;
            savedState = multi.SavedStateByFolder.TryGetValue(folderPath, out var ss)
                ? ss
                : new Dictionary<string, PhotoState>(StringComparer.OrdinalIgnoreCase);
            dbMtime = multi.DbMtimeByFolder.TryGetValue(folderPath, out var mt) ? mt : DateTime.MinValue;
            savedStateByFolder = multi.SavedStateByFolder;
            dbMtimeByFolder = multi.DbMtimeByFolder;
        }
        else
        {
            var catalog = await Task.Run(() => LoadFolderCatalog(folderPath, files, ct), ct);
            if (ct.IsCancellationRequested)
            {
                catalog.Database.Dispose();
                return;
            }
            _db = catalog.Database;
            _cache = catalog.Cache;
            _contexts[folderPath] = new FolderContext(folderPath, _db, _cache);
            catalogPhotos = catalog.Photos;
            catalogTags = catalog.Tags;
            savedState = catalog.SavedState;
            dbMtime = catalog.DatabaseModifiedUtc;
        }

        _xmpWriter = new XmpSidecarWriter();

        foreach (var t in catalogTags)
            Tags.Add(t);

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
        var photosToScan = catalogPhotos;
        var localSavedStateByFolder = savedStateByFolder;
        var localDbMtimeByFolder = dbMtimeByFolder;
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

                // Pick the correct per-folder saved state + mtime for this photo
                // so the grace window doesn't get compared against the wrong DB.
                Dictionary<string, PhotoState> photoSaved = savedState;
                DateTime photoMtime = dbMtime;
                if (localSavedStateByFolder != null)
                {
                    var owner = OwningFolderOf(photo);
                    if (localSavedStateByFolder.TryGetValue(owner, out var ss)) photoSaved = ss;
                    if (localDbMtimeByFolder != null && localDbMtimeByFolder.TryGetValue(owner, out var mt)) photoMtime = mt;
                }

                bool noDbRow = !photoSaved.ContainsKey(photo.FileName);
                if (!noDbRow && sidecarMtime <= photoMtime + grace) continue;
                var data = XmpSidecar.TryRead(photo.FilePath);
                if (data != null) list.Add((photo, data));
            }
            return list;
        }, ct);

        if (pendingMerges.Count > 0)
            ApplyXmpMerges(pendingMerges);

        AllPhotos.ReplaceRange(catalogPhotos);

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
        if (!ct.IsCancellationRequested && SelectedIndex >= 0)
            QueueVideoProxyPrefetch(SelectedIndex);

        if (!ct.IsCancellationRequested)
        {
            var burstSuffix = BurstCount > 0 ? $"  ({BurstCount} burst{(BurstCount == 1 ? "" : "s")})" : "";
            StatusText = $"{files.Count} photos ready{burstSuffix}. [{_extractor.GetType().Name}]";
            IsLoading = false;
            try
            {
                // Persist the *tree root*, not the currently-viewed folder. In-tree
                // navigation reuses LoadFolderAsync for subfolders; writing folderPath
                // here would promote whichever subfolder was last selected to be the
                // restored root next launch, hiding the parent and its siblings.
                var rootToPersist = FolderTreeRoots.Count > 0
                    ? FolderTreeRoots[0].FullPath
                    : folderPath;
                Directory.CreateDirectory(SettingsDir);
                await File.WriteAllTextAsync(LastFolderFile, rootToPersist, ct);
            }
            catch { /* non-critical */ }
        }
    }

    private async Task GeneratePreviewsAsync(CancellationToken ct)
    {
        if (_cache == null) return;

        var photos = AllPhotos.ToList();
        var pendingUpdates = new List<PreviewUpdate>(128);
        var pendingLock = new object();
        var flushQueued = 0;

        void QueuePreviewUpdate(PreviewUpdate update)
        {
            bool shouldSchedule = false;
            lock (pendingLock)
            {
                pendingUpdates.Add(update);
                if (flushQueued == 0)
                {
                    flushQueued = 1;
                    shouldSchedule = true;
                }
            }

            if (shouldSchedule)
            {
                Application.Current.Dispatcher.BeginInvoke(
                    (Action)FlushSomePendingPreviewUpdates,
                    DispatcherPriority.Background);
            }
        }

        void FlushSomePendingPreviewUpdates() => FlushPendingPreviewUpdates(96);

        void FlushPendingPreviewUpdates(int maxBatch)
        {
            List<PreviewUpdate> batch;
            lock (pendingLock)
            {
                if (pendingUpdates.Count == 0)
                {
                    flushQueued = 0;
                    return;
                }

                var count = Math.Min(maxBatch, pendingUpdates.Count);
                batch = pendingUpdates.GetRange(0, count);
                pendingUpdates.RemoveRange(0, count);
            }

            if (!ct.IsCancellationRequested)
            {
                foreach (var update in batch)
                {
                    if (update.ThumbnailJpeg != null)
                        update.Photo.ThumbnailJpeg = update.ThumbnailJpeg;
                    if (update.Metadata != null)
                        update.Photo.Metadata = update.Metadata;
                }
            }

            bool shouldScheduleAgain = false;
            lock (pendingLock)
            {
                if (pendingUpdates.Count == 0)
                    flushQueued = 0;
                else
                    shouldScheduleAgain = true;
            }

            if (shouldScheduleAgain)
            {
                Application.Current.Dispatcher.BeginInvoke(
                    (Action)FlushSomePendingPreviewUpdates,
                    DispatcherPriority.Background);
            }
        }

        // Load cached thumbnails, extract missing thumbnails, and read metadata off
        // the UI thread. UI-bound properties are applied later in small batches.
        // Extraction is CPU+IO bound and per-call independent, so it parallelises cleanly.
        // Cap at ProcessorCount/2 to leave headroom for the UI thread + decode.
        int done = 0;
        int total = photos.Count;
        int parallelism = Math.Max(2, Math.Min(8, Environment.ProcessorCount / 2));

        await Task.Run(() =>
        {
            var po = new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct };
            try
            {
                Parallel.ForEach(photos, po, photo =>
                {
                    var photoCache = CacheFor(photo);
                    byte[]? thumbBytes = photoCache.LoadThumbnail(photo.FileName);
                    if (thumbBytes == null)
                    {
                        var jpeg = ExtractorFor(photo).ExtractThumbnail(photo.FilePath);
                        if (jpeg != null)
                        {
                            var thumb = ProcessJpegForCache(jpeg, ThumbnailDecodeWidth) ?? jpeg;
                            photoCache.SaveThumbnail(photo.FileName, thumb);
                            thumbBytes = thumb;
                        }
                    }

                    var metadata = ExtractorFor(photo).ExtractMetadata(photo.FilePath);
                    if (thumbBytes != null || metadata != null)
                        QueuePreviewUpdate(new PreviewUpdate(photo, thumbBytes, metadata));

                    // Compute the perceptual hash from the thumbnail once and reuse on every
                    // subsequent open via the SQLite cache. Used by BurstDetector below.
                    // The grayscale strip is computed in the same decode and feeds
                    // PanoramaDetector — it's transient (not persisted) so we always
                    // refresh it when a thumbnail is available.
                    if (thumbBytes != null && (photo.Phash == null || photo.GrayBuffer == null))
                    {
                        var (hash, strip) = Rawr.App.Services.PerceptualHash.ComputeWithStrip(thumbBytes);
                        if (photo.Phash == null) photo.Phash = hash;
                        if (photo.GrayBuffer == null) photo.GrayBuffer = strip;
                    }

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
                    if (d % 25 == 0)
                    {
                        var snapshot = d;
                        Application.Current.Dispatcher.BeginInvoke(
                            (Action)(() =>
                            {
                                if (!ct.IsCancellationRequested)
                                    StatusText = $"Generating previews... {snapshot}/{total}";
                            }),
                            DispatcherPriority.Background);
                    }
                });
            }
            catch (OperationCanceledException) { /* folder switched mid-scan */ }
        }, ct);

        if (ct.IsCancellationRequested) return;

        await Application.Current.Dispatcher.InvokeAsync(
            (Action)(() => FlushPendingPreviewUpdates(int.MaxValue)),
            DispatcherPriority.Background);

        // Once metadata is in for every photo, group consecutive shots into bursts.
        // BurstDetector mutates GroupId/BurstBadge on the UI thread (the properties are observable),
        // so run it on the dispatcher. In recursive view bursts must not cross
        // subfolder boundaries — run the detector per owning folder so each subset
        // gets its own GroupId space. HDR/Panorama auto-tagging stays scoped per
        // subfolder too (the system tag lives in that subfolder's DB).
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var (loose, strict) = BurstDetector.ThresholdsFromStrictness(AppSettings.Current.BurstSimilarityStrictness);
            if (IsRecursiveView)
            {
                int totalBursts = 0;
                int idOffset = 0;
                foreach (var grp in AllPhotos.GroupBy(OwningFolderOf, StringComparer.OrdinalIgnoreCase))
                {
                    var subset = grp.ToList();
                    int found = BurstDetector.Detect(subset,
                        TimeSpan.FromSeconds(AppSettings.Current.BurstMaxGapSeconds),
                        looseHammingThreshold: loose,
                        strictHammingThreshold: strict);
                    // Offset GroupId so subsets don't collide with each other.
                    int maxId = 0;
                    foreach (var p in subset)
                    {
                        if (p.GroupId > 0)
                        {
                            p.GroupId += idOffset;
                            if (p.GroupId > maxId) maxId = p.GroupId;
                        }
                    }
                    idOffset = maxId;
                    totalBursts += found;
                }
                BurstCount = totalBursts;
                ApplyHdrDetectionPerFolder();
                ApplyPanoramaDetectionPerFolder();
            }
            else
            {
                BurstCount = BurstDetector.Detect(AllPhotos,
                    TimeSpan.FromSeconds(AppSettings.Current.BurstMaxGapSeconds),
                    looseHammingThreshold: loose,
                    strictHammingThreshold: strict);
                ApplyHdrDetection();
                ApplyPanoramaDetection();
            }
        });

        // Persist burst assignments and freshly-computed perceptual hashes so the
        // next session reuses them without re-decoding every thumbnail. Each
        // photo writes to its own subfolder's DB so the per-folder portability
        // invariant is preserved.
        if (_db != null)
        {
            try { await Task.Run(() => SaveAllPhotosPerOwningDb(AllPhotos), ct); }
            catch (OperationCanceledException) { }
            // LoadFolderAsync's serialization (await previousLoad) should keep
            // the DB alive for the entire batch, but if something else races us
            // — disposal during shutdown, an explicit teardown path — the save
            // is best-effort. Don't crash the UI on a dropped sqlite_stmt.
            catch (ObjectDisposedException) { }
        }

        // CaptureDate sort and the time-of-day filter both key off Metadata.CaptureTime,
        // which only just finished loading; without re-applying, the grid stays stuck in
        // the alphabetical fallback order produced by the initial ApplyFilter (and the
        // calendar-day separators we'd draw between days have nothing to anchor to).
        if (BurstFilter != BurstFilterMode.Any
            || SortField == SortField.Burst
            || SortField == SortField.CaptureDate
            || IsTimeOfDayFilterActive)
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
        _rawPrefetchCts?.Cancel();
        _basePreviewImage = null;
        _baseRawImage = null;
        _fullRawImage = null;
        _rawDecodeStatus = RawDecodeStatus.Pending;
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
        QueueRawNeighborPrefetch(value);
        QueueVideoProxyPrefetch(value);
        UpdateStatus();
    }

    partial void OnSelectedPhotoChanged(PhotoItem? value)
    {
        // Grid view selects by item identity (it binds SelectedItem ↔ SelectedPhoto
        // because its source mixes photos and DateHeaderItems); keep the integer
        // SelectedIndex in step so the filmstrip + all index-keyed navigation /
        // prefetch / eviction logic still sees the same anchor. Setting it to the
        // current value is a no-op via [ObservableProperty]'s SetProperty, so the
        // reverse path (OnSelectedIndexChanged → SelectedPhoto = …) doesn't loop.
        if (value != null)
        {
            var idx = FilteredPhotos.IndexOf(value);
            if (idx >= 0 && SelectedIndex != idx) SelectedIndex = idx;
        }

        OnPropertyChanged(nameof(SelectedPhotoTagAssignments));

        if (_metadataSubscription != null)
            _metadataSubscription.PropertyChanged -= OnSelectedPhotoPropertyChanged;
        _metadataSubscription = value;
        if (value != null)
            value.PropertyChanged += OnSelectedPhotoPropertyChanged;

        // Reset LOG correction when switching videos. VLC's adjust filter is costly
        // on high-FPS 10-bit 4:2:2 clips, so preview playback starts filter-free.
        if (value?.IsVideo == true)
            SelectedLogProfile = LogProfile.None;

        // Default path: any anchor change collapses the multi-selection back to just
        // the new anchor (plain click, arrow keys, undo/redo, filter restore). The
        // Ctrl/Shift-click selection methods set _suspendSelectionReconcile while
        // they manage SelectedPhotos themselves so this collapse doesn't fire.
        if (!_suspendSelectionReconcile)
            ReconcileSingleSelection(value);

        // Persist last-selected so reopening this folder jumps straight back here.
        // Skip when value is null — that's almost always the transient clear during
        // folder load or filter rebuild, not a real "user deselected everything".
        if (value != null) QueueSessionSave();
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
        // Esc takes priority over any other "close" action when in photo-fullscreen.
        if (IsPhotoFullscreen)
        {
            IsPhotoFullscreen = false;
            return;
        }

        if (SelectedPhotos.Count <= 1 && SelectedPhoto != null && !IsGridExpanded)
        {
            ShowGrid = true;
            IsGridExpanded = true;
            return;
        }

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

    public void NotifyShortcutDisplayChanged()
    {
        OnPropertyChanged(nameof(GridExpandedToggleTooltip));
        OnPropertyChanged(nameof(FullGridToggleTooltip));
    }

    private static string ShortcutDisplay(string actionId)
    {
        var action = ShortcutRegistry.All.FirstOrDefault(a => a.Id == actionId);
        if (action == null) return "unbound";

        var (spec, unbound) = ShortcutBinder.ResolveBinding(AppSettings.Current, action);
        return unbound || spec == null ? "unbound" : spec.FormatForDisplay();
    }

    [RelayCommand]
    private void SetHistogramMode(HistogramMode mode) => HistogramMode = mode;

    [RelayCommand]
    private void SetSidePanelView(SidePanelView view) => SidePanelView = view;

    [RelayCommand]
    private void ToggleGridExpanded() => IsGridExpanded = !IsGridExpanded;

    [RelayCommand]
    private void RotatePhoto()
    {
        var photo = SelectedPhoto;
        if (photo == null || photo.IsVideo) return;
        photo.UserRotationDegrees = (photo.UserRotationDegrees + 90) % 360;
        // Re-run the EV pipeline against the existing base so the rotation is
        // re-applied to whatever's currently showing (JPG small/large or RAW).
        var ct = _previewCts?.Token ?? CancellationToken.None;
        _ = ApplyExposureAsync(photo, ExposureCompensation, ct);
    }

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

    [RelayCommand]
    private void ViewPhotoFullscreen()
    {
        if (SelectedPhoto == null) return;
        var enteringFullscreen = !IsPhotoFullscreen;
        IsPhotoFullscreen = enteringFullscreen;
        if (enteringFullscreen) _ = LoadHighResPreviewAsync();
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

    private string _basePreviewLabel = "EV";

    private void SetBasePreview(BitmapSource bitmap, string label = "EV (JPG small)")
    {
        _basePreviewImage = bitmap;
        _basePreviewLabel = label;
        // Once a linear RAW exists for this photo, it owns the screen — even at
        // zoom-time when a higher-resolution JPEG would otherwise arrive. Keep
        // the JPG bytes resident for histogram/export/EXIF, just don't paint.
        if (_baseRawImage != null) return;
        var adjusted = ExposureCompensation == 0.0 ? bitmap : ExposureProcessor.Apply(bitmap, ExposureCompensation);
        PreviewImage = ApplyUserRotation(adjusted);
        ExposureSourceLabel = DecorateLabel(label);
    }

    // Append a suffix to JPG-flavored labels when the RAW pipeline is broken or
    // unavailable, so the user can see *why* they're stuck on JPG (and so the
    // suffix survives slider moves instead of being clobbered by ApplyExposureAsync).
    private string DecorateLabel(string baseLabel) => _rawDecodeStatus switch
    {
        RawDecodeStatus.LibRawUnavailable => baseLabel + " (RAW unavailable)",
        RawDecodeStatus.DecodeFailed      => baseLabel + " (RAW decode failed)",
        _                                  => baseLabel,
    };

    private BitmapSource ApplyUserRotation(BitmapSource bitmap)
    {
        var deg = SelectedPhoto?.UserRotationDegrees ?? 0;
        if (deg == 0) return bitmap;
        var rotated = new TransformedBitmap(bitmap, new RotateTransform(deg));
        rotated.Freeze();
        return rotated;
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
        // RAW path takes precedence whenever it's available — the JPEG shortcut
        // at EV=0 was useful when zoom-time JPGs were the only crisp option, but
        // we now upgrade to full-res RAW at zoom, so the JPG path is JPG-only
        // fallback when RAW didn't decode (or hasn't yet).
        var rawDown = _baseRawImage;
        var rawFull = _fullRawImage;
        if (rawDown != null)
        {
            try
            {
                // Render the downsampled RAW first for slider responsiveness; if
                // a full-res buffer is resident (post-zoom), follow up with a
                // full-res pass. Rapid slider moves cancel the full-res render
                // before it finishes, so drag stays cheap.
                var renderedDown = await Task.Run(() => ExposureProcessor.Render(rawDown, ev, ct), ct);
                if (ct.IsCancellationRequested || SelectedPhoto != photo) return;
                PreviewImage = ApplyUserRotation(renderedDown);
                ExposureSourceLabel = "EV (RAW)";

                if (rawFull != null)
                {
                    var renderedFull = await Task.Run(() => ExposureProcessor.Render(rawFull, ev, ct), ct);
                    if (!ct.IsCancellationRequested && SelectedPhoto == photo)
                        PreviewImage = ApplyUserRotation(renderedFull);
                }
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
            PreviewImage = ApplyUserRotation(adjusted);
            ExposureSourceLabel = DecorateLabel(_basePreviewLabel);
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
        if (!photo.IsRaw || photo.IsVideo)
        {
            _rawDecodeStatus = RawDecodeStatus.NotApplicable;
            return;
        }
        if (_libRaw == null)
        {
            _rawDecodeStatus = RawDecodeStatus.LibRawUnavailable;
            if (SelectedPhoto == photo) ExposureSourceLabel = DecorateLabel(_basePreviewLabel);
            return;
        }
        try
        {
            var raw = await Task.Run(() => LoadOrDecodeLinearRaw(photo, ct), ct);
            if (ct.IsCancellationRequested || SelectedPhoto != photo) return;
            if (raw == null)
            {
                // Surface the failure so users don't silently keep operating on JPEG.
                _rawDecodeStatus = RawDecodeStatus.DecodeFailed;
                ExposureSourceLabel = DecorateLabel(_basePreviewLabel);
                return;
            }

            _baseRawImage = raw;
            _rawDecodeStatus = RawDecodeStatus.Available;
            IsLinearRawReady = true;
            // Clipping detection runs on the linear RAW; if the user already toggled it
            // on while we were decoding, paint the overlay now that sensor data is here.
            if (ClippingEnabled) _ = ComputeClippingAsync(photo, raw, ct);
            // Replace the JPEG-based histogram (already shown) with one computed from
            // the linear sensor data — the JPEG histogram understates highlight clip.
            _ = ComputeHistogramAsync(photo, ct);
            // Re-render the current preview through the linear pipeline. RAW
            // wins unconditionally now — the zoom path upgrades to full-res RAW
            // via LoadHighResRawAsync if the user is pixel-peeping.
            var rendered = await Task.Run(() => ExposureProcessor.Render(raw, ExposureCompensation, ct), ct);
            if (!ct.IsCancellationRequested && SelectedPhoto == photo)
            {
                PreviewImage = ApplyUserRotation(rendered);
                ExposureSourceLabel = "EV (RAW)";
            }
        }
        catch (OperationCanceledException) { /* selection moved on */ }
        catch
        {
            _rawDecodeStatus = RawDecodeStatus.DecodeFailed;
            if (SelectedPhoto == photo) ExposureSourceLabel = DecorateLabel(_basePreviewLabel);
        }
    }

    /// <summary>
    /// Disk-cache-aware linear RAW load. The decode itself is the slow part
    /// (~1-3s for cRAW unpack + dcraw_process); the downsampled buffer is ~20MB
    /// at LinearRawPreviewWidth (2400px-wide, 16-bit RGB) and reads back from
    /// disk in ~30ms. So once a photo has been visited once, subsequent loads
    /// (re-selecting, app restart, neighbour prefetch) skip LibRaw entirely.
    /// PreviewCache.PruneLinearRaw keeps the on-disk total within budget.
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
        try
        {
            cache?.SaveLinearRaw(photo.FileName, photo.FilePath, down.Width, down.Height, down.Pixels);
            // Throttled so a normal browse doesn't enumerate the cache dir every
            // photo. ~every 16 new writes keeps a long session bounded between
            // folder opens. keepFileName guards the buffer we're about to return.
            if (cache != null &&
                Interlocked.Increment(ref _linearRawSavesSincePrune) % 16 == 0)
                cache.PruneLinearRaw(LinearRawCacheBudgetBytes(), photo.FileName);
        }
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
            try { await Task.Run(() => SaveAllPhotosPerOwningDb(AllPhotos)); } catch { }
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

    [RelayCommand]
    private void GoToLastInteractedPhoto()
    {
        var target = FilteredPhotos.LastOrDefault(HasUserInteractionMetadata);
        if (target != null)
        {
            SelectVisiblePhotoOrBurstRepresentative(target);
            return;
        }

        target = ApplySorting(AllPhotos).LastOrDefault(HasUserInteractionMetadata);
        if (target == null)
        {
            StatusText = "No rated, flagged, labelled, or tagged photos in this folder.";
            return;
        }

        if (!SelectVisiblePhotoOrBurstRepresentative(target))
            StatusText = $"Last interacted photo is hidden by the current filters: {target.FileName}.";
    }

    private bool SelectVisiblePhotoOrBurstRepresentative(PhotoItem photo)
    {
        var idx = FilteredPhotos.IndexOf(photo);
        if (idx >= 0)
        {
            SelectedIndex = idx;
            return true;
        }

        if (photo.GroupId > 0)
        {
            for (int i = 0; i < FilteredPhotos.Count; i++)
            {
                if (FilteredPhotos[i].GroupId == photo.GroupId)
                {
                    SelectedIndex = i;
                    return true;
                }
            }
        }

        return false;
    }

    private bool HasUserInteractionMetadata(PhotoItem photo) =>
        photo.Rating != 0
        || photo.Flag != CullFlag.Unflagged
        || photo.ColorLabel != ColorLabel.None
        || Tags.Any(t => !t.IsSystem && photo.TagIds.Contains(t.Id));

    private async Task LoadPreviewForSelectedAsync(CancellationToken ct)
    {
        var photo = SelectedPhoto;
        if (photo == null) return;

        if (photo.IsVideo)
        {
            await LoadVideoPreviewForSelectedAsync(photo, ct);
            return;
        }

        // Switching from a video back to a photo: release the player's file handle.
        if (VideoSourceUri != null) VideoSourceUri = null;

        try
        {
            // Already-resident bytes (set by an earlier prefetch) — skip the disk read.
            var cached = photo.PreviewJpeg ?? _cache?.LoadPreview(photo.FileName);
            if (cached != null)
            {
                var bs = await Task.Run(() => LoadBitmapFromJpeg(cached), ct);
                if (ct.IsCancellationRequested || SelectedPhoto != photo) return;
                photo.PreviewJpeg = cached;
                if (bs != null) SetBasePreview(bs, "EV (JPG small)");
                _ = ComputeHistogramAsync(photo, ct);
                if (FocusPeakingEnabled) _ = ComputeFocusPeakingAsync(photo, ct);
                _ = PreloadFullJpegAsync(photo, ct);
                StartRawDecode(photo);
                return;
            }

            // Fast path: a previous decode already wrote the linear-RAW buffer to
            // disk. If no JPEG preview is cached yet, keep the previous frame up
            // briefly and let the delayed RAW path replace it once navigation settles.
            // Skip when the user opted out of RAW decoding — that path won't paint
            // the preview and we'd be left blank.
            if (photo.IsRaw && _libRaw != null && _cache != null
                && !AppSettings.Current.UseEmbeddedJpegOnly
                && CacheFor(photo).HasLinearRaw(photo.FileName))
            {
                StartRawDecode(photo);
                _ = LoadPreviewJpegInBackgroundAsync(photo, ct);
                return;
            }

            // Show the small thumbnail as a placeholder while the medium preview is being extracted.
            if (photo.ThumbnailJpeg != null)
            {
                var thumbBs = await Task.Run(() => LoadBitmapFromJpeg(photo.ThumbnailJpeg), ct);
                if (!ct.IsCancellationRequested && SelectedPhoto == photo)
                {
                    PreviewImage = thumbBs;
                    ExposureSourceLabel = DecorateLabel("EV (JPG thumb)");
                }
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

            if (fullBs != null) SetBasePreview(fullBs, "EV (JPG small)");
            _ = ComputeHistogramAsync(photo, ct);
            if (FocusPeakingEnabled) _ = ComputeFocusPeakingAsync(photo, ct);
            _ = PreloadFullJpegAsync(photo, ct);
            StartRawDecode(photo);
        }
        catch (OperationCanceledException) { /* selection moved on */ }
    }

    private async Task LoadVideoPreviewForSelectedAsync(PhotoItem photo, CancellationToken ct)
    {
        // Lazy ffprobe pass for the side panel's VIDEO section. Fire-and-forget so
        // it doesn't gate preview-frame loading; the result lands on photo.VideoInfo
        // via INotifyPropertyChanged when ready.
        _ = EnsureVideoProbeAsync(photo, ct);

        try
        {
            var sourceUri = new Uri(photo.FilePath);
            var cachedPreview = photo.PreviewJpeg ?? _cache?.LoadPreview(photo.FileName);
            var cached = cachedPreview ?? photo.ThumbnailJpeg ?? _cache?.LoadThumbnail(photo.FileName);

            if (cached != null)
            {
                var bs = await Task.Run(() => LoadBitmapFromJpeg(cached), ct);
                if (ct.IsCancellationRequested || SelectedPhoto != photo) return;

                if (cachedPreview != null) photo.PreviewJpeg = cachedPreview;
                if (bs != null) PreviewImage = bs;
            }
            else
            {
                PreviewImage = null;
            }

            if (ct.IsCancellationRequested || SelectedPhoto != photo) return;
            VideoSourceUri = sourceUri;

            if (photo.PreviewJpeg != null) return;

            var jpeg = await Task.Run(() => ExtractorFor(photo).ExtractPreview(photo.FilePath), ct);
            if (ct.IsCancellationRequested || jpeg == null || SelectedPhoto != photo) return;

            var processed = await Task.Run(() => ProcessJpegForCache(jpeg, PreviewDecodeWidth) ?? jpeg, ct);
            if (ct.IsCancellationRequested || SelectedPhoto != photo) return;

            _cache?.SavePreview(photo.FileName, processed);
            photo.PreviewJpeg = processed;

            var fullBs = await Task.Run(() => LoadBitmapFromJpeg(processed), ct);
            if (!ct.IsCancellationRequested && SelectedPhoto == photo && fullBs != null)
                PreviewImage = fullBs;
        }
        catch (OperationCanceledException) { /* selection moved on */ }
    }

    private async Task EnsureVideoProbeAsync(PhotoItem photo, CancellationToken ct)
    {
        if (photo.VideoInfo != null) return;
        try
        {
            var info = await VideoProbe.GetAsync(photo.FilePath, ct).ConfigureAwait(false);
            if (info == null || ct.IsCancellationRequested) return;
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (!ct.IsCancellationRequested) photo.VideoInfo = info;
            });
        }
        catch (OperationCanceledException) { /* selection moved on */ }
        catch { /* probe failures shouldn't surface to the user */ }
    }

    public async Task<byte[]?> LoadPreviewJpegForPhotoAsync(PhotoItem photo, CancellationToken ct)
    {
        if (photo.PreviewJpeg != null) return photo.PreviewJpeg;

        var cached = _cache?.LoadPreview(photo.FileName);
        if (cached != null)
        {
            photo.PreviewJpeg = cached;
            return cached;
        }

        var jpeg = await Task.Run(() => ExtractorFor(photo).ExtractPreview(photo.FilePath), ct);
        if (ct.IsCancellationRequested || jpeg == null) return null;

        var processed = await Task.Run(() => ProcessJpegForCache(jpeg, PreviewDecodeWidth) ?? jpeg, ct);
        if (ct.IsCancellationRequested) return null;

        _cache?.SavePreview(photo.FileName, processed);
        photo.PreviewJpeg = processed;
        return processed;
    }

    public async Task<byte[]?> LoadFullJpegForPhotoAsync(PhotoItem photo, CancellationToken ct)
    {
        if (photo.IsVideo) return null;
        if (photo.FullJpeg != null) return photo.FullJpeg;

        var jpeg = await Task.Run(() => ExtractorFor(photo).ExtractFullJpeg(photo.FilePath), ct);
        if (ct.IsCancellationRequested || jpeg == null) return null;

        photo.FullJpeg = jpeg;
        return jpeg;
    }

    private void StartRawDecode(PhotoItem photo)
    {
        if (!photo.IsRaw || photo.IsVideo) return;
        if (AppSettings.Current.UseEmbeddedJpegOnly)
        {
            // RAW decode skipped by user setting — the JPG preview stays on screen.
            // _basePreviewLabel was set by whichever preview path painted it.
            return;
        }
        _rawDecodeCts?.Cancel();
        _rawDecodeCts = new CancellationTokenSource();
        _ = StartRawDecodeAfterSettleAsync(photo, _rawDecodeCts.Token);
    }

    private async Task StartRawDecodeAfterSettleAsync(PhotoItem photo, CancellationToken ct)
    {
        try
        {
            var hasCachedRaw = _cache?.HasLinearRaw(photo.FileName) == true;
            await Task.Delay(hasCachedRaw ? CachedRawDecodeSettleDelayMs : RawDecodeSettleDelayMs, ct);
            if (ct.IsCancellationRequested || SelectedPhoto != photo) return;
            await LoadLinearRawAsync(photo, ct);
        }
        catch (OperationCanceledException) { /* selection moved on before RAW work started */ }
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

        // When a linear RAW already exists for this photo, the user wants the
        // full-resolution RAW render at zoom (not the embedded JPG). The
        // downsampled RAW stays on screen during the ~1-3 s decode.
        if (_baseRawImage != null
            && _libRaw != null
            && !AppSettings.Current.UseEmbeddedJpegOnly)
        {
            await LoadHighResRawAsync(photo, ct);
            return;
        }

        try
        {
            // Reuse pre-extracted bytes if PreloadFullJpegAsync already finished.
            var jpeg = photo.FullJpeg ?? await Task.Run(() => ExtractorFor(photo).ExtractFullJpeg(photo.FilePath), ct);
            if (ct.IsCancellationRequested || jpeg == null || SelectedPhoto != photo) return;

            photo.FullJpeg ??= jpeg;

            var rotation = await Task.Run(() => ResolveJpegRotation(photo, jpeg), ct);
            var bs = await Task.Run(() => LoadBitmapFromJpeg(jpeg, decodePixelWidth: 0, rotationOverride: rotation), ct);
            if (!ct.IsCancellationRequested && SelectedPhoto == photo)
            {
                if (bs != null) SetBasePreview(bs, "EV (JPG large)");
                else PreviewImage = null;
            }
        }
        catch (OperationCanceledException) { /* selection moved on */ }
    }

    // Zoom-time upgrade from the downsampled _baseRawImage (~2400 px wide) to a
    // full-sensor linear RAW render. The full buffer is held in _fullRawImage
    // for the lifetime of the selection so re-zooms and EV-slider moves don't
    // re-pay the LibRaw decode (~1-3 s for cRAW). Falls back to the embedded
    // full-res JPEG if the on-demand decode fails — better than leaving the
    // user stuck with the downsampled view.
    private async Task LoadHighResRawAsync(PhotoItem photo, CancellationToken ct)
    {
        try
        {
            var full = _fullRawImage;
            if (full == null)
            {
                full = await Task.Run(() => _libRaw!.ExtractLinearRgb(photo.FilePath), ct);
                if (ct.IsCancellationRequested || SelectedPhoto != photo) return;
                if (full == null)
                {
                    // Decode failed — surface the embedded JPG so the user gets
                    // a crisp zoom anyway.
                    await LoadHighResJpegAsync(photo, ct);
                    return;
                }
                _fullRawImage = full;
            }

            var rendered = await Task.Run(() => ExposureProcessor.Render(full, ExposureCompensation, ct), ct);
            if (!ct.IsCancellationRequested && SelectedPhoto == photo)
            {
                PreviewImage = ApplyUserRotation(rendered);
                ExposureSourceLabel = "EV (RAW)";
            }
        }
        catch (OperationCanceledException) { /* selection moved on */ }
    }

    private async Task LoadHighResJpegAsync(PhotoItem photo, CancellationToken ct)
    {
        try
        {
            var jpeg = photo.FullJpeg ?? await Task.Run(() => ExtractorFor(photo).ExtractFullJpeg(photo.FilePath), ct);
            if (ct.IsCancellationRequested || jpeg == null || SelectedPhoto != photo) return;

            photo.FullJpeg ??= jpeg;

            var rotation = await Task.Run(() => ResolveJpegRotation(photo, jpeg), ct);
            var bs = await Task.Run(() => LoadBitmapFromJpeg(jpeg, decodePixelWidth: 0, rotationOverride: rotation), ct);
            if (!ct.IsCancellationRequested && SelectedPhoto == photo && bs != null)
                SetBasePreview(bs, "EV (JPG large)");
        }
        catch (OperationCanceledException) { /* selection moved on */ }
    }

    // The largest embedded JPEG often lacks the EXIF orientation tag, so reading
    // from it alone gives 0° and a vertical photo paints sideways at zoom. Try
    // the source file's EXIF first (works for JPEGs and any RAW that has a WIC
    // codec installed), then fall back to extracting the default thumb via the
    // configured extractor. Cache the answer on PhotoItem so subsequent zooms
    // skip the re-extraction.
    private double ResolveJpegRotation(PhotoItem photo, byte[] fullJpeg)
    {
        if (photo.JpegRotationDegrees != 0.0) return photo.JpegRotationDegrees;

        var r = ReadExifRotationFromJpeg(fullJpeg);
        if (r != 0.0) { photo.JpegRotationDegrees = r; return r; }

        r = ReadExifRotationFromFile(photo.FilePath);
        if (r != 0.0) { photo.JpegRotationDegrees = r; return r; }

        if (photo.IsRaw)
        {
            try
            {
                var thumb = ExtractorFor(photo).ExtractPreview(photo.FilePath);
                if (thumb != null) r = ReadExifRotationFromJpeg(thumb);
            }
            catch { /* extraction failure — fall through to aspect heuristic */ }
        }

        // CR3 and a few others ship the medium thumb as pixels-pre-rotated with NO
        // EXIF tag, while the full-res embedded JPEG stays in camera-native
        // landscape (also no EXIF). Aspect alone can't tell 90° CW from 90° CCW
        // (orientation 6 vs 8) and either choice is 180° wrong half the time, so
        // determine the rotation by matching content against the small preview,
        // which we know is already correctly oriented.
        if (r == 0.0 && _basePreviewImage is { } basePreview)
            r = DetermineRotationByContentMatch(basePreview, fullJpeg);

        photo.JpegRotationDegrees = r;
        return r;
    }

    private static double DetermineRotationByContentMatch(BitmapSource reference, byte[] fullJpeg)
    {
        const int GridSize = 32;
        var refGrid = ToGrayscaleGrid(reference, GridSize);
        if (refGrid == null) return 0.0;

        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.StreamSource = new MemoryStream(fullJpeg);
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.DecodePixelWidth = 256; // big enough to keep detail, small enough to stay cheap
            bi.EndInit();
            bi.Freeze();

            double bestDiff = double.MaxValue;
            double bestAngle = 0.0;
            foreach (var angle in new[] { 0.0, 90.0, 180.0, 270.0 })
            {
                BitmapSource candidate = bi;
                if (angle != 0.0)
                {
                    var rotated = new TransformedBitmap(bi, new RotateTransform(angle));
                    rotated.Freeze();
                    candidate = rotated;
                }
                var grid = ToGrayscaleGrid(candidate, GridSize);
                if (grid == null) continue;
                long sum = 0;
                for (int i = 0; i < grid.Length; i++) sum += Math.Abs(grid[i] - refGrid[i]);
                if (sum < bestDiff) { bestDiff = sum; bestAngle = angle; }
            }
            return bestAngle;
        }
        catch { return 0.0; }
    }

    private static byte[]? ToGrayscaleGrid(BitmapSource source, int size)
    {
        try
        {
            var scaled = new TransformedBitmap(source,
                new System.Windows.Media.ScaleTransform((double)size / source.PixelWidth,
                                                       (double)size / source.PixelHeight));
            scaled.Freeze();
            var gray = new FormatConvertedBitmap(scaled, System.Windows.Media.PixelFormats.Gray8, null, 0);
            gray.Freeze();
            var pixels = new byte[size * size];
            gray.CopyPixels(pixels, size, 0);
            return pixels;
        }
        catch { return null; }
    }

    private static double ReadExifRotationFromJpeg(byte[] jpeg)
    {
        try
        {
            using var ms = new MemoryStream(jpeg);
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            return ReadExifRotation(decoder.Frames[0].Metadata as BitmapMetadata);
        }
        catch { return 0.0; }
    }

    private static double ReadExifRotationFromFile(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            return ReadExifRotation(decoder.Frames[0].Metadata as BitmapMetadata);
        }
        catch { return 0.0; }
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
            await Task.Delay(FullJpegPreloadSettleDelayMs, ct);
            if (ct.IsCancellationRequested || SelectedPhoto != photo || photo.FullJpeg != null) return;

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
    /// Warm JPEG previews for photos adjacent to the current selection so
    /// Next/Previous can paint from memory. RAW prefetch is deliberately delayed
    /// until navigation settles; LibRaw work cannot be interrupted mid-decode and
    /// should not compete with rapid arrow-key browsing.
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

        int parallelism = Math.Max(1, Math.Min(targets.Count, 2));
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
    /// Warm the JPEG preview cache for a single photo. Safe to call concurrently
    /// for different photos. Synchronous — caller wraps in Task.Run.
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
    }

    private void QueueVideoProxyPrefetch(int currentIndex)
    {
        _videoProxyPrefetchCts?.Cancel();
        if (currentIndex < 0 || currentIndex >= FilteredPhotos.Count) return;
        var photos = FilteredPhotos.ToList();

        var cts = new CancellationTokenSource();
        _videoProxyPrefetchCts = cts;
        _ = PrefetchVideoProxiesAfterSettleAsync(currentIndex, photos, cts);
    }

    private static List<PhotoItem> BuildVideoProxyPrefetchTargets(IReadOnlyList<PhotoItem> photos, int currentIndex)
    {
        var targets = new List<PhotoItem>();

        for (int distance = 1; distance < photos.Count; distance++)
        {
            AddIfNeeded(currentIndex + distance);
            AddIfNeeded(currentIndex - distance);
        }

        return targets;

        void AddIfNeeded(int i)
        {
            if (i < 0 || i >= photos.Count) return;
            var photo = photos[i];
            if (!VideoProxyCache.ShouldProxy(photo)) return;
            if (VideoProxyCache.TryGetFreshProxyPath(photo, out _)) return;
            targets.Add(photo);
        }
    }

    private async Task PrefetchVideoProxiesAfterSettleAsync(
        int currentIndex,
        IReadOnlyList<PhotoItem> photos,
        CancellationTokenSource cts)
    {
        var ct = cts.Token;
        try
        {
            await Task.Delay(VideoProxyPrefetchSettleDelayMs, ct);
            if (ct.IsCancellationRequested || SelectedIndex != currentIndex) return;

            var targets = BuildVideoProxyPrefetchTargets(photos, currentIndex);
            foreach (var photo in targets)
            {
                if (ct.IsCancellationRequested || SelectedIndex != currentIndex) return;
                if (VideoProxyCache.TryGetFreshProxyPath(photo, out _)) continue;

                try { await VideoProxyCache.GetOrCreateAsync(photo, progress: null, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch { /* proxy warmup is opportunistic; playback will retry on demand */ }
            }
        }
        catch (OperationCanceledException) { /* navigation moved on */ }
        finally
        {
            if (ReferenceEquals(_videoProxyPrefetchCts, cts))
                _videoProxyPrefetchCts = null;
            cts.Dispose();
        }
    }

    private void QueueRawNeighborPrefetch(int currentIndex)
    {
        _rawPrefetchCts?.Cancel();
        if (_libRaw == null || _cache == null) return;

        var cts = new CancellationTokenSource();
        _rawPrefetchCts = cts;
        _ = PrefetchRawNeighborsAfterSettleAsync(currentIndex, cts);
    }

    private async Task PrefetchRawNeighborsAfterSettleAsync(int currentIndex, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        try
        {
            await Task.Delay(RawPrefetchSettleDelayMs, ct);
            if (ct.IsCancellationRequested || SelectedIndex != currentIndex) return;

            var targets = new List<PhotoItem>(2);
            foreach (var offset in new[] { 1, -1 })
            {
                var i = currentIndex + offset;
                if (i < 0 || i >= FilteredPhotos.Count) continue;
                var photo = FilteredPhotos[i];
                if (photo.IsRaw && !photo.IsVideo)
                    targets.Add(photo);
            }

            foreach (var photo in targets)
            {
                if (ct.IsCancellationRequested || SelectedIndex != currentIndex) return;
                await Task.Run(() => PrefetchLinearRaw(photo, ct), ct);
            }
        }
        catch (OperationCanceledException) { /* navigation moved on */ }
        finally
        {
            if (ReferenceEquals(_rawPrefetchCts, cts))
                _rawPrefetchCts = null;
            cts.Dispose();
        }
    }

    private void PrefetchLinearRaw(PhotoItem photo, CancellationToken ct)
    {
        if (_libRaw != null && photo.IsRaw && _cache != null
            && CacheFor(photo).LoadLinearRaw(photo.FileName, photo.FilePath) == null)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var full = _libRaw.ExtractLinearRgb(photo.FilePath);
                var down = full?.Downsample(LinearRawPreviewWidth);
                if (down != null)
                    CacheFor(photo).SaveLinearRaw(photo.FileName, photo.FilePath, down.Width, down.Height, down.Pixels);
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
                        var photoCache = CacheFor(photo);
                        if (photoCache.LoadLinearRaw(photo.FileName, photo.FilePath) == null)
                        {
                            var full = _libRaw.ExtractLinearRgb(photo.FilePath);
                            var down = full?.Downsample(LinearRawPreviewWidth);
                            if (down != null)
                                photoCache.SaveLinearRaw(photo.FileName, photo.FilePath, down.Width, down.Height, down.Pixels);
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
            // Bulk caching can blow far past the budget — evict back down now
            // that the parallel writers have stopped.
            PruneLinearRawCaches();
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
                        var photoCache = CacheFor(photo);
                        byte[]? jpeg = photoCache.LoadPreview(photo.FileName)
                                    ?? photoCache.LoadThumbnail(photo.FileName)
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
                try { await Task.Run(() => SaveAllPhotosPerOwningDb(AllPhotos)); }
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
        if (currentIndex < 0 || currentIndex >= FilteredPhotos.Count)
        {
            ClearRetainedPreviewPhotos();
            return;
        }

        var nextWindow = new HashSet<PhotoItem>();
        int start = Math.Max(0, currentIndex - KeepRadius);
        int end = Math.Min(FilteredPhotos.Count - 1, currentIndex + KeepRadius);
        for (int i = start; i <= end; i++)
            nextWindow.Add(FilteredPhotos[i]);

        foreach (var photo in _retainedPreviewPhotos)
        {
            if (nextWindow.Contains(photo)) continue;
            photo.PreviewJpeg = null;
            photo.FullJpeg = null;
        }

        _retainedPreviewPhotos.Clear();
        foreach (var photo in nextWindow)
            _retainedPreviewPhotos.Add(photo);
    }

    private void ClearRetainedPreviewPhotos()
    {
        foreach (var photo in _retainedPreviewPhotos)
        {
            photo.PreviewJpeg = null;
            photo.FullJpeg = null;
        }
        _retainedPreviewPhotos.Clear();
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

    private static BitmapSource? LoadBitmapFromJpeg(byte[] jpeg, int decodePixelWidth = PreviewDecodeWidth, double? rotationOverride = null)
    {
        try
        {
            // Read EXIF orientation from headers — cheap, no pixel decode.
            // Callers can override when they've resolved rotation from another source
            // (e.g., a fallback thumb's EXIF when this JPEG's EXIF is missing).
            double rotation = rotationOverride ?? 0.0;
            if (rotationOverride == null)
            {
                try
                {
                    using var msMeta = new MemoryStream(jpeg);
                    var metaDecoder = BitmapDecoder.Create(msMeta, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    rotation = ReadExifRotation(metaDecoder.Frames[0].Metadata as BitmapMetadata);
                }
                catch { /* no EXIF — leave at 0 */ }
            }

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

    private List<PhotoItem> SelectedPhotosSnapshot()
    {
        List<PhotoItem> source = SelectedPhotos.Count == 0 && SelectedPhoto != null
            ? [SelectedPhoto]
            : SelectedPhotos.ToList();
        return ExpandCollapsedBurstRepresentatives(source);
    }

    private List<PhotoItem> ExpandCollapsedBurstRepresentatives(IEnumerable<PhotoItem> photos)
    {
        var result = new List<PhotoItem>();
        var seen = new HashSet<PhotoItem>();

        void Add(PhotoItem photo)
        {
            if (seen.Add(photo))
                result.Add(photo);
        }

        foreach (var photo in photos)
        {
            Add(photo);
            if (photo.CollapsedBurstCount <= 0 || photo.GroupId <= 0) continue;

            foreach (var member in GetBurstMembers(photo.GroupId))
                Add(member);
        }

        return result;
    }

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
        if (_ratingFilterExtraValues.Count > 0)
        {
            _ratingFilterExtraValues.Clear();
            OnPropertyChanged(nameof(RatingFilterExtraValues));
            OnPropertyChanged(nameof(RatingFilterActiveValues));
        }
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
            // Leaving Exact discards any multi-selected extras — AtLeast/LessThan
            // only have a single threshold value.
            if (RatingCycleMode != RatingFilterMode.Exact && _ratingFilterExtraValues.Count > 0)
            {
                _ratingFilterExtraValues.Clear();
                OnPropertyChanged(nameof(RatingFilterExtraValues));
                OnPropertyChanged(nameof(RatingFilterActiveValues));
            }
            RatingFilterMode = RatingCycleMode;
            ApplyFilter();
        }
    }

    [RelayCommand]
    private void SetRatingValue(int value) => SetRatingValueCore(value, extend: false);

    public void SetRatingValueCore(int value, bool extend)
    {
        bool exactMode = RatingCycleMode == RatingFilterMode.Exact;
        if (extend && exactMode && RatingFilterMode == RatingFilterMode.Exact)
        {
            // Shift-click in Exact mode toggles the value in the active set
            // (anchor + extras). Plain click resets to single-select.
            if (value == RatingFilterValue)
            {
                // Remove the anchor — promote an extra or turn the filter off.
                if (_ratingFilterExtraValues.Count > 0)
                {
                    var first = _ratingFilterExtraValues.First();
                    _ratingFilterExtraValues.Remove(first);
                    RatingFilterValue = first;
                }
                else
                {
                    RatingFilterMode = RatingFilterMode.Any;
                    RatingFilterExclude = false;
                }
            }
            else if (_ratingFilterExtraValues.Remove(value))
            {
                // Removed from extras — no other state change.
            }
            else
            {
                _ratingFilterExtraValues.Add(value);
            }
            OnPropertyChanged(nameof(RatingFilterExtraValues));
            OnPropertyChanged(nameof(RatingFilterActiveValues));
        }
        else
        {
            // Plain click: single-select. Clear any extras carried over from a
            // previous multi-select session so the new selection stands alone.
            if (_ratingFilterExtraValues.Count > 0)
            {
                _ratingFilterExtraValues.Clear();
                OnPropertyChanged(nameof(RatingFilterExtraValues));
            }
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
            OnPropertyChanged(nameof(RatingFilterActiveValues));
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
    private void SetFlagFilter(CullFlag flag) => SetFlagFilterCore(flag, extend: false);

    public void SetFlagFilterCore(CullFlag flag, bool extend)
    {
        if (extend && FlagFilter.HasValue)
        {
            if (FlagFilter.Value == flag)
            {
                // Toggle off the anchor — promote an extra if available.
                if (_flagFilterExtraValues.Count > 0)
                {
                    var first = _flagFilterExtraValues.First();
                    _flagFilterExtraValues.Remove(first);
                    FlagFilter = first;
                }
                else
                {
                    FlagFilter = null;
                    FlagFilterExclude = false;
                }
            }
            else if (_flagFilterExtraValues.Remove(flag))
            {
                // Removed from extras.
            }
            else
            {
                _flagFilterExtraValues.Add(flag);
            }
            OnPropertyChanged(nameof(FlagFilterExtraValues));
            OnPropertyChanged(nameof(FlagFilterActiveValues));
        }
        else
        {
            if (_flagFilterExtraValues.Count > 0)
            {
                _flagFilterExtraValues.Clear();
                OnPropertyChanged(nameof(FlagFilterExtraValues));
            }
            FlagFilter = FlagFilter == flag ? null : flag;
            if (!FlagFilter.HasValue) FlagFilterExclude = false;
            OnPropertyChanged(nameof(FlagFilterActiveValues));
        }
        ApplyFilter();
    }

    [RelayCommand]
    private void ClearFlagFilter()
    {
        FlagFilter = null;
        FlagFilterExclude = false;
        if (_flagFilterExtraValues.Count > 0)
        {
            _flagFilterExtraValues.Clear();
            OnPropertyChanged(nameof(FlagFilterExtraValues));
            OnPropertyChanged(nameof(FlagFilterActiveValues));
        }
        ApplyFilter();
    }

    [RelayCommand]
    private void SetColorLabelFilter(ColorLabel label) => SetColorLabelFilterCore(label, extend: false);

    public void SetColorLabelFilterCore(ColorLabel label, bool extend)
    {
        if (extend && ColorLabelFilter.HasValue)
        {
            if (ColorLabelFilter.Value == label)
            {
                if (_colorLabelFilterExtraValues.Count > 0)
                {
                    var first = _colorLabelFilterExtraValues.First();
                    _colorLabelFilterExtraValues.Remove(first);
                    ColorLabelFilter = first;
                }
                else
                {
                    ColorLabelFilter = null;
                    ColorLabelFilterExclude = false;
                }
            }
            else if (_colorLabelFilterExtraValues.Remove(label))
            {
                // Removed from extras.
            }
            else
            {
                _colorLabelFilterExtraValues.Add(label);
            }
            OnPropertyChanged(nameof(ColorLabelFilterExtraValues));
            OnPropertyChanged(nameof(ColorLabelFilterActiveValues));
        }
        else
        {
            if (_colorLabelFilterExtraValues.Count > 0)
            {
                _colorLabelFilterExtraValues.Clear();
                OnPropertyChanged(nameof(ColorLabelFilterExtraValues));
            }
            ColorLabelFilter = ColorLabelFilter == label ? null : label;
            if (!ColorLabelFilter.HasValue) ColorLabelFilterExclude = false;
            OnPropertyChanged(nameof(ColorLabelFilterActiveValues));
        }
        ApplyFilter();
    }

    [RelayCommand]
    private void ClearColorLabelFilter()
    {
        ColorLabelFilter = null;
        ColorLabelFilterExclude = false;
        if (_colorLabelFilterExtraValues.Count > 0)
        {
            _colorLabelFilterExtraValues.Clear();
            OnPropertyChanged(nameof(ColorLabelFilterExtraValues));
            OnPropertyChanged(nameof(ColorLabelFilterActiveValues));
        }
        ApplyFilter();
    }

    // ── Camera filter ──

    [RelayCommand]
    private void SetCameraFilter(string camera) => SetCameraFilterCore(camera, extend: false);

    public void SetCameraFilterCore(string camera, bool extend)
    {
        if (string.IsNullOrEmpty(camera)) return;
        bool changed;
        if (extend)
        {
            changed = _cameraFilters.Remove(camera) || _cameraFilters.Add(camera);
        }
        else
        {
            // Plain click: toggle off if this was the sole selection, else snap to it.
            if (_cameraFilters.Count == 1 && _cameraFilters.Contains(camera))
            {
                _cameraFilters.Clear();
                changed = true;
            }
            else
            {
                _cameraFilters.Clear();
                _cameraFilters.Add(camera);
                changed = true;
            }
        }
        if (changed)
        {
            OnPropertyChanged(nameof(CameraFilters));
            OnPropertyChanged(nameof(IsCameraFilterActive));
            OnPropertyChanged(nameof(HasActiveFilters));
        }
        ApplyFilter();
    }

    [RelayCommand]
    private void ClearCameraFilter()
    {
        if (_cameraFilters.Count == 0) return;
        _cameraFilters.Clear();
        OnPropertyChanged(nameof(CameraFilters));
        OnPropertyChanged(nameof(IsCameraFilterActive));
        OnPropertyChanged(nameof(HasActiveFilters));
        ApplyFilter();
    }

    // ── Tag commands ──

    [RelayCommand]
    private void SetTagFilter(PhotoTag tag) => SetTagFilterCore(tag, extend: false);

    public void SetTagFilterCore(PhotoTag tag, bool extend)
    {
        if (extend && TagFilter != null)
        {
            if (TagFilter.Id == tag.Id)
            {
                if (_tagFilterExtraIds.Count > 0)
                {
                    var firstId = _tagFilterExtraIds.First();
                    _tagFilterExtraIds.Remove(firstId);
                    TagFilter = Tags.FirstOrDefault(t => t.Id == firstId);
                }
                else
                {
                    TagFilter = null;
                    TagFilterExclude = false;
                }
            }
            else if (_tagFilterExtraIds.Remove(tag.Id))
            {
                // Removed from extras.
            }
            else
            {
                _tagFilterExtraIds.Add(tag.Id);
            }
            OnPropertyChanged(nameof(TagFilterExtraIds));
            OnPropertyChanged(nameof(TagFilterActiveIds));
        }
        else
        {
            if (_tagFilterExtraIds.Count > 0)
            {
                _tagFilterExtraIds.Clear();
                OnPropertyChanged(nameof(TagFilterExtraIds));
            }
            TagFilter = TagFilter?.Id == tag.Id ? null : tag;
            if (TagFilter == null) TagFilterExclude = false;
            OnPropertyChanged(nameof(TagFilterActiveIds));
        }
        ApplyFilter();
    }

    [RelayCommand]
    private void ClearTagFilter()
    {
        TagFilter = null;
        TagFilterExclude = false;
        if (_tagFilterExtraIds.Count > 0)
        {
            _tagFilterExtraIds.Clear();
            OnPropertyChanged(nameof(TagFilterExtraIds));
            OnPropertyChanged(nameof(TagFilterActiveIds));
        }
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
        if (IsRecursiveView)
        {
            StatusText = "Tag editing is disabled in recursive view. Toggle ‘Include subfolders’ off to manage tags.";
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
        if (_db == null || tag.IsSystem) return;
        if (IsRecursiveView)
        {
            StatusText = "Tag editing is disabled in recursive view.";
            return;
        }
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
        if (_db == null || tag.IsSystem) return;
        if (IsRecursiveView)
        {
            StatusText = "Tag editing is disabled in recursive view.";
            return;
        }
        _db.DeleteGroup(tag.Id);
        foreach (var photo in AllPhotos.Where(p => p.TagIds.Contains(tag.Id)))
        {
            photo.TagIds.Remove(tag.Id);
            UpdateTagDisplay(photo);
        }
        Tags.Remove(tag);
        bool extrasChanged = _tagFilterExtraIds.Remove(tag.Id);
        if (TagFilter?.Id == tag.Id)
        {
            // Promote a remaining extra into the anchor slot if one exists.
            if (_tagFilterExtraIds.Count > 0)
            {
                var firstId = _tagFilterExtraIds.First();
                _tagFilterExtraIds.Remove(firstId);
                TagFilter = Tags.FirstOrDefault(t => t.Id == firstId);
                extrasChanged = true;
            }
            else
            {
                TagFilter = null;
            }
            ApplyFilter();
        }
        if (extrasChanged)
        {
            OnPropertyChanged(nameof(TagFilterExtraIds));
            OnPropertyChanged(nameof(TagFilterActiveIds));
        }
        OnPropertyChanged(nameof(SelectedPhotoTagAssignments));
    }

    [RelayCommand]
    private void ToggleTagForSelected(PhotoTag tag)
    {
        if (SelectedPhoto == null || _db == null) return;
        // System tags (HDR) are managed by RAWR's detectors and aren't user-toggleable.
        if (tag.IsSystem) return;
        if (IsRecursiveView)
        {
            StatusText = "Tag editing is disabled in recursive view. Toggle ‘Include subfolders’ off to edit tags.";
            return;
        }
        var photos = SelectedPhotosSnapshot();
        var hasCollapsedBurstRepresentative =
            SelectedPhoto.CollapsedBurstCount > 0 || SelectedPhotos.Any(p => p.CollapsedBurstCount > 0);

        // Normal multi-select keeps the existing anchor-driven toggle semantics.
        // Collapsed bursts are different: the representative stands for hidden
        // frames too, so a partial tag state fills missing burst members and only
        // toggles off once every target already has the tag.
        var assignToAll = hasCollapsedBurstRepresentative
            ? photos.Any(p => !p.TagIds.Contains(tag.Id))
            : !SelectedPhoto.TagIds.Contains(tag.Id);

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
            if (TagFilter != null || _tagFilterExtraIds.Count > 0) ApplyFilter();
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
            if (TagFilter != null || _tagFilterExtraIds.Count > 0) ApplyFilter();
        }

        ApplyAll();
        var verb = assignToAll ? "Add" : "Remove";
        var label = changedPhotos.Count == 1
            ? $"{verb} tag “{tag.Name}”"
            : $"{verb} tag “{tag.Name}” ({changedPhotos.Count} photos)";
        History.Record(new EditOp(label, SelectedPhoto, ApplyAll, RevertAll));
    }

    [RelayCommand]
    private void ClearAssignedMetadataForSelected()
    {
        if (SelectedPhoto == null) return;
        ClearAssignedMetadata(SelectedPhotosSnapshot(), "selected photos");
    }

    [RelayCommand]
    private void ClearAssignedMetadataForAll()
    {
        if (AllPhotos.Count == 0) return;
        ClearAssignedMetadata(AllPhotos.ToList(), "photos in this folder");
    }

    private void ClearAssignedMetadata(IList<PhotoItem> photos, string scopeLabel)
    {
        var snapshots = photos
            .Where(HasAssignedMetadata)
            .Select(p => new AssignedMetadataSnapshot(
                p,
                p.Rating,
                p.Flag,
                p.ColorLabel,
                p.TagIds.ToArray()))
            .ToList();
        if (snapshots.Count == 0)
        {
            StatusText = $"No assigned metadata to clear for {scopeLabel}.";
            return;
        }

        var prompt = snapshots.Count == 1
            ? $"Remove rating, flag, color label, and tags from \"{snapshots[0].Photo.FileName}\"?"
            : $"Remove ratings, flags, color labels, and tags from {snapshots.Count} {scopeLabel}?";
        var result = MessageBox.Show(
            prompt + "\n\nThis can be undone with Undo.",
            "Clear Assigned Metadata",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.OK) return;

        void ApplyAll()
        {
            foreach (var snapshot in snapshots)
            {
                var photo = snapshot.Photo;
                photo.Rating = 0;
                photo.Flag = CullFlag.Unflagged;
                photo.ColorLabel = ColorLabel.None;
                photo.TagIds.Clear();
                UpdateTagDisplay(photo);
            }
            PersistMetadataSnapshotBatch(snapshots);
            RefreshAfterAssignedMetadataChange();
        }

        void RevertAll()
        {
            foreach (var snapshot in snapshots)
            {
                var photo = snapshot.Photo;
                photo.Rating = snapshot.Rating;
                photo.Flag = snapshot.Flag;
                photo.ColorLabel = snapshot.ColorLabel;
                photo.TagIds.Clear();
                foreach (var tagId in snapshot.TagIds)
                    photo.TagIds.Add(tagId);
                UpdateTagDisplay(photo);
            }
            PersistMetadataSnapshotBatch(snapshots);
            RefreshAfterAssignedMetadataChange();
        }

        ApplyAll();
        var label = snapshots.Count == 1
            ? "Clear assigned metadata"
            : $"Clear assigned metadata ({snapshots.Count} photos)";
        History.Record(new EditOp(label, SelectedPhoto ?? snapshots[0].Photo, ApplyAll, RevertAll));
        StatusText = snapshots.Count == 1
            ? $"Cleared assigned metadata for {snapshots[0].Photo.FileName}."
            : $"Cleared assigned metadata for {snapshots.Count} photos.";
    }

    private static bool HasAssignedMetadata(PhotoItem photo) =>
        photo.Rating != 0
        || photo.Flag != CullFlag.Unflagged
        || photo.ColorLabel != ColorLabel.None
        || photo.TagIds.Count > 0;

    private void PersistMetadataSnapshotBatch(IList<AssignedMetadataSnapshot> snapshots)
    {
        // Group by owning subfolder so each subfolder's DB sees a single
        // transaction. In recursive view the IDs in photo.TagIds are synthetic
        // display IDs that don't map back to local DB IDs (Phase 2 will fix
        // this) — for now we only persist tag *clearing*, not re-assignment.
        foreach (var grp in snapshots.GroupBy(s => OwningFolderOf(s.Photo), StringComparer.OrdinalIgnoreCase))
        {
            if (!_contexts.TryGetValue(grp.Key, out var ctx)) continue;
            ctx.Db.WithTransaction(() =>
            {
                foreach (var snapshot in grp)
                {
                    ctx.Db.ClearGroupsForPhoto(snapshot.Photo.FileName);
                    if (!IsRecursiveView)
                    {
                        foreach (var tagId in snapshot.Photo.TagIds)
                            ctx.Db.AssignGroup(snapshot.Photo.FileName, tagId);
                    }
                }
            });
        }

        SavePhotoBatch(snapshots.Select(s => s.Photo).ToList());
    }

    private void RefreshAfterAssignedMetadataChange()
    {
        OnPropertyChanged(nameof(SelectedPhotoTagAssignments));

        if (RatingFilterMode != RatingFilterMode.Any
            || FlagFilter.HasValue
            || ColorLabelFilter.HasValue
            || TagFilter != null)
        {
            ApplyFilter();
        }
        else
        {
            RefreshFilterBuckets();
            OnPropertyChanged(nameof(CopyTargetCount));
            UpdateStatus();
        }
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

    /// <summary>
    /// Apply a user-defined macro (flag/rating/color/tag) to the current selection
    /// in a single undoable step. Each macro field is "set" semantics — pressing the
    /// macro again on already-matching photos is a no-op, never a toggle. A tag name
    /// that doesn't yet exist is auto-created.
    /// </summary>
    public void ExecuteMacro(Shortcuts.KeyboardMacro macro)
        => ExecuteMacro(macro, SelectedPhotosSnapshot());

    /// <summary>
    /// Macro variant with an explicit target list — used by the burst viewer so the
    /// macro hits only the frame on screen, not whatever multi-selection happens to
    /// be active in the main window.
    /// </summary>
    public void ExecuteMacro(Shortcuts.KeyboardMacro macro, IList<PhotoItem> photos)
    {
        if (!macro.HasAnyAction) return;
        if (photos.Count == 0) return;

        // Resolve / create the target tag once up front so undo can refer to it.
        // In recursive view tag creation/assignment is skipped (Phase 2 will route
        // these per subfolder); the macro still applies any rating/flag/colour
        // parts so the keystroke isn't a silent no-op for those.
        PhotoTag? targetTag = null;
        bool tagCreatedByMacro = false;
        if (!string.IsNullOrWhiteSpace(macro.TagName) && _db != null && !IsRecursiveView)
        {
            targetTag = Tags.FirstOrDefault(t =>
                string.Equals(t.Name, macro.TagName, StringComparison.OrdinalIgnoreCase));
            if (targetTag == null)
            {
                targetTag = _db.CreateGroup(macro.TagName!);
                Tags.Add(targetTag);
                tagCreatedByMacro = true;
            }
        }
        else if (!string.IsNullOrWhiteSpace(macro.TagName) && IsRecursiveView)
        {
            StatusText = "Macro tag part skipped (tag editing is disabled in recursive view).";
        }

        // Snapshot per-photo prior state for the things this macro touches.
        var snapshots = photos.Select(p => new
        {
            Photo = p,
            OldFlag = p.Flag,
            OldRating = p.Rating,
            OldLabel = p.ColorLabel,
            HadTag = targetTag != null && p.TagIds.Contains(targetTag.Id),
        }).ToList();

        var changedPhotos = new List<PhotoItem>();

        void ApplyAll()
        {
            changedPhotos.Clear();
            foreach (var s in snapshots)
            {
                var p = s.Photo;
                bool changed = false;

                if (macro.SetFlag.HasValue && p.Flag != macro.SetFlag.Value)
                {
                    p.Flag = macro.SetFlag.Value;
                    changed = true;
                }
                if (macro.SetRating.HasValue)
                {
                    var r = Math.Clamp(macro.SetRating.Value, 0, 5);
                    if (p.Rating != r) { p.Rating = r; changed = true; }
                }
                if (macro.SetColorLabel.HasValue && p.ColorLabel != macro.SetColorLabel.Value)
                {
                    p.ColorLabel = macro.SetColorLabel.Value;
                    changed = true;
                }
                if (targetTag != null && !p.TagIds.Contains(targetTag.Id))
                {
                    if (_db != null)
                    {
                        p.TagIds.Add(targetTag.Id);
                        _db.AssignGroup(p.FileName, targetTag.Id);
                        UpdateTagDisplay(p);
                    }
                    changed = true;
                }

                if (changed) changedPhotos.Add(p);
            }
            if (changedPhotos.Count > 0) SavePhotoBatch(changedPhotos);
            if (TagFilter != null && targetTag != null) ApplyFilter();
        }

        void RevertAll()
        {
            foreach (var s in snapshots)
            {
                var p = s.Photo;
                if (macro.SetFlag.HasValue) p.Flag = s.OldFlag;
                if (macro.SetRating.HasValue) p.Rating = s.OldRating;
                if (macro.SetColorLabel.HasValue) p.ColorLabel = s.OldLabel;
                if (targetTag != null && !s.HadTag && p.TagIds.Contains(targetTag.Id))
                {
                    if (_db != null)
                    {
                        p.TagIds.Remove(targetTag.Id);
                        _db.UnassignGroup(p.FileName, targetTag.Id);
                        UpdateTagDisplay(p);
                    }
                }
            }
            SavePhotoBatch(snapshots.Select(s => s.Photo).ToList());
            if (TagFilter != null && targetTag != null) ApplyFilter();
        }

        ApplyAll();
        if (changedPhotos.Count == 0 && !tagCreatedByMacro) return;

        var label = string.IsNullOrWhiteSpace(macro.Name)
            ? $"Macro ({changedPhotos.Count} photo{(changedPhotos.Count == 1 ? "" : "s")})"
            : $"Macro: {macro.Name}";
        History.Record(new EditOp(label, SelectedPhoto ?? snapshots[0].Photo, ApplyAll, RevertAll));
    }

    private void UpdateTagDisplay(PhotoItem photo)
    {
        // System tags (HDR, Panorama) render in their own coloured pills, so
        // they're filtered out of the regular tag display string. IsHdr and
        // IsPanorama drive those pills in the thumbnail and metadata templates.
        bool isHdr = false;
        bool isPanorama = false;
        var visibleNames = new List<string>(photo.TagIds.Count);
        foreach (var id in photo.TagIds)
        {
            var t = Tags.FirstOrDefault(x => x.Id == id);
            if (t == null) continue;
            if (t.IsSystem)
            {
                if (string.Equals(t.Name, HdrTagName, StringComparison.Ordinal))
                    isHdr = true;
                else if (string.Equals(t.Name, PanoramaTagName, StringComparison.Ordinal))
                    isPanorama = true;
                continue;
            }
            visibleNames.Add(t.Name);
        }
        ApplyTagPresentation(photo, isHdr, isPanorama, visibleNames);
    }

    private static void UpdateTagDisplay(PhotoItem photo, IReadOnlyDictionary<int, PhotoTag> tagsById)
    {
        bool isHdr = false;
        bool isPanorama = false;
        var visibleNames = new List<string>(photo.TagIds.Count);
        foreach (var id in photo.TagIds)
        {
            if (!tagsById.TryGetValue(id, out var t)) continue;
            if (t.IsSystem)
            {
                if (string.Equals(t.Name, HdrTagName, StringComparison.Ordinal))
                    isHdr = true;
                else if (string.Equals(t.Name, PanoramaTagName, StringComparison.Ordinal))
                    isPanorama = true;
                continue;
            }
            visibleNames.Add(t.Name);
        }
        ApplyTagPresentation(photo, isHdr, isPanorama, visibleNames);
    }

    private static void ApplyTagPresentation(PhotoItem photo, bool isHdr, bool isPanorama, List<string> visibleNames)
    {
        photo.IsHdr = isHdr;
        photo.IsPanorama = isPanorama;
        photo.TagDisplay = visibleNames.Count == 0 ? "" : string.Join("\n", visibleNames);
    }

    private const string HdrTagName = "HDR";
    private const string HdrTagColor = "#FF7A00";
    private const string PanoramaTagName = "Panorama";
    private const string PanoramaTagColor = "#00B8B8";

    /// <summary>
    /// Re-runs HDR classification over already-grouped bursts and syncs the
    /// auto-managed HDR tag in the database. Safe to call repeatedly — only
    /// photos whose HDR state actually changed touch the DB.
    /// </summary>
    private void ApplyHdrDetection()
    {
        if (_db == null || AllPhotos.Count == 0) return;

        // Detection disabled → produce an empty result so any previously-tagged
        // photos get unassigned by the diff loop below. The system tag itself is
        // left in the DB so re-enabling the detector picks it back up cleanly.
        var hdrFiles = AppSettings.Current.HdrDetectionEnabled
            ? HdrDetector.Detect(
                AllPhotos,
                AppSettings.Current.HdrMinBracketSize,
                AppSettings.Current.HdrMinExposureSpread)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var hdrTag = Tags.FirstOrDefault(t => t.IsSystem && t.Name == HdrTagName);
        if (hdrTag == null && hdrFiles.Count > 0)
        {
            hdrTag = _db.FindSystemGroup(HdrTagName)
                  ?? _db.CreateGroup(HdrTagName, isSystem: true, color: HdrTagColor);
            Tags.Add(hdrTag);
        }
        if (hdrTag == null) return;

        int hdrId = hdrTag.Id;
        var toAssign = new List<PhotoItem>();
        var toUnassign = new List<PhotoItem>();
        foreach (var photo in AllPhotos)
        {
            bool shouldHave = hdrFiles.Contains(photo.FileName);
            bool has = photo.TagIds.Contains(hdrId);
            if (shouldHave && !has) toAssign.Add(photo);
            else if (!shouldHave && has) toUnassign.Add(photo);
        }
        if (toAssign.Count > 0 || toUnassign.Count > 0)
        {
            _db.WithTransaction(() =>
            {
                foreach (var p in toAssign)
                {
                    p.TagIds.Add(hdrId);
                    _db.AssignGroup(p.FileName, hdrId);
                }
                foreach (var p in toUnassign)
                {
                    p.TagIds.Remove(hdrId);
                    _db.UnassignGroup(p.FileName, hdrId);
                }
            });
        }

        // HdrDetector reset every IsHdr to false; reconcile from the now-correct
        // TagIds membership so photos that retained their HDR status keep the pill.
        foreach (var p in AllPhotos)
            UpdateTagDisplay(p);
    }

    /// <summary>
    /// Detects panorama sweeps and applies the auto-managed Panorama tag plus a
    /// fresh GroupId/BurstBadge to each member so they collapse with the existing
    /// burst toggle. Independent of HDR — runs on the same data after BurstDetector.
    /// </summary>
    private void ApplyPanoramaDetection()
    {
        if (_db == null || AllPhotos.Count == 0) return;

        // Settings expose overlap %, but the detector talks shift fractions.
        // overlap = 1 - shift, so a "max overlap" of 85% maps to a min shift
        // of 0.15, and a "min overlap" of 20% maps to a max shift of 0.80.
        var s = AppSettings.Current;
        float minShift = Math.Clamp(1f - s.PanoramaMaxOverlapPct / 100f, 0f, 0.99f);
        float maxShift = Math.Clamp(1f - s.PanoramaMinOverlapPct / 100f, minShift + 0.01f, 0.99f);

        PanoramaDetector.Result result;
        if (s.PanoramaDetectionEnabled)
        {
            result = PanoramaDetector.Detect(
                AllPhotos,
                Rawr.App.Services.PerceptualHash.StripWidth,
                Rawr.App.Services.PerceptualHash.StripHeight,
                minChainSize: s.PanoramaMinChainSize,
                maxGapSeconds: s.PanoramaMaxGapSeconds,
                minShift: minShift,
                maxShift: maxShift,
                maxDirectionDeltaDegrees: s.PanoramaDirectionToleranceDeg);
        }
        else
        {
            // Detection disabled — clear IsPanorama so any previous pill state
            // doesn't linger, then take the no-op path through the diff loop.
            foreach (var p in AllPhotos) p.IsPanorama = false;
            result = new PanoramaDetector.Result(Array.Empty<IReadOnlyList<PhotoItem>>());
        }

        var panoFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var seq in result.Sequences)
            foreach (var p in seq) panoFiles.Add(p.FileName);

        var panoTag = Tags.FirstOrDefault(t => t.IsSystem && t.Name == PanoramaTagName);
        if (panoTag == null && panoFiles.Count > 0)
        {
            panoTag = _db.FindSystemGroup(PanoramaTagName)
                  ?? _db.CreateGroup(PanoramaTagName, isSystem: true, color: PanoramaTagColor);
            Tags.Add(panoTag);
        }

        // Assign each detected sequence its own GroupId so the Collapse Bursts
        // toggle stacks the panorama into a single tile. Numbering picks up
        // above whatever BurstDetector already used.
        if (result.Sequences.Count > 0)
        {
            int nextGroupId = 0;
            foreach (var p in AllPhotos)
                if (p.GroupId > nextGroupId) nextGroupId = p.GroupId;

            foreach (var seq in result.Sequences)
            {
                nextGroupId++;
                for (int i = 0; i < seq.Count; i++)
                {
                    seq[i].GroupId = nextGroupId;
                    seq[i].BurstBadge = $"{i + 1}/{seq.Count}";
                }
            }
        }

        if (panoTag != null)
        {
            int panoId = panoTag.Id;
            var toAssign = new List<PhotoItem>();
            var toUnassign = new List<PhotoItem>();
            foreach (var photo in AllPhotos)
            {
                bool shouldHave = panoFiles.Contains(photo.FileName);
                bool has = photo.TagIds.Contains(panoId);
                if (shouldHave && !has) toAssign.Add(photo);
                else if (!shouldHave && has) toUnassign.Add(photo);
            }
            if (toAssign.Count > 0 || toUnassign.Count > 0)
            {
                _db.WithTransaction(() =>
                {
                    foreach (var p in toAssign)
                    {
                        p.TagIds.Add(panoId);
                        _db.AssignGroup(p.FileName, panoId);
                    }
                    foreach (var p in toUnassign)
                    {
                        p.TagIds.Remove(panoId);
                        _db.UnassignGroup(p.FileName, panoId);
                    }
                });
            }
        }

        foreach (var p in AllPhotos)
            UpdateTagDisplay(p);
    }

    /// <summary>
    /// Phase-1 recursive-view variant: run HDR detection but only update the
    /// transient IsHdr pill on each photo. The DB-side system-tag sync is
    /// deferred until Phase 2 (it needs the auto-tag to be created in every
    /// subfolder DB on demand and the tag IDs translated through the merged
    /// display table).
    /// </summary>
    private void ApplyHdrDetectionPerFolder()
    {
        if (AllPhotos.Count == 0) return;
        if (!AppSettings.Current.HdrDetectionEnabled)
        {
            foreach (var p in AllPhotos) p.IsHdr = false;
            return;
        }

        foreach (var grp in AllPhotos.GroupBy(OwningFolderOf, StringComparer.OrdinalIgnoreCase))
        {
            var subset = grp.ToList();
            var hdrFiles = HdrDetector.Detect(
                subset,
                AppSettings.Current.HdrMinBracketSize,
                AppSettings.Current.HdrMinExposureSpread);
            foreach (var p in subset)
                p.IsHdr = hdrFiles.Contains(p.FileName);
        }
    }

    /// <summary>
    /// Phase-1 recursive-view variant of <see cref="ApplyPanoramaDetection"/>:
    /// runs detection per subfolder, sets IsPanorama plus a per-subset
    /// GroupId/BurstBadge so the burst-collapse toggle still stacks panoramas,
    /// but skips the DB-side system-tag sync (deferred to Phase 2).
    /// </summary>
    private void ApplyPanoramaDetectionPerFolder()
    {
        if (AllPhotos.Count == 0) return;
        if (!AppSettings.Current.PanoramaDetectionEnabled)
        {
            foreach (var p in AllPhotos) p.IsPanorama = false;
            return;
        }

        var s = AppSettings.Current;
        float minShift = Math.Clamp(1f - s.PanoramaMaxOverlapPct / 100f, 0f, 0.99f);
        float maxShift = Math.Clamp(1f - s.PanoramaMinOverlapPct / 100f, minShift + 0.01f, 0.99f);

        int nextGroupId = 0;
        foreach (var p in AllPhotos)
            if (p.GroupId > nextGroupId) nextGroupId = p.GroupId;

        foreach (var grp in AllPhotos.GroupBy(OwningFolderOf, StringComparer.OrdinalIgnoreCase))
        {
            var subset = grp.ToList();
            var result = PanoramaDetector.Detect(
                subset,
                Rawr.App.Services.PerceptualHash.StripWidth,
                Rawr.App.Services.PerceptualHash.StripHeight,
                minChainSize: s.PanoramaMinChainSize,
                maxGapSeconds: s.PanoramaMaxGapSeconds,
                minShift: minShift,
                maxShift: maxShift,
                maxDirectionDeltaDegrees: s.PanoramaDirectionToleranceDeg);

            var panoFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var seq in result.Sequences)
            {
                nextGroupId++;
                for (int i = 0; i < seq.Count; i++)
                {
                    seq[i].GroupId = nextGroupId;
                    seq[i].BurstBadge = $"{i + 1}/{seq.Count}";
                    panoFiles.Add(seq[i].FileName);
                }
            }
            foreach (var p in subset)
                p.IsPanorama = panoFiles.Contains(p.FileName);
        }
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
        ClearAllFilterExtras();
        ApplyFilter();
    }

    private void ClearAllFilterExtras()
    {
        bool changed = false;
        if (_ratingFilterExtraValues.Count > 0)    { _ratingFilterExtraValues.Clear();    changed = true; OnPropertyChanged(nameof(RatingFilterExtraValues));    OnPropertyChanged(nameof(RatingFilterActiveValues)); }
        if (_flagFilterExtraValues.Count > 0)      { _flagFilterExtraValues.Clear();      changed = true; OnPropertyChanged(nameof(FlagFilterExtraValues));      OnPropertyChanged(nameof(FlagFilterActiveValues)); }
        if (_colorLabelFilterExtraValues.Count > 0){ _colorLabelFilterExtraValues.Clear();changed = true; OnPropertyChanged(nameof(ColorLabelFilterExtraValues));OnPropertyChanged(nameof(ColorLabelFilterActiveValues)); }
        if (_tagFilterExtraIds.Count > 0)          { _tagFilterExtraIds.Clear();          changed = true; OnPropertyChanged(nameof(TagFilterExtraIds));          OnPropertyChanged(nameof(TagFilterActiveIds)); }
        if (_imageTypeFilterExtraValues.Count > 0) { _imageTypeFilterExtraValues.Clear(); changed = true; OnPropertyChanged(nameof(ImageTypeFilterExtraValues)); OnPropertyChanged(nameof(ImageTypeFilterActiveValues)); }
        if (_cameraFilters.Count > 0)              { _cameraFilters.Clear();              changed = true; OnPropertyChanged(nameof(CameraFilters));              OnPropertyChanged(nameof(IsCameraFilterActive)); }
        if (changed) OnPropertyChanged(nameof(HasActiveFilters));
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
    private void SetImageTypeFilter(ImageTypeFilterMode mode) => SetImageTypeFilterCore(mode, extend: false);

    public void SetImageTypeFilterCore(ImageTypeFilterMode mode, bool extend)
    {
        if (mode == ImageTypeFilterMode.Any) return;
        if (extend && ImageTypeFilter != ImageTypeFilterMode.Any)
        {
            if (ImageTypeFilter == mode)
            {
                if (_imageTypeFilterExtraValues.Count > 0)
                {
                    var first = _imageTypeFilterExtraValues.First();
                    _imageTypeFilterExtraValues.Remove(first);
                    ImageTypeFilter = first;
                }
                else
                {
                    ImageTypeFilter = ImageTypeFilterMode.Any;
                    ImageTypeFilterExclude = false;
                }
            }
            else if (_imageTypeFilterExtraValues.Remove(mode))
            {
                // Removed from extras.
            }
            else
            {
                _imageTypeFilterExtraValues.Add(mode);
            }
            OnPropertyChanged(nameof(ImageTypeFilterExtraValues));
            OnPropertyChanged(nameof(ImageTypeFilterActiveValues));
        }
        else
        {
            if (_imageTypeFilterExtraValues.Count > 0)
            {
                _imageTypeFilterExtraValues.Clear();
                OnPropertyChanged(nameof(ImageTypeFilterExtraValues));
            }
            ImageTypeFilter = ImageTypeFilter == mode ? ImageTypeFilterMode.Any : mode;
            if (ImageTypeFilter == ImageTypeFilterMode.Any) ImageTypeFilterExclude = false;
            OnPropertyChanged(nameof(ImageTypeFilterActiveValues));
        }
        ApplyFilter();
    }

    [RelayCommand]
    private void ClearImageTypeFilter()
    {
        ImageTypeFilter = ImageTypeFilterMode.Any;
        ImageTypeFilterExclude = false;
        if (_imageTypeFilterExtraValues.Count > 0)
        {
            _imageTypeFilterExtraValues.Clear();
            OnPropertyChanged(nameof(ImageTypeFilterExtraValues));
            OnPropertyChanged(nameof(ImageTypeFilterActiveValues));
        }
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
    /// Priority: user-pinned > rated+picked > highest rated > any pick > first chronologically.
    /// </summary>
    private static PhotoItem SelectBurstRepresentative(List<PhotoItem> members)
    {
        var pinned = members.FirstOrDefault(p => p.IsBestInGroup);
        if (pinned != null) return pinned;

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
        if (_db == null) return;
        DbFor(photo).Save(photo);
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

                // In recursive view we skip keyword→tag creation/assignment
                // because tag IDs are synthetic display IDs that don't map back
                // to the photo's subfolder DB. The keyword still gets re-applied
                // the next time the folder is opened single-folder.
                if (IsRecursiveView) continue;

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
            DbFor(photo).Save(photo);
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

        var folderPhotos = AllPhotos.ToList();
        var tagNames = Tags.ToDictionary(t => t.Id, t => t.Name);
        var snapshots = folderPhotos
            .Where(p => !p.IsVideo)
            .Select(p => (path: p.FilePath, data: XmpSidecar.Snapshot(p, tagNames)))
            .ToList();
        StatusText = $"Writing XMP for {snapshots.Count}/{folderPhotos.Count} photos in this folder...";

        int written = 0;
        await Task.Run(() =>
        {
            foreach (var (path, data) in snapshots)
            {
                try { XmpSidecar.Write(path, data); written++; }
                catch { /* skip files we can't write next to (read-only media, locked, …) */ }
            }
        });

        StatusText = $"Wrote {written}/{snapshots.Count} XMP sidecars for this folder.";
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
        ApplyHdrDetection();
        ApplyPanoramaDetection();
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

        IEnumerable<PhotoItem> visible = AllPhotos;

        Func<PhotoItem, bool>? ratingPred;
        if (RatingFilterMode == RatingFilterMode.Exact && _ratingFilterExtraValues.Count > 0)
        {
            // Multi-select in Exact mode: photo's rating must match any value in
            // the anchor ∪ extras set. Snapshot to avoid capturing the mutable set.
            var ratings = new HashSet<int>(_ratingFilterExtraValues) { RatingFilterValue };
            ratingPred = p => ratings.Contains(p.Rating);
        }
        else
        {
            ratingPred = RatingFilterMode switch
            {
                RatingFilterMode.Exact    => p => p.Rating == RatingFilterValue,
                RatingFilterMode.AtLeast  => p => p.Rating >= RatingFilterValue,
                RatingFilterMode.LessThan => p => p.Rating <  RatingFilterValue,
                _                         => null
            };
        }
        if (ratingPred != null)
            visible = RatingFilterExclude ? visible.Where(p => !ratingPred(p)) : visible.Where(ratingPred);

        if (FlagFilter.HasValue)
        {
            if (_flagFilterExtraValues.Count > 0)
            {
                var flags = new HashSet<CullFlag>(_flagFilterExtraValues) { FlagFilter.Value };
                visible = FlagFilterExclude ? visible.Where(p => !flags.Contains(p.Flag)) : visible.Where(p => flags.Contains(p.Flag));
            }
            else
            {
                var f = FlagFilter.Value;
                visible = FlagFilterExclude ? visible.Where(p => p.Flag != f) : visible.Where(p => p.Flag == f);
            }
        }
        if (ColorLabelFilter.HasValue)
        {
            if (_colorLabelFilterExtraValues.Count > 0)
            {
                var labels = new HashSet<ColorLabel>(_colorLabelFilterExtraValues) { ColorLabelFilter.Value };
                visible = ColorLabelFilterExclude ? visible.Where(p => !labels.Contains(p.ColorLabel)) : visible.Where(p => labels.Contains(p.ColorLabel));
            }
            else
            {
                var c = ColorLabelFilter.Value;
                visible = ColorLabelFilterExclude ? visible.Where(p => p.ColorLabel != c) : visible.Where(p => p.ColorLabel == c);
            }
        }
        if (TagFilter != null)
        {
            if (_tagFilterExtraIds.Count > 0)
            {
                var tagIds = new HashSet<int>(_tagFilterExtraIds) { TagFilter.Id };
                visible = TagFilterExclude
                    ? visible.Where(p => !p.TagIds.Any(id => tagIds.Contains(id)))
                    : visible.Where(p => p.TagIds.Any(id => tagIds.Contains(id)));
            }
            else
            {
                var tagId = TagFilter.Id;
                visible = TagFilterExclude ? visible.Where(p => !p.TagIds.Contains(tagId)) : visible.Where(p => p.TagIds.Contains(tagId));
            }
        }

        if (_cameraFilters.Count > 0)
        {
            // Snapshot the set so the closure isn't tied to the mutable HashSet.
            var camSet = new HashSet<string>(_cameraFilters, StringComparer.Ordinal);
            bool includeUnknown = camSet.Contains(UnknownCameraKey);
            Func<PhotoItem, bool> camPred = p =>
            {
                var name = p.Metadata?.CameraFormatted;
                if (string.IsNullOrEmpty(name)) return includeUnknown;
                return camSet.Contains(name);
            };
            visible = visible.Where(camPred);
        }

        Func<PhotoItem, bool>? typePred;
        if (ImageTypeFilter != ImageTypeFilterMode.Any && _imageTypeFilterExtraValues.Count > 0)
        {
            var modes = new HashSet<ImageTypeFilterMode>(_imageTypeFilterExtraValues) { ImageTypeFilter };
            typePred = p =>
            {
                if (modes.Contains(ImageTypeFilterMode.RawOnly)   && p.IsRaw) return true;
                if (modes.Contains(ImageTypeFilterMode.JpegOnly)  && !p.IsRaw && !p.IsVideo) return true;
                if (modes.Contains(ImageTypeFilterMode.VideoOnly) && p.IsVideo) return true;
                return false;
            };
        }
        else
        {
            typePred = ImageTypeFilter switch
            {
                ImageTypeFilterMode.RawOnly   => p => p.IsRaw,
                ImageTypeFilterMode.JpegOnly  => p => !p.IsRaw && !p.IsVideo,
                ImageTypeFilterMode.VideoOnly => p => p.IsVideo,
                _                             => null
            };
        }
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

        var filtered = new List<PhotoItem>(sorted.Count);

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
                    filtered.Add(photo);
                    continue;
                }
                if (!seenGroups.Add(photo.GroupId)) continue; // already represented
                var members = membersByGroup[photo.GroupId];
                var rep = SelectBurstRepresentative(members);
                rep.CollapsedBurstCount = members.Count;
                filtered.Add(rep);
            }
        }
        else
        {
            foreach (var photo in sorted)
                filtered.Add(photo);
        }

        FilteredPhotos.ReplaceRange(filtered);
        RebuildGridItems(filtered);
        VisibleCount = filtered.Count;
        UpdateFilterDescription();

        RestoreSelection(previousSelection);
        RefreshFilterBuckets();
        OnPropertyChanged(nameof(CopyTargetCount));
        SaveSessionIfNeeded();
    }

    // Interleaves DateHeaderItem separators between calendar-day boundaries for
    // the grid view. Only active when sorted by capture time — for any other
    // sort, chronological gaps are meaningless and we hand the grid a flat copy.
    private void RebuildGridItems(IReadOnlyList<PhotoItem> filtered)
    {
        var items = new List<object>(filtered.Count + 16);
        if (SortField != SortField.CaptureDate || filtered.Count == 0)
        {
            items.AddRange(filtered);
            GridItems.ReplaceRange(items);
            return;
        }

        DateTime? lastDate = null;
        foreach (var photo in filtered)
        {
            var capture = photo.Metadata?.CaptureTime;
            // Photos without capture time keep flowing under the most recent
            // header; they'd otherwise scatter into a single "unknown" bucket
            // that adds noise without helping.
            if (capture.HasValue)
            {
                var d = capture.Value.Date;
                if (lastDate == null || d != lastDate.Value)
                {
                    items.Add(new DateHeaderItem(d, FormatHeaderDate(d)));
                    lastDate = d;
                }
            }
            items.Add(photo);
        }
        GridItems.ReplaceRange(items);
    }

    private static string FormatHeaderDate(DateTime date)
    {
        // Long date with weekday — readable at a glance without parsing yyyy-mm-dd.
        return date.ToString("dddd, MMMM d, yyyy", System.Globalization.CultureInfo.CurrentCulture);
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
        RatingFilterExtraValues = _ratingFilterExtraValues.Count > 0 ? _ratingFilterExtraValues.ToList() : null,
        FlagFilterExtraValues = _flagFilterExtraValues.Count > 0 ? _flagFilterExtraValues.ToList() : null,
        ColorLabelFilterExtraValues = _colorLabelFilterExtraValues.Count > 0 ? _colorLabelFilterExtraValues.ToList() : null,
        TagFilterExtraIds = _tagFilterExtraIds.Count > 0 ? _tagFilterExtraIds.ToList() : null,
        ImageTypeFilterExtraValues = _imageTypeFilterExtraValues.Count > 0 ? _imageTypeFilterExtraValues.ToList() : null,
        CameraFilters = _cameraFilters.Count > 0 ? _cameraFilters.ToList() : null,
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

        // Restore shift-click multi-selection extras. Skip values that don't make
        // sense for the current mode (e.g. Rating extras when not in Exact mode)
        // and tag IDs that no longer exist in this folder's tag set.
        _ratingFilterExtraValues.Clear();
        if (s.RatingFilterExtraValues != null && RatingFilterMode == RatingFilterMode.Exact)
            foreach (var v in s.RatingFilterExtraValues)
                if (v != s.RatingFilterValue) _ratingFilterExtraValues.Add(v);
        OnPropertyChanged(nameof(RatingFilterExtraValues));
        OnPropertyChanged(nameof(RatingFilterActiveValues));

        _flagFilterExtraValues.Clear();
        if (s.FlagFilterExtraValues != null && s.FlagFilter.HasValue)
            foreach (var v in s.FlagFilterExtraValues)
                if (v != s.FlagFilter.Value) _flagFilterExtraValues.Add(v);
        OnPropertyChanged(nameof(FlagFilterExtraValues));
        OnPropertyChanged(nameof(FlagFilterActiveValues));

        _colorLabelFilterExtraValues.Clear();
        if (s.ColorLabelFilterExtraValues != null && s.ColorLabelFilter.HasValue)
            foreach (var v in s.ColorLabelFilterExtraValues)
                if (v != s.ColorLabelFilter.Value) _colorLabelFilterExtraValues.Add(v);
        OnPropertyChanged(nameof(ColorLabelFilterExtraValues));
        OnPropertyChanged(nameof(ColorLabelFilterActiveValues));

        _tagFilterExtraIds.Clear();
        if (s.TagFilterExtraIds != null && TagFilter != null)
        {
            var existingIds = Tags.Select(t => t.Id).ToHashSet();
            foreach (var id in s.TagFilterExtraIds)
                if (id != TagFilter.Id && existingIds.Contains(id))
                    _tagFilterExtraIds.Add(id);
        }
        OnPropertyChanged(nameof(TagFilterExtraIds));
        OnPropertyChanged(nameof(TagFilterActiveIds));

        _imageTypeFilterExtraValues.Clear();
        if (s.ImageTypeFilterExtraValues != null && s.ImageTypeFilter != ImageTypeFilterMode.Any)
            foreach (var v in s.ImageTypeFilterExtraValues)
                if (v != s.ImageTypeFilter && v != ImageTypeFilterMode.Any) _imageTypeFilterExtraValues.Add(v);
        OnPropertyChanged(nameof(ImageTypeFilterExtraValues));
        OnPropertyChanged(nameof(ImageTypeFilterActiveValues));

        _cameraFilters.Clear();
        if (s.CameraFilters != null)
            foreach (var c in s.CameraFilters) _cameraFilters.Add(c);
        OnPropertyChanged(nameof(CameraFilters));
        OnPropertyChanged(nameof(IsCameraFilterActive));
        OnPropertyChanged(nameof(HasActiveFilters));

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

    private void QueueSessionSave()
    {
        if (_suppressSessionSave) return;
        if (string.IsNullOrEmpty(_sessionFolder)) return;

        _sessionSaveCts?.Cancel();
        var cts = new CancellationTokenSource();
        _sessionSaveCts = cts;
        _ = SaveSessionAfterDelayAsync(cts);
    }

    private async Task SaveSessionAfterDelayAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(SessionSaveDebounceMs, cts.Token);
            if (!cts.IsCancellationRequested)
                SaveSessionIfNeeded();
        }
        catch (OperationCanceledException) { /* superseded by a newer selection */ }
        finally
        {
            if (ReferenceEquals(_sessionSaveCts, cts))
                _sessionSaveCts = null;
            cts.Dispose();
        }
    }

    private void FlushSessionSave()
    {
        _sessionSaveCts?.Cancel();
        _sessionSaveCts = null;
        SaveSessionIfNeeded();
    }

    private void RestoreSelection(PhotoItem? previousSelection)
    {
        if (previousSelection != null)
        {
            var idx = FilteredPhotos.IndexOf(previousSelection);
            if (idx >= 0)
            {
                RestoreSelectionAt(idx);
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
                        RestoreSelectionAt(i);
                        return;
                    }
                }
            }
        }

        if (FilteredPhotos.Count > 0)
        {
            RestoreSelectionAt(0);
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

    private void RestoreSelectionAt(int index)
    {
        if (index < 0 || index >= FilteredPhotos.Count) return;

        var restored = FilteredPhotos[index];
        if (SelectedIndex == index)
            OnSelectedIndexChanged(index);
        else
            SelectedIndex = index;

        // ApplyFilter intentionally clears the multi-selection before rebuilding
        // the view. If the restored index/photo is identical to the previous
        // selection, the generated property setter is a no-op, so rebuild the
        // selected set explicitly. This also lets collapsed burst representatives
        // stand in for every frame even when a tag/rating filter only matched one.
        ReconcileSingleSelection(restored);
    }

    private void UpdateFilterDescription()
    {
        var parts = new List<string>();
        static string Tag(bool exclude, string s) => exclude ? "NOT " + s : s;
        static string DescribeStar(int v) => v == 0 ? "0★" : $"{v}★";

        string? ratingDesc;
        if (RatingFilterMode == RatingFilterMode.Exact && _ratingFilterExtraValues.Count > 0)
        {
            var values = new List<int>(_ratingFilterExtraValues) { RatingFilterValue };
            values.Sort();
            ratingDesc = string.Join("+", values.Select(DescribeStar));
        }
        else
        {
            ratingDesc = RatingFilterMode switch
            {
                RatingFilterMode.Exact    => RatingFilterValue == 0 ? "No stars" : $"={RatingFilterValue}★",
                RatingFilterMode.AtLeast  => $"≥{RatingFilterValue}★",
                RatingFilterMode.LessThan => $"<{RatingFilterValue}★",
                _                         => null
            };
        }
        if (ratingDesc != null) parts.Add(Tag(RatingFilterExclude, ratingDesc));

        if (FlagFilter.HasValue)
        {
            string flagDesc = _flagFilterExtraValues.Count > 0
                ? string.Join("+", new[] { FlagFilter.Value }.Concat(_flagFilterExtraValues).Distinct().Select(f => f.ToString()))
                : FlagFilter.Value.ToString();
            parts.Add(Tag(FlagFilterExclude, flagDesc));
        }
        if (ColorLabelFilter.HasValue)
        {
            string colorDesc = _colorLabelFilterExtraValues.Count > 0
                ? string.Join("+", new[] { ColorLabelFilter.Value }.Concat(_colorLabelFilterExtraValues).Distinct().Select(c => c.ToString()))
                : ColorLabelFilter.Value.ToString();
            parts.Add(Tag(ColorLabelFilterExclude, colorDesc));
        }
        if (TagFilter != null)
        {
            string tagDesc = TagFilter.Name;
            if (_tagFilterExtraIds.Count > 0)
            {
                var names = new List<string> { TagFilter.Name };
                foreach (var id in _tagFilterExtraIds)
                {
                    var t = Tags.FirstOrDefault(x => x.Id == id);
                    if (t != null) names.Add(t.Name);
                }
                tagDesc = string.Join("+", names);
            }
            parts.Add(Tag(TagFilterExclude, tagDesc));
        }
        if (_cameraFilters.Count > 0)
            parts.Add(string.Join("+", _cameraFilters));
        if (BurstFilter == BurstFilterMode.OnlyInBursts) parts.Add(Tag(BurstFilterExclude, "Bursts"));
        else if (BurstFilter == BurstFilterMode.OnlySingles) parts.Add(Tag(BurstFilterExclude, "Singles"));
        if (ImageTypeFilter != ImageTypeFilterMode.Any)
        {
            static string TypeLabel(ImageTypeFilterMode m) => m switch
            {
                ImageTypeFilterMode.RawOnly => "RAW",
                ImageTypeFilterMode.JpegOnly => "JPG",
                ImageTypeFilterMode.VideoOnly => "Video",
                _ => ""
            };
            string typeDesc = _imageTypeFilterExtraValues.Count > 0
                ? string.Join("+", new[] { ImageTypeFilter }.Concat(_imageTypeFilterExtraValues).Distinct().Select(TypeLabel))
                : TypeLabel(ImageTypeFilter);
            parts.Add(Tag(ImageTypeFilterExclude, typeDesc));
        }
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

    // Picks the survivor that should become the new anchor after `victims` are
    // removed: the first non-victim after the current anchor, falling back to
    // the first non-victim before it. Returns null if no survivor exists or
    // the anchor isn't in the current view.
    private PhotoItem? PickAnchorAfterDeletion(IReadOnlyCollection<PhotoItem> victims)
    {
        if (SelectedPhoto == null || victims.Count == 0) return null;
        var anchorIdx = FilteredPhotos.IndexOf(SelectedPhoto);
        if (anchorIdx < 0) return null;
        var toDelete = victims as HashSet<PhotoItem> ?? new HashSet<PhotoItem>(victims);
        for (int i = anchorIdx + 1; i < FilteredPhotos.Count; i++)
            if (!toDelete.Contains(FilteredPhotos[i])) return FilteredPhotos[i];
        for (int i = anchorIdx - 1; i >= 0; i--)
            if (!toDelete.Contains(FilteredPhotos[i])) return FilteredPhotos[i];
        return null;
    }

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

        var nextAnchor = PickAnchorAfterDeletion(photos);

        int deleted = 0;
        var failed = new List<string>();
        foreach (var photo in photos)
        {
            try
            {
                FileSystem.DeleteFile(photo.FilePath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                DbFor(photo).DeletePhoto(photo.FileName);
                AllPhotos.Remove(photo);
                deleted++;
            }
            catch (Exception ex)
            {
                failed.Add($"{photo.FileName}: {ex.Message}");
            }
        }

        // Hand RestoreSelection a survivor adjacent to the old anchor so it
        // doesn't fall back to FilteredPhotos[0] when the anchor was deleted.
        if (nextAnchor != null && AllPhotos.Contains(nextAnchor))
            SelectedPhoto = nextAnchor;

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

        var nextAnchor = PickAnchorAfterDeletion(rejected);

        int deleted = 0;
        foreach (var photo in rejected)
        {
            try
            {
                FileSystem.DeleteFile(photo.FilePath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                DbFor(photo).DeletePhoto(photo.FileName);
                AllPhotos.Remove(photo);
                deleted++;
            }
            catch { /* skip files that can't be deleted */ }
        }

        if (nextAnchor != null && AllPhotos.Contains(nextAnchor))
            SelectedPhoto = nextAnchor;

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
        if (_db == null) return;
        DbFor(photo).Save(photo);
        ScheduleXmpWrite(photo);
    }

    // Bulk-edit fast path: every per-photo Save() opens its own SQLite transaction,
    // so each one fsyncs separately — 20 photos meant 20 fsyncs and a visible UI
    // stall for what should be a metadata flick. SaveBatch wraps the whole set in
    // one transaction so it's effectively a single disk hit.
    private void SavePhotoBatch(IList<PhotoItem> photos)
    {
        if (photos.Count == 0) return;
        SaveAllPhotosPerOwningDb(photos);
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

    // Sidebar bucket highlighting: a value is "active" if it's the anchor or in the
    // shift-click extras set. The bucket itself is single-select when clicked from
    // the sidebar (ClearOtherSidebarFilters wipes extras), but multi-select made
    // via the Filter popup still reflects in the sidebar so the two stay in sync.
    private bool RatingExactActive(int value) =>
        RatingFilterMode == RatingFilterMode.Exact &&
        (RatingFilterValue == value || _ratingFilterExtraValues.Contains(value));

    public bool IsRating5Active       => RatingExactActive(5);
    public bool IsRating4Active       => RatingExactActive(4);
    public bool IsRating3Active       => RatingExactActive(3);
    public bool IsRating2Active       => RatingExactActive(2);
    public bool IsRating1Active       => RatingExactActive(1);
    public bool IsRatingUnratedActive => RatingExactActive(0);

    private bool LabelActive(ColorLabel label) =>
        ColorLabelFilter == label || _colorLabelFilterExtraValues.Contains(label);

    public bool IsLabelRedActive    => LabelActive(ColorLabel.Red);
    public bool IsLabelYellowActive => LabelActive(ColorLabel.Yellow);
    public bool IsLabelGreenActive  => LabelActive(ColorLabel.Green);
    public bool IsLabelBlueActive   => LabelActive(ColorLabel.Blue);
    public bool IsLabelPurpleActive => LabelActive(ColorLabel.Purple);

    private bool FlagActive(CullFlag flag) =>
        FlagFilter == flag || _flagFilterExtraValues.Contains(flag);

    public bool IsFlagPickActive      => FlagActive(CullFlag.Pick);
    public bool IsFlagRejectActive    => FlagActive(CullFlag.Reject);
    public bool IsFlagUnflaggedActive => FlagActive(CullFlag.Unflagged);

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
        // The sidebar is single-select by design — drop any multi-selection state
        // accumulated from the Filter popup so a sidebar click can't leave stale extras.
        ClearAllFilterExtras();

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

        RefreshAvailableCameras();
    }

    // Rebuilds AvailableCameras from the current AllPhotos. Cameras are derived from
    // PhotoMetadata.CameraFormatted. Photos still being indexed (Metadata == null)
    // don't trigger the "(Unknown)" bucket — only photos where indexing has run and
    // produced no camera EXIF (typical for edited JPGs and some videos) do. That way
    // the popup isn't littered with "(Unknown)" the instant a folder opens. Drops
    // CameraFilters entries that no longer exist so a folder switch doesn't leave the
    // popup with stale highlights.
    private void RefreshAvailableCameras()
    {
        var seen = new SortedSet<string>(StringComparer.Ordinal);
        bool sawUnknown = false;
        foreach (var p in AllPhotos)
        {
            if (p.Metadata == null) continue;
            var name = p.Metadata.CameraFormatted;
            if (string.IsNullOrEmpty(name)) sawUnknown = true;
            else seen.Add(name);
        }

        // Build the desired list: known cameras alphabetically, with "(Unknown)" appended only
        // when at least one photo is missing camera EXIF (so the bucket isn't shown for nothing).
        var desired = new List<string>(seen);
        if (sawUnknown) desired.Add(UnknownCameraKey);

        if (AvailableCameras.SequenceEqual(desired, StringComparer.Ordinal)) return;
        AvailableCameras.Clear();
        foreach (var c in desired) AvailableCameras.Add(c);
        // Don't prune _cameraFilters against `desired` here: indexing surfaces cameras
        // incrementally, and a session-restored filter for a camera not yet detected
        // would otherwise get silently dropped before its photos finish indexing.
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
        _rawPrefetchCts?.Cancel();
        _videoProxyPrefetchCts?.Cancel();
        _analyzeFacesCts?.Cancel();
        _analyzeFacesCts?.Dispose();
        _faceAnalyzer?.Dispose();
        // Drain any debounced XMP writes that haven't fired yet so an immediate
        // app exit doesn't lose recent rating/flag/label edits. Bounded so a
        // wedged disk can't keep the process alive.
        _xmpWriter?.Flush(TimeSpan.FromSeconds(2));
        _xmpWriter?.Dispose();
        foreach (var c in _contexts.Values) c.Db.Dispose();
        _contexts.Clear();
        // _db points into _contexts in normal flow; the loop above already
        // disposed it. The redundant null-cond Dispose is harmless if the
        // primary context was ever assigned out-of-band.
        _db = null;
    }
}
