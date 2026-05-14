using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Rawr.Core.Models;

namespace Rawr.App.Services;

/// <summary>
/// Snapshot of proxy-generation progress. <see cref="Fraction"/> is in [0, 1]
/// when the source duration is known, or NaN for indeterminate progress.
/// </summary>
internal readonly record struct VideoProxyProgress(double Fraction, string Text)
{
    public bool HasFraction => !double.IsNaN(Fraction);
    public static VideoProxyProgress Indeterminate(string text) => new(double.NaN, text);
}

internal static class VideoProxyCache
{
    private const int ProxyVersion = 4;
    private const int TargetMaxWidth = 720;
    private const int TargetFps = 24;
    private const int TargetCrf = 30;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly SemaphoreSlim EncodeGate = new(1, 1);

    public static bool ShouldProxy(PhotoItem? photo)
    {
        if (photo?.IsVideo != true) return false;

        var make = photo.Metadata?.CameraMake ?? "";
        var model = photo.Metadata?.CameraModel ?? "";
        var camera = $"{make} {model}".ToUpperInvariant();
        if (camera.Contains("CANON", StringComparison.Ordinal) && camera.Contains("R5", StringComparison.Ordinal))
            return true;

        var width = photo.Metadata?.WidthPx ?? 0;
        var height = photo.Metadata?.HeightPx ?? 0;
        if ((width <= 0 || height <= 0) && TryReadContainerDimensions(photo.FilePath, out var containerWidth, out var containerHeight))
        {
            width = containerWidth;
            height = containerHeight;
        }

        var size = photo.Metadata?.FileSizeBytes ?? SafeFileSize(photo.FilePath);
        if (width >= 3840 || height >= 2160)
            return true;

        // Fallback for camera files whose make/model metadata is missing: very
        // dense video is usually the same HEVC / high-frame-rate class that VLC
        // cannot preview smoothly on many GPUs. If dimensions are not known yet,
        // file size alone is enough to put the clip on the safe proxy path.
        return size >= 200L * 1024 * 1024
            && (width == 0 || height == 0 || width >= 1920 || height >= 1080);
    }

    public static bool TryGetFreshProxyPath(PhotoItem photo, out string proxyPath)
    {
        proxyPath = GetProxyPath(photo.FilePath);
        return IsFresh(photo.FilePath, proxyPath, GetManifestPath(photo.FilePath));
    }

    public static async Task<string?> GetOrCreateAsync(
        PhotoItem photo,
        IProgress<VideoProxyProgress>? progress,
        CancellationToken ct)
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg == null) return null;

        var sourcePath = photo.FilePath;
        var proxyPath = GetProxyPath(sourcePath);
        var manifestPath = GetManifestPath(sourcePath);
        if (IsFresh(sourcePath, proxyPath, manifestPath))
            return proxyPath;

        progress?.Report(VideoProxyProgress.Indeterminate("Preparing smooth preview…"));

        // The VM populates VideoInfo via a fire-and-forget probe — frequently
        // not ready yet when a heavy clip is selected from a cold state. We
        // treat its duration as a hint only; the authoritative value comes
        // from ffmpeg's own "Duration:" header parsed off stderr below, which
        // works even when ffprobe couldn't read the container.
        var durationHintSeconds = photo.VideoInfo?.DurationSeconds ?? 0;

        var cacheDir = Path.GetDirectoryName(proxyPath);
        if (string.IsNullOrWhiteSpace(cacheDir)) return null;
        Directory.CreateDirectory(cacheDir);

        var tempPath = Path.Combine(cacheDir, $"{Path.GetFileNameWithoutExtension(proxyPath)}.{Guid.NewGuid():N}.tmp.mp4");

        try
        {
            await EncodeGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (IsFresh(sourcePath, proxyPath, manifestPath))
                    return proxyPath;

                await RunFfmpegAsync(ffmpeg, sourcePath, tempPath, durationHintSeconds, progress, ct).ConfigureAwait(false);

                var tempInfo = new FileInfo(tempPath);
                if (!tempInfo.Exists || tempInfo.Length == 0)
                    return null;

                if (File.Exists(proxyPath)) File.Delete(proxyPath);
                File.Move(tempPath, proxyPath);

                var manifest = ProxyManifest.FromSource(sourcePath);
                await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions), ct)
                    .ConfigureAwait(false);

                return proxyPath;
            }
            finally
            {
                EncodeGate.Release();
            }
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    private static async Task RunFfmpegAsync(
        string ffmpegPath,
        string sourcePath,
        string outputPath,
        double durationSeconds,
        IProgress<VideoProxyProgress>? progress,
        CancellationToken ct)
    {
        var failures = new List<string>();
        foreach (var encoder in GetEncoderPlans())
        {
            try
            {
                await RunFfmpegAsync(ffmpegPath, sourcePath, outputPath, encoder, durationSeconds, progress, ct).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add($"{encoder.Name}: {ex.Message}");
                TryDelete(outputPath);
            }
        }

        throw new InvalidOperationException($"ffmpeg could not create the video proxy. {string.Join(" ", failures)}");
    }

    private static async Task RunFfmpegAsync(
        string ffmpegPath,
        string sourcePath,
        string outputPath,
        EncoderPlan encoder,
        double durationHintSeconds,
        IProgress<VideoProxyProgress>? progress,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

        AddArgs(
            psi,
            "-hide_banner",
            "-y",
            "-nostdin",
            // -v info is required for the "Duration:" header that we parse off
            // stderr to drive the progress bar. ffmpeg also prints periodic
            // "time=HH:MM:SS.ff ..." stats lines at this level, which give us
            // the encoded position without needing a separate -progress channel.
            "-v", "info",
            "-threads", "0",
            // -skip_frame bidir drops every B-frame at decode (huge win for 4K
            // HEVC 4:2:2 where NVDEC can't help and software decode dominates).
            // -skip_loop_filter all disables the in-loop deblocker for another
            // ~15-20% decode speedup. Cost: visible stutter during fast motion
            // and slightly blockier output. Acceptable for a culling preview.
            "-skip_frame", "bidir",
            "-skip_loop_filter", "all");

        // Plan-specific pre-input args (e.g. -hwaccel cuda) MUST come before -i.
        AddArgs(psi, encoder.InputArgs);

        AddArgs(
            psi,
            "-i", sourcePath,
            "-an",
            "-map", "0:v:0",
            "-vf", $"scale='min({TargetMaxWidth},iw)':-2:flags=fast_bilinear,fps={TargetFps}");

        AddArgs(psi, encoder.EncoderArgs);

        AddArgs(psi, "-movflags", "+faststart", outputPath);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start ffmpeg.");
        var stderrTask = ConsumeStderrAsync(process.StandardError, durationHintSeconds, progress, ct);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        var stderr = await stderrTask.ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg exited with {process.ExitCode}: {stderr}{stdout}");
    }

    private static async Task<string> ConsumeStderrAsync(
        StreamReader stderr,
        double durationHintSeconds,
        IProgress<VideoProxyProgress>? progress,
        CancellationToken ct)
    {
        // ffmpeg at -v info prints two things we care about, both to stderr:
        //   • Once, near startup:   "  Duration: 00:00:30.50, start: 0.000000, ..."
        //   • Repeatedly, during encode: "frame=N fps=N q=... time=HH:MM:SS.ff bitrate=... speed=Nx"
        // The stats line is terminated by '\r' (so it overwrites itself in a TTY);
        // StreamReader.ReadLineAsync treats \r as a line terminator, so each stats
        // tick comes through as its own line. Everything is also buffered for
        // error diagnostics in the non-zero-exit path.
        var buffer = new System.Text.StringBuilder();
        var durationSeconds = durationHintSeconds;
        var lastReportedPercent = -1;
        string? line;
        while ((line = await stderr.ReadLineAsync(ct).ConfigureAwait(false)) != null)
        {
            buffer.AppendLine(line);
            if (progress == null) continue;

            if (durationSeconds <= 0)
            {
                var d = TryParseDurationHeader(line);
                if (d > 0) durationSeconds = d;
            }

            if (durationSeconds <= 0) continue;

            var t = TryParseTimeStat(line);
            if (t <= 0) continue;

            var fraction = Math.Clamp(t / durationSeconds, 0.0, 1.0);
            var percent = (int)Math.Round(fraction * 100);
            if (percent == lastReportedPercent) continue;
            lastReportedPercent = percent;
            progress.Report(new VideoProxyProgress(fraction, $"Preparing smooth preview… {percent}%"));
        }
        return buffer.ToString();
    }

    private static double TryParseDurationHeader(string line)
    {
        // Looking for the literal "  Duration: " prefix that ffmpeg emits once
        // per input. Tolerate other text on the line (e.g. ", start: ..., bitrate: ...")
        // by parsing only the timestamp that follows the marker.
        const string marker = "Duration:";
        var idx = line.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return 0;
        return ParseHmsTimestamp(line.AsSpan(idx + marker.Length).TrimStart());
    }

    private static double TryParseTimeStat(string line)
    {
        // Stats lines contain "time=HH:MM:SS.ff" surrounded by other key=value
        // pairs. We don't anchor to start-of-line because the prefix is
        // whitespace-padded (e.g. "frame=  120 ..."). "N/A" before the first
        // frame is rejected by the digit check in ParseHmsTimestamp.
        const string marker = "time=";
        var idx = line.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return 0;
        return ParseHmsTimestamp(line.AsSpan(idx + marker.Length));
    }

    private static double ParseHmsTimestamp(ReadOnlySpan<char> span)
    {
        // Reads a leading "HH:MM:SS.ff" timestamp, stopping at the first
        // non-numeric / non-':' / non-'.' character. Returns 0 if the prefix
        // isn't a valid timestamp (covers ffmpeg's "N/A" placeholder).
        int end = 0;
        while (end < span.Length)
        {
            var c = span[end];
            if ((c >= '0' && c <= '9') || c == ':' || c == '.') { end++; continue; }
            break;
        }
        if (end < 7) return 0; // "0:00:00" is the shortest valid form

        var token = span[..end];
        int first = token.IndexOf(':');
        if (first <= 0) return 0;
        int second = token[(first + 1)..].IndexOf(':');
        if (second <= 0) return 0;
        second += first + 1;

        if (!int.TryParse(token[..first], System.Globalization.NumberStyles.Integer,
                          System.Globalization.CultureInfo.InvariantCulture, out var h)) return 0;
        if (!int.TryParse(token[(first + 1)..second], System.Globalization.NumberStyles.Integer,
                          System.Globalization.CultureInfo.InvariantCulture, out var m)) return 0;
        if (!double.TryParse(token[(second + 1)..], System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out var s)) return 0;
        if (h < 0 || m < 0 || s < 0) return 0;
        return h * 3600.0 + m * 60.0 + s;
    }

    private static EncoderPlan[] GetEncoderPlans()
    {
        var quality = TargetCrf.ToString(System.Globalization.CultureInfo.InvariantCulture);

        string[] nvencArgs =
        [
            "-c:v", "h264_nvenc",
            "-preset", "p1",
            "-tune", "ll",
            "-rc", "vbr",
            "-cq", quality,
            "-b:v", "0",
            "-pix_fmt", "yuv420p",
        ];

        string[] libx264Args =
        [
            "-c:v", "libx264",
            "-preset", "ultrafast",
            "-crf", quality,
            "-pix_fmt", "yuv420p",
        ];

        // Plan order is fastest → most compatible. NVDEC handles 4:2:0 HEVC/H.264
        // on the GPU (3–5× faster than software decode for 4K sources). For
        // codecs/profiles it can't handle (HEVC 4:2:2, AV1 on older GPUs, etc.)
        // CUDA init fails and the next plan retries on the CPU path.
        return
        [
            new EncoderPlan("cuda+nvenc", ["-hwaccel", "cuda"], nvencArgs),
            new EncoderPlan("nvenc", [], nvencArgs),
            new EncoderPlan("libx264", [], libx264Args),
        ];
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void AddArgs(ProcessStartInfo psi, params string[] args)
    {
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
    }

    private sealed record EncoderPlan(string Name, string[] InputArgs, string[] EncoderArgs);

    private static bool IsFresh(string sourcePath, string proxyPath, string manifestPath)
    {
        try
        {
            if (!File.Exists(proxyPath) || new FileInfo(proxyPath).Length == 0) return false;
            if (!File.Exists(manifestPath)) return false;

            var manifest = JsonSerializer.Deserialize<ProxyManifest>(File.ReadAllText(manifestPath), JsonOptions);
            if (manifest == null) return false;

            var info = new FileInfo(sourcePath);
            var isCurrentProxy =
                manifest.Version == ProxyVersion
                && manifest.ProxyMaxWidth == TargetMaxWidth
                && manifest.ProxyFps == TargetFps
                && manifest.ProxyCrf == TargetCrf;
            // v3 proxies (960p / 30 fps / CRF 28) were higher-quality but much
            // slower to build on 4K HEVC 4:2:2. Keep them valid so a user who
            // already paid the encode cost doesn't lose the proxy on upgrade.
            var isV3Proxy =
                manifest.Version == 3
                && manifest.ProxyMaxWidth == 960
                && manifest.ProxyFps == 30
                && manifest.ProxyCrf == 28;
            var isFastPreviewProxy =
                manifest.Version == 2
                && manifest.ProxyMaxWidth == 1280
                && manifest.ProxyFps == 30
                && manifest.ProxyCrf == 27;
            var isLegacyProxy =
                manifest.Version == 1
                && manifest.ProxyMaxWidth == 1920
                && manifest.ProxyFps == 30
                && manifest.ProxyCrf == 22;

            return info.Exists
                && manifest.SourceSize == info.Length
                && manifest.SourceLastWriteUtcTicks == info.LastWriteTimeUtc.Ticks
                && (isCurrentProxy || isV3Proxy || isFastPreviewProxy || isLegacyProxy);
        }
        catch
        {
            return false;
        }
    }

    private static string GetProxyPath(string sourcePath) =>
        Path.Combine(GetCacheDir(sourcePath), $"{Path.GetFileNameWithoutExtension(sourcePath)}_proxy.mp4");

    private static string GetManifestPath(string sourcePath) =>
        Path.Combine(GetCacheDir(sourcePath), $"{Path.GetFileNameWithoutExtension(sourcePath)}_proxy.json");

    private static string GetCacheDir(string sourcePath)
    {
        var folder = Path.GetDirectoryName(sourcePath);
        if (string.IsNullOrWhiteSpace(folder))
            folder = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(folder, ".rawr", "cache");
    }

    private static string? FindFfmpeg()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "Resources", "ffmpeg.exe");
        if (File.Exists(bundled)) return bundled;

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return null;

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "ffmpeg.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }

        return null;
    }

    private static long SafeFileSize(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    private static bool TryReadContainerDimensions(string sourcePath, out int width, out int height)
    {
        width = 0;
        height = 0;

        try
        {
            var bytes = ReadFilePrefix(sourcePath, 16L * 1024 * 1024);
            if (bytes.Length == 0) return false;

            for (int i = 4; i < bytes.Length - 100; i++)
            {
                if (bytes[i] != (byte)'t'
                    || bytes[i + 1] != (byte)'k'
                    || bytes[i + 2] != (byte)'h'
                    || bytes[i + 3] != (byte)'d')
                {
                    continue;
                }

                var boxStart = i - 4;
                var boxSize = ReadBigEndianUInt32(bytes, boxStart);
                if (boxSize < 92 || boxStart + boxSize > bytes.Length)
                    continue;

                var version = bytes[i + 4];
                var widthOffset = i + (version == 1 ? 92 : 80);
                var heightOffset = widthOffset + 4;
                if (heightOffset + 4 > boxStart + boxSize)
                    continue;

                var w = (int)(ReadBigEndianUInt32(bytes, widthOffset) >> 16);
                var h = (int)(ReadBigEndianUInt32(bytes, heightOffset) >> 16);
                if (w is > 0 and <= 100_000 && h is > 0 and <= 100_000)
                {
                    width = w;
                    height = h;
                    return true;
                }
            }
        }
        catch { }

        return false;
    }

    private static byte[] ReadFilePrefix(string filePath, long maxBytes)
    {
        var info = new FileInfo(filePath);
        var scanLength = (int)Math.Min(info.Length, maxBytes);
        if (scanLength <= 0) return [];

        var bytes = new byte[scanLength];
        using var fs = File.OpenRead(filePath);
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = fs.Read(bytes, offset, bytes.Length - offset);
            if (read == 0) break;
            offset += read;
        }

        if (offset != bytes.Length)
            Array.Resize(ref bytes, offset);
        return bytes;
    }

    private static uint ReadBigEndianUInt32(byte[] bytes, int offset)
    {
        if (offset < 0 || offset + 4 > bytes.Length) return 0;
        return (uint)((bytes[offset] << 24)
            | (bytes[offset + 1] << 16)
            | (bytes[offset + 2] << 8)
            | bytes[offset + 3]);
    }

    private sealed record ProxyManifest(
        int Version,
        long SourceSize,
        long SourceLastWriteUtcTicks,
        int ProxyMaxWidth,
        int ProxyFps,
        int ProxyCrf)
    {
        public static ProxyManifest FromSource(string sourcePath)
        {
            var info = new FileInfo(sourcePath);
            return new ProxyManifest(
                ProxyVersion,
                info.Length,
                info.LastWriteTimeUtc.Ticks,
                TargetMaxWidth,
                TargetFps,
                TargetCrf);
        }
    }
}
