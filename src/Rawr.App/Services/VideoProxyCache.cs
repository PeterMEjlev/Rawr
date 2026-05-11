using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Rawr.Core.Models;

namespace Rawr.App.Services;

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

    public static async Task<string?> GetOrCreateAsync(PhotoItem photo, IProgress<string>? progress, CancellationToken ct)
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg == null) return null;

        var sourcePath = photo.FilePath;
        var proxyPath = GetProxyPath(sourcePath);
        var manifestPath = GetManifestPath(sourcePath);
        if (IsFresh(sourcePath, proxyPath, manifestPath))
            return proxyPath;

        progress?.Report("Preparing smooth preview...");

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

                await RunFfmpegAsync(ffmpeg, sourcePath, tempPath, ct).ConfigureAwait(false);

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

    private static async Task RunFfmpegAsync(string ffmpegPath, string sourcePath, string outputPath, CancellationToken ct)
    {
        var failures = new List<string>();
        foreach (var encoder in GetEncoderPlans())
        {
            try
            {
                await RunFfmpegAsync(ffmpegPath, sourcePath, outputPath, encoder, ct).ConfigureAwait(false);
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
            "-v", "warning",
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
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
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
