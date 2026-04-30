using System.Runtime.InteropServices;

namespace Rawr.Raw;

/// <summary>
/// P/Invoke declarations for LibRaw native library.
///
/// SETUP REQUIRED:
/// 1. Download LibRaw 0.21+ (or 0.22 for latest Canon cameras) from https://www.libraw.org/download
/// 2. Get the Windows DLL build (libraw.dll) — either build from source or find a prebuilt binary.
///    - Prebuilt: check https://www.libraw.org/download or vcpkg: `vcpkg install libraw:x64-windows`
/// 3. Place libraw.dll in the application output directory (alongside RAWR.exe).
///
/// IMPORTANT VERSION NOTES:
/// - LibRaw 0.20: CR3 support (lossless only), single thumbnail extraction
/// - LibRaw 0.21+: CR3 cRAW (lossy) support, thumbs_list with multiple previews, unpack_thumb_ex()
/// - LibRaw 0.22: latest Canon camera support (R5 II, R6 II, R8, etc.)
///
/// For CR3 files, libraw_open_file() alone parses metadata + builds thumbnail list.
/// No demosaicing is needed for preview extraction — this is essentially seek + read.
///
/// TODO: Validate that the specific LibRaw build you use supports:
///   - thumbs_list (thumbcount > 1 for CR3 files)
///   - unpack_thumb_ex() with index parameter
///   If using an older build, fall back to unpack_thumb() which extracts only the largest preview.
/// </summary>
internal static partial class LibRawInterop
{
    private const string LibName = "raw";

    // ── Lifecycle ──

    [LibraryImport(LibName, EntryPoint = "libraw_init")]
    internal static partial nint Init(uint flags);

    [LibraryImport(LibName, EntryPoint = "libraw_close")]
    internal static partial void Close(nint handle);

    [LibraryImport(LibName, EntryPoint = "libraw_recycle")]
    internal static partial void Recycle(nint handle);

    // ── File I/O ──

    [LibraryImport(LibName, EntryPoint = "libraw_open_file", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int OpenFile(nint handle, string fileName);

    // ── Thumbnail extraction ──

    /// <summary>
    /// Extract the default (largest) thumbnail. Available in all LibRaw versions with CR3 support.
    /// </summary>
    [LibraryImport(LibName, EntryPoint = "libraw_unpack_thumb")]
    internal static partial int UnpackThumb(nint handle);

    /// <summary>
    /// Extract thumbnail by index from thumbs_list. Requires LibRaw 0.21+.
    /// Index 0 is typically the largest (full-res JPEG for CR3).
    /// </summary>
    [LibraryImport(LibName, EntryPoint = "libraw_unpack_thumb_ex")]
    internal static partial int UnpackThumbEx(nint handle, int thumbIndex);

    /// <summary>
    /// Get the extracted thumbnail as an in-memory image.
    /// For JPEG thumbnails: returns raw JPEG bytes (no conversion needed).
    /// Caller must free with dcraw_clear_mem().
    /// </summary>
    [LibraryImport(LibName, EntryPoint = "libraw_dcraw_make_mem_thumb")]
    internal static partial nint MakeMemThumb(nint handle, out int errorCode);

    [LibraryImport(LibName, EntryPoint = "libraw_dcraw_clear_mem")]
    internal static partial void ClearMem(nint image);

    // ── RAW pixel decoding ──

    /// <summary>Decode the Bayer sensor data from the RAW file. Required before dcraw_process.</summary>
    [LibraryImport(LibName, EntryPoint = "libraw_unpack")]
    internal static partial int Unpack(nint handle);

    /// <summary>
    /// Run the dcraw-equivalent processing pipeline (demosaic, white balance, color
    /// conversion, gamma) on the unpacked RAW data. Output is controlled by the
    /// libraw_set_* configuration calls below.
    /// </summary>
    [LibraryImport(LibName, EntryPoint = "libraw_dcraw_process")]
    internal static partial int DcrawProcess(nint handle);

    /// <summary>
    /// Get the processed image as an in-memory bitmap. Returns a libraw_processed_image_t*
    /// with type=LIBRAW_IMAGE_BITMAP. Caller must free with dcraw_clear_mem().
    /// </summary>
    [LibraryImport(LibName, EntryPoint = "libraw_dcraw_make_mem_image")]
    internal static partial nint MakeMemImage(nint handle, out int errorCode);

    // ── Output configuration ──
    // These are libraw_set_* accessor functions exported by LibRaw 0.18+.
    // They mutate the params struct without us needing to know its memory layout.

    [LibraryImport(LibName, EntryPoint = "libraw_set_output_bps")]
    internal static partial void SetOutputBps(nint handle, int value);

    [LibraryImport(LibName, EntryPoint = "libraw_set_no_auto_bright")]
    internal static partial void SetNoAutoBright(nint handle, int value);

    [LibraryImport(LibName, EntryPoint = "libraw_set_gamma")]
    internal static partial void SetGamma(nint handle, int index, float value);

    [LibraryImport(LibName, EntryPoint = "libraw_set_output_color")]
    internal static partial void SetOutputColor(nint handle, int value);

    [LibraryImport(LibName, EntryPoint = "libraw_set_demosaic")]
    internal static partial void SetDemosaic(nint handle, int value);

    /// <summary>
    /// Read a single channel of the camera-recorded white balance multiplier (0=R, 1=G, 2=B, 3=G2).
    /// Populated after libraw_unpack().
    /// </summary>
    [LibraryImport(LibName, EntryPoint = "libraw_get_cam_mul")]
    internal static partial float GetCamMul(nint handle, int index);

    /// <summary>
    /// Set a user white balance multiplier. Setting these to the cam_mul values is
    /// equivalent to use_camera_wb=1 for builds where that setter isn't exported.
    /// </summary>
    [LibraryImport(LibName, EntryPoint = "libraw_set_user_mul")]
    internal static partial void SetUserMul(nint handle, int index, float value);

    // ── Error handling ──

    [LibraryImport(LibName, EntryPoint = "libraw_strerror")]
    internal static partial nint StrError(int errorCode);

    internal static string GetError(int code)
    {
        var ptr = StrError(code);
        return Marshal.PtrToStringAnsi(ptr) ?? $"LibRaw error {code}";
    }

    // ── Data structure offsets ──
    // These offsets depend on the LibRaw version and compilation settings.
    // The safest approach is to use the C API accessor functions.
    // For direct struct access, see LibRaw's libraw_types.h.

    // We use a simplified approach: read metadata via accessor patterns
    // rather than mapping the full libraw_data_t structure, which is very large
    // and version-dependent.

    /// <summary>
    /// libraw_data_t.thumbs_list.thumbcount — number of available thumbnails.
    /// For CR3 files, typically 3 (full JPEG, medium preview, thumbnail).
    ///
    /// NOTE: The offset of thumbs_list within libraw_data_t is version-dependent.
    /// This is a known challenge with LibRaw P/Invoke. Options:
    /// 1. Build a tiny C wrapper DLL that exposes accessor functions (recommended for production)
    /// 2. Use Sdcb.LibRaw NuGet package which handles struct mapping
    /// 3. Use the simple approach below: just call unpack_thumb() for the default preview
    ///
    /// TODO: For MVP, we use unpack_thumb() (default largest preview).
    /// Add indexed extraction via a C wrapper in a future iteration.
    /// </summary>
    internal const int THUMB_FORMAT_JPEG = 1;
}
