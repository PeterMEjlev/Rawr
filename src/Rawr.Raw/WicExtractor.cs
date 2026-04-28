using System.IO;
using System.Windows.Media.Imaging;
using Rawr.Core.Models;

namespace Rawr.Raw;

/// <summary>
/// Fallback preview extractor using Windows Imaging Component (WIC).
///
/// REQUIREMENTS:
/// - "Microsoft Raw Image Extension" must be installed from the Microsoft Store (free).
///   Without it, WIC cannot decode CR3 files.
/// - Works for CR2, DNG, and other formats that Windows has native codecs for.
///
/// LIMITATIONS vs LibRaw:
/// - Cannot extract specific embedded previews (thumbnail vs medium vs full).
///   WIC decodes the image through the full codec pipeline, which is slower.
/// - Does not expose raw EXIF fields like ISO, shutter speed, etc. directly.
///   Metadata extraction here is minimal.
///
/// This extractor is useful for:
/// - Initial development/testing without setting up LibRaw
/// - Formats where the Raw Image Extension works but LibRaw doesn't
/// </summary>
public sealed class WicExtractor : IPreviewExtractor
{
    public bool IsAvailable => true; // WIC is always present, but codecs may not decode all formats

    public byte[]? ExtractThumbnail(string filePath) => ExtractEmbeddedOrDecode(filePath, maxWidth: 320);

    public byte[]? ExtractPreview(string filePath) => ExtractEmbeddedOrDecode(filePath, maxWidth: 1620);

    public byte[]? ExtractFullJpeg(string filePath) => DecodeToJpeg(filePath, maxWidth: 0);

    public PhotoMetadata? ExtractMetadata(string filePath)
    {
        try
        {
            var size = new FileInfo(filePath).Length;
            using var stream = File.OpenRead(filePath);
            return ExifHelper.ReadFromStream(stream, size);
        }
        catch { return null; }
    }

    /// <summary>
    /// Fast path: try the codec's embedded thumbnail first. For CR3 the Microsoft
    /// Raw Image Extension exposes the embedded preview JPEG via BitmapFrame.Thumbnail,
    /// which avoids the demosaic pipeline entirely (~10-50 ms vs ~1-3 s).
    /// Falls back to a full decode + scale if the codec doesn't expose one.
    /// </summary>
    private static byte[]? ExtractEmbeddedOrDecode(string filePath, int maxWidth)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.None);

            var frame = decoder.Frames[0];
            BitmapSource? source = TryGetThumbnail(frame);

            if (source == null)
            {
                // No embedded thumb — fall through to the slow full-decode path.
                source = frame;
            }

            // Down-scale if the embedded thumb is larger than we need.
            if (maxWidth > 0 && source.PixelWidth > maxWidth)
            {
                double scale = (double)maxWidth / source.PixelWidth;
                source = new TransformedBitmap(source, new System.Windows.Media.ScaleTransform(scale, scale));
            }

            return EncodeJpeg(source);
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? DecodeToJpeg(string filePath, int maxWidth)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.None);
            var frame = decoder.Frames[0];

            BitmapSource source = frame;
            if (maxWidth > 0 && frame.PixelWidth > maxWidth)
            {
                double scale = (double)maxWidth / frame.PixelWidth;
                source = new TransformedBitmap(frame, new System.Windows.Media.ScaleTransform(scale, scale));
            }

            return EncodeJpeg(source);
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource? TryGetThumbnail(BitmapFrame frame)
    {
        // WIC codecs may throw on Thumbnail access if the codec doesn't expose one.
        try { return frame.Thumbnail; }
        catch { return null; }
    }

    private static byte[] EncodeJpeg(BitmapSource source)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

}
