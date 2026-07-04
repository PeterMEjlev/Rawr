using System.Numerics;

namespace Rawr.App.Services;

/// <summary>
/// 64-bit difference hash (dHash) over JPEG bytes. Used to compare visual
/// similarity between burst candidates without decoding the full RAW.
/// </summary>
public static class PerceptualHash
{
    private const int HashWidth = 9;   // 9 columns → 8 horizontal comparisons per row
    private const int HashHeight = 8;

    // Larger grayscale buffer used by FrameShiftEstimator. Large enough that 2D
    // cross-correlation can resolve panorama-scale shifts.
    public const int StripWidth = 32;
    public const int StripHeight = 24;

    public static ulong? Compute(byte[]? jpegBytes)
        => ComputeWithStrip(jpegBytes).Hash;

    /// <summary>
    /// Decodes the thumbnail once and returns both the dHash and a downsampled
    /// grayscale buffer (<see cref="StripWidth"/>×<see cref="StripHeight"/>) for
    /// inter-frame shift estimation. Either field may be null on decode failure.
    /// </summary>
    public static (ulong? Hash, byte[]? Strip) ComputeWithStrip(byte[]? jpegBytes)
    {
        var decoded = ThumbnailPixels.DecodeBgr24(jpegBytes);
        if (decoded == null) return (null, null);
        return ComputeWithStrip(decoded.Value.Bgr, decoded.Value.Width, decoded.Value.Height, decoded.Value.Stride);
    }

    /// <summary>
    /// Overload for callers that already decoded the thumbnail to a packed Bgr24
    /// buffer (the folder-indexing pass shares one decode with the clipping stats).
    /// The buffer is converted to luma and box-averaged down to the hash grid and
    /// strip. Hash values are effectively identical to the former Gray8/64px path —
    /// the dHash compares relative cell brightness, which box-averaging preserves
    /// across source resolutions — so persisted hashes stay comparable.
    /// </summary>
    public static (ulong? Hash, byte[]? Strip) ComputeWithStrip(byte[] bgr, int width, int height, int stride)
    {
        try
        {
            if (width < HashWidth || height < HashHeight) return (null, null);

            // Pack to a single-channel luma buffer (stride = width) so Resample can
            // box-average it. Rec.601 coefficients match WIC's Bgr→Gray8 conversion.
            var gray = new byte[width * height];
            for (int y = 0; y < height; y++)
            {
                int row = y * stride;
                int grow = y * width;
                for (int x = 0; x < width; x++)
                {
                    int i = row + x * 3;
                    gray[grow + x] = (byte)((bgr[i + 2] * 299 + bgr[i + 1] * 587 + bgr[i] * 114) / 1000);
                }
            }

            var small = Resample(gray, width, width, height, HashWidth, HashHeight);

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

            byte[]? strip = null;
            if (width >= StripWidth && height >= StripHeight)
                strip = Resample(gray, width, width, height, StripWidth, StripHeight);

            return (hash, strip);
        }
        catch
        {
            return (null, null);
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
