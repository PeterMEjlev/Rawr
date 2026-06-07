using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Rawr.Core.Models;

namespace Rawr.Raw;

/// <summary>
/// Pulls a thumbnail/preview frame for a video file (or any non-RAW file) via
/// the Windows Shell's <c>IShellItemImageFactory</c>. This is the same source
/// Explorer uses, so we get a frame extracted by the OS without shipping ffmpeg
/// or implementing Media Foundation P/Invoke ourselves.
///
/// Returned bytes are JPEG-encoded for parity with the rest of the pipeline.
/// </summary>
public sealed class ShellThumbnailExtractor : IPreviewExtractor
{
    public bool IsAvailable => true;

    public byte[]? ExtractThumbnail(string filePath) => GetShellImage(filePath, 320);

    public byte[]? ExtractPreview(string filePath) => GetShellImage(filePath, 1280);

    // No "full" frame — for videos the preview is as far as we go; the player
    // takes over for actual playback. Returning null is fine, callers tolerate it.
    public byte[]? ExtractFullJpeg(string filePath) => null;

    public PhotoMetadata? ExtractMetadata(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);

            var props = new ShellMediaProps { CaptureTime = info.LastWriteTime };
            TryReadShellProperties(filePath, ref props);
            TryReadMp4Dimensions(filePath, ref props);
            var embeddedExif = TryReadEmbeddedExifMetadata(filePath, info.Length);

            return new PhotoMetadata
            {
                FileSizeBytes = info.Length,
                CaptureTime   = embeddedExif?.CaptureTime ?? props.CaptureTime,
                WidthPx       = PreferPositive(props.Width, embeddedExif?.WidthPx ?? 0),
                HeightPx      = PreferPositive(props.Height, embeddedExif?.HeightPx ?? 0),
                CameraMake    = PreferText(embeddedExif?.CameraMake, props.Make),
                CameraModel   = PreferText(embeddedExif?.CameraModel, props.Model),
                LensModel     = embeddedExif?.LensModel ?? "",
                ISO           = PreferPositive(embeddedExif?.ISO, props.ISO),
                Aperture      = PreferPositive(embeddedExif?.Aperture, props.Aperture),
                ShutterSpeed  = PreferPositive(embeddedExif?.ShutterSpeed, props.ShutterSpeed),
                FocalLength   = PreferPositive(embeddedExif?.FocalLength, props.FocalLength),
                ExposureBias  = embeddedExif?.ExposureBias,
                GpsLatitude   = embeddedExif?.GpsLatitude,
                GpsLongitude  = embeddedExif?.GpsLongitude,
                GpsAltitude   = embeddedExif?.GpsAltitude,
            };
        }
        catch { return null; }
    }

    private static int PreferPositive(int? preferred, int fallback)
    {
        // Shell can occasionally surface corrupt video dimensions through the
        // property store. Keep the bound conservative and let embedded EXIF win.
        if (preferred is > 0 and <= 100_000) return preferred.Value;
        return fallback is > 0 and <= 100_000 ? fallback : 0;
    }

    private static float PreferPositive(float? preferred, float fallback)
    {
        if (preferred is > 0) return preferred.Value;
        return fallback > 0 ? fallback : 0f;
    }

    private static string PreferText(string? preferred, string? fallback) =>
        !string.IsNullOrWhiteSpace(preferred) ? preferred.Trim()
        : !string.IsNullOrWhiteSpace(fallback) ? fallback.Trim()
        : "";

    private static PhotoMetadata? TryReadEmbeddedExifMetadata(string filePath, long fileSizeBytes)
    {
        try
        {
            var bytes = ReadFilePrefix(filePath, 16L * 1024 * 1024);
            if (bytes.Length == 0) return null;

            var exifStart = FindEmbeddedExifStart(bytes, out var exifLength);
            if (exifStart < 0 || exifLength <= 0) return null;

            var exif = new byte[exifLength];
            Buffer.BlockCopy(bytes, exifStart, exif, 0, exif.Length);

            var metadata = ExifHelper.ReadFromExifBytes(exif, fileSizeBytes);
            return HasUsefulExif(metadata) ? metadata : null;
        }
        catch
        {
            return null;
        }
    }

    private static void TryReadMp4Dimensions(string filePath, ref ShellMediaProps props)
    {
        try
        {
            var bytes = ReadFilePrefix(filePath, 16L * 1024 * 1024);
            if (bytes.Length == 0) return;

            for (int i = 4; i < bytes.Length - 100; i++)
            {
                if (bytes[i] != (byte)'t'
                    || bytes[i + 1] != (byte)'k'
                    || bytes[i + 2] != (byte)'h'
                    || bytes[i + 3] != (byte)'d')
                {
                    continue;
                }

                var boxStart = i - 4;
                var boxSize = ReadBigEndianUInt32(bytes, boxStart);
                if (boxSize < 92 || boxStart + boxSize > bytes.Length)
                    continue;

                var version = bytes[i + 4];
                var widthOffset = i + (version == 1 ? 92 : 80);
                var heightOffset = widthOffset + 4;
                if (heightOffset + 4 > boxStart + boxSize)
                    continue;

                var width = (int)(ReadBigEndianUInt32(bytes, widthOffset) >> 16);
                var height = (int)(ReadBigEndianUInt32(bytes, heightOffset) >> 16);
                if (width is > 0 and <= 100_000 && height is > 0 and <= 100_000)
                {
                    props.Width = width;
                    props.Height = height;
                    return;
                }
            }
        }
        catch { /* best-effort */ }
    }

    private static byte[] ReadFilePrefix(string filePath, long maxBytes)
    {
        var info = new FileInfo(filePath);
        var scanLength = (int)Math.Min(info.Length, maxBytes);
        if (scanLength <= 0) return [];

        var bytes = new byte[scanLength];
        using var fs = File.OpenRead(filePath);
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = fs.Read(bytes, offset, bytes.Length - offset);
            if (read == 0) break;
            offset += read;
        }

        if (offset != bytes.Length)
            Array.Resize(ref bytes, offset);
        return bytes;
    }

    private static uint ReadBigEndianUInt32(byte[] bytes, int offset)
    {
        if (offset < 0 || offset + 4 > bytes.Length) return 0;
        return (uint)((bytes[offset] << 24)
            | (bytes[offset + 1] << 16)
            | (bytes[offset + 2] << 8)
            | bytes[offset + 3]);
    }

    private static int FindEmbeddedExifStart(byte[] bytes, out int length)
    {
        length = 0;
        for (int i = 0; i < bytes.Length - 12; i++)
        {
            if (bytes[i] == (byte)'E'
                && bytes[i + 1] == (byte)'x'
                && bytes[i + 2] == (byte)'i'
                && bytes[i + 3] == (byte)'f'
                && bytes[i + 4] == 0
                && bytes[i + 5] == 0
                && IsTiffHeader(bytes, i + 6))
            {
                length = bytes.Length - i;
                if (i >= 4 && bytes[i - 4] == 0xFF && bytes[i - 3] == 0xE1)
                {
                    var app1Length = (bytes[i - 2] << 8) | bytes[i - 1];
                    if (app1Length > 2 && i + app1Length - 2 <= bytes.Length)
                        length = app1Length - 2;
                }

                return i;
            }
        }

        return -1;
    }

    private static bool IsTiffHeader(byte[] bytes, int offset) =>
        offset + 4 <= bytes.Length
        && ((bytes[offset] == (byte)'I' && bytes[offset + 1] == (byte)'I' && bytes[offset + 2] == 42 && bytes[offset + 3] == 0)
            || (bytes[offset] == (byte)'M' && bytes[offset + 1] == (byte)'M' && bytes[offset + 2] == 0 && bytes[offset + 3] == 42));

    private static bool HasUsefulExif(PhotoMetadata metadata) =>
        !string.IsNullOrWhiteSpace(metadata.CameraMake)
        || !string.IsNullOrWhiteSpace(metadata.CameraModel)
        || !string.IsNullOrWhiteSpace(metadata.LensModel)
        || metadata.CaptureTime.HasValue
        || metadata.ISO > 0
        || metadata.Aperture > 0
        || metadata.ShutterSpeed > 0
        || metadata.FocalLength > 0
        || metadata.WidthPx > 0
        || metadata.HeightPx > 0;

    private struct ShellMediaProps
    {
        public int Width;
        public int Height;
        public string? Make;
        public string? Model;
        public DateTime? CaptureTime;
        public float ISO;
        public float Aperture;
        public float ShutterSpeed;
        public float FocalLength;
    }

    /// <summary>
    /// Best-effort enrichment from the Windows Property Store — the same source
    /// Explorer reads. Cameras embed EXIF inside MP4/MOV containers (dimensions
    /// always; make/model/ISO/aperture/shutter/focal/date when the camera writes
    /// them), and the OS surfaces them through this API for any media format it
    /// understands. Failures are silent: we keep whatever defaults the caller set.
    /// </summary>
    private static void TryReadShellProperties(string filePath, ref ShellMediaProps props)
    {
        IPropertyStore? store = null;
        try
        {
            var ipsGuid = typeof(IPropertyStore).GUID;
            int hr = SHGetPropertyStoreFromParsingName(filePath, IntPtr.Zero, GPS_DEFAULT, ref ipsGuid, out store);
            if (hr != 0 || store == null) return;

            if (TryReadUInt32(store, PKEY_Video_FrameWidth, out var w))  props.Width  = (int)w;
            if (TryReadUInt32(store, PKEY_Video_FrameHeight, out var h)) props.Height = (int)h;

            var make = TryReadString(store, PKEY_Photo_CameraManufacturer);
            if (!string.IsNullOrWhiteSpace(make)) props.Make = make;
            var model = TryReadString(store, PKEY_Photo_CameraModel);
            if (!string.IsNullOrWhiteSpace(model)) props.Model = model;

            // Prefer the camera-stamped capture time; fall back to the encoder's date.
            if (TryReadDateTime(store, PKEY_Photo_DateTaken, out var dt) ||
                TryReadDateTime(store, PKEY_Media_DateEncoded, out dt))
            {
                props.CaptureTime = dt;
            }

            if (TryReadDouble(store, PKEY_Photo_ISOSpeed, out var iso))         props.ISO          = (float)iso;
            if (TryReadDouble(store, PKEY_Photo_FNumber, out var fn))           props.Aperture     = (float)fn;
            if (TryReadDouble(store, PKEY_Photo_ExposureTime, out var exp))     props.ShutterSpeed = (float)exp;
            if (TryReadDouble(store, PKEY_Photo_FocalLength, out var focal))    props.FocalLength  = (float)focal;
        }
        catch { /* best-effort */ }
        finally
        {
            if (store != null) Marshal.ReleaseComObject(store);
        }
    }

    private static bool TryReadUInt32(IPropertyStore store, PROPERTYKEY key, out uint value)
    {
        value = 0;
        var pv = new PROPVARIANT();
        try
        {
            if (store.GetValue(ref key, ref pv) != 0 || pv.vt == 0) return false;
            return PropVariantToUInt32(ref pv, out value) == 0;
        }
        finally { PropVariantClear(ref pv); }
    }

    private static bool TryReadDouble(IPropertyStore store, PROPERTYKEY key, out double value)
    {
        value = 0;
        var pv = new PROPVARIANT();
        try
        {
            if (store.GetValue(ref key, ref pv) != 0 || pv.vt == 0) return false;
            return PropVariantToDouble(ref pv, out value) == 0;
        }
        finally { PropVariantClear(ref pv); }
    }

    private static string? TryReadString(IPropertyStore store, PROPERTYKEY key)
    {
        var pv = new PROPVARIANT();
        try
        {
            if (store.GetValue(ref key, ref pv) != 0 || pv.vt == 0) return null;
            if (PropVariantToStringAlloc(ref pv, out var pwsz) != 0) return null;
            try { return Marshal.PtrToStringUni(pwsz); }
            finally { Marshal.FreeCoTaskMem(pwsz); }
        }
        finally { PropVariantClear(ref pv); }
    }

    private static bool TryReadDateTime(IPropertyStore store, PROPERTYKEY key, out DateTime value)
    {
        value = default;
        var pv = new PROPVARIANT();
        try
        {
            if (store.GetValue(ref key, ref pv) != 0 || pv.vt == 0) return false;
            // PSTF_LOCAL hands back a FILETIME whose ticks already encode the
            // local wall-clock (the property store converts UTC → local for us).
            // DateTime.FromFileTime would then *also* apply UTC → local, shifting
            // the result by another TZ offset — so use FromFileTimeUtc to take
            // the ticks verbatim, matching how ExifHelper.ParseExifDate treats
            // EXIF DateTimeOriginal as a wall-clock without TZ conversion.
            if (PropVariantToFileTime(ref pv, PSTIME_FLAGS.PSTF_LOCAL, out var ft) != 0) return false;
            long ticks = ((long)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;
            value = DateTime.FromFileTimeUtc(ticks);
            return true;
        }
        finally { PropVariantClear(ref pv); }
    }

    private static byte[]? GetShellImage(string filePath, int size)
    {
        if (!File.Exists(filePath)) return null;

        IShellItem? shellItem = null;
        IShellItemImageFactory? factory = null;
        nint hbitmap = 0;
        try
        {
            var shellItemGuid = typeof(IShellItem).GUID;
            int hr = SHCreateItemFromParsingName(filePath, IntPtr.Zero, ref shellItemGuid, out shellItem);
            if (hr != 0 || shellItem == null) return null;

            factory = shellItem as IShellItemImageFactory;
            if (factory == null) return null;

            hr = factory.GetImage(new SIZE(size, size),
                SIIGBF.SIIGBF_BIGGERSIZEOK | SIIGBF.SIIGBF_THUMBNAILONLY,
                out hbitmap);
            if (hr != 0 || hbitmap == 0)
            {
                // SIIGBF_THUMBNAILONLY can fail if the shell hasn't generated one yet;
                // retry without it so the shell is allowed to render on demand.
                hr = factory.GetImage(new SIZE(size, size), SIIGBF.SIIGBF_BIGGERSIZEOK, out hbitmap);
                if (hr != 0 || hbitmap == 0) return null;
            }

            var source = Imaging.CreateBitmapSourceFromHBitmap(
                hbitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();

            var encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(Rawr.Core.RawrTuning.CacheJpegQuality, 1, 100) };
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hbitmap != 0) DeleteObject(hbitmap);
            if (factory != null) Marshal.ReleaseComObject(factory);
            if (shellItem != null) Marshal.ReleaseComObject(shellItem);
        }
    }

    // ── Win32 / COM interop ──

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        nint pbc,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
        public SIZE(int cx, int cy) { this.cx = cx; this.cy = cy; }
    }

    [Flags]
    private enum SIIGBF
    {
        SIIGBF_RESIZETOFIT     = 0x00,
        SIIGBF_BIGGERSIZEOK    = 0x01,
        SIIGBF_MEMORYONLY      = 0x02,
        SIIGBF_ICONONLY        = 0x04,
        SIIGBF_THUMBNAILONLY   = 0x08,
        SIIGBF_INCACHEONLY     = 0x10,
    }

    [ComImport]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        // We only need the QI to IShellItemImageFactory; no methods accessed directly.
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, SIIGBF flags, out nint phbm);
    }

    // ── Property Store interop (for reading shell-surfaced media metadata) ──

    private const uint GPS_DEFAULT = 0;

    private enum PSTIME_FLAGS : uint
    {
        PSTF_UTC = 0,
        PSTF_LOCAL = 1,
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
        public PROPERTYKEY(Guid fmtid, uint pid) { this.fmtid = fmtid; this.pid = pid; }
    }

    // Minimal PROPVARIANT layout. We never read the union directly — the
    // PropVariantTo* helpers in propsys.dll handle the type conversion for us.
    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public IntPtr p1;
        public IntPtr p2;
    }

    [ComImport]
    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint cProps);
        [PreserveSig] int GetAt(uint iProp, out PROPERTYKEY pkey);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
        [PreserveSig] int Commit();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHGetPropertyStoreFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        uint flags,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore ppv);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT pvar);

    [DllImport("propsys.dll")]
    private static extern int PropVariantToUInt32(ref PROPVARIANT pv, out uint pulRet);

    [DllImport("propsys.dll")]
    private static extern int PropVariantToDouble(ref PROPVARIANT pv, out double pdblRet);

    [DllImport("propsys.dll", CharSet = CharSet.Unicode)]
    private static extern int PropVariantToStringAlloc(ref PROPVARIANT pv, out IntPtr ppszOut);

    [DllImport("propsys.dll")]
    private static extern int PropVariantToFileTime(
        ref PROPVARIANT pv,
        PSTIME_FLAGS flags,
        out System.Runtime.InteropServices.ComTypes.FILETIME pftRet);

    // PROPERTYKEY definitions from propkey.h.
    private static readonly Guid FMTID_Video = new("64440491-4C8B-11D1-8B70-080036B11A03");
    private static readonly Guid FMTID_Photo = new("14B81DA1-0135-4D31-96D9-6CBFC9671A99");
    private static readonly Guid FMTID_Media = new("2E4B640D-5019-46D8-8881-55414CC5CAA0");

    private static readonly PROPERTYKEY PKEY_Video_FrameWidth         = new(FMTID_Video, 8);
    private static readonly PROPERTYKEY PKEY_Video_FrameHeight        = new(FMTID_Video, 4);
    private static readonly PROPERTYKEY PKEY_Photo_CameraManufacturer = new(FMTID_Photo, 271);
    private static readonly PROPERTYKEY PKEY_Photo_CameraModel        = new(FMTID_Photo, 272);
    private static readonly PROPERTYKEY PKEY_Photo_DateTaken          = new(FMTID_Photo, 36867);
    private static readonly PROPERTYKEY PKEY_Photo_ISOSpeed           = new(FMTID_Photo, 34855);
    private static readonly PROPERTYKEY PKEY_Photo_FNumber            = new(FMTID_Photo, 33437);
    private static readonly PROPERTYKEY PKEY_Photo_ExposureTime       = new(FMTID_Photo, 33434);
    private static readonly PROPERTYKEY PKEY_Photo_FocalLength        = new(FMTID_Photo, 37386);
    private static readonly PROPERTYKEY PKEY_Media_DateEncoded        = new(FMTID_Media, 100);
}
