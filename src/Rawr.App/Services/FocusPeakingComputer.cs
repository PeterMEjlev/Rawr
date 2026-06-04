using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Rawr.App.Services;

public enum GradientOperator { Scharr, Sobel }

// Tenengrad   = gradient magnitude only (stable, the live-preview default).
// Laplacian   = |second derivative| only (crisp detail, noise-sensitive).
// Hybrid      = gradient magnitude boosted by normalised |Laplacian|.
public enum FocusMeasureMode { Tenengrad, Laplacian, Hybrid }

// MedianAbsoluteDeviation is the robust default; MeanStdDev is the cheaper
// single-pass fallback the brief lists as an alternative.
public enum ThresholdMethod { MedianAbsoluteDeviation, MeanStdDev }

public enum OverlayColorMode { SingleColor, HeatMap }

/// <summary>
/// Central, tunable configuration for <see cref="FocusPeakingComputer"/>. One
/// object holds every knob the adaptive pipeline exposes so the math can be
/// retuned in one place (and persisted via <c>AppSettings.FocusPeaking</c>).
/// </summary>
public sealed class FocusPeakingOptions
{
    // Analysis is done on a downscaled luminance copy; the overlay is scaled
    // back up by the WPF render transforms. 1024 keeps fine detail while
    // staying cheap enough to recompute on every photo switch.
    public int AnalysisWidth { get; set; } = 1024;

    // 0 = no denoise, 1 = 3×3 Gaussian (default), 2 = 5×5. Focus peaking needs
    // fine detail, so we never blur harder than this.
    public int DenoiseRadius { get; set; } = 1;

    public GradientOperator Operator { get; set; } = GradientOperator.Scharr;
    public FocusMeasureMode Mode { get; set; } = FocusMeasureMode.Tenengrad;
    public ThresholdMethod ThresholdMethod { get; set; } = ThresholdMethod.MedianAbsoluteDeviation;

    // Confidence thresholds, expressed as multiples of the robust spread (MAD or
    // std-dev) above the median/mean. The brief's suggested defaults: 3 / 5 / 7
    // for MAD. The "strictness" slider shifts all three up or down adaptively.
    public double WeakMultiplier { get; set; } = 3.0;
    public double MediumMultiplier { get; set; } = 5.0;
    public double StrongMultiplier { get; set; } = 7.0;

    // Absolute sharpness floor on the focus-score scale (≈ per-pixel luminance
    // gradient for Tenengrad/Scharr). A pixel must clear BOTH this floor and the
    // adaptive threshold to count as a peak. The median+MAD threshold is purely
    // *relative*, so on a globally out-of-focus frame it still flags whatever is
    // relatively sharpest (soft high-contrast ramps). This fixed floor is what
    // distinguishes "actually in focus" from "the least-blurry blur": raise it
    // until soft/OOF frames stop lighting up. 0 disables it (pure adaptive).
    // The scale is mode-dependent — enable Debug to read peakScore and tune.
    public double AbsoluteMinScore { get; set; } = 30.0;

    // For Hybrid mode: how strongly a crisp Laplacian boosts the gradient score.
    // score = gradient * (1 + LaplacianBoost * normalisedLaplacian).
    public double LaplacianBoost { get; set; } = 1.0;

    // ── Highlight (bokeh / specular) suppression ──
    // A defocused bright blob has a high-contrast rim that clears the gradient
    // floor even though it's out of focus. Raising the global floor would hurt
    // darker scenes, so instead we key off brightness: when the brightest luma
    // within HighlightRadius px of a pixel exceeds HighlightCutoff (1..255), its
    // focus score is scaled toward HighlightSuppression (0 = drop entirely,
    // 1 = no change), rolling off smoothly from the cutoff up to pure white.
    // HighlightCutoff = 0 disables the test. Trade-off: also dampens genuinely
    // sharp detail abutting a blown highlight (white text on black, sun glints).
    public int HighlightCutoff { get; set; } = 150;             // 0 = off; try ~225
    public double HighlightSuppression { get; set; } = 0.10;
    public int HighlightRadius { get; set; } = 8;         // how far a rim "sees" into a blob

    public int MinBlobSize { get; set; } = 4;     // drop isolated speckle smaller than this
    public int DilationRadius { get; set; } = 1;   // 1–2 px so peaks are visible, not chunky

    public OverlayColorMode ColorMode { get; set; } = OverlayColorMode.SingleColor;
    public byte ColorR { get; set; } = 255;        // single-colour overlay (default red)
    public byte ColorG { get; set; }
    public byte ColorB { get; set; }
    public double OverlayOpacity { get; set; } = 1.0; // 0..1 master alpha scale

    // Emits per-frame threshold values and peak-pixel counts via Debug.WriteLine.
    public bool Debug { get; set; }

    public FocusPeakingOptions Clone() => (FocusPeakingOptions)MemberwiseClone();
}

/// <summary>
/// Adaptive focus-peaking detector.
///
/// Pipeline: luminance → light Gaussian denoise → Tenengrad/Laplacian focus
/// score (kept in float, never clamped to 8-bit mid-stream) → robust adaptive
/// thresholds (median + k·MAD) → 3-level confidence mask → speckle removal →
/// small dilation → coloured overlay.
///
/// Why adaptive thresholds: edge strength varies enormously with scene
/// contrast, ISO and exposure, so any *fixed* cutoff either floods low-contrast
/// shots with nothing or buries high-contrast shots in noise. Anchoring the
/// thresholds to the image's own median and median-absolute-deviation makes the
/// detector self-calibrate per frame — at high ISO the noise floor lifts the
/// median/MAD, which automatically raises the bar so only genuinely sharp
/// detail survives.
/// </summary>
public static class FocusPeakingComputer
{
    /// <param name="strictness">
    /// 10–100 user slider. Shifts all three confidence multipliers up (stricter,
    /// fewer peaks) or down (looser, more peaks) in robust-spread units.
    /// </param>
    public static BitmapSource Compute(byte[] jpegBytes, int strictness, FocusPeakingOptions? options = null)
    {
        var opt = options ?? new FocusPeakingOptions();

        // ── Decode (with EXIF orientation) to oriented Bgra32 ───────────────
        double rotation = ReadExifRotation(jpegBytes);

        var bi = new BitmapImage();
        bi.BeginInit();
        bi.StreamSource = new MemoryStream(jpegBytes);
        bi.DecodePixelWidth = Math.Max(64, opt.AnalysisWidth);
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

        var bgra = oriented.Format == PixelFormats.Bgra32
            ? oriented
            : new FormatConvertedBitmap(oriented, PixelFormats.Bgra32, null, 0);
        if (bgra is FormatConvertedBitmap fcb) fcb.Freeze();

        int w = bgra.PixelWidth;
        int h = bgra.PixelHeight;
        int stride = w * 4;
        if (w < 3 || h < 3) return EmptyOverlay(w, h);

        byte[] pixels = new byte[h * stride];
        bgra.CopyPixels(pixels, stride, 0);

        // ── 1. Luminance (Rec.601 weights, kept as float) ──────────────────
        float[] luma = new float[w * h];
        for (int i = 0, p = 0; i < w * h; i++, p += 4)
        {
            // Bgra32 byte order: B, G, R, A
            luma[i] = 0.114f * pixels[p] + 0.587f * pixels[p + 1] + 0.299f * pixels[p + 2];
        }

        // ── 2. Light Gaussian denoise (configurable; never over-blur) ───────
        if (opt.DenoiseRadius > 0)
            luma = GaussianBlur(luma, w, h, opt.DenoiseRadius);

        // ── 3+4. Focus score (Tenengrad / Laplacian / Hybrid), in float ────
        float[] score = ComputeScore(luma, w, h, opt);

        // ── 4b. Knock down peaks riding the rim of a bright defocused blob
        // (bokeh / specular). These rims are high-contrast but out of focus;
        // keying off nearby near-white luminance removes them without raising the
        // global floor (which would over-suppress darker, in-focus scenes).
        if (opt.HighlightCutoff > 0)
            SuppressHighlights(score, luma, w, h, opt);

        // ── 5. Robust adaptive thresholds ──────────────────────────────────
        double center, spread;
        if (opt.ThresholdMethod == ThresholdMethod.MedianAbsoluteDeviation)
            (center, spread) = MedianMad(score, w, h);
        else
            (center, spread) = MeanStdDev(score, w, h);

        // Strictness re-centres the multipliers: slider 50 is neutral, every 25
        // points adds/removes one spread-unit from each confidence band.
        double shift = (strictness - 50) / 25.0;
        double weakK = Math.Max(0.5, opt.WeakMultiplier + shift);
        double medK = Math.Max(0.5, opt.MediumMultiplier + shift);
        double strongK = Math.Max(0.5, opt.StrongMultiplier + shift);

        // Combine the adaptive (relative) threshold with the absolute floor, so a
        // peak must be both an outlier in this frame AND genuinely sharp. On an
        // all-blurry frame the floor dominates and nothing passes.
        double floor = Math.Max(0.0, opt.AbsoluteMinScore);
        double weakT = Math.Max(center + weakK * spread, floor);
        double medT = Math.Max(center + medK * spread, floor);
        double strongT = Math.Max(center + strongK * spread, floor);

        // ── 6. Confidence mask (1 = weak, 2 = medium, 3 = strong) ──────────
        byte[] mask = new byte[w * h];
        int cWeak = 0, cMed = 0, cStrong = 0;
        // Skip only when there's nothing to threshold against: a near-flat frame
        // (spread ≈ 0) with no floor set would otherwise flood from the degenerate
        // "everything above the median" case.
        if (spread > 1e-6 || floor > 0)
        {
            for (int i = 0; i < w * h; i++)
            {
                float s = score[i];
                if (s < weakT) continue;          // below every gate (floor included)
                if (s >= strongT) { mask[i] = 3; cStrong++; }
                else if (s >= medT) { mask[i] = 2; cMed++; }
                else { mask[i] = 1; cWeak++; }
            }
        }

        // ── 7. Clean up: drop speckle, then a small dilation for visibility ─
        if (opt.MinBlobSize > 1)
            RemoveSmallBlobs(mask, w, h, opt.MinBlobSize);
        if (opt.DilationRadius > 0)
            mask = DilateMax(mask, w, h, opt.DilationRadius);

        // ── 8. Compose the BGRA overlay ────────────────────────────────────
        byte[] overlay = ComposeOverlay(mask, w, h, opt);

        if (opt.Debug)
        {
            int drawn = 0;
            for (int i = 0; i < mask.Length; i++) if (mask[i] != 0) drawn++;
            float peak = 0f;
            for (int i = 0; i < score.Length; i++) if (score[i] > peak) peak = score[i];
            string spreadName = opt.ThresholdMethod == ThresholdMethod.MedianAbsoluteDeviation ? "MAD" : "std";
            Debug.WriteLine(
                $"[FocusPeaking] {w}x{h} mode={opt.Mode} op={opt.Operator} " +
                $"center={center:F2} {spreadName}={spread:F2} peakScore={peak:F1} floor={floor:F1} " +
                $"T(weak/med/strong)={weakT:F1}/{medT:F1}/{strongT:F1} " +
                $"peaks(weak/med/strong)={cWeak}/{cMed}/{cStrong} drawn={drawn}");
        }

        var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, overlay, stride);
        result.Freeze();
        return result;
    }

    // ── EXIF orientation → display rotation ────────────────────────────────
    private static double ReadExifRotation(byte[] jpegBytes)
    {
        try
        {
            using var ms = new MemoryStream(jpegBytes);
            var meta = BitmapDecoder.Create(ms, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None)
                           .Frames[0].Metadata as BitmapMetadata;
            return Convert.ToInt32(meta?.GetQuery("/app1/ifd/{ushort=274}")) switch
            {
                3 => 180.0,
                6 => 90.0,
                8 => 270.0,
                _ => 0.0
            };
        }
        catch { return 0.0; }
    }

    // ── 2. Separable Gaussian, float in/out ────────────────────────────────
    private static float[] GaussianBlur(float[] src, int w, int h, int radius)
    {
        // radius 1 → [1 2 1], radius 2 → [1 4 6 4 1]; both are binomial kernels.
        int[] k = radius >= 2 ? new[] { 1, 4, 6, 4, 1 } : new[] { 1, 2, 1 };
        int half = k.Length / 2;
        int kw = 0; foreach (var v in k) kw += v;
        float inv = 1f / kw;

        float[] tmp = new float[w * h];
        // horizontal
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                float acc = 0;
                for (int t = -half; t <= half; t++)
                {
                    int xx = Math.Clamp(x + t, 0, w - 1);
                    acc += src[row + xx] * k[t + half];
                }
                tmp[row + x] = acc * inv;
            }
        }
        // vertical
        float[] dst = new float[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float acc = 0;
                for (int t = -half; t <= half; t++)
                {
                    int yy = Math.Clamp(y + t, 0, h - 1);
                    acc += tmp[yy * w + x] * k[t + half];
                }
                dst[y * w + x] = acc * inv;
            }
        }
        return dst;
    }

    // ── 4b. Highlight suppression ──────────────────────────────────────────
    private static void SuppressHighlights(float[] score, float[] luma, int w, int h, FocusPeakingOptions opt)
    {
        int radius = Math.Max(1, opt.HighlightRadius);
        int cutoff = opt.HighlightCutoff;
        float floorFactor = (float)Math.Clamp(opt.HighlightSuppression, 0.0, 1.0);
        float span = Math.Max(1f, 255f - cutoff);

        // localMax lets a rim pixel "see" the bright blob it borders; the score is
        // ramped from ×1 at the cutoff down to ×floorFactor at pure white.
        float[] localMax = LocalMax(luma, w, h, radius);
        for (int i = 0; i < score.Length; i++)
        {
            float lm = localMax[i];
            if (lm <= cutoff) continue;
            float t = (lm - cutoff) / span;               // 0 at cutoff, 1 at white
            score[i] *= 1f - t * (1f - floorFactor);      // ×1 → ×floorFactor
        }
    }

    // Separable window maximum (radius px), float in/out.
    private static float[] LocalMax(float[] src, int w, int h, int radius)
    {
        float[] tmp = new float[w * h];
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int x0 = Math.Max(0, x - radius), x1 = Math.Min(w - 1, x + radius);
                float m = 0f;
                for (int xx = x0; xx <= x1; xx++)
                    if (src[row + xx] > m) m = src[row + xx];
                tmp[row + x] = m;
            }
        }
        float[] dst = new float[w * h];
        for (int y = 0; y < h; y++)
        {
            int y0 = Math.Max(0, y - radius), y1 = Math.Min(h - 1, y + radius);
            for (int x = 0; x < w; x++)
            {
                float m = 0f;
                for (int yy = y0; yy <= y1; yy++)
                {
                    float v = tmp[yy * w + x];
                    if (v > m) m = v;
                }
                dst[y * w + x] = m;
            }
        }
        return dst;
    }

    // ── 3+4. Focus score ───────────────────────────────────────────────────
    private static float[] ComputeScore(float[] y, int w, int h, FocusPeakingOptions opt)
    {
        float[] score = new float[w * h];

        // Laplacian-only: |∇²Y|. Crisp but noise-sensitive (hence kept optional).
        if (opt.Mode == FocusMeasureMode.Laplacian)
        {
            for (int yy = 1; yy < h - 1; yy++)
            {
                int rp = (yy - 1) * w, rc = yy * w, rn = (yy + 1) * w;
                for (int x = 1; x < w - 1; x++)
                {
                    float lap = 4f * y[rc + x] - y[rp + x] - y[rn + x] - y[rc + x - 1] - y[rc + x + 1];
                    score[rc + x] = Math.Abs(lap);
                }
            }
            return score;
        }

        // Tenengrad / Hybrid: gradient magnitude from Scharr (default) or Sobel.
        // Scharr has better rotational symmetry than Sobel → steadier magnitude.
        bool scharr = opt.Operator == GradientOperator.Scharr;
        float a = scharr ? 3f : 1f;   // corner weight
        float b = scharr ? 10f : 2f;  // edge weight
        float norm = scharr ? 1f / 16f : 1f / 4f; // keep magnitude on the luminance scale

        bool hybrid = opt.Mode == FocusMeasureMode.Hybrid;
        float[]? lapAbs = hybrid ? new float[w * h] : null;
        float lapMax = 0f;

        for (int yy = 1; yy < h - 1; yy++)
        {
            int rp = (yy - 1) * w, rc = yy * w, rn = (yy + 1) * w;
            for (int x = 1; x < w - 1; x++)
            {
                float tl = y[rp + x - 1], tc = y[rp + x], tr = y[rp + x + 1];
                float ml = y[rc + x - 1], mr = y[rc + x + 1];
                float bl = y[rn + x - 1], bc = y[rn + x], br = y[rn + x + 1];

                float gx = a * (tr - tl) + b * (mr - ml) + a * (br - bl);
                float gy = a * (bl - tl) + b * (bc - tc) + a * (br - tr);
                float mag = MathF.Sqrt(gx * gx + gy * gy) * norm;
                score[rc + x] = mag;

                if (hybrid)
                {
                    float lap = MathF.Abs(4f * y[rc + x] - tc - bc - ml - mr);
                    lapAbs![rc + x] = lap;
                    if (lap > lapMax) lapMax = lap;
                }
            }
        }

        if (hybrid && lapMax > 1e-6f)
        {
            // score = gradient · (1 + boost · normalisedLaplacian). The Laplacian
            // only ever *amplifies* genuinely crisp detail; it can't create a peak
            // on its own, which keeps the noisy second derivative from dominating.
            float invLapMax = 1f / lapMax;
            float boost = (float)opt.LaplacianBoost;
            for (int i = 0; i < w * h; i++)
                score[i] *= 1f + boost * (lapAbs![i] * invLapMax);
        }

        return score;
    }

    // ── 5. Robust statistics (interior only; 1-px gradient border is zero) ──
    private static (double center, double spread) MedianMad(float[] s, int w, int h)
    {
        const int Bins = 1024;

        double max = 0;
        for (int y = 1; y < h - 1; y++)
        {
            int row = y * w;
            for (int x = 1; x < w - 1; x++)
                if (s[row + x] > max) max = s[row + x];
        }
        if (max <= 0) return (0, 0);

        // Median via histogram (O(n), no big sort/allocation).
        int[] hist = new int[Bins];
        double scale = (Bins - 1) / max;
        long total = 0;
        for (int y = 1; y < h - 1; y++)
        {
            int row = y * w;
            for (int x = 1; x < w - 1; x++)
            {
                int bin = (int)(s[row + x] * scale);
                if (bin >= Bins) bin = Bins - 1; else if (bin < 0) bin = 0;
                hist[bin]++; total++;
            }
        }
        double median = Percentile(hist, total, scale);

        // MAD = median(|score − median|), the robust analogue of std-dev.
        double maxDev = Math.Max(median, max - median);
        if (maxDev <= 0) return (median, 0);
        int[] devHist = new int[Bins];
        double devScale = (Bins - 1) / maxDev;
        for (int y = 1; y < h - 1; y++)
        {
            int row = y * w;
            for (int x = 1; x < w - 1; x++)
            {
                int bin = (int)(Math.Abs(s[row + x] - median) * devScale);
                if (bin >= Bins) bin = Bins - 1; else if (bin < 0) bin = 0;
                devHist[bin]++;
            }
        }
        double mad = Percentile(devHist, total, devScale);
        // 1.4826·MAD ≈ σ for normal data; we keep raw MAD to match the brief's
        // 3/5/7 multipliers, which were chosen against raw MAD.
        return (median, mad);
    }

    private static double Percentile(int[] hist, long total, double scale)
    {
        long target = total / 2;
        long acc = 0;
        for (int i = 0; i < hist.Length; i++)
        {
            acc += hist[i];
            if (acc >= target) return i / scale;
        }
        return (hist.Length - 1) / scale;
    }

    private static (double center, double spread) MeanStdDev(float[] s, int w, int h)
    {
        double sum = 0, sumSq = 0;
        long n = 0;
        for (int y = 1; y < h - 1; y++)
        {
            int row = y * w;
            for (int x = 1; x < w - 1; x++)
            {
                double v = s[row + x];
                sum += v; sumSq += v * v; n++;
            }
        }
        if (n == 0) return (0, 0);
        double mean = sum / n;
        double var = Math.Max(0, sumSq / n - mean * mean);
        return (mean, Math.Sqrt(var));
    }

    // ── 7a. Connected-component speckle removal (8-connectivity) ───────────
    private static void RemoveSmallBlobs(byte[] mask, int w, int h, int minSize)
    {
        int n = w * h;
        bool[] seen = new bool[n];
        int[] stack = new int[n];
        int[] comp = new int[n];

        for (int start = 0; start < n; start++)
        {
            if (mask[start] == 0 || seen[start]) continue;
            int sp = 0, cc = 0;
            stack[sp++] = start; seen[start] = true;
            while (sp > 0)
            {
                int q = stack[--sp];
                comp[cc++] = q;
                int qx = q % w, qy = q / w;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int ny = qy + dy;
                    if (ny < 0 || ny >= h) continue;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = qx + dx;
                        if (nx < 0 || nx >= w) continue;
                        int nb = ny * w + nx;
                        if (mask[nb] == 0 || seen[nb]) continue;
                        seen[nb] = true;
                        stack[sp++] = nb;
                    }
                }
            }
            if (cc < minSize)
                for (int i = 0; i < cc; i++) mask[comp[i]] = 0;
        }
    }

    // ── 7b. Separable max-filter dilation (preserves confidence ranking) ───
    private static byte[] DilateMax(byte[] mask, int w, int h, int radius)
    {
        byte[] tmp = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                byte m = 0;
                for (int t = -radius; t <= radius; t++)
                {
                    int xx = x + t;
                    if (xx < 0 || xx >= w) continue;
                    if (mask[row + xx] > m) m = mask[row + xx];
                }
                tmp[row + x] = m;
            }
        }
        byte[] dst = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                byte m = 0;
                for (int t = -radius; t <= radius; t++)
                {
                    int yy = y + t;
                    if (yy < 0 || yy >= h) continue;
                    byte v = tmp[yy * w + x];
                    if (v > m) m = v;
                }
                dst[y * w + x] = m;
            }
        }
        return dst;
    }

    // ── 8. Overlay composition ─────────────────────────────────────────────
    private static byte[] ComposeOverlay(byte[] mask, int w, int h, FocusPeakingOptions opt)
    {
        int stride = w * 4;
        byte[] overlay = new byte[h * stride];
        double op = Math.Clamp(opt.OverlayOpacity, 0.0, 1.0);

        // Per-confidence alpha so weak peaks read dimmer than strong ones, even
        // in single-colour mode.
        byte aWeak = (byte)(op * 110);
        byte aMed = (byte)(op * 170);
        byte aStrong = (byte)(op * 235);

        for (int i = 0; i < w * h; i++)
        {
            byte lvl = mask[i];
            if (lvl == 0) continue;

            byte r, g, b, alpha;
            if (opt.ColorMode == OverlayColorMode.HeatMap)
            {
                // green → amber → red as confidence rises.
                (r, g, b) = lvl switch
                {
                    3 => ((byte)255, (byte)0, (byte)0),
                    2 => ((byte)255, (byte)190, (byte)0),
                    _ => ((byte)0, (byte)210, (byte)0),
                };
            }
            else
            {
                r = opt.ColorR; g = opt.ColorG; b = opt.ColorB;
            }
            alpha = lvl switch { 3 => aStrong, 2 => aMed, _ => aWeak };

            int p = i * 4;
            overlay[p] = b;
            overlay[p + 1] = g;
            overlay[p + 2] = r;
            overlay[p + 3] = alpha;
        }
        return overlay;
    }

    private static BitmapSource EmptyOverlay(int w, int h)
    {
        w = Math.Max(1, w); h = Math.Max(1, h);
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, new byte[w * h * 4], w * 4);
        bmp.Freeze();
        return bmp;
    }
}
