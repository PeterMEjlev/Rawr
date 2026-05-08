using CommunityToolkit.Mvvm.ComponentModel;

namespace Rawr.Core.Models;

/// <summary>
/// Represents a single photo in the culling session.
/// Observable properties drive the UI via data binding.
/// </summary>
public sealed partial class PhotoItem : ObservableObject
{
    public required string FilePath { get; init; }
    public string FileName => Path.GetFileName(FilePath);
    public string Extension => Path.GetExtension(FilePath).ToUpperInvariant();
    public bool IsVideo => Extension is ".MP4" or ".MOV";
    public bool IsRaw => !IsVideo && Extension is not ".JPG" and not ".JPEG";

    [ObservableProperty] private int _rating; // 0-5
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

    // 64-bit dHash over the embedded thumbnail. Used by BurstDetector to gate
    // grouping on visual similarity. Null until computed; persisted in culling.db.
    public ulong? Phash { get; set; }

    // Small grayscale buffer (~32×24, 768 bytes) computed from the thumbnail
    // alongside the dHash and used by PanoramaDetector to estimate frame-to-frame
    // shift. Transient — recomputed on next folder open, not persisted.
    public byte[]? GrayBuffer { get; set; }

    // Share of thumbnail pixels (0-100) classified as clipped highlights or
    // crushed shadows respectively. Computed once from the cached thumbnail JPEG
    // and persisted; null means "not yet computed for this photo".
    public float? HighlightClippedPct { get; set; }
    public float? ShadowClippedPct { get; set; }

    // Results of the user-triggered face / closed-eye analysis pass.
    // Null on any of these means "not yet analysed for this photo".
    // FaceCount = number of faces detected in the cached preview JPEG.
    // ClosedEyeCount = how many of those faces had at least one eye classified
    // as closed (above the user's confidence threshold at analysis time).
    // MinEyeOpenScore = the worst (lowest) "open" probability seen across all
    // eyes in all faces — closer to 0 means at least one eye is confidently
    // closed. Used by the sidebar bucket gate.
    public int? FaceCount { get; set; }
    public int? ClosedEyeCount { get; set; }
    public float? MinEyeOpenScore { get; set; }

    // Full sensor-resolution JPEG bytes (~3-5 MB). Pre-extracted in the background
    // for the active selection so zoom-in is instant. Cleared by eviction when the
    // user navigates far enough away. Not observable — never bound to UI.
    public byte[]? FullJpeg { get; set; }

    /// <summary>
    /// Clamp rating to 0-5 range.
    /// </summary>
    partial void OnRatingChanging(int value)
    {
        if (value < 0) _rating = 0;
        else if (value > 5) _rating = 5;
    }
}
