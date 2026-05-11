namespace Rawr.Core.Models;

/// <summary>
/// Detailed video stream/container metadata extracted via ffprobe. Populated
/// lazily when a video is selected (not during folder scan, which would add a
/// per-file ffprobe spawn). Null until the probe completes.
/// </summary>
public sealed class VideoMetadata
{
    public int WidthPx { get; init; }
    public int HeightPx { get; init; }
    public double FrameRate { get; init; }
    public string CodecName { get; init; } = "";       // e.g. "hevc", "h264"
    public string CodecProfile { get; init; } = "";    // e.g. "Main 10"
    public int BitDepth { get; init; }                 // 8, 10, 12
    public string ChromaSubsampling { get; init; } = ""; // "4:2:0", "4:2:2", "4:4:4"
    public long VideoBitrateBps { get; init; }
    public double DurationSeconds { get; init; }
    public long FileSizeBytes { get; init; }

    public string AudioCodecName { get; init; } = "";  // e.g. "pcm_s16le", "aac"
    public int AudioSampleRate { get; init; }
    public int AudioBitDepth { get; init; }
    public int AudioChannels { get; init; }

    public string DimensionsFormatted =>
        WidthPx > 0 && HeightPx > 0 ? $"{WidthPx} × {HeightPx}" : "";

    public string FrameRateFormatted =>
        FrameRate > 0 ? $"{FrameRate:0.###} fps" : "";

    /// <summary>Friendlier codec label, e.g. "HEVC / H.265" instead of raw "hevc".</summary>
    public string CodecFormatted
    {
        get
        {
            var name = (CodecName ?? "").Trim().ToLowerInvariant();
            var label = name switch
            {
                "hevc"  => "HEVC / H.265",
                "h264"  => "H.264 / AVC",
                "av1"   => "AV1",
                "vp9"   => "VP9",
                "prores"=> "ProRes",
                ""      => "",
                _       => name.ToUpperInvariant(),
            };
            return string.IsNullOrWhiteSpace(CodecProfile) ? label : $"{label} ({CodecProfile})";
        }
    }

    public string BitDepthFormatted =>
        BitDepth > 0 ? $"{BitDepth}-bit" : "";

    public string VideoBitrateFormatted
    {
        get
        {
            if (VideoBitrateBps <= 0) return "";
            const double Mb = 1_000_000.0;
            return VideoBitrateBps >= Mb
                ? $"{VideoBitrateBps / Mb:F0} Mb/s"
                : $"{VideoBitrateBps / 1000.0:F0} kb/s";
        }
    }

    public string DurationFormatted =>
        DurationSeconds > 0 ? $"{DurationSeconds:0.###} sec" : "";

    public string FileSizeFormatted
    {
        get
        {
            if (FileSizeBytes <= 0) return "";
            const double KiB = 1024, MiB = KiB * 1024, GiB = MiB * 1024;
            return FileSizeBytes switch
            {
                >= (long)GiB => $"{FileSizeBytes / GiB:F2} GiB",
                >= (long)MiB => $"{FileSizeBytes / MiB:F1} MiB",
                _            => $"{FileSizeBytes / KiB:F0} KiB",
            };
        }
    }

    /// <summary>One-line summary like "PCM, 48 kHz, 16-bit, stereo".</summary>
    public string AudioFormatted
    {
        get
        {
            if (string.IsNullOrWhiteSpace(AudioCodecName) && AudioSampleRate == 0) return "";

            var name = (AudioCodecName ?? "").ToLowerInvariant();
            string label = name switch
            {
                var s when s.StartsWith("pcm") => "PCM",
                "aac"   => "AAC",
                "ac3"   => "AC-3",
                "eac3"  => "E-AC-3",
                "opus"  => "Opus",
                "mp3"   => "MP3",
                "flac"  => "FLAC",
                ""      => "",
                _       => name.ToUpperInvariant(),
            };

            var parts = new List<string>();
            if (!string.IsNullOrEmpty(label)) parts.Add(label);
            if (AudioSampleRate > 0) parts.Add($"{AudioSampleRate / 1000.0:0.###} kHz");
            if (AudioBitDepth > 0) parts.Add($"{AudioBitDepth}-bit");
            parts.Add(AudioChannels switch
            {
                1 => "mono",
                2 => "stereo",
                > 2 => $"{AudioChannels} ch",
                _ => "",
            });
            parts.RemoveAll(string.IsNullOrEmpty);
            return string.Join(", ", parts);
        }
    }

    public string ChromaSubsamplingFormatted => ChromaSubsampling ?? "";
}
