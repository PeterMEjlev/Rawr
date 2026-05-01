using System.Numerics;
using Rawr.Core.Models;

namespace Rawr.Core.Services;

/// <summary>
/// Groups photos shot in rapid succession on the same camera into "bursts".
/// Two consecutive same-camera photos are grouped if their EXIF capture times
/// are close enough AND their thumbnails are visually similar (per dHash).
/// Without that visual check, two unrelated shots taken seconds apart would
/// be erroneously grouped just because they happened back-to-back.
/// </summary>
public static class BurstDetector
{
    public static readonly TimeSpan DefaultRelaxedGap = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan DefaultStrictGap = TimeSpan.FromSeconds(10);

    // Default Hamming distance thresholds on the 64-bit dHash. Callers can
    // override via the strictness setting in AppSettings.
    public const int DefaultLooseHammingThreshold = 18;   // 0-2s window
    public const int DefaultStrictHammingThreshold = 10;  // 2-10s window

    // Maps a 0-100 strictness slider to a (loose, strict) Hamming pair.
    // 0 → 32/18 (very permissive), 50 → 16/9 (≈ defaults), 100 → 0/0 (near-identical only).
    public static (int Loose, int Strict) ThresholdsFromStrictness(int strictness)
    {
        var s = Math.Clamp(strictness, 0, 100);
        int loose = (int)Math.Round((100 - s) * 32.0 / 100.0);
        int strict = (int)Math.Round(loose * 0.55);
        return (loose, strict);
    }

    /// <summary>
    /// Assigns <see cref="PhotoItem.GroupId"/> and <see cref="PhotoItem.BurstBadge"/>
    /// for each photo. Returns the number of bursts detected (groups of ≥ 2 photos).
    /// </summary>
    /// <param name="relaxedGap">
    /// Time gap within which photos only need to be loosely similar to group.
    /// Defaults to 2s.
    /// </param>
    /// <param name="strictGap">
    /// Hard upper bound on gap between consecutive shots in a burst. Between
    /// <paramref name="relaxedGap"/> and this, photos must be strongly similar.
    /// Defaults to 10s. If less than relaxedGap, falls back to relaxedGap.
    /// </param>
    public static int Detect(
        IReadOnlyList<PhotoItem> photos,
        TimeSpan? relaxedGap = null,
        TimeSpan? strictGap = null,
        int looseHammingThreshold = DefaultLooseHammingThreshold,
        int strictHammingThreshold = DefaultStrictHammingThreshold)
    {
        var loose = relaxedGap ?? DefaultRelaxedGap;
        var strict = strictGap ?? DefaultStrictGap;
        if (strict < loose) strict = loose;

        foreach (var p in photos)
        {
            p.GroupId = 0;
            p.BurstBadge = "";
        }

        var ordered = photos
            .Where(p => !p.IsVideo && p.Metadata?.CaptureTime.HasValue == true)
            .OrderBy(p => p.Metadata!.CameraModel, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Metadata!.CaptureTime!.Value)
            .ToList();

        int nextGroupId = 0;
        int burstCount = 0;
        var current = new List<PhotoItem>();
        PhotoItem? prev = null;

        void Flush()
        {
            if (current.Count >= 2)
            {
                nextGroupId++;
                burstCount++;
                for (int i = 0; i < current.Count; i++)
                {
                    current[i].GroupId = nextGroupId;
                    current[i].BurstBadge = $"{i + 1}/{current.Count}";
                }
            }
            current.Clear();
        }

        foreach (var p in ordered)
        {
            if (prev is null || !ShouldContinueBurst(prev, p, loose, strict, looseHammingThreshold, strictHammingThreshold))
                Flush();

            current.Add(p);
            prev = p;
        }
        Flush();

        return burstCount;
    }

    private static bool ShouldContinueBurst(
        PhotoItem prev, PhotoItem curr,
        TimeSpan loose, TimeSpan strict,
        int looseHamming, int strictHamming)
    {
        var prevCam = prev.Metadata?.CameraModel ?? "";
        var currCam = curr.Metadata?.CameraModel ?? "";
        if (!string.Equals(prevCam, currCam, StringComparison.OrdinalIgnoreCase))
            return false;

        var dt = curr.Metadata!.CaptureTime!.Value - prev.Metadata!.CaptureTime!.Value;
        if (dt > strict) return false;

        // No hashes yet → preserve legacy time-only grouping inside the relaxed window.
        if (prev.Phash is null || curr.Phash is null)
            return dt <= loose;

        int dist = BitOperations.PopCount(prev.Phash.Value ^ curr.Phash.Value);
        return dt <= loose
            ? dist <= looseHamming
            : dist <= strictHamming;
    }
}
