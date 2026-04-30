namespace Rawr.Core.Services;

/// <summary>
/// Scans a folder for supported image files (RAW and JPEG).
/// Designed to return results fast — just a file listing, no I/O on the images themselves.
/// </summary>
public static class FolderScanner
{
    // RAW file extensions. CR3 is the primary target.
    public static readonly HashSet<string> RawExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cr3",   // Canon RAW v3 (main target, including cRAW)
        ".cr2",   // Canon RAW v2
        ".crw",   // Canon RAW (legacy)
        ".nef",   // Nikon
        ".nrw",   // Nikon
        ".arw",   // Sony
        ".orf",   // Olympus / OM System
        ".rw2",   // Panasonic
        ".raf",   // Fujifilm
        ".dng",   // Adobe DNG / Leica / others
        ".pef",   // Pentax
        ".srw",   // Samsung
    };

    public static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4",
        ".mov",
    };

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cr3", ".cr2", ".crw", ".nef", ".nrw", ".arw", ".orf", ".rw2", ".raf", ".dng", ".pef", ".srw",
        ".jpg", ".jpeg",
        ".mp4", ".mov",
    };

    /// <summary>
    /// Returns all supported image files in the given folder (non-recursive).
    /// Files are returned sorted by name for a predictable initial order.
    /// </summary>
    public static List<string> Scan(string folderPath)
    {
        if (!Directory.Exists(folderPath))
            return [];

        return Directory.EnumerateFiles(folderPath)
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f)))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Quick check: does this folder contain any supported image files?
    /// </summary>
    public static bool HasRawFiles(string folderPath)
    {
        if (!Directory.Exists(folderPath))
            return false;

        return Directory.EnumerateFiles(folderPath)
            .Any(f => SupportedExtensions.Contains(Path.GetExtension(f)));
    }

    public static bool IsSupported(string filePath) =>
        SupportedExtensions.Contains(Path.GetExtension(filePath));

    public static bool IsVideo(string filePath) =>
        VideoExtensions.Contains(Path.GetExtension(filePath));
}
