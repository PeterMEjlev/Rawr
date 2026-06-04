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

    /// <summary>
    /// Delete a photo's cached thumbnail + preview so the next pass re-extracts
    /// them. Best-effort; never throws. Used by the one-time JPEG blur-fix
    /// migration (see <see cref="NeedsJpegBlurFix"/>) — the linear-RAW buffer is
    /// left alone, so RAW caches are untouched.
    /// </summary>
    public void InvalidatePreview(string fileName)
    {
        try { var p = GetThumbnailPath(fileName); if (File.Exists(p)) File.Delete(p); } catch { }
        try { var p = GetPreviewPath(fileName); if (File.Exists(p)) File.Delete(p); } catch { }
    }

    // One-time migration marker. Thumbs/previews cached before the WIC blur fix
    // were upscaled from the tiny embedded EXIF thumbnail, so they're full-width
    // but soft and can't be told apart from good caches by dimensions. We drop
    // and re-extract the JPEG ones once per cache dir, gated by this sentinel so
    // it doesn't repeat on every folder open.
    private string JpegBlurFixMarkerPath => Path.Combine(_cacheDir, ".jpegblurfix");

    public bool NeedsJpegBlurFix() => !File.Exists(JpegBlurFixMarkerPath);

    public void MarkJpegBlurFixDone()
    {
        try { File.WriteAllText(JpegBlurFixMarkerPath, "1"); } catch { }
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
    // v2: bumped after the WB fix in LibRawExtractor. v1 caches written by the
    // pre-fix decode contain pure-R-with-zero-G/B pixels (the partial-WB bug),
    // so we invalidate them en masse rather than rely on users manually deleting
    // *_linearraw.bin files.
    // v3: bumped after the Downsample integer-factor no-op fix. v2 caches were
    // persisted at full half-size resolution (the "downsample" returned the image
    // untouched for non-integer ratios), so they are ~3-4x oversized. Invalidating
    // them en masse reclaims the disk on first re-visit instead of stranding the
    // bloat until the user manually clears the cache.
    private const int LinearRawVersion = 3;
    private const int LinearRawHeaderBytes = 32;

    /// <summary>
    /// Try to load a previously-cached downsampled linear RAW for this file. Returns
    /// null if the cache is missing, malformed, or stale relative to the source RAW.
    /// </summary>
    public LinearRawCacheEntry? LoadLinearRaw(string fileName, string sourceRawPath)
    {
        var path = GetLinearRawPath(fileName);
        if (!File.Exists(path)) return null;

        // Declared outside the try so the finally can see it (a variable declared
        // inside a try body is out of scope in its finally).
        bool deadFile = false;
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

            // A file that is structurally unreadable by *this* build (wrong
            // magic/version, impossible dimensions, length inconsistent with its
            // own header, truncated) can never be a cache hit for anyone — it is
            // pure dead weight. Delete it instead of just missing, so a version
            // bump (e.g. the Downsample size fix that obsoleted every v2 buffer)
            // reclaims the disk on first touch rather than stranding ~2x-oversized
            // files until each photo happens to be revisited. A mere source
            // size/ticks mismatch is NOT treated as dead: the next decode
            // overwrites it, and deleting on a transiently-odd source (network
            // drop) would throw away a still-valid cache.
            LinearRawCacheEntry entry;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (fs.Length < LinearRawHeaderBytes) { deadFile = true; return null; }

                Span<byte> header = stackalloc byte[LinearRawHeaderBytes];
                int readTotal = 0;
                while (readTotal < LinearRawHeaderBytes)
                {
                    int n = fs.Read(header[readTotal..]);
                    if (n <= 0) { deadFile = true; return null; }
                    readTotal += n;
                }

                uint magic = BitConverter.ToUInt32(header[..4]);
                int version = BitConverter.ToInt32(header[4..8]);
                int width = BitConverter.ToInt32(header[8..12]);
                int height = BitConverter.ToInt32(header[12..16]);
                long size = BitConverter.ToInt64(header[16..24]);
                long ticks = BitConverter.ToInt64(header[24..32]);

                if (magic != LinearRawMagic || version != LinearRawVersion) { deadFile = true; return null; }
                if (width <= 0 || height <= 0 || width > 32768 || height > 32768) { deadFile = true; return null; }
                if (size != expectedSize || ticks != expectedTicks) return null;

                long pixelBytes = (long)width * height * 3 * 2;
                if (fs.Length - LinearRawHeaderBytes != pixelBytes) { deadFile = true; return null; }

                var pixels = new ushort[width * height * 3];
                var pixelSpan = System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixels.AsSpan());
                int copied = 0;
                while (copied < pixelSpan.Length)
                {
                    int n = fs.Read(pixelSpan[copied..]);
                    if (n <= 0) { deadFile = true; return null; }
                    copied += n;
                }

                entry = new LinearRawCacheEntry(width, height, pixels);
            }

            // Stamp the file as recently used so PruneLinearRaw's LRU ordering
            // reflects real access, not write order. Windows disables NTFS
            // last-access-time updates by default, so last-write-time re-stamped
            // on each hit is the only dependable recency signal. Cache validity
            // is keyed on the *source RAW* size/ticks embedded in the header
            // above — never on this file's own timestamp — so re-stamping it
            // can't make a stale cache look fresh. Best-effort; loss is harmless.
            try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch { }

            return entry;
        }
        catch
        {
            return null;
        }
        finally
        {
            // Runs after the using has released the handle, so the delete can't
            // hit a sharing violation. Only fires for provably-dead files.
            if (deadFile) { try { File.Delete(path); } catch { } }
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
    /// One-pass sweep that deletes every *_linearraw.bin whose header magic or
    /// version doesn't match the current build. <see cref="LoadLinearRaw"/>
    /// already self-deletes a dead file when its photo is visited, but that only
    /// reclaims space as the user navigates; on a version bump the bulk of the
    /// cache is obsolete the moment the new build runs. Calling this on folder
    /// open reclaims it all immediately (e.g. ~2x-oversized v2 buffers after the
    /// Downsample fix) instead of leaving it stranded behind a disk budget that,
    /// by coincidence of being just above the bloated total, may never trigger.
    /// Only an 8-byte read per file; best-effort, never throws.
    /// </summary>
    public void PruneStaleLinearRaw()
    {
        try
        {
            var dir = new DirectoryInfo(_cacheDir);
            if (!dir.Exists) return;

            Span<byte> head = stackalloc byte[8];
            foreach (var f in dir.GetFiles("*_linearraw.bin"))
            {
                bool stale;
                try
                {
                    using var fs = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
                    int read = 0;
                    while (read < head.Length)
                    {
                        int n = fs.Read(head[read..]);
                        if (n <= 0) break;
                        read += n;
                    }
                    // Too short to even hold magic+version, or the format/version
                    // tag doesn't match: unreadable by this build → stale.
                    stale = read < 8
                        || BitConverter.ToUInt32(head[..4]) != LinearRawMagic
                        || BitConverter.ToInt32(head[4..8]) != LinearRawVersion;
                }
                catch { continue; }   // locked / vanished — leave it for next time

                if (stale) { try { f.Delete(); } catch { } }
            }
        }
        catch { /* enumeration failed — leave the cache untouched */ }
    }

    /// <summary>
    /// Enforce a disk budget for the linear-RAW cache. The *_linearraw.bin files
    /// are by far the largest cache artifacts (uncompressed 16-bit RGB — bigger
    /// per photo than the source RAW), so left unbounded a few large shoots make
    /// the cache dwarf the originals. Deletes least-recently-used .bin files —
    /// ordered by last-write time, which <see cref="LoadLinearRaw"/> re-stamps on
    /// every cache hit so it tracks real usage — until the total fits the budget.
    /// The tiny JPEG thumb/preview files are never touched (cheap to keep, costly
    /// to regenerate). Best-effort: any IO failure just stops pruning early; it
    /// never throws, so callers can fire it after a save without guarding.
    /// </summary>
    /// <param name="budgetBytes">
    /// Maximum total bytes of *_linearraw.bin to retain. &lt;= 0 disables pruning.
    /// </param>
    /// <param name="keepFileName">
    /// Optional source filename whose .bin is retained regardless of age — the
    /// photo currently being decoded, so an in-flight load can't evict itself.
    /// </param>
    public void PruneLinearRaw(long budgetBytes, string? keepFileName = null)
    {
        if (budgetBytes <= 0) return;

        try
        {
            var dir = new DirectoryInfo(_cacheDir);
            if (!dir.Exists) return;

            var files = dir.GetFiles("*_linearraw.bin");
            long total = 0;
            foreach (var f in files) total += f.Length;
            if (total <= budgetBytes) return;

            var keepPath = keepFileName != null ? GetLinearRawPath(keepFileName) : null;

            // Oldest last-write first == least-recently-used (LoadLinearRaw bumps
            // the timestamp on each hit), so eviction sheds the coldest data first.
            foreach (var f in files.OrderBy(f => f.LastWriteTimeUtc))
            {
                if (total <= budgetBytes) break;
                if (keepPath != null &&
                    string.Equals(f.FullName, keepPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                long len = f.Length;
                try
                {
                    f.Delete();
                    total -= len;
                }
                catch { /* locked or already gone — skip it, keep pruning the rest */ }
            }
        }
        catch { /* enumeration failed — leave the cache untouched */ }
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
