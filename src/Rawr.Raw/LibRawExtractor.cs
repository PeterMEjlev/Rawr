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

    // For zoom-time inspection we need the sensor-sized JPEG. On CR3 the default
    // unpack_thumb often returns the medium ~1620×1080 PRVW preview rather than the
    // full-res JPEG in track 1, so we probe the indexed thumbs_list and pick the
    // largest. Falls back to ExtractDefaultThumb on older LibRaw builds without
    // unpack_thumb_ex (LibRaw 0.21+).
    public byte[]? ExtractFullJpeg(string filePath) => ExtractLargestThumb(filePath);

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

            // 16-bit, linear (gamma=1.0), no auto-bright, sRGB primaries.
            //
            // half_size=1 averages each 2×2 Bayer cell into one RGB sample. The
            // demosaic stage and the managed copy each shrink by ~4×, cutting
            // first-visit time by ~30-40%. Quality at preview resolution is
            // unchanged: we then box-average that half-size buffer down to
            // LinearRawPreviewWidth (~2400) anyway, so the only difference is
            // where the averaging happens. demosaic value is ignored when
            // half_size is on, but we still pass demosaic=0 for older LibRaw
            // builds where half_size might fall through.
            LibRawInterop.SetOutputBps(handle, 16);
            LibRawInterop.SetNoAutoBright(handle, 1);
            LibRawInterop.SetGamma(handle, 0, 1.0f);
            LibRawInterop.SetGamma(handle, 1, 1.0f);
            LibRawInterop.SetOutputColor(handle, 1);
            LibRawInterop.SetDemosaic(handle, 0);
            try { LibRawInterop.SetHalfSize(handle, 1); }
            catch (EntryPointNotFoundException) { /* pre-0.18 LibRaw — fall back to full demosaic */ }

            ret = LibRawInterop.Unpack(handle);
            if (ret != 0) return null;

            // Apply camera WB. The accessor `libraw_set_use_camera_wb` isn't
            // exported on the LibRaw 0.22.1 build we ship, so we have to plumb
            // the multipliers through user_mul. Two failure modes to guard:
            //   1. cam_mul not yet populated after Unpack for some Canon CR2s —
            //      it goes (X, 0, 0, 0) and dcraw_process then zeroes G+B during
            //      demosaic, producing the pure-red bug observed on portrait
            //      0Q9A9997.CR2 / 0Q9A0132.CR2. Re-check pre_mul (populated by
            //      identify() during open_file, more reliable but Canon usually
            //      leaves G2=0 — G1 is a fine substitute for the second green).
            //   2. Neither source available — fall back to neutral (1,1,1,1).
            //      Image will be colour-cast but balanced, which beats a
            //      Bayer-channel-zeroed sensor-noise render.
            float wbR  = LibRawInterop.GetCamMul(handle, 0);
            float wbG1 = LibRawInterop.GetCamMul(handle, 1);
            float wbB  = LibRawInterop.GetCamMul(handle, 2);
            float wbG2 = LibRawInterop.GetCamMul(handle, 3);
            bool camMulOk = wbR > 0 && wbG1 > 0 && wbB > 0 && wbG2 > 0;
            if (!camMulOk)
            {
                float pR  = LibRawInterop.GetPreMul(handle, 0);
                float pG1 = LibRawInterop.GetPreMul(handle, 1);
                float pB  = LibRawInterop.GetPreMul(handle, 2);
                float pG2 = LibRawInterop.GetPreMul(handle, 3);
                if (pG2 <= 0) pG2 = pG1;
                if (pR > 0 && pG1 > 0 && pB > 0)
                {
                    wbR = pR; wbG1 = pG1; wbB = pB; wbG2 = pG2;
                    camMulOk = true;
                }
            }
            LibRawInterop.SetUserMul(handle, 0, camMulOk ? wbR  : 1.0f);
            LibRawInterop.SetUserMul(handle, 1, camMulOk ? wbG1 : 1.0f);
            LibRawInterop.SetUserMul(handle, 2, camMulOk ? wbB  : 1.0f);
            LibRawInterop.SetUserMul(handle, 3, camMulOk ? wbG2 : 1.0f);

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
        try
        {
            handle = LibRawInterop.Init(0);
            if (handle == 0) return null;

            int ret = LibRawInterop.OpenFile(handle, filePath);
            if (ret != 0) return null;

            ret = LibRawInterop.UnpackThumb(handle);
            if (ret != 0) return null;

            return ReadCurrentThumb(handle, out _);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (handle != 0)
            {
                LibRawInterop.Recycle(handle);
                LibRawInterop.Close(handle);
            }
        }
    }

    // Probe the indexed thumbs_list and return the largest preview by pixel count.
    // CR3 carries up to three previews (THMB ~320×214, PRVW ~1620×1080, full-sensor in
    // track 1); only the indexed variant reliably reaches track 1. Iterates a small
    // bounded range and picks the largest successfully decoded thumb.
    private byte[]? ExtractLargestThumb(string filePath)
    {
        if (!_isAvailable) return null;

        nint handle = 0;
        try
        {
            handle = LibRawInterop.Init(0);
            if (handle == 0) return null;

            int ret = LibRawInterop.OpenFile(handle, filePath);
            if (ret != 0) return null;

            byte[]? best = null;
            int bestLen = 0;
            bool exAvailable = true;

            for (int idx = 0; idx < 4 && exAvailable; idx++)
            {
                try
                {
                    if (LibRawInterop.UnpackThumbEx(handle, idx) != 0)
                        break; // index out of range or decode failure — done
                }
                catch (EntryPointNotFoundException)
                {
                    exAvailable = false;
                    break;
                }

                // Rank by byte length, not width*height: LibRaw leaves the struct's
                // width/height at 0 for JPEG-type thumbs, so a pixel-count ranking
                // silently prefers an *uncompressed* bitmap preview (some DNGs carry
                // one) over the real JPEG — and the caller then can't decode it
                // (black zoom). Only JPEG-encoded thumbs are usable here; byte
                // length is a fine size proxy for same-codec previews.
                var data = ReadCurrentThumb(handle, out _);
                if (IsJpeg(data) && data!.Length > bestLen)
                {
                    best = data;
                    bestLen = data.Length;
                }
            }

            if (best != null) return best;

            // Fallback: older LibRaw without unpack_thumb_ex (or no JPEG thumb in the
            // indexed list). UnpackThumb yields the default/largest preview; keep it
            // only if it's actually a JPEG.
            if (LibRawInterop.UnpackThumb(handle) != 0) return null;
            var def = ReadCurrentThumb(handle, out _);
            return IsJpeg(def) ? def : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (handle != 0)
            {
                LibRawInterop.Recycle(handle);
                LibRawInterop.Close(handle);
            }
        }
    }

    // Reads the most recently unpacked thumb out of LibRaw's processed-image struct.
    // libraw_processed_image_t layout (validated against MSVC/Windows alignment):
    //   int    type       (0)   — LIBRAW_IMAGE_JPEG=1, LIBRAW_IMAGE_BITMAP=2
    //   ushort height     (4)
    //   ushort width      (6)
    //   ushort colors     (8)
    //   ushort bits       (10)
    //   int    data_size  (12)
    //   byte[] data       (16)
    // SOI marker check — the only reliable way to tell a JPEG thumb from an
    // uncompressed bitmap thumb, since ReadCurrentThumb hands back the raw bytes
    // either way and LibRaw's struct dims are unreliable for JPEGs.
    private static bool IsJpeg(byte[]? data) =>
        data is { Length: >= 2 } && data[0] == 0xFF && data[1] == 0xD8;

    private static byte[]? ReadCurrentThumb(nint handle, out int pixels)
    {
        pixels = 0;
        nint thumbImage = LibRawInterop.MakeMemThumb(handle, out int errCode);
        if (thumbImage == 0 || errCode != 0) return null;

        try
        {
            ushort height = (ushort)Marshal.ReadInt16(thumbImage, 4);
            ushort width = (ushort)Marshal.ReadInt16(thumbImage, 6);
            int dataSize = Marshal.ReadInt32(thumbImage, 12);

            if (dataSize <= 0 || dataSize > 100_000_000) // sanity: cap at 100MB
                return null;

            var data = new byte[dataSize];
            Marshal.Copy(thumbImage + 16, data, 0, dataSize);
            pixels = width * height;
            return data;
        }
        finally
        {
            LibRawInterop.ClearMem(thumbImage);
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
