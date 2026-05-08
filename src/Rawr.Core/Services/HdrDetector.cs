using System.Numerics;
using Rawr.Core.Models;

namespace Rawr.Core.Services;

/// <summary>
/// Identifies which already-grouped bursts are actually HDR / auto-bracket
/// sequences. Runs as a post-pass after <see cref="BurstDetector"/>: brackets
/// look like bursts (tight timing, near-identical framing) so they're already
/// grouped — this just decides which of those groups deserve the HDR tag.
///
/// Signal: members of the same burst with a meaningful spread of exposure
/// values and tightly matching framing (perceptual hash). A handheld bracket
/// shows the same scene at different exposures; a regular burst shows slightly
/// different moments at the same exposure.
/// </summary>
public static class HdrDetector
{
    /// <summary>Default minimum bracket size. Most cameras shoot 3/5/7.</summary>
    public const int DefaultMinBracketSize = 3;

    /// <summary>Default minimum spread of exposure scores (in stops).</summary>
    public const float DefaultMinExposureSpread = 0.9f;

    /// <summary>
    /// Maximum allowed Hamming distance between any two frames' dHashes inside a bracket.
    /// Brackets shot of the same scene should look almost identical apart from brightness;
    /// this is much tighter than the burst-grouping threshold. Internal — too low-level
    /// to surface in the settings UI; the user-facing knobs are bracket size + spread.
    /// </summary>
    public const int MaxBracketHamming = 12;

    /// <summary>
    /// Sets <see cref="PhotoItem.IsHdr"/> on every photo and returns the file names
    /// that belong to detected HDR bursts. Photos with GroupId == 0 are skipped.
    /// </summary>
    public static HashSet<string> Detect(
        IReadOnlyList<PhotoItem> photos,
        int minBracketSize = DefaultMinBracketSize,
        float minExposureSpread = DefaultMinExposureSpread)
    {
        var hdrFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in photos)
            p.IsHdr = false;

        if (minBracketSize < 2) minBracketSize = 2;
        if (minExposureSpread < 0f) minExposureSpread = 0f;

        var byGroup = photos
            .Where(p => p.GroupId > 0)
            .GroupBy(p => p.GroupId);

        foreach (var group in byGroup)
        {
            var members = group.ToList();
            if (!IsHdrBracket(members, minBracketSize, minExposureSpread)) continue;

            foreach (var p in members)
            {
                p.IsHdr = true;
                hdrFiles.Add(p.FileName);
            }
        }

        return hdrFiles;
    }

    private static bool IsHdrBracket(
        IReadOnlyList<PhotoItem> members,
        int minBracketSize,
        float minExposureSpread)
    {
        if (members.Count < minBracketSize) return false;

        // Every frame needs an exposure reading; a partial bracket isn't classifiable.
        var scores = new List<float>(members.Count);
        foreach (var p in members)
        {
            var s = p.Metadata?.ExposureScore;
            if (!s.HasValue) return false;
            scores.Add(s.Value);
        }

        float min = scores.Min();
        float max = scores.Max();
        if (max - min < minExposureSpread) return false;

        // Distinct-exposure floor scales with the requested bracket size: a 5-shot
        // bracket should have 5 different exposures, not 5 frames at 2 EV apart.
        // Capped at 3 because real brackets always have at least 3 stops.
        var distinct = new HashSet<int>();
        foreach (var s in scores)
            distinct.Add((int)Math.Round(s * 10.0));
        int requiredDistinct = Math.Min(minBracketSize, 3);
        if (distinct.Count < requiredDistinct) return false;

        // All frames must share near-identical framing. Without phashes we can't
        // distinguish a bracket from a moving-subject burst, so bail.
        for (int i = 0; i < members.Count; i++)
        for (int j = i + 1; j < members.Count; j++)
        {
            var a = members[i].Phash;
            var b = members[j].Phash;
            if (a is null || b is null) return false;
            int dist = BitOperations.PopCount(a.Value ^ b.Value);
            if (dist > MaxBracketHamming) return false;
        }

        return true;
    }
}
