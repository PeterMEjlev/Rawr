using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Rawr.App.Services;

/// <summary>
/// Decodes a cached thumbnail JPEG once into a Bgr24 pixel buffer that the folder
/// indexing pass shares between the perceptual hash and the clipping-stats
/// computation. JPEG decode dominates that pass (see CLAUDE.md perf notes), so
/// decoding the same bytes once instead of twice per photo roughly halves its cost.
/// </summary>
public static class ThumbnailPixels
{
    // Matches the width ClippingStatsComputer historically decoded at: enough
    // samples for stable clipping percentages and a smooth box-downsample to the
    // dHash grid, while keeping the decode cheap.
    public const int DecodeWidth = 512;

    public readonly record struct Decoded(byte[] Bgr, int Width, int Height, int Stride);

    /// <summary>
    /// Decode <paramref name="jpegBytes"/> to a packed Bgr24 buffer (stride =
    /// width*3, no row padding). Returns null on malformed input.
    /// </summary>
    public static Decoded? DecodeBgr24(byte[]? jpegBytes)
    {
        if (jpegBytes == null || jpegBytes.Length == 0) return null;
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.StreamSource = new MemoryStream(jpegBytes);
            bi.DecodePixelWidth = DecodeWidth;
            bi.CreateOptions = BitmapCreateOptions.None;
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.EndInit();
            bi.Freeze();

            var converted = new FormatConvertedBitmap(bi, PixelFormats.Bgr24, null, 0);
            converted.Freeze();

            int w = converted.PixelWidth;
            int h = converted.PixelHeight;
            int stride = w * 3;
            var pixels = new byte[h * stride];
            converted.CopyPixels(pixels, stride, 0);
            return new Decoded(pixels, w, h, stride);
        }
        catch
        {
            return null;
        }
    }
}
