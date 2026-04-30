using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Rawr.App.Services;

public static class FocusPeakingComputer
{
    private const int DecodeWidth = 1024;

    public static BitmapSource Compute(byte[] jpegBytes, byte threshold = 30)
    {
        double rotation = 0.0;
        try
        {
            using var msMeta = new MemoryStream(jpegBytes);
            var meta = BitmapDecoder.Create(msMeta, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None)
                           .Frames[0].Metadata as BitmapMetadata;
            rotation = Convert.ToInt32(meta?.GetQuery("/app1/ifd/{ushort=274}")) switch
            {
                3 => 180.0,
                6 => 90.0,
                8 => 270.0,
                _ => 0.0
            };
        }
        catch { }

        var bi = new BitmapImage();
        bi.BeginInit();
        bi.StreamSource = new MemoryStream(jpegBytes);
        bi.DecodePixelWidth = DecodeWidth;
        bi.CreateOptions = BitmapCreateOptions.None;
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.EndInit();
        bi.Freeze();

        BitmapSource oriented = bi;
        if (rotation != 0.0)
        {
            var rotated = new TransformedBitmap(bi, new RotateTransform(rotation));
            rotated.Freeze();
            oriented = rotated;
        }

        var gray = new FormatConvertedBitmap(oriented, PixelFormats.Gray8, null, 0);
        gray.Freeze();

        int w = gray.PixelWidth;
        int h = gray.PixelHeight;
        byte[] src = new byte[h * w];
        gray.CopyPixels(src, w, 0);

        // Gaussian blur (3×3, σ≈1) — suppresses noise before the second-derivative step.
        byte[] blurred = new byte[w * h];
        for (int y = 1; y < h - 1; y++)
        {
            int rp = (y - 1) * w, rc = y * w, rn = (y + 1) * w;
            for (int x = 1; x < w - 1; x++)
            {
                blurred[rc + x] = (byte)((
                      src[rp + x - 1] + 2 * src[rp + x] +     src[rp + x + 1] +
                  2 * src[rc + x - 1] + 4 * src[rc + x] + 2 * src[rc + x + 1] +
                      src[rn + x - 1] + 2 * src[rn + x] +     src[rn + x + 1]
                ) >> 4);
            }
        }

        // Laplacian of Gaussian (LoG) — the second derivative responds proportionally to
        // 1/σ² of the edge width, vs 1/σ for the first derivative (Sobel). A soft
        // contrast ramp (treeline/sky) has near-zero Laplacian in its smooth interior;
        // only pixels with rapidly changing gradients — genuinely sharp focus — score high.
        byte[] mag = new byte[w * h];
        for (int y = 1; y < h - 1; y++)
        {
            int rp = (y - 1) * w, rc = y * w, rn = (y + 1) * w;
            for (int x = 1; x < w - 1; x++)
            {
                int lap = 4 * blurred[rc + x]
                          - blurred[rp + x] - blurred[rn + x]
                          - blurred[rc + x - 1] - blurred[rc + x + 1];
                mag[rc + x] = (byte)Math.Min(255, Math.Abs(lap) * 8);
            }
        }

        // BGRA overlay: above-threshold pixels highlighted in red, proportional alpha
        int stride = w * 4;
        byte[] overlay = new byte[h * stride];
        for (int i = 0; i < w * h; i++)
        {
            if (mag[i] >= threshold)
            {
                overlay[i * 4 + 2] = 255;                            // R
                overlay[i * 4 + 3] = (byte)Math.Min(255, mag[i] * 2); // A
            }
        }

        var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, overlay, stride);
        result.Freeze();
        return result;
    }
}
