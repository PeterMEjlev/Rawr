using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Rawr.App.Converters;

/// <summary>
/// Converts a JPEG byte[] into a frozen BitmapSource suitable for binding to Image.Source.
/// Uses DecodePixelWidth for fast scaled decode (the JPEG codec scales natively at decode
/// time). Applies EXIF orientation so portrait shots appear upright.
/// </summary>
public sealed class JpegBytesToImageConverter : IValueConverter
{
    /// <summary>Target decode width in pixels. 0 = full resolution.</summary>
    public int DecodePixelWidth { get; set; } = 240;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not byte[] bytes || bytes.Length == 0)
            return null;

        try
        {
            // Read EXIF orientation from headers — cheap, no pixel decode.
            double rotation = 0.0;
            try
            {
                using var msMeta = new MemoryStream(bytes);
                var metaDecoder = BitmapDecoder.Create(msMeta, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                rotation = ReadExifRotation(metaDecoder.Frames[0].Metadata as BitmapMetadata);
            }
            catch { /* no EXIF — leave at 0 */ }

            var bi = new BitmapImage();
            bi.BeginInit();
            bi.StreamSource = new MemoryStream(bytes);
            bi.CacheOption = BitmapCacheOption.OnLoad;
            if (DecodePixelWidth > 0)
                bi.DecodePixelWidth = DecodePixelWidth;
            bi.EndInit();
            bi.Freeze();

            if (rotation == 0.0) return bi;

            var rotated = new TransformedBitmap(bi, new RotateTransform(rotation));
            rotated.Freeze();
            return rotated;
        }
        catch
        {
            return null;
        }
    }

    private static double ReadExifRotation(BitmapMetadata? metadata)
    {
        try
        {
            var raw = metadata?.GetQuery("/app1/ifd/{ushort=274}");
            if (raw == null) return 0.0;
            int orientation = System.Convert.ToInt32(raw);
            return orientation switch
            {
                3 => 180.0,
                6 => 90.0,
                8 => 270.0,
                _ => 0.0
            };
        }
        catch { return 0.0; }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
