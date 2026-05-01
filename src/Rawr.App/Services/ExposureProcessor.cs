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
    public static BitmapSource Render(LinearRawImage raw, double stops, CancellationToken ct = default)
    {
        int w = raw.Width;
        int h = raw.Height;
        int n = w * h;
        int stride = w * 3;
        byte[] bgr = new byte[h * stride];
        ushort[] src = raw.Pixels;

        double gain = Math.Pow(2.0, stops);
        var lut = LinearToSrgbF;
        var po = new ParallelOptions { CancellationToken = ct };

        // Decompose post-tone-curve RGB into luma + Rec.709 chroma differences so
        // the chroma planes can be blurred independently. Sensor read noise is
        // roughly equal across channels, but the eye sees it as colour speckle —
        // i.e. it concentrates in chroma. A small box on chroma kills the speckle
        // while leaving luma detail (the part the eye locks onto) untouched.
        float[] luma = new float[n];
        float[] cb = new float[n]; // B - Y
        float[] cr = new float[n]; // R - Y

        Parallel.For(0, h, po, yy =>
        {
            int rowI = yy * w;
            int srcIdx = rowI * 3;
            for (int x = 0; x < w; x++)
            {
                int r = (int)(src[srcIdx] * gain);
                int g = (int)(src[srcIdx + 1] * gain);
                int b = (int)(src[srcIdx + 2] * gain);
                if (r > 65535) r = 65535; else if (r < 0) r = 0;
                if (g > 65535) g = 65535; else if (g < 0) g = 0;
                if (b > 65535) b = 65535; else if (b < 0) b = 0;

                float lr = lut[r];
                float lg = lut[g];
                float lb = lut[b];
                float y = 0.2126f * lr + 0.7152f * lg + 0.0722f * lb;
                int i = rowI + x;
                luma[i] = y;
                cb[i] = lb - y;
                cr[i] = lr - y;
                srcIdx += 3;
            }
        });

        // Separable 5-tap box on chroma only. Radius 2 at preview resolution is
        // strong enough to dissolve high-ISO chroma blotching without bleeding
        // visible colour into adjacent regions.
        BoxBlurSeparable(cb, w, h, 2, po);
        BoxBlurSeparable(cr, w, h, 2, po);

        // Saturation boost folds into the chroma scale: lr = y + cr*sat is
        // algebraically identical to the old luma + (lr - luma)*sat path.
        const float saturation = 1.15f;

        Parallel.For(0, h, po, yy =>
        {
            // Per-row XorShift seed (Knuth multiplicative constant, then offset)
            // — gives each row an independent deterministic dither stream so the
            // result doesn't change with thread scheduling.
            uint rng = unchecked((uint)yy * 2654435761u + 0x12345678u);
            if (rng == 0) rng = 1;

            int row = yy * stride;
            int rowI = yy * w;
            for (int x = 0; x < w; x++)
            {
                int i = rowI + x;
                float y = luma[i];
                float crv = cr[i] * saturation;
                float cbv = cb[i] * saturation;
                float lr = y + crv;
                float lb = y + cbv;
                float lg = (y - 0.2126f * lr - 0.0722f * lb) * (1f / 0.7152f);

                // TPDF dither (sum of two uniforms): triangular distribution in
                // [-1, 1) per channel. Independent per channel keeps the dither
                // noise neutral-coloured.
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

                int rv = (int)(lr + dr + 0.5f);
                int gv = (int)(lg + dg + 0.5f);
                int bv = (int)(lb + db + 0.5f);

                int o = row + x * 3;
                bgr[o]     = (byte)(bv < 0 ? 0 : bv > 255 ? 255 : bv);
                bgr[o + 1] = (byte)(gv < 0 ? 0 : gv > 255 ? 255 : gv);
                bgr[o + 2] = (byte)(rv < 0 ? 0 : rv > 255 ? 255 : rv);
            }
        });

        ct.ThrowIfCancellationRequested();
        var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr24, null, bgr, stride);
        result.Freeze();
        return result;
    }

    // Sliding-window box blur, horizontal then vertical. Edges clamp-extend.
    private static void BoxBlurSeparable(float[] plane, int w, int h, int radius, ParallelOptions po)
    {
        int taps = radius * 2 + 1;
        float inv = 1f / taps;
        var tmp = new float[plane.Length];

        Parallel.For(0, h, po, y =>
        {
            int row = y * w;
            float sum = 0f;
            for (int k = -radius; k <= radius; k++)
            {
                int xc = k < 0 ? 0 : k >= w ? w - 1 : k;
                sum += plane[row + xc];
            }
            for (int x = 0; x < w; x++)
            {
                tmp[row + x] = sum * inv;
                int addX = x + radius + 1;
                int subX = x - radius;
                if (addX > w - 1) addX = w - 1;
                if (subX < 0) subX = 0;
                sum += plane[row + addX] - plane[row + subX];
            }
        });

        Parallel.For(0, w, po, x =>
        {
            float sum = 0f;
            for (int k = -radius; k <= radius; k++)
            {
                int yc = k < 0 ? 0 : k >= h ? h - 1 : k;
                sum += tmp[yc * w + x];
            }
            for (int y = 0; y < h; y++)
            {
                plane[y * w + x] = sum * inv;
                int addY = y + radius + 1;
                int subY = y - radius;
                if (addY > h - 1) addY = h - 1;
                if (subY < 0) subY = 0;
                sum += tmp[addY * w + x] - tmp[subY * w + x];
            }
        });
    }

    private static float[] BuildLinearToSrgbLutF()
    {
        // 65536-entry table mapping 16-bit linear [0..65535] → fractional 8-bit sRGB
        // [0..255]. Fractional output is what makes dither effective: rounding +
        // dither at output time produces sub-byte transitions that the eye averages
        // back into smooth gradients.
        //
        // Tone-curve shape, calibrated against in-camera JPEG previews:
        //   1. sRGB encode (gamma 2.4 piecewise) — undoes the linear exposure space.
        //   2. Midtone gamma lift — sRGB-encoded value^0.78. Camera JPEGs lift
        //      midtones substantially relative to a pure sRGB encode (Picture Style
        //      "Standard" et al.); without this the RAW preview looks markedly
        //      darker than the JPG it replaces (median brightness ~38 vs ~75 in
        //      our reference scenes).
        //   3. Tanh S-curve pivoted at 0.5, blended at 30% — adds contrast in the
        //      midtone region without the shadow-crushing that smoothstep produces
        //      below 0.5. The previous smoothstep-at-60% curve squashed shadows
        //      and dimmed midtones, the opposite of the camera look.
        const double midtoneLift = 0.70;
        const double contrastBlend = 0.65;
        const double tanhSlope = 2.0;
        double tanhNorm = Math.Tanh(tanhSlope);

        var lut = new float[65536];
        for (int i = 0; i < 65536; i++)
        {
            double linear = i / 65535.0;
            double srgb = linear <= 0.0031308
                ? 12.92 * linear
                : 1.055 * Math.Pow(linear, 1.0 / 2.4) - 0.055;

            double lifted = Math.Pow(srgb, midtoneLift);

            double t = lifted * 2.0 - 1.0;
            double sCurve = (Math.Tanh(t * tanhSlope) / tanhNorm + 1.0) * 0.5;

            double curved = lifted * (1.0 - contrastBlend) + sCurve * contrastBlend;

            if (curved < 0.0) curved = 0.0; else if (curved > 1.0) curved = 1.0;
            lut[i] = (float)(curved * 255.0);
        }
        return lut;
    }
}
