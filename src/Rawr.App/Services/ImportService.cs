using System.IO;

namespace Rawr.App.Services;

public sealed record ImportProgress(
    int FilesCompleted,
    int FilesTotal,
    long BytesCompleted,
    long BytesTotal,
    string CurrentFile);

public sealed record ImportResult(
    int Copied,
    int Skipped,
    int Failed,
    long BytesCopied,
    IReadOnlyList<string> CopiedDestinations);

/// <summary>
/// Copies a set of source files into a destination folder. Skips files where a
/// same-named file with identical byte size already exists at the destination.
/// Never deletes from the source.
/// </summary>
public static class ImportService
{
    /// <param name="targetFolderResolver">
    /// Maps a source path to the absolute folder it should be copied into. Lets
    /// the caller route categories (RAW / JPEG / Video) into subfolders. When
    /// null, every file goes straight into <paramref name="destinationFolder"/>
    /// (the original flat behaviour).
    /// </param>
    public static async Task<ImportResult> CopyAsync(
        IReadOnlyList<string> sources,
        string destinationFolder,
        Func<string, string>? targetFolderResolver,
        IProgress<ImportProgress>? progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(destinationFolder);

        // Total bytes for progress reporting.
        long bytesTotal = 0;
        foreach (var src in sources)
        {
            try { bytesTotal += new FileInfo(src).Length; }
            catch { /* unreadable — skip from total, will fail later */ }
        }

        int copied = 0, skipped = 0, failed = 0;
        long bytesDone = 0;
        var copiedPaths = new List<string>(sources.Count);

        for (int i = 0; i < sources.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var src = sources[i];
            var name = Path.GetFileName(src);
            var targetFolder = targetFolderResolver?.Invoke(src) ?? destinationFolder;
            Directory.CreateDirectory(targetFolder);
            var dst = Path.Combine(targetFolder, name);

            long size = 0;
            try { size = new FileInfo(src).Length; } catch { }

            progress?.Report(new ImportProgress(i, sources.Count, bytesDone, bytesTotal, name));

            try
            {
                if (File.Exists(dst))
                {
                    long existing = new FileInfo(dst).Length;
                    if (existing == size)
                    {
                        skipped++;
                        bytesDone += size;
                        continue;
                    }
                    // Same name, different size → don't overwrite; pick a unique name.
                    dst = NextAvailableName(targetFolder, name);
                }

                await CopyFileAsync(src, dst, ct);
                CopySidecars(src, targetFolder);
                copied++;
                copiedPaths.Add(dst);
                bytesDone += size;
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                failed++;
            }
        }

        progress?.Report(new ImportProgress(sources.Count, sources.Count, bytesDone, bytesTotal, ""));
        return new ImportResult(copied, skipped, failed, bytesDone, copiedPaths);
    }

    private static string NextAvailableName(string folder, string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (int n = 1; n < 10000; n++)
        {
            var candidate = Path.Combine(folder, $"{stem} ({n}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(folder, $"{stem}_{Guid.NewGuid():N}{ext}");
    }

    private static async Task CopyFileAsync(string src, string dst, CancellationToken ct)
    {
        const int bufferSize = 1 << 20; // 1 MiB
        await using var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(dst, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize, FileOptions.Asynchronous);
        await input.CopyToAsync(output, bufferSize, ct);
    }

    private static readonly string[] SidecarExtensions = { ".xmp", ".thm", ".lrv" };

    private static void CopySidecars(string src, string destFolder)
    {
        var srcDir = Path.GetDirectoryName(src);
        if (string.IsNullOrEmpty(srcDir)) return;
        var stem = Path.GetFileNameWithoutExtension(src);

        foreach (var ext in SidecarExtensions)
        {
            var sidecar = Path.Combine(srcDir, stem + ext);
            if (!File.Exists(sidecar)) continue;
            var target = Path.Combine(destFolder, stem + ext);
            try
            {
                if (File.Exists(target) && new FileInfo(target).Length == new FileInfo(sidecar).Length)
                    continue;
                File.Copy(sidecar, target, overwrite: false);
            }
            catch { /* sidecar copy is best-effort */ }
        }
    }
}
