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

        var (gpsLat, gpsLon) = ParseGpsCoords(meta);

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
            ExposureBias = SignedRationalOrNull(meta, "/app1/ifd/exif/{ushort=37380}", "/ifd/exif/{ushort=37380}"),
            WidthPx      = widthPx,
            HeightPx     = heightPx,
            FileSizeBytes = fileSizeBytes,
            CaptureTime  = captureTime,
            GpsLatitude  = gpsLat,
            GpsLongitude = gpsLon,
            GpsAltitude  = ParseGpsAltitude(meta),
        };
    }

    private static (double? lat, double? lon) ParseGpsCoords(BitmapMetadata? meta)
    {
        var lat = GpsDms(meta, "/app1/ifd/gps/{ushort=2}") ?? GpsDms(meta, "/ifd/gps/{ushort=2}");
        if (lat == null) return (null, null);
        var lon = GpsDms(meta, "/app1/ifd/gps/{ushort=4}") ?? GpsDms(meta, "/ifd/gps/{ushort=4}");
        if (lon == null) return (null, null);

        var latRef = Str(meta, "/app1/ifd/gps/{ushort=1}") ?? Str(meta, "/ifd/gps/{ushort=1}");
        var lonRef = Str(meta, "/app1/ifd/gps/{ushort=3}") ?? Str(meta, "/ifd/gps/{ushort=3}");
        if (latRef?.Trim() == "S") lat = -lat;
        if (lonRef?.Trim() == "W") lon = -lon;
        return (lat, lon);
    }

    private static double? ParseGpsAltitude(BitmapMetadata? meta)
    {
        var alt = FltOr(meta, "/app1/ifd/gps/{ushort=6}", "/ifd/gps/{ushort=6}");
        if (alt <= 0) return null;
        try
        {
            var refObj = meta?.GetQuery("/app1/ifd/gps/{ushort=5}") ?? meta?.GetQuery("/ifd/gps/{ushort=5}");
            bool belowSea = refObj is byte b && b == 1;
            return belowSea ? -(double)alt : (double)alt;
        }
        catch { return (double)alt; }
    }

    // GPS latitude/longitude tags store degrees-minutes-seconds as an array of three RATIONALs.
    // WIC returns them as ulong[] or long[] using the same low/high-32 packing as scalar RATIONALs.
    private static double? GpsDms(BitmapMetadata? meta, string query)
    {
        if (meta == null) return null;
        try
        {
            var obj = meta.GetQuery(query);
            if (obj == null) return null;

            ulong[]? arr = null;
            if (obj is ulong[] ua) arr = ua;
            else if (obj is long[] la)
            {
                arr = new ulong[la.Length];
                for (int i = 0; i < la.Length; i++) arr[i] = (ulong)la[i];
            }
            if (arr == null || arr.Length < 2) return null;

            double deg = RationalToDouble(arr[0]);
            double min = RationalToDouble(arr[1]);
            double sec = arr.Length > 2 ? RationalToDouble(arr[2]) : 0;
            return deg + min / 60.0 + sec / 3600.0;
        }
        catch { return null; }
    }

    private static double RationalToDouble(ulong ul) =>
        ul == 0 ? 0 : (uint)(ul & 0xFFFFFFFF) / (double)(uint)(ul >> 32);

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

    // ExposureBiasValue is an SRATIONAL (signed rational). Numerator and denominator are
    // both signed 32-bit; WIC packs them low/high inside a 64-bit value but, unlike
    // RATIONAL, the numerator may be negative (e.g. -2 EV). Returns null when the tag
    // is absent so callers can distinguish "no compensation stamped" from "exactly 0 EV".
    private static float? SignedRationalOrNull(BitmapMetadata? meta, string q1, string q2)
    {
        var v = SignedRational(meta, q1);
        return v ?? SignedRational(meta, q2);
    }

    private static float? SignedRational(BitmapMetadata? meta, string query)
    {
        if (meta == null) return null;
        try
        {
            var obj = meta.GetQuery(query);
            return obj switch
            {
                null    => (float?)null,
                double d => (float)d,
                float f => f,
                long l  => DecodeSignedRational(l),
                ulong u => DecodeSignedRational(unchecked((long)u)),
                int i   => i,
                short s => s,
                _       => null,
            };
        }
        catch { return null; }
    }

    private static float? DecodeSignedRational(long packed)
    {
        int num = (int)(packed & 0xFFFFFFFF);
        int den = (int)((packed >> 32) & 0xFFFFFFFF);
        if (den == 0) return null;
        return (float)((double)num / den);
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
