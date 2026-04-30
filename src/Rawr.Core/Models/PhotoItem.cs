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
    public bool IsRaw => Extension is not ".JPG" and not ".JPEG";

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

    public HashSet<int> TagIds { get; } = new();

    // Preview state (set by background workers, consumed by UI)
    [ObservableProperty] private byte[]? _thumbnailJpeg;  // small JPEG bytes (~320px)
    [ObservableProperty] private byte[]? _previewJpeg;    // medium JPEG bytes (~1620px)
    [ObservableProperty] private PhotoMetadata? _metadata;

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
