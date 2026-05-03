using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rawr.Core.Models;
using Rawr.Raw;

namespace Rawr.App.Services;

public static class HistogramComputer
{
    // 16-bit linear → 8-bit sRGB bin index. Built once; ~64 KB.
    // We bin RAW values on a perceptual (sRGB-gamma) axis so shadows aren't
    // crushed into bin 0. Sensor black sits at bin 0, sensor clip at bin 255.
    // No tone curve is applied — only the standard sRGB transfer function —
    // so the histogram reflects the linear capture, not the rendered preview.
    private static readonly byte[] LinearToSrgbBin = BuildLinearToSrgbBin();

    /// <summary>
    /// True-RAW histogram from 16-bit linear sensor data (post-WB, post-color-matrix,
    /// pre-tone-curve). Highlight clipping shown here is real sensor clipping —
    /// unlike the JPEG histogram, which reflects the camera's baked tone curve.
    /// </summary>
    public static HistogramData Compute(LinearRawImage raw)
    {
        var src = raw.Pixels;
        var lut = LinearToSrgbBin;
        var data = new HistogramData();
        for (int i = 0; i < src.Length; i += 3)
        {
            ushort r = src[i];
            ushort g = src[i + 1];
            ushort b = src[i + 2];
            data.R[lut[r]]++;
            data.G[lut[g]]++;
            data.B[lut[b]]++;
            // Rec.709 luma in linear space, then sRGB-encode for binning.
            int yLin = (r * 2126 + g * 7152 + b * 722) / 10000;
            if (yLin > 65535) yLin = 65535;
            data.Combined[lut[yLin]]++;
        }
        return data;
    }

    public static HistogramData Compute(byte[] jpegBytes)
    {
        // Decode at a capped width — plenty of samples for accurate histograms, fast to process.
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.StreamSource = new MemoryStream(jpegBytes);
        bi.DecodePixelWidth = 512;
        bi.CreateOptions = BitmapCreateOptions.None;
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.EndInit();
        bi.Freeze();

        var converted = new FormatConvertedBitmap(bi, PixelFormats.Bgr24, null, 0);
        converted.Freeze();

        int w = converted.PixelWidth;
        int h = converted.PixelHeight;
        int stride = w * 3;
        byte[] pixels = new byte[h * stride];
        converted.CopyPixels(pixels, stride, 0);

        var data = new HistogramData();
        for (int i = 0; i < pixels.Length; i += 3)
        {
            byte b = pixels[i];
            byte g = pixels[i + 1];
            byte r = pixels[i + 2];
            data.R[r]++;
            data.G[g]++;
            data.B[b]++;
            // Rec.709 luminance coefficients
            data.Combined[(r * 2126 + g * 7152 + b * 722) / 10000]++;
        }
        return data;
    }

    public static HistogramData Compute(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0);
        converted.Freeze();

        int w = converted.PixelWidth;
        int h = converted.PixelHeight;
        int stride = w * 3;
        byte[] pixels = new byte[h * stride];
        converted.CopyPixels(pixels, stride, 0);

        var data = new HistogramData();
        for (int i = 0; i < pixels.Length; i += 3)
        {
            byte b = pixels[i];
            byte g = pixels[i + 1];
            byte r = pixels[i + 2];
            data.R[r]++;
            data.G[g]++;
            data.B[b]++;
            data.Combined[(r * 2126 + g * 7152 + b * 722) / 10000]++;
        }
        return data;
    }

    private static byte[] BuildLinearToSrgbBin()
    {
        var lut = new byte[65536];
        for (int i = 0; i < 65536; i++)
        {
            double linear = i / 65535.0;
            double srgb = linear <= 0.0031308
                ? 12.92 * linear
                : 1.055 * Math.Pow(linear, 1.0 / 2.4) - 0.055;
            int bin = (int)(srgb * 255.0 + 0.5);
            if (bin < 0) bin = 0; else if (bin > 255) bin = 255;
            lut[i] = (byte)bin;
        }
        return lut;
    }
}
