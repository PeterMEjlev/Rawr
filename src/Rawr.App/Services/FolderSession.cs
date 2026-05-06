using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Rawr.App.ViewModels;
using Rawr.Core.Models;

namespace Rawr.App.Services;

/// <summary>
/// Per-folder "resume where I left off" state — last selection, filter settings,
/// sort, burst-collapse. Stored alongside the culling DB at &lt;folder&gt;/.rawr/session.json
/// so it travels with the photos when copied/moved between machines.
/// </summary>
public sealed class FolderSession
{
    public string? LastSelectedFile { get; set; }

    public RatingFilterMode RatingFilterMode { get; set; }
    public int RatingFilterValue { get; set; }
    public RatingFilterMode RatingCycleMode { get; set; } = RatingFilterMode.Exact;
    public CullFlag? FlagFilter { get; set; }
    public ColorLabel? ColorLabelFilter { get; set; }
    public BurstFilterMode BurstFilter { get; set; } = BurstFilterMode.Any;
    public ImageTypeFilterMode ImageTypeFilter { get; set; } = ImageTypeFilterMode.Any;
    public ExposureFilterMode ExposureFilter { get; set; } = ExposureFilterMode.Any;
    public FaceFilterMode FaceFilter { get; set; } = FaceFilterMode.Any;
    public int? TagFilterId { get; set; }

    public bool RatingFilterExclude { get; set; }
    public bool FlagFilterExclude { get; set; }
    public bool ColorLabelFilterExclude { get; set; }
    public bool TagFilterExclude { get; set; }
    public bool BurstFilterExclude { get; set; }
    public bool ImageTypeFilterExclude { get; set; }
    public bool ExposureFilterExclude { get; set; }
    public bool FaceFilterExclude { get; set; }

    public bool? BurstCollapsed { get; set; }
    public SortField? SortField { get; set; }
    public bool SortDescending { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string FilePathFor(string folderPath) =>
        Path.Combine(folderPath, ".rawr", "session.json");

    public static FolderSession? TryLoad(string folderPath)
    {
        try
        {
            var path = FilePathFor(folderPath);
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<FolderSession>(json, JsonOptions);
        }
        catch { return null; }
    }

    public void Save(string folderPath)
    {
        try
        {
            var path = FilePathFor(folderPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch { /* non-critical */ }
    }
}
