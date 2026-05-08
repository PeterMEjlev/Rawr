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
    /// <summary>Minimum number of frames in a bracket. Most cameras shoot 3/5/7.</summary>
    public const int MinBracketSize = 3;

    /// <summary>Minimum total spread of exposure scores (in stops) across the bracket.</summary>
    public const float MinExposureSpread = 0.9f;

    /// <summary>Minimum number of distinct exposure scores (rounded to 0.1 EV).</summary>
    public const int MinDistinctExposures = 3;

    /// <summary>
    /// Maximum allowed Hamming distance between any two frames' dHashes inside a bracket.
    /// Brackets shot of the same scene should look almost identical apart from brightness;
    /// this is much tighter than the burst-grouping threshold.
    /// </summary>
    public const int MaxBracketHamming = 12;

    /// <summary>
    /// Sets <see cref="PhotoItem.IsHdr"/> on every photo and returns the file names
    /// that belong to detected HDR bursts. Photos with GroupId == 0 are skipped.
    /// </summary>
    public static HashSet<string> Detect(IReadOnlyList<PhotoItem> photos)
    {
        var hdrFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in photos)
            p.IsHdr = false;

        var byGroup = photos
            .Where(p => p.GroupId > 0)
            .GroupBy(p => p.GroupId);

        foreach (var group in byGroup)
        {
            var members = group.ToList();
            if (!IsHdrBracket(members)) continue;

            foreach (var p in members)
            {
                p.IsHdr = true;
                hdrFiles.Add(p.FileName);
            }
        }

        return hdrFiles;
    }

    private static bool IsHdrBracket(IReadOnlyList<PhotoItem> members)
    {
        if (members.Count < MinBracketSize) return false;

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
        if (max - min < MinExposureSpread) return false;

        var distinct = new HashSet<int>();
        foreach (var s in scores)
            distinct.Add((int)Math.Round(s * 10.0));
        if (distinct.Count < MinDistinctExposures) return false;

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
