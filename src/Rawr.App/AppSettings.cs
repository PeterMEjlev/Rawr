using System.IO;
using System.Text.Json;
using Rawr.App.ViewModels;

namespace Rawr.App;

public enum BurstThumbnailMode { HighestRated, FirstChronological }

public enum ClippingMode { Highlights, Shadows, Both }

public sealed class AppSettings
{
    public static AppSettings Current { get; set; } = new();

    public int BurstMaxGapSeconds { get; set; } = 2;

    // 0 = group anything within the time gap (visual filter effectively off).
    // 100 = only near-identical photos group. 50 ≈ the BurstDetector defaults.
    public int BurstSimilarityStrictness { get; set; } = 50;

    public BurstThumbnailMode BurstThumbnailMode { get; set; } = BurstThumbnailMode.HighestRated;
    public string DateFormat { get; set; } = "dd-MM-yyyy  HH:mm:ss";
    public bool CollapseBurstsOnOpen { get; set; } = true;
    public SortField DefaultSortField { get; set; } = SortField.FileName;

    public byte FocusPeakingThreshold { get; set; } = 60;

    public ClippingMode ClippingMode { get; set; } = ClippingMode.Highlights;

    // Threshold defines what counts as "rail-clipped" on the linear sensor scale:
    // a highlight pixel is flagged when any channel ≥ ClippingThreshold% of max,
    // a shadow when every channel is within (100 − ClippingThreshold)% of black.
    // 99 means strictly near-saturated; lower values are more permissive.
    public byte ClippingThreshold { get; set; } = 99;

    // Gates the sidebar "Clipped Highlights" / "Crushed Shadows" buckets: a photo
    // shows up only when the share of its (thumbnail) pixels flagged at the
    // per-pixel threshold above meets or exceeds this percentage.
    public byte ClippedAreaThreshold { get; set; } = 5;

    public double DoubleClickZoom { get; set; } = 3.0;

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
        BurstSimilarityStrictness = BurstSimilarityStrictness,
        BurstThumbnailMode = BurstThumbnailMode,
        DateFormat = DateFormat,
        CollapseBurstsOnOpen = CollapseBurstsOnOpen,
        DefaultSortField = DefaultSortField,
        FocusPeakingThreshold = FocusPeakingThreshold,
        ClippingMode = ClippingMode,
        ClippingThreshold = ClippingThreshold,
        ClippedAreaThreshold = ClippedAreaThreshold,
        DoubleClickZoom = DoubleClickZoom,
        KeyBindings = new Dictionary<string, string>(KeyBindings),
    };

}
