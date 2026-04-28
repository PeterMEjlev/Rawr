using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Rawr.Core.Data;
using Rawr.Core.Models;
using Rawr.Core.Services;
using Rawr.Raw;

namespace Rawr.App.ViewModels;

public enum SortField { FileName, Rating, CaptureDate, ColorLabel, Flag }
public enum RatingFilterMode { Any, Exact, AtLeast, LessThan }

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IPreviewExtractor _extractor;
    private PreviewCache? _cache;
    private CullingDatabase? _db;
    private CancellationTokenSource? _indexCts;
    private bool _highResPreviewLoaded;

    // ── Observable state ──

    [ObservableProperty] private string _currentFolder = "";
    [ObservableProperty] private string _statusText = "Open a folder to begin (Ctrl+O)";
    [ObservableProperty] private BitmapSource? _previewImage;
    [ObservableProperty] private PhotoItem? _selectedPhoto;
    [ObservableProperty] private int _selectedIndex = -1;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _filterDescription = "All";
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _visibleCount;
    [ObservableProperty] private double _gridThumbnailSize = 90.0; // derived in code-behind from GridColumnCount
    [ObservableProperty] private int _gridColumnCount = 2;
    [ObservableProperty] private double _filmstripItemWidth = 140.0; // derived in code-behind from filmstrip height

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

    public bool HasActiveFilters => RatingFilterMode != RatingFilterMode.Any || FlagFilter.HasValue || ColorLabelFilter.HasValue;

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
        PreviewImage = null;
        SelectedPhoto = null;
        SelectedIndex = -1;

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
            StatusText = $"{files.Count} photos ready. [{_extractor.GetType().Name}]";
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
    }

    // ── Navigation ──

    partial void OnSelectedIndexChanged(int value)
    {
        if (value >= 0 && value < FilteredPhotos.Count)
        {
            SelectedPhoto = FilteredPhotos[value];
            _highResPreviewLoaded = false;
            _ = LoadPreviewForSelectedAsync();
            UpdateStatus();
        }
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

    private async Task LoadPreviewForSelectedAsync()
    {
        var photo = SelectedPhoto;
        if (photo == null) return;

        // Try cache first
        var cached = _cache?.LoadPreview(photo.FileName);
        if (cached != null)
        {
            PreviewImage = LoadBitmapFromJpeg(cached);
            return;
        }

        // Extract in background
        PreviewImage = photo.ThumbnailJpeg != null ? LoadBitmapFromJpeg(photo.ThumbnailJpeg) : null;

        var jpeg = await Task.Run(() => _extractor.ExtractPreview(photo.FilePath));
        if (jpeg != null && SelectedPhoto == photo) // still the same selection
        {
            // Shrink to screen-sized JPEG with orientation baked in for fast subsequent loads.
            var processed = await Task.Run(() => ProcessJpegForCache(jpeg, PreviewDecodeWidth) ?? jpeg);
            if (SelectedPhoto != photo) return;
            _cache?.SavePreview(photo.FileName, processed);
            photo.PreviewJpeg = processed;
            PreviewImage = LoadBitmapFromJpeg(processed);
        }
    }

    public async Task LoadHighResPreviewAsync()
    {
        if (_highResPreviewLoaded) return;
        var photo = SelectedPhoto;
        if (photo == null) return;

        _highResPreviewLoaded = true; // guard against duplicate concurrent calls
        var jpeg = await Task.Run(() => _extractor.ExtractFullJpeg(photo.FilePath));
        if (jpeg != null && SelectedPhoto == photo)
            PreviewImage = LoadBitmapFromJpeg(jpeg, decodePixelWidth: 0); // full resolution for zoom
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

    [RelayCommand]
    private void ClearFilters()
    {
        RatingFilterMode = RatingFilterMode.Any;
        FlagFilter = null;
        ColorLabelFilter = null;
        ApplyFilter();
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

        foreach (var photo in ApplySorting(visible))
            FilteredPhotos.Add(photo);

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

        FilterDescription = parts.Count > 0 ? string.Join(", ", parts) : "All";
    }

    // ── File operations ──

    [RelayCommand]
    private async Task CopyPickedAsync()
    {
        var picked = FilteredPhotos.Where(p => p.Flag == CullFlag.Pick).ToList();
        if (picked.Count == 0)
        {
            StatusText = "No picked photos to copy.";
            return;
        }

        var dialog = new OpenFolderDialog { Title = "Select destination folder" };
        if (dialog.ShowDialog() != true) return;

        StatusText = $"Copying {picked.Count} photos...";
        var progress = new Progress<(int current, int total, string fileName)>(p =>
        {
            StatusText = $"Copying {p.current}/{p.total}: {p.fileName}";
        });

        var count = await FileOperations.CopyFilesAsync(picked, dialog.FolderName, progress);
        StatusText = $"Copied {count} photos to {dialog.FolderName}";
    }

    [RelayCommand]
    private async Task ExportFileListAsync()
    {
        var picked = FilteredPhotos.Where(p => p.Flag == CullFlag.Pick).ToList();
        if (picked.Count == 0)
        {
            StatusText = "No picked photos to export.";
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
        _db?.Dispose();
    }
}
