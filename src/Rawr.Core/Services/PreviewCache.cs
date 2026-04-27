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
    /// Remove all cached previews. Useful if the user wants to re-extract.
    /// </summary>
    public void Clear()
    {
        if (Directory.Exists(_cacheDir))
            Directory.Delete(_cacheDir, recursive: true);
        Directory.CreateDirectory(_cacheDir);
    }
}
