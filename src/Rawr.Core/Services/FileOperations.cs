using Rawr.Core.Models;

namespace Rawr.Core.Services;

public static class FileOperations
{
    public static async Task ExportFileListAsync(
        IEnumerable<PhotoItem> photos,
        string outputPath,
        CancellationToken ct = default)
    {
        var lines = photos.Select(p => p.FilePath);
        await File.WriteAllLinesAsync(outputPath, lines, ct);
    }
}
