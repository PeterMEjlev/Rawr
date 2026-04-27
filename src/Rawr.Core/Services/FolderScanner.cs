namespace Rawr.Core.Services;

/// <summary>
/// Scans a folder for supported RAW image files.
/// Designed to return results fast — just a file listing, no I/O on the images themselves.
/// </summary>
public static class FolderScanner
{
    // Supported RAW extensions. CR3 is the primary target.
    // Additional formats can be added here as support is validated.
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
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

    /// <summary>
    /// Returns all supported RAW files in the given folder (non-recursive).
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
    /// Quick check: does this folder contain any supported RAW files?
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
}
