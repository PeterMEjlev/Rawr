namespace Rawr.Raw;

/// <summary>
/// 16-bit linear RGB pixels decoded from a RAW file's sensor data.
///
/// Produced by LibRaw with no_auto_bright=1, gamma=1.0, output_bps=16, so the
/// values are linear scene-referred light: 0 = sensor black level, 65535 = sensor
/// clipping point. Camera white balance has been applied; sRGB primaries.
///
/// This is the substrate for accurate exposure compensation: applying gain in this
/// linear space before tone-mapping reflects the true recoverability of highlights
/// and shadows in the RAW capture.
/// </summary>
public sealed class LinearRawImage
{
    public int Width { get; }
    public int Height { get; }

    /// <summary>RGB-interleaved 16-bit linear pixels, length = Width * Height * 3.</summary>
    public ushort[] Pixels { get; }

    public LinearRawImage(int width, int height, ushort[] pixels)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    /// <summary>
    /// Box-average downsample to (approximately) <paramref name="targetWidth"/>
    /// pixels wide, preserving aspect ratio. Returns this only when the image is
    /// already at or below the target. Box averaging is the right filter for
    /// dither-friendly previews — it preserves smooth gradients and reduces sensor
    /// noise by the square root of the block area, without the ringing bicubic adds.
    ///
    /// Uses a fractional (non-integer) box. The previous integer-factor version
    /// (<c>factor = Width / targetWidth; if (factor &lt; 2) return this;</c>)
    /// silently returned the image untouched whenever that ratio was below 2 — e.g.
    /// a half-size 4096px sensor buffer against a 2400px target truncated to
    /// factor 1 — so the cached linear-RAW buffer was persisted at full half-size
    /// (~3-4x the intended bytes, larger than the source RAW itself). Mapping each
    /// destination pixel onto a real source box makes the target width hold for
    /// every sensor instead of only for exact integer ratios.
    /// </summary>
    public LinearRawImage Downsample(int targetWidth)
    {
        if (targetWidth <= 0 || Width <= targetWidth) return this;

        double scale = (double)Width / targetWidth;        // strictly > 1 here
        int newW = targetWidth;
        int newH = Math.Max(1, (int)Math.Round(Height / scale));
        var dst = new ushort[newW * newH * 3];
        int srcStride = Width * 3;
        int dstStride = newW * 3;
        var src = Pixels;

        // Rows are independent — parallelising shaves ~80-90% off the wall time on
        // multi-core CPUs. No locking needed: each row writes to its own slice of dst.
        Parallel.For(0, newH, y =>
        {
            // Source box for this destination row. Clamp the start to the last
            // valid row and force a >= 1px span so count can never be zero.
            int sy0 = Math.Min(Height - 1, (int)(y * scale));
            int sy1 = Math.Min(Height, (int)((y + 1) * scale));
            if (sy1 <= sy0) sy1 = sy0 + 1;
            int dstRow = y * dstStride;
            for (int x = 0; x < newW; x++)
            {
                int sx0 = Math.Min(Width - 1, (int)(x * scale));
                int sx1 = Math.Min(Width, (int)((x + 1) * scale));
                if (sx1 <= sx0) sx1 = sx0 + 1;

                long sumR = 0, sumG = 0, sumB = 0;
                for (int sy = sy0; sy < sy1; sy++)
                {
                    int s = sy * srcStride + sx0 * 3;
                    for (int sx = sx0; sx < sx1; sx++)
                    {
                        sumR += src[s];
                        sumG += src[s + 1];
                        sumB += src[s + 2];
                        s += 3;
                    }
                }
                long count = (long)(sy1 - sy0) * (sx1 - sx0);
                int d = dstRow + x * 3;
                dst[d] = (ushort)(sumR / count);
                dst[d + 1] = (ushort)(sumG / count);
                dst[d + 2] = (ushort)(sumB / count);
            }
        });

        return new LinearRawImage(newW, newH, dst);
    }
}
