using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Rawr.App.Services;

public static class ExposureProcessor
{
    public static BitmapSource Apply(BitmapSource source, double stops)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0);
        converted.Freeze();

        int w = converted.PixelWidth;
        int h = converted.PixelHeight;
        int stride = w * 3;
        byte[] pixels = new byte[h * stride];
        converted.CopyPixels(pixels, stride, 0);

        float gain = (float)Math.Pow(2.0, stops);
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = (byte)Math.Clamp(pixels[i] * gain, 0f, 255f);

        var result = BitmapSource.Create(w, h, source.DpiX, source.DpiY, PixelFormats.Bgr24, null, pixels, stride);
        result.Freeze();
        return result;
    }
}
