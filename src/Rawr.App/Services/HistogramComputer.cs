using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rawr.Core.Models;

namespace Rawr.App.Services;

public static class HistogramComputer
{
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
}
