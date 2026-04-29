using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32;
using Rawr.App.Dialogs;
using Rawr.Core.Data;
using Rawr.Core.Models;
using Rawr.Core.Services;
using Rawr.Raw;

namespace Rawr.App.ViewModels;

public enum SortField { FileName, Rating, CaptureDate, ColorLabel, Flag, Burst }
public enum RatingFilterMode { Any, Exact, AtLeast, LessThan }
public enum BurstFilterMode { Any, OnlyInBursts, OnlySingles }

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IPreviewExtractor _extractor;
    private PreviewCache? _cache;
    private CullingDatabase? _db;
    private CancellationTokenSource? _indexCts;
    private CancellationTokenSource? _previewCts;
    private bool _highResPreviewLoaded;
    private PhotoItem? _metadataSubscription;

    // Photos within this radius of the current selection keep their PreviewJpeg /
    // FullJpeg bytes in memory for instant browsing. Photos outside the window are
    // evicted on selection change to keep memory bounded.
    private const int KeepRadius = 2;

    // ── Observable state ──

    [ObservableProperty] private string _currentFolder = "";
    [ObservableProperty] private string _statusText = "Open a folder to begin (Ctrl+O)";
    [ObservableProperty] private BitmapSource? _previewImage;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedPhotoCaptureDateFormatted))]
    private PhotoItem? _selectedPhoto;
    [ObservableProperty] private int _selectedIndex = -1;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _filterDescription = "All";
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _visibleCount;
    [ObservableProperty] private double _gridThumbnailSize = 90.0; // derived in code-behind from GridColumnCount
    [ObservableProperty] private int _gridColumnCount = 2;
    [ObservableProperty] private double _filmstripItemWidth = 140.0; // derived in code-behind from filmstrip height
    [ObservableProperty] private bool _showGrid = true;
    [ObservableProperty] private bool _showFilmstrip = true;

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

    public bool HasActiveFilters => RatingFilterMode != RatingFilterMode.Any || FlagFilter.HasValue || ColorLabelFilter.HasValue || TagFilter != null || BurstFilter != BurstFilterMode.Any;

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

    // Copy criteria state (independent of filter; defaults to "Pick" to match original behaviour)
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

    public string ExtractorName { get; }

    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RAWR");
    private static readonly string LastFolderFile = Path.Combine(SettingsDir, "lastfolder.txt");

    public MainViewModel()
    {
        // Try LibRaw first, fall back to WIC
        var libraw = new LibRawExtractor();
        _extractor = libraw.IsAvailable ? libraw : new WicExtractor();
        ExtractorName = libraw.IsAvailable ? "LibRaw" : "WIC";
    }

    public async Task RestoreLastFolderAsync()
    {
        try
        {
            if (!File.Exists(LastFolderFile)) return;
            var folder = (await File.ReadAllTextAsync(LastFolderFile)).Trim();
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                await LoadFolderAsync(folder);
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

        await LoadFolderAsync(dialog.FolderName);
    }

    public async Task LoadFolderAsync(string folderPath)
    {
        // Cancel any in-progress indexing
        _indexCts?.Cancel();
        _indexCts = new CancellationTokenSource();
        var ct = _indexCts.Token;

        IsLoading = true;
        CurrentFolder = folderPath;
        StatusText = "Scanning folder...";

        // Dispose previous session
        _db?.Dispose();

        AllPhotos.Clear();
        FilteredPhotos.Clear();
        Tags.Clear();
        TagFilter = null;
        PreviewImage = null;
        SelectedPhoto = null;
        SelectedIndex = -1;

        BurstCollapsed = AppSettings.Current.CollapseBurstsOnOpen;
        SortField = AppSettings.Current.DefaultSortField;

        // Scan for RAW files
        var files = await Task.Run(() => FolderScanner.Scan(folderPath), ct);
        TotalCount = files.Count;

        if (files.Count == 0)
        {
            StatusText = "No supported RAW files found in this folder.";
            IsLoading = false;
            return;
        }

        StatusText = $"Found {files.Count} RAW files. Loading...";

        // Open database and preview cache
        _db = CullingDatabase.Open(folderPath);
        _cache = new PreviewCache(folderPath);
        var savedState = _db.LoadAll();

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

        ApplyFilter();

        // Select first photo
        if (FilteredPhotos.Count > 0)
        {
            SelectedIndex = 0;
            SelectedPhoto = FilteredPhotos[0];
        }

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
                    if (needsThumb.Contains(photo))
                    {
                        var jpeg = _extractor.ExtractThumbnail(photo.FilePath);
                        if (jpeg != null)
                        {
                            var thumb = ProcessJpegForCache(jpeg, ThumbnailDecodeWidth) ?? jpeg;
                            _cache!.SaveThumbnail(photo.FileName, thumb);
                            Application.Current.Dispatcher.Invoke(() => photo.ThumbnailJpeg = thumb);
                        }
                    }

                    var metadata = _extractor.ExtractMetadata(photo.FilePath);
                    if (metadata != null)
                        Application.Current.Dispatcher.Invoke(() => photo.Metadata = metadata);

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
            BurstCount = BurstDetector.Detect(AllPhotos,
                TimeSpan.FromSeconds(AppSettings.Current.BurstMaxGapSeconds));
        });

        if (BurstCount > 0 && _db != null)
        {
            try { await Task.Run(() => _db.SaveBatch(AllPhotos), ct); }
            catch (OperationCanceledException) { }
        }

        if (BurstFilter != BurstFilterMode.Any || SortField == SortField.Burst)
            ApplyFilter();
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
    }

    private void OnSelectedPhotoPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PhotoItem.Metadata))
            OnPropertyChanged(nameof(SelectedPhotoCaptureDateFormatted));
    }

    public string SelectedPhotoCaptureDateFormatted =>
        SelectedPhoto?.Metadata?.CaptureTime.HasValue == true
            ? SelectedPhoto.Metadata.CaptureTime.Value.ToString(AppSettings.Current.DateFormat)
            : "—";

    public void NotifyDateFormatChanged() =>
        OnPropertyChanged(nameof(SelectedPhotoCaptureDateFormatted));

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

        try
        {
            // Already-resident bytes (set by an earlier prefetch) — skip the disk read.
            var cached = photo.PreviewJpeg ?? _cache?.LoadPreview(photo.FileName);
            if (cached != null)
            {
                var bs = await Task.Run(() => LoadBitmapFromJpeg(cached), ct);
                if (ct.IsCancellationRequested || SelectedPhoto != photo) return;
                photo.PreviewJpeg = cached;
                PreviewImage = bs;
                _ = PreloadFullJpegAsync(photo, ct);
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

            var jpeg = await Task.Run(() => _extractor.ExtractPreview(photo.FilePath), ct);
            if (ct.IsCancellationRequested || jpeg == null || SelectedPhoto != photo) return;

            // Shrink to screen-sized JPEG with orientation baked in for fast subsequent loads.
            var processed = await Task.Run(() => ProcessJpegForCache(jpeg, PreviewDecodeWidth) ?? jpeg, ct);
            if (ct.IsCancellationRequested || SelectedPhoto != photo) return;

            _cache?.SavePreview(photo.FileName, processed);
            photo.PreviewJpeg = processed;

            var fullBs = await Task.Run(() => LoadBitmapFromJpeg(processed), ct);
            if (ct.IsCancellationRequested || SelectedPhoto != photo) return;

            PreviewImage = fullBs;
            _ = PreloadFullJpegAsync(photo, ct);
        }
        catch (OperationCanceledException) { /* selection moved on */ }
    }

    public async Task LoadHighResPreviewAsync()
    {
        if (_highResPreviewLoaded) return;
        var photo = SelectedPhoto;
        if (photo == null) return;

        _highResPreviewLoaded = true; // guard against duplicate concurrent calls
        var ct = _previewCts?.Token ?? CancellationToken.None;

        try
        {
            // Reuse pre-extracted bytes if PreloadFullJpegAsync already finished.
            var jpeg = photo.FullJpeg ?? await Task.Run(() => _extractor.ExtractFullJpeg(photo.FilePath), ct);
            if (ct.IsCancellationRequested || jpeg == null || SelectedPhoto != photo) return;

            photo.FullJpeg ??= jpeg;

            var bs = await Task.Run(() => LoadBitmapFromJpeg(jpeg, decodePixelWidth: 0), ct);
            if (!ct.IsCancellationRequested && SelectedPhoto == photo)
                PreviewImage = bs;
        }
        catch (OperationCanceledException) { /* selection moved on */ }
    }

    /// <summary>
    /// Background-extract the full sensor-resolution JPEG bytes for the current photo
    /// so that a subsequent zoom can decode them immediately without disk I/O.
    /// </summary>
    private async Task PreloadFullJpegAsync(PhotoItem photo, CancellationToken ct)
    {
        if (photo.FullJpeg != null) return;
        try
        {
            var jpeg = await Task.Run(() => _extractor.ExtractFullJpeg(photo.FilePath), ct);
            if (!ct.IsCancellationRequested && jpeg != null)
                photo.FullJpeg = jpeg;
        }
        catch (OperationCanceledException) { /* selection moved on */ }
        catch { /* extraction failed — fall back to on-demand on zoom */ }
    }

    /// <summary>
    /// Warm the disk/memory preview cache for photos adjacent to the current selection
    /// so Next/Previous feels instant.
    /// </summary>
    private async Task PrefetchNeighborsAsync(int currentIndex, CancellationToken ct)
    {
        // Process in alternating order so the immediate neighbours land first.
        var offsets = new[] { 1, -1, 2, -2 };
        foreach (var offset in offsets)
        {
            if (ct.IsCancellationRequested) return;
            var i = currentIndex + offset;
            if (i < 0 || i >= FilteredPhotos.Count) continue;

            var photo = FilteredPhotos[i];
            if (photo.PreviewJpeg != null) continue;

            try
            {
                var cached = _cache?.LoadPreview(photo.FileName);
                if (cached != null)
                {
                    photo.PreviewJpeg = cached;
                    continue;
                }

                var jpeg = await Task.Run(() => _extractor.ExtractPreview(photo.FilePath), ct);
                if (ct.IsCancellationRequested || jpeg == null) continue;

                var processed = await Task.Run(() => ProcessJpegForCache(jpeg, PreviewDecodeWidth) ?? jpeg, ct);
                if (ct.IsCancellationRequested) continue;

                _cache?.SavePreview(photo.FileName, processed);
                photo.PreviewJpeg = processed;
            }
            catch (OperationCanceledException) { return; }
            catch { /* one neighbour failing should not block the others */ }
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

    // ── Rating ──

    [RelayCommand]
    private void SetRating(int rating)
    {
        if (SelectedPhoto == null) return;
        SelectedPhoto.Rating = Math.Clamp(rating, 0, 5);
        SavePhoto(SelectedPhoto);
    }

    // ── Flagging ──

    [RelayCommand]
    private void TogglePick()
    {
        if (SelectedPhoto == null) return;
        SelectedPhoto.Flag = SelectedPhoto.Flag == CullFlag.Pick ? CullFlag.Unflagged : CullFlag.Pick;
        SavePhoto(SelectedPhoto);
    }

    [RelayCommand]
    private void ToggleReject()
    {
        if (SelectedPhoto == null) return;
        SelectedPhoto.Flag = SelectedPhoto.Flag == CullFlag.Reject ? CullFlag.Unflagged : CullFlag.Reject;
        SavePhoto(SelectedPhoto);
    }

    [RelayCommand]
    private void Unflag()
    {
        if (SelectedPhoto == null) return;
        SelectedPhoto.Flag = CullFlag.Unflagged;
        SavePhoto(SelectedPhoto);
    }

    // ── Color labels ──

    [RelayCommand]
    private void SetColorLabel(ColorLabel label)
    {
        if (SelectedPhoto == null) return;
        SelectedPhoto.ColorLabel = SelectedPhoto.ColorLabel == label ? ColorLabel.None : label;
        SavePhoto(SelectedPhoto);
    }

    // ── Filtering ──

    [RelayCommand]
    private void ClearRatingFilter()
    {
        RatingFilterMode = RatingFilterMode.Any;
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
            RatingFilterMode = RatingFilterMode.Any;
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
        ApplyFilter();
    }

    [RelayCommand]
    private void ClearFlagFilter()
    {
        FlagFilter = null;
        ApplyFilter();
    }

    [RelayCommand]
    private void SetColorLabelFilter(ColorLabel label)
    {
        ColorLabelFilter = ColorLabelFilter == label ? null : label;
        ApplyFilter();
    }

    [RelayCommand]
    private void ClearColorLabelFilter()
    {
        ColorLabelFilter = null;
        ApplyFilter();
    }

    // ── Tag commands ──

    [RelayCommand]
    private void SetTagFilter(PhotoTag tag)
    {
        TagFilter = TagFilter?.Id == tag.Id ? null : tag;
        ApplyFilter();
    }

    [RelayCommand]
    private void ClearTagFilter()
    {
        TagFilter = null;
        ApplyFilter();
    }

    [RelayCommand]
    private void CreateTag()
    {
        if (_db == null) return;
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
        if (SelectedPhoto.TagIds.Contains(tag.Id))
        {
            SelectedPhoto.TagIds.Remove(tag.Id);
            _db.UnassignGroup(SelectedPhoto.FileName, tag.Id);
        }
        else
        {
            SelectedPhoto.TagIds.Add(tag.Id);
            _db.AssignGroup(SelectedPhoto.FileName, tag.Id);
        }
        UpdateTagDisplay(SelectedPhoto);
        OnPropertyChanged(nameof(SelectedPhotoTagAssignments));
        if (TagFilter != null)
            ApplyFilter();
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
        ApplyFilter();
    }

    // ── Burst filter ──

    [RelayCommand]
    private void SetBurstFilter(BurstFilterMode mode)
    {
        BurstFilter = BurstFilter == mode ? BurstFilterMode.Any : mode;
        ApplyFilter();
    }

    [RelayCommand]
    private void ClearBurstFilter()
    {
        BurstFilter = BurstFilterMode.Any;
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

    public void PersistPhoto(PhotoItem photo) => _db?.Save(photo);

    /// <summary>
    /// Re-runs burst detection with the current AppSettings and refreshes the view.
    /// Call after AppSettings.Current has been updated.
    /// </summary>
    public void ApplyBurstSettings()
    {
        if (AllPhotos.Count == 0) return;
        BurstCount = BurstDetector.Detect(AllPhotos,
            TimeSpan.FromSeconds(AppSettings.Current.BurstMaxGapSeconds));
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
        _ => SortDescending
            ? items.OrderByDescending(p => p.FileName, StringComparer.OrdinalIgnoreCase)
            : items.OrderBy(p => p.FileName, StringComparer.OrdinalIgnoreCase)
    };

    private void ApplyFilter()
    {
        var previousSelection = SelectedPhoto;
        FilteredPhotos.Clear();

        IEnumerable<PhotoItem> visible = AllPhotos;
        visible = RatingFilterMode switch
        {
            RatingFilterMode.Exact    => visible.Where(p => p.Rating == RatingFilterValue),
            RatingFilterMode.AtLeast  => visible.Where(p => p.Rating >= RatingFilterValue),
            RatingFilterMode.LessThan => visible.Where(p => p.Rating < RatingFilterValue),
            _                         => visible
        };
        if (FlagFilter.HasValue)
            visible = visible.Where(p => p.Flag == FlagFilter.Value);
        if (ColorLabelFilter.HasValue)
            visible = visible.Where(p => p.ColorLabel == ColorLabelFilter.Value);
        if (TagFilter != null)
            visible = visible.Where(p => p.TagIds.Contains(TagFilter.Id));
        visible = BurstFilter switch
        {
            BurstFilterMode.OnlyInBursts => visible.Where(p => p.GroupId > 0),
            BurstFilterMode.OnlySingles  => visible.Where(p => p.GroupId == 0),
            _                            => visible
        };

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

        // Try to restore selection
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

        // Fall back to first item
        if (FilteredPhotos.Count > 0)
        {
            SelectedIndex = 0;
        }
        else
        {
            SelectedIndex = -1;
            SelectedPhoto = null;
            PreviewImage = null;
        }
    }

    private void UpdateFilterDescription()
    {
        var parts = new List<string>();

        var ratingDesc = RatingFilterMode switch
        {
            RatingFilterMode.Exact    => RatingFilterValue == 0 ? "No stars" : $"={RatingFilterValue}★",
            RatingFilterMode.AtLeast  => $"≥{RatingFilterValue}★",
            RatingFilterMode.LessThan => $"<{RatingFilterValue}★",
            _                         => null
        };
        if (ratingDesc != null) parts.Add(ratingDesc);
        if (FlagFilter.HasValue)
            parts.Add(FlagFilter.Value.ToString());
        if (ColorLabelFilter.HasValue)
            parts.Add(ColorLabelFilter.Value.ToString());
        if (TagFilter != null)
            parts.Add(TagFilter.Name);
        if (BurstFilter == BurstFilterMode.OnlyInBursts) parts.Add("Bursts");
        else if (BurstFilter == BurstFilterMode.OnlySingles) parts.Add("Singles");

        FilterDescription = parts.Count > 0 ? string.Join(", ", parts) : "All";
    }

    // ── File operations ──

    [RelayCommand]
    private async Task CopyPickedAsync()
    {
        IEnumerable<PhotoItem> candidates = AllPhotos;
        candidates = CopyRatingFilterMode switch
        {
            RatingFilterMode.Exact    => candidates.Where(p => p.Rating == CopyRatingFilterValue),
            RatingFilterMode.AtLeast  => candidates.Where(p => p.Rating >= CopyRatingFilterValue),
            RatingFilterMode.LessThan => candidates.Where(p => p.Rating < CopyRatingFilterValue),
            _                         => candidates
        };
        if (CopyFlagFilter.HasValue)
            candidates = candidates.Where(p => p.Flag == CopyFlagFilter.Value);
        if (CopyColorLabelFilter.HasValue)
            candidates = candidates.Where(p => p.ColorLabel == CopyColorLabelFilter.Value);

        var photos = candidates.ToList();
        if (photos.Count == 0)
        {
            StatusText = "No photos match the copy criteria.";
            return;
        }

        var dialog = new OpenFolderDialog { Title = "Select destination folder" };
        if (dialog.ShowDialog() != true) return;

        StatusText = $"Copying {photos.Count} photos...";
        var progress = new Progress<(int current, int total, string fileName)>(p =>
        {
            StatusText = $"Copying {p.current}/{p.total}: {p.fileName}";
        });

        var count = await FileOperations.CopyFilesAsync(photos, dialog.FolderName, progress);
        StatusText = $"Copied {count} photos to {dialog.FolderName}";
    }

    [RelayCommand]
    private async Task ExportFileListAsync()
    {
        IEnumerable<PhotoItem> candidates = AllPhotos;
        candidates = CopyRatingFilterMode switch
        {
            RatingFilterMode.Exact    => candidates.Where(p => p.Rating == CopyRatingFilterValue),
            RatingFilterMode.AtLeast  => candidates.Where(p => p.Rating >= CopyRatingFilterValue),
            RatingFilterMode.LessThan => candidates.Where(p => p.Rating < CopyRatingFilterValue),
            _                         => candidates
        };
        if (CopyFlagFilter.HasValue)
            candidates = candidates.Where(p => p.Flag == CopyFlagFilter.Value);
        if (CopyColorLabelFilter.HasValue)
            candidates = candidates.Where(p => p.ColorLabel == CopyColorLabelFilter.Value);

        var picked = candidates.ToList();
        if (picked.Count == 0)
        {
            StatusText = "No photos match the copy criteria.";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export file list",
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            DefaultExt = ".txt",
            FileName = "selected_photos.txt"
        };

        if (dialog.ShowDialog() != true) return;

        await FileOperations.ExportFileListAsync(picked, dialog.FileName);
        StatusText = $"Exported {picked.Count} file paths to {dialog.FileName}";
    }

    // ── Delete ──

    [RelayCommand]
    private void DeletePhoto()
    {
        if (SelectedPhoto == null) return;
        var photo = SelectedPhoto;
        var result = MessageBox.Show(
            $"Move \"{photo.FileName}\" to the Recycle Bin?",
            "Delete Photo",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            FileSystem.DeleteFile(photo.FilePath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not delete \"{photo.FileName}\": {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _db?.DeletePhoto(photo.FileName);
        AllPhotos.Remove(photo);
        TotalCount = AllPhotos.Count;
        ApplyFilter();
        StatusText = $"Moved \"{photo.FileName}\" to the Recycle Bin.";
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
        SelectedPhoto.Flag = CullFlag.Pick;
        SavePhoto(SelectedPhoto);
        NextPhoto();
    }

    [RelayCommand]
    private void RejectAndAdvance()
    {
        if (SelectedPhoto == null) return;
        SelectedPhoto.Flag = CullFlag.Reject;
        SavePhoto(SelectedPhoto);
        NextPhoto();
    }

    // ── Helpers ──

    private void SavePhoto(PhotoItem photo) => _db?.Save(photo);

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

    public void Dispose()
    {
        _indexCts?.Cancel();
        _indexCts?.Dispose();
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _db?.Dispose();
    }
}
