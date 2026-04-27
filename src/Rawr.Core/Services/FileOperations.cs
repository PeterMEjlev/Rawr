using Rawr.Core.Models;

namespace Rawr.Core.Services;

/// <summary>
/// Handles file operations: copy/move selected RAW files to output folder.
/// </summary>
public static class FileOperations
{
    /// <summary>
    /// Copy selected photos to the destination folder.
    /// Returns the count of files successfully copied.
    /// </summary>
    public static async Task<int> CopyFilesAsync(
        IEnumerable<PhotoItem> photos,
        string destinationFolder,
        IProgress<(int current, int total, string fileName)>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(destinationFolder);

        var fileList = photos.ToList();
        int copied = 0;

        for (int i = 0; i < fileList.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var photo = fileList[i];
            var destPath = Path.Combine(destinationFolder, photo.FileName);

            // Avoid overwriting — append suffix if file exists
            destPath = GetUniqueDestPath(destPath);

            await Task.Run(() => File.Copy(photo.FilePath, destPath, overwrite: false), ct);
            copied++;

            progress?.Report((i + 1, fileList.Count, photo.FileName));
        }

        return copied;
    }

    /// <summary>
    /// Move selected photos to the destination folder.
    /// </summary>
    public static async Task<int> MoveFilesAsync(
        IEnumerable<PhotoItem> photos,
        string destinationFolder,
        IProgress<(int current, int total, string fileName)>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(destinationFolder);

        var fileList = photos.ToList();
        int moved = 0;

        for (int i = 0; i < fileList.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var photo = fileList[i];
            var destPath = Path.Combine(destinationFolder, photo.FileName);
            destPath = GetUniqueDestPath(destPath);

            await Task.Run(() => File.Move(photo.FilePath, destPath), ct);
            moved++;

            progress?.Report((i + 1, fileList.Count, photo.FileName));
        }

        return moved;
    }

    /// <summary>
    /// Export a text list of selected file paths.
    /// </summary>
    public static async Task ExportFileListAsync(
        IEnumerable<PhotoItem> photos,
        string outputPath,
        CancellationToken ct = default)
    {
        var lines = photos.Select(p => p.FilePath);
        await File.WriteAllLinesAsync(outputPath, lines, ct);
    }

    private static string GetUniqueDestPath(string path)
    {
        if (!File.Exists(path))
            return path;

        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        int counter = 1;

        string candidate;
        do
        {
            candidate = Path.Combine(dir, $"{name}_{counter}{ext}");
            counter++;
        } while (File.Exists(candidate));

        return candidate;
    }
}
