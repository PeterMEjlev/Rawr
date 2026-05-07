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
        new("Email",  "≤1024 px JPEG Q75 — small enough to email", true,  75, 1024),
        new("Web",    "≤2560 px JPEG Q85",                         true,  85, 2560),
        new("Medium", "Full resolution JPEG Q85",                  true,  85, 0),
        new("High",   "Full resolution JPEG Q95",                  true,  95, 0),
        new("Full",   "Original file, no conversion",              false, 0,  0),
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
        var (source, metadata) = LoadSourceAndMetadata(photo);
        if (source == null) return false;

        if (preset.MaxLongEdge > 0)
        {
            int longEdge = Math.Max(source.PixelWidth, source.PixelHeight);
            if (longEdge > preset.MaxLongEdge)
            {
                double scale = (double)preset.MaxLongEdge / longEdge;
                source = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            }
        }

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

    private (BitmapSource? source, BitmapMetadata? metadata) LoadSourceAndMetadata(PhotoItem photo)
    {
        if (photo.IsRaw)
        {
            // Embedded full-resolution JPEG: camera-baked colour, sensor-sized, fast to read.
            // Going through the linear demosaic path would be 10-100× slower and is overkill
            // for a "copy at lower quality" workflow.
            byte[]? jpegBytes = _extractor.ExtractFullJpeg(photo.FilePath);
            if (jpegBytes == null) return (null, null);
            return DecodeFromBytes(jpegBytes);
        }

        try
        {
            using var stream = File.OpenRead(photo.FilePath);
            return DecodeFromStream(stream);
        }
        catch
        {
            return (null, null);
        }
    }

    private static (BitmapSource? source, BitmapMetadata? metadata) DecodeFromBytes(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            return DecodeFromStream(ms);
        }
        catch
        {
            return (null, null);
        }
    }

    private static (BitmapSource? source, BitmapMetadata? metadata) DecodeFromStream(Stream stream)
    {
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        BitmapMetadata? meta = null;
        try { meta = frame.Metadata as BitmapMetadata; } catch { }
        return (frame, meta);
    }
}
