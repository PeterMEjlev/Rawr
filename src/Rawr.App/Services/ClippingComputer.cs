using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rawr.Raw;

namespace Rawr.App.Services;

/// <summary>
/// Builds an overlay marking sensor-clipped pixels — highlights in red, crushed
/// shadows in blue. Detection runs on the 16-bit linear sensor data so the marks
/// reflect what's actually recoverable from the RAW capture, not what the JPEG
/// tone-curve happens to render. Independent of any exposure compensation the
/// user is applying to the live preview.
///
/// Operates on the cached (downsampled) linear preview. Isolated single-pixel
/// hot spots may be averaged out near block boundaries, but the large saturated
/// regions photographers care about (skies, specular highlights, blown windows)
/// are reliably flagged.
/// </summary>
public static class ClippingComputer
{
    private const ushort LinearMax = 65535;
    private const byte OverlayAlpha = 220;

    public static BitmapSource Compute(LinearRawImage raw, ClippingMode mode, int thresholdPct)
    {
        int w = raw.Width;
        int h = raw.Height;
        int n = w * h;

        // Threshold maps to a linear-scale rail proximity:
        //   highlight clip: any channel ≥ thresholdPct% of LinearMax
        //   shadow   clip: every channel ≤ (100 − thresholdPct)% of LinearMax
        int hiCut = (int)Math.Round(thresholdPct * (double)LinearMax / 100.0);
        int loCut = LinearMax - hiCut;

        bool flagHighlights = mode != ClippingMode.Shadows;
        bool flagShadows = mode != ClippingMode.Highlights;

        int stride = w * 4;
        byte[] overlay = new byte[h * stride];
        var px = raw.Pixels;

        for (int i = 0; i < n; i++)
        {
            int s = i * 3;
            int r = px[s];
            int g = px[s + 1];
            int b = px[s + 2];

            if (flagHighlights && (r >= hiCut || g >= hiCut || b >= hiCut))
            {
                int o = i * 4;
                // BGRA: red highlight
                overlay[o + 2] = 255;
                overlay[o + 3] = OverlayAlpha;
            }
            else if (flagShadows && r <= loCut && g <= loCut && b <= loCut)
            {
                int o = i * 4;
                // BGRA: blue shadow
                overlay[o] = 255;
                overlay[o + 3] = OverlayAlpha;
            }
        }

        var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, overlay, stride);
        result.Freeze();
        return result;
    }
}
