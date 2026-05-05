using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Rawr.App.Services;

/// <summary>
/// Estimates the share of clipped highlights / crushed shadows in a photo by
/// scanning its cached thumbnail JPEG. Used by the sidebar Exposure buckets to
/// gate which photos show up — not for the on-screen overlay, which still runs
/// against the linear RAW for sensor-truth precision.
///
/// Operating on the thumbnail (not the linear RAW) keeps this cheap enough to
/// run for every photo during indexing. The JPEG bakes in the camera's tone
/// curve, so absolute headroom isn't represented faithfully — but the relative
/// ordering ("which shots are heavily blown out") tracks closely enough for a
/// triage filter and matches what photographers see on the back of the camera.
/// </summary>
public static class ClippingStatsComputer
{
    public readonly record struct Stats(float HighlightPct, float ShadowPct);

    public static Stats Compute(byte[] jpegBytes, byte thresholdPct)
    {
        // Decode at the same width HistogramComputer uses — plenty of samples,
        // fast enough to run inside the parallel preview pipeline.
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

        int hiCut = (int)Math.Round(thresholdPct * 255.0 / 100.0);
        int loCut = 255 - hiCut;

        int total = w * h;
        int highlightHits = 0;
        int shadowHits = 0;
        for (int i = 0; i < pixels.Length; i += 3)
        {
            byte b = pixels[i];
            byte g = pixels[i + 1];
            byte r = pixels[i + 2];
            if (r >= hiCut || g >= hiCut || b >= hiCut)
                highlightHits++;
            else if (r <= loCut && g <= loCut && b <= loCut)
                shadowHits++;
        }

        return new Stats(
            HighlightPct: 100f * highlightHits / total,
            ShadowPct:    100f * shadowHits    / total);
    }
}
