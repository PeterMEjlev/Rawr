using System.IO;
using System.Windows.Media.Imaging;
using Rawr.Core.Models;

namespace Rawr.Raw;

internal static class ExifHelper
{
    internal static PhotoMetadata ReadFromJpegBytes(byte[] jpeg, long fileSizeBytes)
    {
        try
        {
            using var ms = new MemoryStream(jpeg);
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            return Build(frame.Metadata as BitmapMetadata, frame.PixelWidth, frame.PixelHeight, fileSizeBytes);
        }
        catch
        {
            return new PhotoMetadata { FileSizeBytes = fileSizeBytes };
        }
    }

    internal static PhotoMetadata ReadFromStream(Stream stream, long fileSizeBytes)
    {
        try
        {
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            return Build(frame.Metadata as BitmapMetadata, frame.PixelWidth, frame.PixelHeight, fileSizeBytes);
        }
        catch
        {
            return new PhotoMetadata { FileSizeBytes = fileSizeBytes };
        }
    }

    private static PhotoMetadata Build(BitmapMetadata? meta, int widthPx, int heightPx, long fileSizeBytes)
    {
        DateTime? captureTime = null;
        try
        {
            var dateTaken = meta?.DateTaken;
            if (!string.IsNullOrEmpty(dateTaken) && DateTime.TryParse(dateTaken, out var dt))
                captureTime = dt;
        }
        catch { }

        // Try both JPEG (/app1/ifd/...) and TIFF (/ifd/...) path prefixes so this
        // works on JPEG thumbnails extracted by LibRaw and on raw files opened via WIC.
        return new PhotoMetadata
        {
            CameraMake   = Str(meta, "/app1/ifd/{ushort=271}")        ?? Str(meta, "/ifd/{ushort=271}")        ?? "",
            CameraModel  = Str(meta, "/app1/ifd/{ushort=272}")        ?? Str(meta, "/ifd/{ushort=272}")        ?? "",
            LensModel    = Str(meta, "/app1/ifd/exif/{ushort=42036}") ?? Str(meta, "/ifd/exif/{ushort=42036}") ?? "",
            ISO          = FltOr(meta, "/app1/ifd/exif/{ushort=34855}", "/ifd/exif/{ushort=34855}"),
            Aperture     = FltOr(meta, "/app1/ifd/exif/{ushort=33437}", "/ifd/exif/{ushort=33437}"),
            ShutterSpeed = FltOr(meta, "/app1/ifd/exif/{ushort=33434}", "/ifd/exif/{ushort=33434}"),
            FocalLength  = FltOr(meta, "/app1/ifd/exif/{ushort=37386}", "/ifd/exif/{ushort=37386}"),
            WidthPx      = widthPx,
            HeightPx     = heightPx,
            FileSizeBytes = fileSizeBytes,
            CaptureTime  = captureTime,
        };
    }

    private static string? Str(BitmapMetadata? meta, string query)
    {
        try { return meta?.GetQuery(query)?.ToString(); }
        catch { return null; }
    }

    private static float FltOr(BitmapMetadata? meta, string q1, string q2)
    {
        var v = Flt(meta, q1);
        return v > 0 ? v : Flt(meta, q2);
    }

    private static float Flt(BitmapMetadata? meta, string query)
    {
        if (meta == null) return 0f;
        try
        {
            return meta.GetQuery(query) switch
            {
                double d                  => (float)d,
                float  f                  => f,
                // EXIF RATIONAL = [4-byte numerator][4-byte denominator] in little-endian memory.
                // WIC packs these into a ulong as low-32 = numerator, high-32 = denominator.
                ulong  ul when ul != 0    => (float)((uint)(ul & 0xFFFFFFFF) / (double)(uint)(ul >> 32)),
                long   l  when l  != 0    => (float)((uint)(l  & 0xFFFFFFFF) / (double)(uint)(l  >> 32)),
                uint   u                  => (float)u,
                ushort s                  => (float)s,
                int    i                  => (float)i,
                _                         => 0f,
            };
        }
        catch { return 0f; }
    }
}
