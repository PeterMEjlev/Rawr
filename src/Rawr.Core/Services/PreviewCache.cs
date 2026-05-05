namespace Rawr.Core.Services;

/// <summary>
/// Disk-backed cache for extracted JPEG previews and thumbnails.
/// Stored in ".rawr/cache/" alongside the culling database.
/// Keyed by original filename so previews survive app restarts.
/// </summary>
public sealed class PreviewCache
{
    private readonly string _cacheDir;

    public PreviewCache(string folderPath)
    {
        _cacheDir = Path.Combine(folderPath, ".rawr", "cache");
        Directory.CreateDirectory(_cacheDir);
    }

    public string GetThumbnailPath(string fileName) =>
        Path.Combine(_cacheDir, $"{Path.GetFileNameWithoutExtension(fileName)}_thumb.jpg");

    public string GetPreviewPath(string fileName) =>
        Path.Combine(_cacheDir, $"{Path.GetFileNameWithoutExtension(fileName)}_preview.jpg");

    public string GetLinearRawPath(string fileName) =>
        Path.Combine(_cacheDir, $"{Path.GetFileNameWithoutExtension(fileName)}_linearraw.bin");

    /// <summary>
    /// Cheap existence check (no read, no validation). Use to gate fast-path
    /// rendering decisions where the cost of being wrong is just falling through
    /// to the slow path on the next step.
    /// </summary>
    public bool HasLinearRaw(string fileName) =>
        File.Exists(GetLinearRawPath(fileName));

    public bool HasThumbnail(string fileName) =>
        File.Exists(GetThumbnailPath(fileName));

    public bool HasPreview(string fileName) =>
        File.Exists(GetPreviewPath(fileName));

    public byte[]? LoadThumbnail(string fileName)
    {
        var path = GetThumbnailPath(fileName);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    public byte[]? LoadPreview(string fileName)
    {
        var path = GetPreviewPath(fileName);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    public void SaveThumbnail(string fileName, byte[] jpegData)
    {
        File.WriteAllBytes(GetThumbnailPath(fileName), jpegData);
    }

    public void SavePreview(string fileName, byte[] jpegData)
    {
        File.WriteAllBytes(GetPreviewPath(fileName), jpegData);
    }

    // Linear-RAW cache binary format. The downsampled 16-bit linear RGB buffer
    // produced by the LibRaw decode is the slowest thing to recompute (~1-3s vs
    // ~30ms to read back from disk), so persisting it across navigations and app
    // restarts is the single biggest perceived speedup.
    //
    // Layout: 32-byte header + width*height*3*2 bytes of pixel data.
    //   [0..3]   magic "RAWL"
    //   [4..7]   version (currently 1)
    //   [8..11]  width  (int32)
    //   [12..15] height (int32)
    //   [16..23] source file size (int64) — invalidate if source RAW changes
    //   [24..31] source last-write-time UTC ticks (int64)
    //   [32..]   pixels: ushort[width*height*3], little-endian, RGB-interleaved
    private const uint LinearRawMagic = 0x4C574152u; // 'R','A','W','L' little-endian
    private const int LinearRawVersion = 1;
    private const int LinearRawHeaderBytes = 32;

    /// <summary>
    /// Try to load a previously-cached downsampled linear RAW for this file. Returns
    /// null if the cache is missing, malformed, or stale relative to the source RAW.
    /// </summary>
    public LinearRawCacheEntry? LoadLinearRaw(string fileName, string sourceRawPath)
    {
        var path = GetLinearRawPath(fileName);
        if (!File.Exists(path)) return null;

        try
        {
            long expectedSize;
            long expectedTicks;
            try
            {
                var info = new FileInfo(sourceRawPath);
                if (!info.Exists) return null;
                expectedSize = info.Length;
                expectedTicks = info.LastWriteTimeUtc.Ticks;
            }
            catch { return null; }

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Length < LinearRawHeaderBytes) return null;

            Span<byte> header = stackalloc byte[LinearRawHeaderBytes];
            int readTotal = 0;
            while (readTotal < LinearRawHeaderBytes)
            {
                int n = fs.Read(header[readTotal..]);
                if (n <= 0) return null;
                readTotal += n;
            }

            uint magic = BitConverter.ToUInt32(header[..4]);
            int version = BitConverter.ToInt32(header[4..8]);
            int width = BitConverter.ToInt32(header[8..12]);
            int height = BitConverter.ToInt32(header[12..16]);
            long size = BitConverter.ToInt64(header[16..24]);
            long ticks = BitConverter.ToInt64(header[24..32]);

            if (magic != LinearRawMagic || version != LinearRawVersion) return null;
            if (width <= 0 || height <= 0 || width > 32768 || height > 32768) return null;
            if (size != expectedSize || ticks != expectedTicks) return null;

            long pixelBytes = (long)width * height * 3 * 2;
            if (fs.Length - LinearRawHeaderBytes != pixelBytes) return null;

            var pixels = new ushort[width * height * 3];
            var pixelSpan = System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixels.AsSpan());
            int copied = 0;
            while (copied < pixelSpan.Length)
            {
                int n = fs.Read(pixelSpan[copied..]);
                if (n <= 0) return null;
                copied += n;
            }

            return new LinearRawCacheEntry(width, height, pixels);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Persist the downsampled linear RAW alongside the JPEG cache. Writes via a
    /// temp file + rename so a crash mid-write can't leave a half-valid file that
    /// would later be deserialized into garbage pixels.
    /// </summary>
    public void SaveLinearRaw(string fileName, string sourceRawPath, int width, int height, ushort[] pixels)
    {
        if (width <= 0 || height <= 0) return;
        if (pixels.Length != width * height * 3) return;

        long size;
        long ticks;
        try
        {
            var info = new FileInfo(sourceRawPath);
            if (!info.Exists) return;
            size = info.Length;
            ticks = info.LastWriteTimeUtc.Ticks;
        }
        catch { return; }

        var path = GetLinearRawPath(fileName);
        var tmp = path + ".tmp";

        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                Span<byte> header = stackalloc byte[LinearRawHeaderBytes];
                BitConverter.TryWriteBytes(header[..4], LinearRawMagic);
                BitConverter.TryWriteBytes(header[4..8], LinearRawVersion);
                BitConverter.TryWriteBytes(header[8..12], width);
                BitConverter.TryWriteBytes(header[12..16], height);
                BitConverter.TryWriteBytes(header[16..24], size);
                BitConverter.TryWriteBytes(header[24..32], ticks);
                fs.Write(header);

                var pixelSpan = System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixels.AsSpan());
                fs.Write(pixelSpan);
            }

            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    /// <summary>
    /// Remove all cached previews. Useful if the user wants to re-extract.
    /// </summary>
    public void Clear()
    {
        if (Directory.Exists(_cacheDir))
            Directory.Delete(_cacheDir, recursive: true);
        Directory.CreateDirectory(_cacheDir);
    }
}

/// <summary>
/// Plain-data result from <see cref="PreviewCache.LoadLinearRaw"/>. Lives in Core
/// so PreviewCache stays decoupled from the Raw assembly; callers project it into
/// their own LinearRawImage type.
/// </summary>
public sealed record LinearRawCacheEntry(int Width, int Height, ushort[] Pixels);
