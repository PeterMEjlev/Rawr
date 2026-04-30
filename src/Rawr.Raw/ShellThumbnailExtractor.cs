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
            return new PhotoMetadata
            {
                FileSizeBytes = info.Length,
                CaptureTime = info.LastWriteTime,
            };
        }
        catch { return null; }
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

            var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
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
}
