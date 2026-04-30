using System.IO;
using System.Runtime.InteropServices;
using Rawr.Core.Models;

namespace Rawr.Raw;

/// <summary>
/// Production preview extractor using LibRaw native library.
///
/// Strategy for CR3 files:
/// - libraw_open_file() parses the ISOBMFF container and builds the thumbnail list (~1ms)
/// - libraw_unpack_thumb() extracts the default (largest) embedded JPEG (~5-20ms for seek+read)
/// - No demosaicing, no color conversion — just raw JPEG bytes from the file
///
/// CR3 embedded JPEG sizes (typical):
///   Thumbnail:     ~320x214    (~20 KB)     — from THMB box
///   Medium:        ~1620x1080  (~400 KB)    — from PRVW box
///   Full-res JPEG: sensor size (~3-5 MB)    — from Track 1 in mdat
///
/// cRAW (lossy compressed) files have identical embedded previews to regular CR3.
///
/// TODO: Add indexed thumbnail extraction (unpack_thumb_ex) via C wrapper
///       to enable loading small thumbnails for grid view and medium previews separately.
///       For now, we extract the default (largest) preview for all use cases.
/// </summary>
public sealed class LibRawExtractor : IPreviewExtractor
{
    private readonly bool _isAvailable;

    public LibRawExtractor()
    {
        _isAvailable = CheckAvailability();
    }

    public bool IsAvailable => _isAvailable;

    public byte[]? ExtractThumbnail(string filePath) => ExtractDefaultThumb(filePath);

    public byte[]? ExtractPreview(string filePath) => ExtractDefaultThumb(filePath);

    public byte[]? ExtractFullJpeg(string filePath) => ExtractDefaultThumb(filePath);

    /// <summary>
    /// Decode the RAW sensor data into a 16-bit linear RGB image (camera WB applied,
    /// sRGB primaries, gamma=1.0). This is the actual sensor data — clipping in the
    /// returned pixels reflects the sensor's true highlight ceiling, and shadow values
    /// preserve the full bit depth recorded by the camera.
    ///
    /// Significantly slower than thumbnail extraction (~300ms-2s depending on sensor
    /// size and CPU); intended for the currently selected photo, not bulk scanning.
    /// </summary>
    public LinearRawImage? ExtractLinearRgb(string filePath)
    {
        if (!_isAvailable) return null;

        nint handle = 0;
        nint imagePtr = 0;
        try
        {
            handle = LibRawInterop.Init(0);
            if (handle == 0) return null;

            int ret = LibRawInterop.OpenFile(handle, filePath);
            if (ret != 0) return null;

            // 16-bit, linear (gamma=1.0), no auto-bright, sRGB primaries, fast linear
            // demosaic. Linear demosaic (demosaic=0) is rough on fine detail but ~4-5x
            // faster than AHD — fine for a culling preview.
            LibRawInterop.SetOutputBps(handle, 16);
            LibRawInterop.SetNoAutoBright(handle, 1);
            LibRawInterop.SetGamma(handle, 0, 1.0f);
            LibRawInterop.SetGamma(handle, 1, 1.0f);
            LibRawInterop.SetOutputColor(handle, 1);
            LibRawInterop.SetDemosaic(handle, 0);

            ret = LibRawInterop.Unpack(handle);
            if (ret != 0) return null;

            // Use camera-recorded WB. cam_mul is populated by Unpack(); copying it to
            // user_mul stands in for use_camera_wb=1, whose setter isn't always exported.
            for (int i = 0; i < 4; i++)
            {
                float m = LibRawInterop.GetCamMul(handle, i);
                if (m > 0) LibRawInterop.SetUserMul(handle, i, m);
            }

            ret = LibRawInterop.DcrawProcess(handle);
            if (ret != 0) return null;

            imagePtr = LibRawInterop.MakeMemImage(handle, out int errCode);
            if (imagePtr == 0 || errCode != 0) return null;

            // libraw_processed_image_t actual layout — `type` is a C enum which
            // compiles to a 4-byte int on MSVC/Windows, not a ushort. The thumb path
            // above only reads `type` (low 2 bytes happen to hold value 1 = JPEG) and
            // `data_size` (offset 12 is right either way), so it gets away with
            // reading at offset 2. We need the real offsets:
            //   int    type;       (0)   = LIBRAW_IMAGE_BITMAP=2
            //   ushort height;     (4)
            //   ushort width;      (6)
            //   ushort colors;     (8)
            //   ushort bits;       (10)
            //   int    data_size;  (12)
            //   byte[] data;       (16)
            int type = Marshal.ReadInt32(imagePtr, 0);
            ushort height = (ushort)Marshal.ReadInt16(imagePtr, 4);
            ushort width = (ushort)Marshal.ReadInt16(imagePtr, 6);
            ushort colors = (ushort)Marshal.ReadInt16(imagePtr, 8);
            ushort bits = (ushort)Marshal.ReadInt16(imagePtr, 10);
            int dataSize = Marshal.ReadInt32(imagePtr, 12);
            if (type != 2) return null;

            if (colors != 3 || bits != 16 || dataSize <= 0) return null;
            int pixelCount = width * height * 3;
            if (dataSize != pixelCount * 2) return null;

            var pixels = new ushort[pixelCount];
            unsafe
            {
                fixed (ushort* dst = pixels)
                {
                    Buffer.MemoryCopy((void*)(imagePtr + 16), dst, dataSize, dataSize);
                }
            }

            return new LinearRawImage(width, height, pixels);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (imagePtr != 0) LibRawInterop.ClearMem(imagePtr);
            if (handle != 0)
            {
                LibRawInterop.Recycle(handle);
                LibRawInterop.Close(handle);
            }
        }
    }

    public PhotoMetadata? ExtractMetadata(string filePath)
    {
        if (!_isAvailable) return null;

        var size = new FileInfo(filePath).Length;

        // Primary: read EXIF directly from the CR3 file via WIC — complete EXIF including
        // focal length, which Canon CR3 embedded JPEGs commonly strip from their EXIF block.
        // Requires Microsoft Raw Image Extension; silently falls through if unavailable.
        try
        {
            using var stream = File.OpenRead(filePath);
            var wicMeta = ExifHelper.ReadFromStream(stream, size);
            if (!string.IsNullOrEmpty(wicMeta.CameraModel))
                return wicMeta;
        }
        catch { }

        // Fallback: extract the embedded JPEG and parse its EXIF
        var jpeg = ExtractDefaultThumb(filePath);
        return jpeg != null
            ? ExifHelper.ReadFromJpegBytes(jpeg, size)
            : new PhotoMetadata { FileSizeBytes = size };
    }

    private byte[]? ExtractDefaultThumb(string filePath)
    {
        if (!_isAvailable) return null;

        nint handle = 0;
        nint thumbImage = 0;
        try
        {
            handle = LibRawInterop.Init(0);
            if (handle == 0) return null;

            int ret = LibRawInterop.OpenFile(handle, filePath);
            if (ret != 0) return null;

            ret = LibRawInterop.UnpackThumb(handle);
            if (ret != 0) return null;

            thumbImage = LibRawInterop.MakeMemThumb(handle, out int errCode);
            if (thumbImage == 0 || errCode != 0) return null;

            // libraw_processed_image_t layout:
            //   ushort type       (offset 0)  — LIBRAW_IMAGE_JPEG=1 or LIBRAW_IMAGE_BITMAP=2
            //   ushort height     (offset 2)
            //   ushort width      (offset 4)
            //   ushort colors     (offset 6)
            //   ushort bits       (offset 8)
            //   int    data_size  (offset 12, accounting for alignment)
            //   byte[] data       (offset 16)
            //
            // NOTE: These offsets may vary by platform/alignment. Validate with your LibRaw build.
            // TODO: Consider using a C wrapper for reliable struct access.

            var type = Marshal.ReadInt16(thumbImage, 0);
            var dataSize = Marshal.ReadInt32(thumbImage, 12);

            if (dataSize <= 0 || dataSize > 50_000_000) // sanity check: max 50MB
                return null;

            var data = new byte[dataSize];
            Marshal.Copy(thumbImage + 16, data, 0, dataSize);

            return data;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (thumbImage != 0) LibRawInterop.ClearMem(thumbImage);
            if (handle != 0)
            {
                LibRawInterop.Recycle(handle);
                LibRawInterop.Close(handle);
            }
        }
    }

    private static bool CheckAvailability()
    {
        try
        {
            var handle = LibRawInterop.Init(0);
            if (handle != 0)
            {
                LibRawInterop.Close(handle);
                return true;
            }
            return false;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }
}
