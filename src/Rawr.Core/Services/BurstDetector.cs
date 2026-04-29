using Rawr.Core.Models;

namespace Rawr.Core.Services;

/// <summary>
/// Groups photos shot in rapid succession on the same camera into "bursts".
/// Two photos are in the same burst if their EXIF capture times are within
/// <see cref="MaxGap"/> of each other and they were shot on the same camera body.
/// Photos without capture time are left ungrouped.
/// </summary>
public static class BurstDetector
{
    public static readonly TimeSpan DefaultMaxGap = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Assigns <see cref="PhotoItem.GroupId"/> and <see cref="PhotoItem.BurstBadge"/>
    /// for each photo. Returns the number of bursts detected (groups of ≥ 2 photos).
    /// Photos that aren't part of a burst get GroupId=0 and BurstBadge="".
    /// </summary>
    public static int Detect(IReadOnlyList<PhotoItem> photos, TimeSpan? maxGap = null)
    {
        var gap = maxGap ?? DefaultMaxGap;

        // Reset existing burst state — this pass is the source of truth.
        foreach (var p in photos)
        {
            p.GroupId = 0;
            p.BurstBadge = "";
        }

        // Sort by camera (so two cameras shooting simultaneously don't collide),
        // then by capture time. Photos with no capture time are skipped entirely.
        var ordered = photos
            .Where(p => p.Metadata?.CaptureTime.HasValue == true)
            .OrderBy(p => p.Metadata!.CameraModel, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Metadata!.CaptureTime!.Value)
            .ToList();

        int nextGroupId = 0;
        int burstCount = 0;
        var current = new List<PhotoItem>();
        DateTime? prevTime = null;
        string? prevCam = null;

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
            var t = p.Metadata!.CaptureTime!.Value;
            var cam = p.Metadata.CameraModel ?? "";
            if (prevTime is null
                || !string.Equals(cam, prevCam, StringComparison.OrdinalIgnoreCase)
                || (t - prevTime.Value) > gap)
            {
                Flush();
            }
            current.Add(p);
            prevTime = t;
            prevCam = cam;
        }
        Flush();

        return burstCount;
    }
}
