namespace Rawr.Core.Services;

/// <summary>
/// Estimates how far the photographer translated the camera between two
/// adjacent frames using sum-of-absolute-differences over small grayscale
/// buffers. The output is a (dx, dy) shift expressed as a fraction of the
/// image dimensions — so a 30%-right pan returns roughly (0.30, 0).
///
/// Used by <see cref="PanoramaDetector"/> to distinguish panorama sweeps
/// (consistent moderate shift) from bursts (near-zero shift) and from
/// random unrelated shots (no consistent shift).
/// </summary>
public static class FrameShiftEstimator
{
    /// <summary>Maximum search range, expressed as a fraction of width/height.</summary>
    public const float MaxSearchFraction = 0.85f;

    /// <summary>Minimum required overlap area, as a fraction of total pixels.</summary>
    public const float MinOverlapFraction = 0.15f;

    /// <summary>
    /// Returns the camera-pan shift between A and B as fractions of the buffer
    /// dimensions, or null if no plausible alignment exists. Positive dx means
    /// B's content lies to the right of A's (camera panned right between shots).
    /// </summary>
    /// <param name="a">First frame's grayscale buffer (row-major, width*height bytes).</param>
    /// <param name="b">Second frame's grayscale buffer.</param>
    /// <param name="width">Buffer width in pixels (must match for both buffers).</param>
    /// <param name="height">Buffer height in pixels.</param>
    public static (float Dx, float Dy)? Estimate(byte[] a, byte[] b, int width, int height)
    {
        if (a.Length != width * height || b.Length != width * height) return null;

        int maxDx = (int)(width * MaxSearchFraction);
        int maxDy = (int)(height * MaxSearchFraction);
        int minOverlapArea = (int)(width * height * MinOverlapFraction);

        long bestScore = long.MaxValue;
        int bestDx = 0;
        int bestDy = 0;
        bool found = false;

        // Positive shift = camera panned right. We align A's pixel (x, y) with
        // B's pixel (x - dx, y - dy); the overlap region is where both fall
        // inside their respective buffers.
        for (int dy = -maxDy; dy <= maxDy; dy++)
        {
            int ay0 = Math.Max(0, dy);
            int ay1 = Math.Min(height, height + dy);
            int rows = ay1 - ay0;
            if (rows <= 0) continue;

            for (int dx = -maxDx; dx <= maxDx; dx++)
            {
                int ax0 = Math.Max(0, dx);
                int ax1 = Math.Min(width, width + dx);
                int cols = ax1 - ax0;
                if (cols <= 0) continue;

                int area = rows * cols;
                if (area < minOverlapArea) continue;

                long sum = 0;
                for (int y = ay0; y < ay1; y++)
                {
                    int aRow = y * width;
                    int bRow = (y - dy) * width;
                    for (int x = ax0; x < ax1; x++)
                    {
                        int diff = a[aRow + x] - b[bRow + x - dx];
                        sum += diff < 0 ? -diff : diff;
                    }
                }

                // Normalise by area so big-overlap matches don't dominate. The
                // x1024 keeps integer arithmetic accurate without floats.
                long score = sum * 1024 / area;
                if (score < bestScore)
                {
                    bestScore = score;
                    bestDx = dx;
                    bestDy = dy;
                    found = true;
                }
            }
        }

        if (!found) return null;
        return ((float)bestDx / width, (float)bestDy / height);
    }
}
