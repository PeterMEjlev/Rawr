using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rawr.Core.Models;
using Rawr.Raw;

namespace Rawr.App.Services;

/// <summary>
/// One stop on the copy-quality slider. <see cref="TranscodeToJpeg"/> = false means
/// "copy the original bytes verbatim"; otherwise the source is decoded and re-encoded
/// as JPEG at <see cref="JpegQuality"/>, optionally downscaled so the long edge is
/// at most <see cref="MaxLongEdge"/> pixels (0 = no scaling).
/// </summary>
public sealed record CopyQualityPreset(
    string Label,
    string Description,
    bool TranscodeToJpeg,
    int JpegQuality,
    int MaxLongEdge);

public static class CopyQualityPresets
{
    // Indexed lowest-to-highest so a slider with Min=0, Max=4 reads naturally
    // (left = small/lossy, right = original RAW). Default is FullIndex.
    public static readonly CopyQualityPreset[] All =
    [
        new("Email (JPG)",  "≤1024 px JPEG Q75 - small enough to email", true,  75, 1024),
        new("Web (JPG)",    "≤2560 px JPEG Q85",                         true,  85, 2560),
        new("Medium (JPG)", "Full resolution JPEG Q85",                  true,  85, 0),
        new("High (JPG)",   "Full resolution JPEG Q95",                  true,  95, 0),
        new("Full (RAW)",   "Original file, no conversion",              false, 0,  0),
    ];

    public const int FullIndex = 4;
}

/// <summary>
/// Writes a photo to a destination folder, applying a quality preset. Videos and the
/// "Full" preset are byte-for-byte copies; the other presets re-encode photos as JPEG.
/// </summary>
public sealed class PhotoExporter
{
    private readonly IPreviewExtractor _extractor;

    public PhotoExporter(IPreviewExtractor extractor)
    {
        _extractor = extractor;
    }

    /// <returns>true if a file was written, false on decode failure.</returns>
    public Task<bool> ExportAsync(
        PhotoItem photo,
        string destinationFolder,
        CopyQualityPreset preset,
        string? customBaseName,
        int sequenceNumber,
        int sequencePadding,
        CancellationToken ct = default)
    {
        bool transcode = preset.TranscodeToJpeg && !photo.IsVideo;
        string outExtension = transcode ? ".jpg" : Path.GetExtension(photo.FileName);

        string destName = string.IsNullOrWhiteSpace(customBaseName)
            ? (transcode
                ? Path.GetFileNameWithoutExtension(photo.FileName) + outExtension
                : photo.FileName)
            : $"{customBaseName}_{sequenceNumber.ToString().PadLeft(Math.Max(sequencePadding, 3), '0')}{outExtension}";

        string destPath = Path.Combine(destinationFolder, destName);

        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            if (!transcode)
            {
                File.Copy(photo.FilePath, destPath, overwrite: true);
                return true;
            }
            return TranscodeAndWrite(photo, destPath, preset, ct);
        }, ct);
    }

    private bool TranscodeAndWrite(PhotoItem photo, string destPath, CopyQualityPreset preset, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Downscaling now happens inside the decode (WIC scales in the JPEG's DCT
        // domain) rather than via a full decode + TransformedBitmap, so the source
        // arrives already at the target size.
        var (source, metadata) = LoadSourceAndMetadata(photo, preset.MaxLongEdge);
        if (source == null) return false;

        var encoder = new JpegBitmapEncoder { QualityLevel = preset.JpegQuality };
        encoder.Frames.Add(CreateFrame(source, metadata));

        using var fs = File.Create(destPath);
        encoder.Save(fs);
        return true;
    }

    // BitmapMetadata produced by one decoder occasionally rejects re-attachment to a
    // fresh encoder (codec quirks around unknown tags). Falling back without metadata
    // preserves the image at the cost of EXIF, which is the right trade.
    private static BitmapFrame CreateFrame(BitmapSource source, BitmapMetadata? metadata)
    {
        if (metadata != null)
        {
            try { return BitmapFrame.Create(source, thumbnail: null, metadata, colorContexts: null); }
            catch { }
        }
        return BitmapFrame.Create(source);
    }

    private (BitmapSource? source, BitmapMetadata? metadata) LoadSourceAndMetadata(PhotoItem photo, int maxLongEdge)
    {
        byte[]? bytes;
        if (photo.IsRaw)
        {
            // Embedded full-resolution JPEG: camera-baked colour, sensor-sized, fast to read.
            // Going through the linear demosaic path would be 10-100× slower and is overkill
            // for a "copy at lower quality" workflow.
            bytes = _extractor.ExtractFullJpeg(photo.FilePath);
        }
        else
        {
            try { bytes = File.ReadAllBytes(photo.FilePath); }
            catch { bytes = null; }
        }

        if (bytes == null) return (null, null);
        return DecodeFromBytes(bytes, maxLongEdge);
    }

    private static (BitmapSource? source, BitmapMetadata? metadata) DecodeFromBytes(byte[] bytes, int maxLongEdge)
    {
        try
        {
            // Header-only probe (DelayCreation + no cache): native dimensions and
            // EXIF without decoding any pixels. Cloning the metadata detaches it from
            // the probe stream so we can dispose the stream immediately.
            int nativeW, nativeH;
            BitmapMetadata? meta;
            using (var probe = new MemoryStream(bytes, writable: false))
            {
                var frame0 = BitmapDecoder.Create(
                    probe,
                    BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                    BitmapCacheOption.None).Frames[0];
                nativeW = frame0.PixelWidth;
                nativeH = frame0.PixelHeight;
                meta = TryReadMetadata(frame0);
            }

            // Downscale during decode when the source exceeds the target long edge.
            // WIC scales in the JPEG's DCT domain — far cheaper and higher quality
            // than decoding the full sensor-sized image and shrinking it afterwards
            // (a 45MP source is ~260MB of pixels; bilinear at 8:1 also aliases).
            // DecodePixelWidth/Height operate on the un-oriented pixel grid, matching
            // the native dimensions read above; orientation stays carried in metadata
            // exactly as before, so there's no rotation change.
            if (maxLongEdge > 0 && Math.Max(nativeW, nativeH) > maxLongEdge)
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.StreamSource = new MemoryStream(bytes, writable: false);
                bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bi.CacheOption = BitmapCacheOption.OnLoad;
                if (nativeW >= nativeH) bi.DecodePixelWidth = maxLongEdge;
                else bi.DecodePixelHeight = maxLongEdge;
                bi.EndInit();
                bi.Freeze();
                return (bi, meta);
            }

            // Full-resolution presets (and already-small sources): decode natively,
            // preserving the source pixel format.
            using var full = new MemoryStream(bytes, writable: false);
            var frame = BitmapDecoder.Create(
                full,
                BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.OnLoad).Frames[0];
            return (frame, meta);
        }
        catch
        {
            return (null, null);
        }
    }

    // Cloned so the returned metadata survives disposal of the stream it was read
    // from. BitmapMetadata.Clone() materialises the values into an in-memory copy.
    private static BitmapMetadata? TryReadMetadata(BitmapFrame frame)
    {
        try { return (frame.Metadata as BitmapMetadata)?.Clone() as BitmapMetadata; }
        catch { return null; }
    }
}
