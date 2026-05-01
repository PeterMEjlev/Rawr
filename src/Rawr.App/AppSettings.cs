using System.IO;
using System.Text.Json;
using Rawr.App.ViewModels;

namespace Rawr.App;

public enum BurstThumbnailMode { HighestRated, FirstChronological }

public sealed class AppSettings
{
    public static AppSettings Current { get; set; } = new();

    public int BurstMaxGapSeconds { get; set; } = 2;
    public BurstThumbnailMode BurstThumbnailMode { get; set; } = BurstThumbnailMode.HighestRated;
    public string DateFormat { get; set; } = "dd-MM-yyyy  HH:mm:ss";
    public bool CollapseBurstsOnOpen { get; set; } = true;
    public SortField DefaultSortField { get; set; } = SortField.FileName;

    // Keys are ShortcutAction.Id. Value is a serialized KeySpec ("Ctrl+Shift+X"),
    // or empty string to mean "explicitly unbound". Missing entries fall back to the default.
    public Dictionary<string, string> KeyBindings { get; set; } = new();

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RAWR", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new();
        }
        catch { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch { }
    }

    public AppSettings Clone() => new()
    {
        BurstMaxGapSeconds = BurstMaxGapSeconds,
        BurstThumbnailMode = BurstThumbnailMode,
        DateFormat = DateFormat,
        CollapseBurstsOnOpen = CollapseBurstsOnOpen,
        DefaultSortField = DefaultSortField,
        KeyBindings = new Dictionary<string, string>(KeyBindings),
    };

}
