using System.IO;
using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Rawr.App.Services;

/// <summary>
/// 64-bit difference hash (dHash) over JPEG bytes. Used to compare visual
/// similarity between burst candidates without decoding the full RAW.
/// </summary>
public static class PerceptualHash
{
    private const int HashWidth = 9;   // 9 columns → 8 horizontal comparisons per row
    private const int HashHeight = 8;
    private const int DecodeWidth = 64;

    public static ulong? Compute(byte[]? jpegBytes)
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

            var gray = new FormatConvertedBitmap(bi, PixelFormats.Gray8, null, 0);
            gray.Freeze();

            int w = gray.PixelWidth;
            int h = gray.PixelHeight;
            if (w < HashWidth || h < HashHeight) return null;

            int stride = (w + 3) & ~3; // Gray8 stride is rounded up to 4 bytes
            var src = new byte[stride * h];
            gray.CopyPixels(src, stride, 0);

            var small = Resample(src, stride, w, h, HashWidth, HashHeight);

            ulong hash = 0UL;
            int bit = 0;
            for (int y = 0; y < HashHeight; y++)
            {
                int row = y * HashWidth;
                for (int x = 0; x < HashWidth - 1; x++)
                {
                    if (small[row + x] < small[row + x + 1])
                        hash |= 1UL << bit;
                    bit++;
                }
            }
            return hash;
        }
        catch
        {
            return null;
        }
    }

    public static int HammingDistance(ulong a, ulong b)
        => BitOperations.PopCount(a ^ b);

    private static byte[] Resample(byte[] src, int srcStride, int sw, int sh, int dw, int dh)
    {
        var dst = new byte[dw * dh];
        for (int y = 0; y < dh; y++)
        {
            int sy0 = y * sh / dh;
            int sy1 = ((y + 1) * sh / dh);
            if (sy1 <= sy0) sy1 = sy0 + 1;
            for (int x = 0; x < dw; x++)
            {
                int sx0 = x * sw / dw;
                int sx1 = ((x + 1) * sw / dw);
                if (sx1 <= sx0) sx1 = sx0 + 1;

                int sum = 0, count = 0;
                for (int yy = sy0; yy < sy1; yy++)
                {
                    int rowOff = yy * srcStride;
                    for (int xx = sx0; xx < sx1; xx++)
                    {
                        sum += src[rowOff + xx];
                        count++;
                    }
                }
                dst[y * dw + x] = (byte)(sum / count);
            }
        }
        return dst;
    }
}
