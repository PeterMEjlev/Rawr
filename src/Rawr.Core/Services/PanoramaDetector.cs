using Rawr.Core.Models;

namespace Rawr.Core.Services;

/// <summary>
/// Identifies panorama sweeps among temporally-adjacent same-camera shots.
/// Independent of <see cref="BurstDetector"/> because pano frames have ~50%
/// overlap — too dissimilar for the burst threshold to keep them grouped.
///
/// Algorithm: for each adjacent pair within a generous time gap, estimate
/// the camera-pan shift via <see cref="FrameShiftEstimator"/>. Chain edges
/// where the shift magnitude lies in a panorama-plausible range AND the
/// direction stays consistent across the chain. Runs of ≥3 frames are
/// classified as panoramas.
/// </summary>
public static class PanoramaDetector
{
    public const int DefaultMinChainSize = 3;
    public const int DefaultMaxGapSeconds = 20;
    public const float DefaultMinShift = 0.15f;
    public const float DefaultMaxShift = 0.80f;
    public const float DefaultMaxDirectionDeltaDegrees = 30f;

    public sealed record Result(IReadOnlyList<IReadOnlyList<PhotoItem>> Sequences);

    /// <summary>
    /// Sets <see cref="PhotoItem.IsPanorama"/> on every photo and returns the
    /// detected sequences in order. Photos without metadata, capture time, or
    /// a grayscale buffer are skipped — they can't be analysed.
    /// </summary>
    /// <param name="minChainSize">Minimum number of frames that must chain together.</param>
    /// <param name="maxGapSeconds">Maximum gap between adjacent frames in the same panorama.</param>
    /// <param name="minShift">Inter-frame shift below this magnitude (fraction of image dim)
    /// is treated as a regular burst, not a panorama step.</param>
    /// <param name="maxShift">Above this magnitude the frames are too disjoint to chain.</param>
    /// <param name="maxDirectionDeltaDegrees">Maximum allowed direction change between
    /// consecutive panorama edges relative to the running-mean direction.</param>
    public static Result Detect(
        IReadOnlyList<PhotoItem> photos,
        int gridWidth,
        int gridHeight,
        int minChainSize = DefaultMinChainSize,
        int maxGapSeconds = DefaultMaxGapSeconds,
        float minShift = DefaultMinShift,
        float maxShift = DefaultMaxShift,
        float maxDirectionDeltaDegrees = DefaultMaxDirectionDeltaDegrees)
    {
        if (minChainSize < 2) minChainSize = 2;
        if (maxGapSeconds < 1) maxGapSeconds = 1;
        if (minShift < 0f) minShift = 0f;
        if (maxShift > 0.99f) maxShift = 0.99f;
        if (maxShift <= minShift) maxShift = minShift + 0.01f;
        if (maxDirectionDeltaDegrees < 0f) maxDirectionDeltaDegrees = 0f;
        if (maxDirectionDeltaDegrees > 180f) maxDirectionDeltaDegrees = 180f;
        var maxGap = TimeSpan.FromSeconds(maxGapSeconds);

        foreach (var p in photos) p.IsPanorama = false;

        var ordered = photos
            .Where(p => !p.IsVideo
                     && p.Metadata?.CaptureTime.HasValue == true
                     && p.GrayBuffer != null
                     && p.GrayBuffer.Length == gridWidth * gridHeight)
            .OrderBy(p => p.Metadata!.CameraModel, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Metadata!.CaptureTime!.Value)
            .ToList();

        var sequences = new List<List<PhotoItem>>();
        var current = new List<PhotoItem>();
        // Direction is tracked as the running mean unit-vector across all edges
        // in the current chain. Comparing each new edge against the mean keeps
        // a slow drift coherent (each step rotates the mean a little).
        float meanUx = 0, meanUy = 0;
        int edgeCount = 0;

        void Flush()
        {
            if (current.Count >= minChainSize)
            {
                foreach (var p in current) p.IsPanorama = true;
                sequences.Add(current.ToList());
            }
            current.Clear();
            meanUx = 0;
            meanUy = 0;
            edgeCount = 0;
        }

        for (int i = 0; i < ordered.Count - 1; i++)
        {
            var a = ordered[i];
            var b = ordered[i + 1];

            // Camera change → previous chain ends, no edge across the boundary.
            if (!string.Equals(
                    a.Metadata!.CameraModel,
                    b.Metadata!.CameraModel,
                    StringComparison.OrdinalIgnoreCase))
            {
                Flush();
                continue;
            }

            var dt = b.Metadata!.CaptureTime!.Value - a.Metadata!.CaptureTime!.Value;
            if (dt > maxGap)
            {
                Flush();
                continue;
            }

            var shift = FrameShiftEstimator.Estimate(
                a.GrayBuffer!, b.GrayBuffer!, gridWidth, gridHeight);
            if (shift is null) { Flush(); continue; }

            float dx = shift.Value.Dx;
            float dy = shift.Value.Dy;
            float mag = MathF.Sqrt(dx * dx + dy * dy);

            if (mag < minShift || mag > maxShift) { Flush(); continue; }

            float ux = dx / mag;
            float uy = dy / mag;

            if (edgeCount > 0)
            {
                // cos(angle) between this edge and the running mean direction.
                float meanMag = MathF.Sqrt(meanUx * meanUx + meanUy * meanUy);
                if (meanMag < 1e-6f) { Flush(); current.Add(a); meanUx = ux; meanUy = uy; edgeCount = 1; current.Add(b); continue; }
                float cos = (ux * meanUx + uy * meanUy) / meanMag;
                cos = Math.Clamp(cos, -1f, 1f);
                float angleDeg = MathF.Acos(cos) * 180f / MathF.PI;
                if (angleDeg > maxDirectionDeltaDegrees) { Flush(); continue; }
            }

            if (current.Count == 0) current.Add(a);
            current.Add(b);

            meanUx += ux;
            meanUy += uy;
            edgeCount++;
        }
        Flush();

        return new Result(sequences);
    }
}
