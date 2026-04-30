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
    /// Box-average downsample to roughly the requested width. Returns this if no
    /// reduction is needed. Box averaging is the right filter for dither-friendly
    /// previews — it preserves smooth gradients and reduces sensor noise by the
    /// square root of the block area, without introducing ringing that bicubic
    /// would add.
    /// </summary>
    public LinearRawImage Downsample(int targetWidth)
    {
        if (targetWidth <= 0 || Width <= targetWidth) return this;
        int factor = Width / targetWidth;
        if (factor < 2) return this;

        int newW = Width / factor;
        int newH = Height / factor;
        var dst = new ushort[newW * newH * 3];
        int srcStride = Width * 3;
        int dstStride = newW * 3;
        int blockArea = factor * factor;

        for (int y = 0; y < newH; y++)
        {
            int dstRow = y * dstStride;
            int srcRowBase = y * factor * srcStride;
            for (int x = 0; x < newW; x++)
            {
                int sumR = 0, sumG = 0, sumB = 0;
                int srcColBase = x * factor * 3;
                for (int dy = 0; dy < factor; dy++)
                {
                    int rowOffset = srcRowBase + dy * srcStride + srcColBase;
                    for (int dx = 0; dx < factor; dx++)
                    {
                        int s = rowOffset + dx * 3;
                        sumR += Pixels[s];
                        sumG += Pixels[s + 1];
                        sumB += Pixels[s + 2];
                    }
                }
                int d = dstRow + x * 3;
                dst[d] = (ushort)(sumR / blockArea);
                dst[d + 1] = (ushort)(sumG / blockArea);
                dst[d + 2] = (ushort)(sumB / blockArea);
            }
        }

        return new LinearRawImage(newW, newH, dst);
    }
}
