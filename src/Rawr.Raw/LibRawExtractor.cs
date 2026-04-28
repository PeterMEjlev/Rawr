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

    public PhotoMetadata? ExtractMetadata(string filePath)
    {
        if (!_isAvailable) return null;
        // Extract the embedded JPEG (which carries the full EXIF block) then parse
        // EXIF from those bytes — avoids needing a C wrapper for libraw_data_t struct access.
        var jpeg = ExtractDefaultThumb(filePath);
        var size = new FileInfo(filePath).Length;
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
