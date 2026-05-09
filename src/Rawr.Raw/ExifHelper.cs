using System.IO;
using System.Text;
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

    internal static PhotoMetadata ReadFromExifBytes(byte[] exifBytes, long fileSizeBytes)
    {
        try
        {
            return TiffExifReader.Read(exifBytes, fileSizeBytes);
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

    private sealed class TiffExifReader
    {
        private readonly byte[] _data;
        private readonly int _tiffStart;
        private readonly bool _littleEndian;

        private TiffExifReader(byte[] data)
        {
            _data = data;
            _tiffStart = StartsWithExifHeader(data) ? 6 : 0;
            if (_data.Length < _tiffStart + 8)
                throw new InvalidDataException();

            _littleEndian = _data[_tiffStart] == (byte)'I' && _data[_tiffStart + 1] == (byte)'I';
            var bigEndian = _data[_tiffStart] == (byte)'M' && _data[_tiffStart + 1] == (byte)'M';
            if (!_littleEndian && !bigEndian)
                throw new InvalidDataException();
            if (ReadUShort(_tiffStart + 2) != 42)
                throw new InvalidDataException();
        }

        internal static PhotoMetadata Read(byte[] exifBytes, long fileSizeBytes)
        {
            var r = new TiffExifReader(exifBytes);
            var ifd0 = r.GetIfdEntries(r.ReadInt(r._tiffStart + 4));
            var exif = r.GetIfdEntries(r.ReadLongTag(ifd0, 34665));

            var capture = r.ParseExifDate(
                r.ReadAsciiTag(exif, 36867)
                ?? r.ReadAsciiTag(exif, 36868)
                ?? r.ReadAsciiTag(ifd0, 306));

            return new PhotoMetadata
            {
                CameraMake   = r.ReadAsciiTag(ifd0, 271) ?? "",
                CameraModel  = r.ReadAsciiTag(ifd0, 272) ?? "",
                LensModel    = r.ReadAsciiTag(exif, 42036) ?? "",
                ISO          = r.ReadFloatTag(exif, 34855),
                Aperture     = r.ReadFloatTag(exif, 33437),
                ShutterSpeed = r.ReadFloatTag(exif, 33434),
                FocalLength  = r.ReadFloatTag(exif, 37386),
                ExposureBias = r.ReadSignedFloatTag(exif, 37380),
                WidthPx      = r.ReadIntTag(exif, 40962),
                HeightPx     = r.ReadIntTag(exif, 40963),
                FileSizeBytes = fileSizeBytes,
                CaptureTime  = capture,
            };
        }

        private static bool StartsWithExifHeader(byte[] data) =>
            data.Length >= 10
            && data[0] == (byte)'E'
            && data[1] == (byte)'x'
            && data[2] == (byte)'i'
            && data[3] == (byte)'f'
            && data[4] == 0
            && data[5] == 0;

        private Dictionary<ushort, IfdEntry> GetIfdEntries(int offset)
        {
            var result = new Dictionary<ushort, IfdEntry>();
            var pos = _tiffStart + offset;
            if (offset <= 0 || pos < 0 || pos + 2 > _data.Length)
                return result;

            var count = ReadUShort(pos);
            pos += 2;
            for (int i = 0; i < count; i++)
            {
                var entryPos = pos + i * 12;
                if (entryPos + 12 > _data.Length) break;

                var tag = ReadUShort(entryPos);
                result[tag] = new IfdEntry(
                    ReadUShort(entryPos + 2),
                    ReadInt(entryPos + 4),
                    entryPos + 8);
            }

            return result;
        }

        private string? ReadAsciiTag(Dictionary<ushort, IfdEntry> entries, ushort tag)
        {
            if (!entries.TryGetValue(tag, out var e) || e.Type != 2 || e.Count <= 0)
                return null;

            var bytes = ReadEntryBytes(e);
            if (bytes.Length == 0) return null;

            var length = Array.IndexOf(bytes, (byte)0);
            if (length < 0) length = bytes.Length;

            return Encoding.ASCII.GetString(bytes, 0, length).Trim();
        }

        private int ReadIntTag(Dictionary<ushort, IfdEntry> entries, ushort tag)
        {
            if (!entries.TryGetValue(tag, out var e)) return 0;
            return e.Type switch
            {
                3 => ReadUShort(ValuePosition(e)),
                4 => ReadInt(ValuePosition(e)),
                _ => 0,
            };
        }

        private int ReadLongTag(Dictionary<ushort, IfdEntry> entries, ushort tag)
        {
            if (!entries.TryGetValue(tag, out var e)) return 0;
            return e.Type switch
            {
                3 => ReadUShort(ValuePosition(e)),
                4 => ReadInt(ValuePosition(e)),
                _ => 0,
            };
        }

        private float ReadFloatTag(Dictionary<ushort, IfdEntry> entries, ushort tag)
        {
            if (!entries.TryGetValue(tag, out var e)) return 0f;
            return e.Type switch
            {
                3 => ReadUShort(ValuePosition(e)),
                4 => ReadInt(ValuePosition(e)),
                5 => ReadRational(ValuePosition(e)),
                _ => 0f,
            };
        }

        private float? ReadSignedFloatTag(Dictionary<ushort, IfdEntry> entries, ushort tag)
        {
            if (!entries.TryGetValue(tag, out var e)) return null;
            return e.Type switch
            {
                9 => ReadInt(ValuePosition(e)),
                10 => ReadSignedRational(ValuePosition(e)),
                3 => ReadUShort(ValuePosition(e)),
                4 => ReadInt(ValuePosition(e)),
                _ => null,
            };
        }

        private DateTime? ParseExifDate(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            return DateTime.TryParseExact(
                value.Trim(),
                "yyyy:MM:dd HH:mm:ss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var dt)
                ? dt
                : null;
        }

        private byte[] ReadEntryBytes(IfdEntry e)
        {
            var byteCount = CheckedByteCount(e);
            if (byteCount <= 0) return [];

            var pos = byteCount <= 4 ? e.ValueOffsetPosition : _tiffStart + ReadInt(e.ValueOffsetPosition);
            if (pos < 0 || pos + byteCount > _data.Length)
                return [];

            var bytes = new byte[byteCount];
            Buffer.BlockCopy(_data, pos, bytes, 0, byteCount);
            return bytes;
        }

        private int ValuePosition(IfdEntry e) =>
            CheckedByteCount(e) <= 4 ? e.ValueOffsetPosition : _tiffStart + ReadInt(e.ValueOffsetPosition);

        private static int CheckedByteCount(IfdEntry e)
        {
            var unit = e.Type switch
            {
                1 or 2 or 7 => 1,
                3 => 2,
                4 or 9 => 4,
                5 or 10 => 8,
                _ => 0,
            };
            if (unit == 0 || e.Count <= 0 || e.Count > int.MaxValue / unit)
                return 0;
            return e.Count * unit;
        }

        private float ReadRational(int pos)
        {
            if (pos < 0 || pos + 8 > _data.Length) return 0f;
            var num = ReadUInt(pos);
            var den = ReadUInt(pos + 4);
            return den == 0 ? 0f : (float)(num / (double)den);
        }

        private float? ReadSignedRational(int pos)
        {
            if (pos < 0 || pos + 8 > _data.Length) return null;
            var num = ReadInt(pos);
            var den = ReadInt(pos + 4);
            return den == 0 ? null : (float)(num / (double)den);
        }

        private ushort ReadUShort(int pos)
        {
            if (pos < 0 || pos + 2 > _data.Length) return 0;
            return _littleEndian
                ? (ushort)(_data[pos] | (_data[pos + 1] << 8))
                : (ushort)((_data[pos] << 8) | _data[pos + 1]);
        }

        private uint ReadUInt(int pos)
        {
            if (pos < 0 || pos + 4 > _data.Length) return 0;
            return _littleEndian
                ? (uint)(_data[pos] | (_data[pos + 1] << 8) | (_data[pos + 2] << 16) | (_data[pos + 3] << 24))
                : (uint)((_data[pos] << 24) | (_data[pos + 1] << 16) | (_data[pos + 2] << 8) | _data[pos + 3]);
        }

        private int ReadInt(int pos) => unchecked((int)ReadUInt(pos));

        private readonly record struct IfdEntry(ushort Type, int Count, int ValueOffsetPosition);
    }
}
