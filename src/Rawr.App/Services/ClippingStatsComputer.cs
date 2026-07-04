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
        var decoded = ThumbnailPixels.DecodeBgr24(jpegBytes)
            ?? throw new InvalidOperationException("thumbnail decode failed");
        return Compute(decoded.Bgr, decoded.Width, decoded.Height, decoded.Stride, thresholdPct);
    }

    /// <summary>
    /// Overload for callers that have already decoded the thumbnail to a packed
    /// Bgr24 buffer (the folder-indexing pass shares one decode with the perceptual
    /// hash). <paramref name="stride"/> is bytes per row (width*3 for packed input).
    /// </summary>
    public static Stats Compute(byte[] bgr, int width, int height, int stride, byte thresholdPct)
    {
        int hiCut = (int)Math.Round(thresholdPct * 255.0 / 100.0);
        int loCut = 255 - hiCut;

        int total = width * height;
        if (total <= 0) return new Stats(0f, 0f);

        int highlightHits = 0;
        int shadowHits = 0;
        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            for (int x = 0; x < width; x++)
            {
                int i = row + x * 3;
                byte b = bgr[i];
                byte g = bgr[i + 1];
                byte r = bgr[i + 2];
                if (r >= hiCut || g >= hiCut || b >= hiCut)
                    highlightHits++;
                else if (r <= loCut && g <= loCut && b <= loCut)
                    shadowHits++;
            }
        }

        return new Stats(
            HighlightPct: 100f * highlightHits / total,
            ShadowPct:    100f * shadowHits    / total);
    }
}
