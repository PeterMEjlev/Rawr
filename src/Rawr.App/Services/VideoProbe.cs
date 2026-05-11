using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Rawr.Core.Models;

namespace Rawr.App.Services;

/// <summary>
/// Runs the bundled ffprobe.exe against a video file and maps the JSON output to
/// a <see cref="VideoMetadata"/>. Results are cached in-process keyed by
/// (path, size, mtime) so navigating back to a recently-seen video is instant.
/// Probing is gated by a semaphore so opening many videos in quick succession
/// doesn't fan out ffprobe processes.
/// </summary>
internal static class VideoProbe
{
    private static readonly SemaphoreSlim ProbeGate = new(2, 2);
    private static readonly Dictionary<string, VideoMetadata> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object CacheLock = new();

    public static async Task<VideoMetadata?> GetAsync(string filePath, CancellationToken ct)
    {
        FileInfo info;
        try { info = new FileInfo(filePath); }
        catch { return null; }
        if (!info.Exists) return null;

        var cacheKey = $"{filePath}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        lock (CacheLock)
        {
            if (Cache.TryGetValue(cacheKey, out var cached)) return cached;
        }

        var ffprobe = FindFfprobe();
        if (ffprobe == null) return null;

        await ProbeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check cache after acquiring the gate — another concurrent caller
            // for the same file may have populated it while we waited.
            lock (CacheLock)
            {
                if (Cache.TryGetValue(cacheKey, out var cached)) return cached;
            }

            var json = await RunFfprobeAsync(ffprobe, filePath, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json)) return null;

            var meta = Parse(json, info.Length);
            if (meta == null) return null;

            lock (CacheLock) Cache[cacheKey] = meta;
            return meta;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
        finally { ProbeGate.Release(); }
    }

    private static async Task<string?> RunFfprobeAsync(string ffprobePath, string sourcePath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffprobePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[]
        {
            "-hide_banner",
            "-v", "error",
            "-print_format", "json",
            "-show_format",
            "-show_streams",
            sourcePath,
        })
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start ffprobe.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        // ffprobe surfaces parse warnings on stderr even on success; only treat a
        // non-zero exit + missing stdout as a real failure.
        return process.ExitCode != 0 && string.IsNullOrWhiteSpace(stdout) ? null : stdout;
    }

    private static VideoMetadata? Parse(string json, long fileSizeBytes)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            JsonElement? video = null;
            JsonElement? audio = null;
            if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in streams.EnumerateArray())
                {
                    var codecType = TryGetString(s, "codec_type");
                    if (video == null && codecType == "video") video = s;
                    else if (audio == null && codecType == "audio") audio = s;
                }
            }

            if (video == null) return null;
            var v = video.Value;

            int width = TryGetInt(v, "width");
            int height = TryGetInt(v, "height");
            double fps = ParseRational(TryGetString(v, "avg_frame_rate"));
            if (fps <= 0) fps = ParseRational(TryGetString(v, "r_frame_rate"));

            string codecName = TryGetString(v, "codec_name") ?? "";
            string codecProfile = TryGetString(v, "profile") ?? "";

            string pixFmt = TryGetString(v, "pix_fmt") ?? "";
            int bitDepth = ExtractBitDepth(pixFmt, v);
            string chroma = ExtractChroma(pixFmt);

            long videoBitrate = TryGetLong(v, "bit_rate");
            if (videoBitrate <= 0
                && root.TryGetProperty("format", out var fmtForVideoBitrate)
                && fmtForVideoBitrate.ValueKind == JsonValueKind.Object)
            {
                // Container-level bitrate when the stream doesn't carry one
                // (common for variable-bitrate captures from cameras).
                videoBitrate = TryGetLong(fmtForVideoBitrate, "bit_rate");
            }

            double duration = TryGetDouble(v, "duration");
            if (duration <= 0
                && root.TryGetProperty("format", out var fmtForDuration)
                && fmtForDuration.ValueKind == JsonValueKind.Object)
            {
                duration = TryGetDouble(fmtForDuration, "duration");
            }

            string audioCodec = "";
            int audioSampleRate = 0;
            int audioBitDepth = 0;
            int audioChannels = 0;
            if (audio != null)
            {
                var a = audio.Value;
                audioCodec = TryGetString(a, "codec_name") ?? "";
                audioSampleRate = ParseIntString(TryGetString(a, "sample_rate"));
                audioChannels = TryGetInt(a, "channels");
                audioBitDepth = TryGetInt(a, "bits_per_raw_sample");
                if (audioBitDepth <= 0) audioBitDepth = ExtractPcmBitDepth(audioCodec);
            }

            return new VideoMetadata
            {
                WidthPx = width,
                HeightPx = height,
                FrameRate = fps,
                CodecName = codecName,
                CodecProfile = codecProfile,
                BitDepth = bitDepth,
                ChromaSubsampling = chroma,
                VideoBitrateBps = videoBitrate,
                DurationSeconds = duration,
                FileSizeBytes = fileSizeBytes,
                AudioCodecName = audioCodec,
                AudioSampleRate = audioSampleRate,
                AudioBitDepth = audioBitDepth,
                AudioChannels = audioChannels,
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int TryGetInt(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) return s;
        return 0;
    }

    private static long TryGetLong(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var s)) return s;
        return 0;
    }

    private static double TryGetDouble(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String
            && double.TryParse(v.GetString(), System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out var s))
            return s;
        return 0;
    }

    private static int ParseIntString(string? s) =>
        int.TryParse(s, out var n) ? n : 0;

    /// <summary>Parse ffprobe rationals like "60000/1001" → 59.94.</summary>
    private static double ParseRational(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        var idx = s.IndexOf('/');
        if (idx < 0)
        {
            return double.TryParse(s, System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
        }
        if (!double.TryParse(s.AsSpan(0, idx), System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out var num)) return 0;
        if (!double.TryParse(s.AsSpan(idx + 1), System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out var den) || den == 0) return 0;
        return num / den;
    }

    /// <summary>
    /// Derives bit depth from the pixel format string ("yuv422p10le" → 10) with
    /// a fallback to the bits_per_raw_sample field if present.
    /// </summary>
    private static int ExtractBitDepth(string pixFmt, JsonElement video)
    {
        // ffprobe pixel-format strings embed the bit depth after the chroma tag:
        // "yuv420p" → 8, "yuv420p10le" → 10, "yuv422p12be" → 12.
        if (!string.IsNullOrEmpty(pixFmt))
        {
            int digits = 0;
            int value = 0;
            for (int i = 0; i < pixFmt.Length; i++)
            {
                char c = pixFmt[i];
                if (c >= '0' && c <= '9') { value = value * 10 + (c - '0'); digits++; }
                else if (digits > 0) break;
            }
            if (digits > 0 && value is >= 8 and <= 16) return value;
            if (digits == 0) return 8; // formats like "yuv420p" carry no explicit depth
        }

        var fallback = TryGetInt(video, "bits_per_raw_sample");
        return fallback > 0 ? fallback : 0;
    }

    private static string ExtractChroma(string pixFmt)
    {
        if (string.IsNullOrEmpty(pixFmt)) return "";
        // Recognise the standard tags inside the pixfmt string.
        if (pixFmt.Contains("420")) return "4:2:0";
        if (pixFmt.Contains("422")) return "4:2:2";
        if (pixFmt.Contains("444")) return "4:4:4";
        if (pixFmt.Contains("411")) return "4:1:1";
        if (pixFmt.Contains("440")) return "4:4:0";
        return "";
    }

    private static int ExtractPcmBitDepth(string codecName)
    {
        // pcm_s16le / pcm_s24le / pcm_f32le → 16 / 24 / 32. Best-effort fallback
        // when ffprobe omits bits_per_raw_sample.
        if (string.IsNullOrEmpty(codecName)) return 0;
        int digits = 0, value = 0;
        for (int i = 0; i < codecName.Length; i++)
        {
            char c = codecName[i];
            if (c >= '0' && c <= '9') { value = value * 10 + (c - '0'); digits++; }
            else if (digits > 0) break;
        }
        return digits > 0 && value is >= 8 and <= 64 ? value : 0;
    }

    private static string? FindFfprobe()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "Resources", "ffprobe.exe");
        if (File.Exists(bundled)) return bundled;

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return null;

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "ffprobe.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        return null;
    }
}
