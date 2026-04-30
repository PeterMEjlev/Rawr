using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rawr.Raw;

namespace Rawr.App.Services;

/// <summary>
/// Renders a preview from either a baked JPEG (legacy path) or a 16-bit linear
/// RAW image (FastRawViewer-style path). The RAW path applies exposure as a gain
/// in linear space and clips at the sensor white level — pulling exposure down
/// reveals highlights that the JPEG had already clipped, and pushing it up
/// reveals real shadow detail rather than amplifying JPEG quantisation.
/// </summary>
public static class ExposureProcessor
{
    private static readonly float[] LinearToSrgbF = BuildLinearToSrgbLutF();

    /// <summary>JPEG-pixel exposure: 8-bit gain with hard clipping. Lossy at boundaries.</summary>
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

    /// <summary>
    /// Linear-RAW exposure: gain × clip × sRGB-encode. Pixels at 65535 in the source
    /// are sensor-clipped and stay clipped regardless of stops; everything below has
    /// real headroom that gets revealed when exposure is pulled down.
    ///
    /// We dither in 8-bit output space before rounding. Without dither, the sRGB
    /// gamma compresses the bright end so that many distinct linear values map to
    /// the same 8-bit byte after high-gain exposure boosts — visible as banding in
    /// smooth gradients (skin, sky). With ±0.5-byte uniform dither and a sub-byte
    /// LUT, the rounding becomes probabilistic and banding turns into fine noise.
    /// </summary>
    public static BitmapSource Render(LinearRawImage raw, double stops)
    {
        int w = raw.Width;
        int h = raw.Height;
        int stride = w * 3;
        byte[] bgr = new byte[h * stride];
        ushort[] src = raw.Pixels;

        double gain = Math.Pow(2.0, stops);
        var lut = LinearToSrgbF;

        // Per-thread XorShift32 — fast, decent entropy, deterministic for repro.
        uint rng = 0x12345678u;

        int srcIdx = 0;
        for (int y = 0; y < h; y++)
        {
            int row = y * stride;
            for (int x = 0; x < w; x++)
            {
                int r = (int)(src[srcIdx] * gain);
                int g = (int)(src[srcIdx + 1] * gain);
                int b = (int)(src[srcIdx + 2] * gain);
                if (r > 65535) r = 65535; else if (r < 0) r = 0;
                if (g > 65535) g = 65535; else if (g < 0) g = 0;
                if (b > 65535) b = 65535; else if (b < 0) b = 0;

                // TPDF dither (sum of two uniforms): triangular distribution in
                // [-1, 1) per channel. Gives ~2x amplitude over uniform ±0.5 and a
                // smoother visual character — strong enough to survive WPF's display
                // scaling without becoming visible noise. Independent per channel
                // keeps the noise neutral-coloured.
                rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
                float ar = (rng & 0xFFFF) * (1f / 65536f) - 0.5f;
                rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
                float br = (rng & 0xFFFF) * (1f / 65536f) - 0.5f;
                float dr = ar + br;
                rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
                float ag = (rng & 0xFFFF) * (1f / 65536f) - 0.5f;
                rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
                float bg = (rng & 0xFFFF) * (1f / 65536f) - 0.5f;
                float dg = ag + bg;
                rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
                float ab = (rng & 0xFFFF) * (1f / 65536f) - 0.5f;
                rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
                float bb = (rng & 0xFFFF) * (1f / 65536f) - 0.5f;
                float db = ab + bb;

                int rv = (int)(lut[r] + dr + 0.5f);
                int gv = (int)(lut[g] + dg + 0.5f);
                int bv = (int)(lut[b] + db + 0.5f);

                int o = row + x * 3;
                bgr[o]     = (byte)(bv < 0 ? 0 : bv > 255 ? 255 : bv);
                bgr[o + 1] = (byte)(gv < 0 ? 0 : gv > 255 ? 255 : gv);
                bgr[o + 2] = (byte)(rv < 0 ? 0 : rv > 255 ? 255 : rv);
                srcIdx += 3;
            }
        }

        var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr24, null, bgr, stride);
        result.Freeze();
        return result;
    }

    private static float[] BuildLinearToSrgbLutF()
    {
        // 65536-entry table mapping 16-bit linear [0..65535] → fractional 8-bit sRGB
        // [0..255]. Fractional output is what makes dither effective: rounding +
        // dither at output time produces sub-byte transitions that the eye averages
        // back into smooth gradients.
        //
        // We blend a smoothstep S-curve into the post-sRGB output to approximate the
        // contrast that camera Picture Styles bake into the embedded JPEG — without
        // it, the linear pipeline looks correct but flat compared to what the user
        // sees during the JPEG-preview phase. Blend factor is a taste call; 0.4 is
        // a moderate Adobe-Standard-ish look without crushing shadows or clipping
        // highlights beyond what the sensor already lost.
        const double contrastBlend = 0.6;

        var lut = new float[65536];
        for (int i = 0; i < 65536; i++)
        {
            double linear = i / 65535.0;
            double srgb = linear <= 0.0031308
                ? 12.92 * linear
                : 1.055 * Math.Pow(linear, 1.0 / 2.4) - 0.055;

            double smooth = srgb * srgb * (3.0 - 2.0 * srgb);
            double curved = srgb * (1.0 - contrastBlend) + smooth * contrastBlend;

            lut[i] = (float)(curved * 255.0);
        }
        return lut;
    }
}
