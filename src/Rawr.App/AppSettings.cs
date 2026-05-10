using System.IO;
using System.Text.Json;
using Rawr.App.Shortcuts;
using Rawr.App.ViewModels;
using Rawr.Core.Services;

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

    // ── HDR auto-grouping ──
    // Detector classifies tightly-aligned bursts that span a meaningful exposure
    // range as HDR / auto-bracket sequences and applies the system "HDR" tag.
    public bool HdrDetectionEnabled { get; set; } = true;
    public int HdrMinBracketSize { get; set; } = 3;          // frames in a bracket
    public float HdrMinExposureSpread { get; set; } = 0.9f;  // total EV range across the bracket

    // ── Panorama auto-grouping ──
    // Detector chains adjacent same-camera frames whose inter-frame shift looks
    // like a coherent camera pan, applies the system "Panorama" tag, and assigns
    // a fresh GroupId so the burst-collapse toggle stacks the sweep.
    public bool PanoramaDetectionEnabled { get; set; } = true;
    public int PanoramaMinChainSize { get; set; } = 3;
    public int PanoramaMaxGapSeconds { get; set; } = 20;
    // Frame-to-frame overlap range, in percent. Below the minimum the detector
    // assumes unrelated shots; above the maximum it assumes a regular burst.
    public int PanoramaMinOverlapPct { get; set; } = 20;
    public int PanoramaMaxOverlapPct { get; set; } = 85;
    // Maximum allowed direction change between consecutive panorama edges.
    public int PanoramaDirectionToleranceDeg { get; set; } = 30;

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

    // Gate for the sidebar "Closed eyes" bucket and filter chip. An eye is
    // considered closed when the classifier's "open" probability falls below
    // this threshold (range 0–100 == 0.0–1.0). Higher = stricter (fewer photos
    // pass); 50 ≈ "the model thinks it's more likely closed than open".
    public byte ClosedEyeThreshold { get; set; } = 50;

    public double DoubleClickZoom { get; set; } = 3.0;
    public int ScrollSpeedPercent { get; set; } = Rawr.App.Controls.ScrollSpeed.DefaultPercent;

    // Step size for the Shift+Left/Right video seek shortcuts.
    public int VideoSeekStepSeconds { get; set; } = 5;

    // Keys are ShortcutAction.Id. Value is a serialized KeySpec ("Ctrl+Shift+X"),
    // or empty string to mean "explicitly unbound". Missing entries fall back to the default.
    public Dictionary<string, string> KeyBindings { get; set; } = new();

    // User-defined keyboard macros. Each binds a single key combo to a sequence
    // of edits (flag, rating, color label, tag) applied to the current selection
    // as one undoable step.
    public List<KeyboardMacro> Macros { get; set; } = new();

    // Per-LOG-profile adjust-filter overrides. Key is LogProfile enum name
    // (e.g. "SLog3"). Missing entries fall back to LogProfilePreset.For defaults.
    public Dictionary<string, LogProfilePreset> LogProfileOverrides { get; set; } = new();

    // Returns the user's customized preset for this profile, or the built-in
    // default if untouched. Always returns a fresh instance.
    public LogProfilePreset GetLogProfilePreset(LogProfile profile) =>
        LogProfileOverrides.TryGetValue(profile.ToString(), out var v)
            ? v.Clone()
            : LogProfilePreset.For(profile);

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
        HdrDetectionEnabled = HdrDetectionEnabled,
        HdrMinBracketSize = HdrMinBracketSize,
        HdrMinExposureSpread = HdrMinExposureSpread,
        PanoramaDetectionEnabled = PanoramaDetectionEnabled,
        PanoramaMinChainSize = PanoramaMinChainSize,
        PanoramaMaxGapSeconds = PanoramaMaxGapSeconds,
        PanoramaMinOverlapPct = PanoramaMinOverlapPct,
        PanoramaMaxOverlapPct = PanoramaMaxOverlapPct,
        PanoramaDirectionToleranceDeg = PanoramaDirectionToleranceDeg,
        DateFormat = DateFormat,
        CollapseBurstsOnOpen = CollapseBurstsOnOpen,
        DefaultSortField = DefaultSortField,
        FocusPeakingThreshold = FocusPeakingThreshold,
        ClippingMode = ClippingMode,
        ClippingThreshold = ClippingThreshold,
        ClippedAreaThreshold = ClippedAreaThreshold,
        ClosedEyeThreshold = ClosedEyeThreshold,
        DoubleClickZoom = DoubleClickZoom,
        ScrollSpeedPercent = ScrollSpeedPercent,
        VideoSeekStepSeconds = VideoSeekStepSeconds,
        KeyBindings = new Dictionary<string, string>(KeyBindings),
        LogProfileOverrides = LogProfileOverrides.ToDictionary(kv => kv.Key, kv => kv.Value.Clone()),
        Macros = Macros.Select(m => new KeyboardMacro
        {
            Id = m.Id,
            Name = m.Name,
            KeyBinding = m.KeyBinding,
            SetFlag = m.SetFlag,
            SetRating = m.SetRating,
            SetColorLabel = m.SetColorLabel,
            TagName = m.TagName,
        }).ToList(),
    };

}
