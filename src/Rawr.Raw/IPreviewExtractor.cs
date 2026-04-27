using Rawr.Core.Models;

namespace Rawr.Raw;

/// <summary>
/// Extracts preview images and metadata from RAW files.
/// Implementations:
///   - LibRawExtractor: production path, uses LibRaw native library
///   - WicExtractor: fallback, uses Windows Imaging Component (requires Raw Image Extension)
/// </summary>
public interface IPreviewExtractor
{
    /// <summary>Is the underlying library/codec available on this system?</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Extract the smallest embedded JPEG (thumbnail, typically ~320px wide).
    /// Returns null if extraction fails.
    /// </summary>
    byte[]? ExtractThumbnail(string filePath);

    /// <summary>
    /// Extract a medium-resolution embedded JPEG preview (~1620px wide for CR3).
    /// Falls back to thumbnail if no medium preview exists.
    /// </summary>
    byte[]? ExtractPreview(string filePath);

    /// <summary>
    /// Extract the full-resolution embedded JPEG.
    /// For CR3 files, this is sensor-resolution (e.g. 8192x5464 for R5).
    /// </summary>
    byte[]? ExtractFullJpeg(string filePath);

    /// <summary>
    /// Read EXIF/camera metadata without extracting any image data.
    /// </summary>
    PhotoMetadata? ExtractMetadata(string filePath);
}
