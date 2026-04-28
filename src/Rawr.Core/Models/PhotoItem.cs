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

    [ObservableProperty] private int _rating; // 0-5
    [ObservableProperty] private CullFlag _flag;
    [ObservableProperty] private ColorLabel _colorLabel;
    [ObservableProperty] private int _groupId; // 0 = ungrouped
    [ObservableProperty] private bool _isBestInGroup;
    [ObservableProperty] private bool _isSelected;

    public HashSet<int> GroupTags { get; } = new();

    // Preview state (set by background workers, consumed by UI)
    [ObservableProperty] private byte[]? _thumbnailJpeg;  // small JPEG bytes (~320px)
    [ObservableProperty] private byte[]? _previewJpeg;    // medium JPEG bytes (~1620px)
    [ObservableProperty] private PhotoMetadata? _metadata;

    /// <summary>
    /// Clamp rating to 0-5 range.
    /// </summary>
    partial void OnRatingChanging(int value)
    {
        if (value < 0) _rating = 0;
        else if (value > 5) _rating = 5;
    }
}
