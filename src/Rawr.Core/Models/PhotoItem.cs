using CommunityToolkit.Mvvm.ComponentModel;

namespace Rawr.Core.Models;

/// <summary>
/// Represents a single photo in the culling session.
/// Observable properties drive the UI via data binding.
/// </summary>
public sealed partial class PhotoItem : ObservableObject
{
    // FilePath is set once via the init accessor; FileName / Extension / IsVideo /
    // IsRaw are derived from it deterministically, so precomputing them here turns
    // every later access into a field read instead of recomputing Path.GetExtension
    // + ToUpperInvariant (and allocating two strings) per call. These getters are
    // hit on every filter predicate, sort key, and per-photo loop over AllPhotos
    // — adding up to thousands of redundant allocations on a 10k-photo folder.
    // The init accessor runs before the object is published to any other thread,
    // so the fields are safely visible without locking.
    private string _filePath = "";
    public required string FilePath
    {
        get => _filePath;
        init
        {
            _filePath = value;
            _fileName = Path.GetFileName(value);
            _extension = Path.GetExtension(value).ToUpperInvariant();
            _isVideo = _extension is ".MP4" or ".MOV";
            _isRaw = !_isVideo && _extension is not ".JPG" and not ".JPEG";
        }
    }

    private string _fileName = "";
    public string FileName => _fileName;

    private string _extension = "";
    public string Extension => _extension;

    private bool _isVideo;
    public bool IsVideo => _isVideo;

    private bool _isRaw;
    public bool IsRaw => _isRaw;

    // Short file-kind tag for the fullscreen overlay: "RAW" / "JPG" / "" (video).
    public string FileTypeBadge => _isVideo ? "" : _isRaw ? "RAW" : "JPG";

    // Rating is hand-rolled rather than [ObservableProperty] so the 0–5 clamp
    // actually takes effect. The source generator's OnXxxChanging hook runs
    // *before* the unconditional `_rating = value` it emits, so any field
    // assignment we made from inside that hook would be overwritten — clamp via
    // SetProperty here instead so out-of-range writes are coerced.
    private int _rating;
    public int Rating
    {
        get => _rating;
        set => SetProperty(ref _rating, Math.Clamp(value, 0, 5));
    }
    [ObservableProperty] private CullFlag _flag;
    [ObservableProperty] private ColorLabel _colorLabel;
    [ObservableProperty] private int _groupId; // 0 = ungrouped, > 0 = burst id assigned by BurstDetector
    [ObservableProperty] private bool _isBestInGroup;
    [ObservableProperty] private string _burstBadge = ""; // e.g. "2/5" for the 2nd shot in a 5-shot burst; "" if not in a burst

    // > 0 only when this PhotoItem is acting as the visible representative of a collapsed burst.
    // The number is the count of (filtered) burst members the representative stands in for.
    [ObservableProperty] private int _collapsedBurstCount;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _tagDisplay = "";

    // True when this photo is part of a detected HDR/auto-bracket burst. Drives
    // the orange HDR pill on thumbnails. Set by HdrDetector during folder load.
    [ObservableProperty] private bool _isHdr;

    // True when this photo is part of a detected panorama sweep. Drives the
    // teal Panorama pill. Set by PanoramaDetector during folder load.
    [ObservableProperty] private bool _isPanorama;

    public HashSet<int> TagIds { get; } = new();

    // Preview state (set by background workers, consumed by UI)
    [ObservableProperty] private byte[]? _thumbnailJpeg;  // small JPEG bytes (~320px)
    [ObservableProperty] private byte[]? _previewJpeg;    // medium JPEG bytes (~1620px)
    [ObservableProperty] private PhotoMetadata? _metadata;

    // Detailed video stream/container info (codec, fps, bit depth, chroma, bitrate,
    // duration, audio). Populated lazily by VideoProbe on selection; null for
    // photos and for videos not yet probed.
    [ObservableProperty] private VideoMetadata? _videoInfo;

    // 64-bit dHash over the embedded thumbnail. Used by BurstDetector to gate
    // grouping on visual similarity. Null until computed; persisted in culling.db.
    public ulong? Phash { get; set; }

    // Small grayscale buffer (~32×24, 768 bytes) computed from the thumbnail
    // alongside the dHash and used by PanoramaDetector to estimate frame-to-frame
    // shift. Persisted as a BLOB so a reopen can skip the thumbnail decode.
    public byte[]? GrayBuffer { get; set; }

    // Source file size + last-write ticks captured when this photo's cached
    // derived data (EXIF metadata, grayscale strip) was computed. Used as the
    // staleness key so a reopen can trust the cached Metadata/GrayBuffer only when
    // the underlying file is unchanged. 0 = not yet stamped. Not persisted on the
    // PhotoItem itself — written to / read from the DB row.
    public long MetaSourceSize { get; set; }
    public long MetaSourceMtimeTicks { get; set; }

    // Share of thumbnail pixels (0-100) classified as clipped highlights or
    // crushed shadows respectively. Computed once from the cached thumbnail JPEG
    // and persisted; null means "not yet computed for this photo".
    public float? HighlightClippedPct { get; set; }
    public float? ShadowClippedPct { get; set; }

    // Results of the user-triggered face / closed-eye analysis pass.
    // Null on any of these means "not yet analysed for this photo".
    // FaceCount = number of faces detected in the cached preview JPEG.
    // ClosedEyeCount = how many of those faces had no open eye — every analysed
    // eye fell below the user's confidence threshold at analysis time. A single
    // open eye clears the face (a wink isn't a closed-eyes reject).
    // MinEyeOpenScore = the worst (lowest) "open" probability seen across all
    // eyes in all faces — closer to 0 means at least one eye is confidently
    // closed. Used by the sidebar bucket gate.
    public int? FaceCount { get; set; }
    public int? ClosedEyeCount { get; set; }
    public float? MinEyeOpenScore { get; set; }

    // Coarse subject categories from the zero-shot CLIP classifier. null means
    // the classifier hasn't run on this photo yet; a value (possibly
    // SubjectTag.None) means the run is complete. Bitmask so a photo can
    // legitimately carry several tags. Persisted as an integer column.
    public SubjectTag? SubjectTags { get; set; }

    // Full sensor-resolution JPEG bytes (~3-5 MB). Pre-extracted in the background
    // for the active selection so zoom-in is instant. Cleared by eviction when the
    // user navigates far enough away. Not observable — never bound to UI.
    public byte[]? FullJpeg { get; set; }

    // Lazily resolved by the VM on first preview: true when this is a DNG whose
    // embedded XMP carries Adobe Camera Raw edits. Such DNGs ship an already-
    // edited embedded preview, so RAWR keeps it instead of overriding it with a
    // neutral linear-RAW render (which would discard the edits on screen).
    // null = not yet determined. Transient — recomputed per session.
    public bool? PrefersEmbeddedPreview { get; set; }

    // EXIF orientation resolved to a degrees-clockwise rotation. The full-resolution
    // embedded JPEG often lacks the orientation tag (CR3 etc.) so we fall back to
    // the smaller default thumb and cache the answer here for the rest of the
    // session. Transient — not persisted.
    public double JpegRotationDegrees { get; set; }

    // Extra clockwise rotation the user has dialled in for the current session via
    // the rotate shortcut (R). Applied on top of whatever orientation the preview
    // pipeline produced. Transient — resets when the app restarts.
    public double UserRotationDegrees { get; set; }

}
