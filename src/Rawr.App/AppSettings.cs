using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Rawr.App.Services;
using Rawr.App.Shortcuts;
using Rawr.App.ViewModels;
using Rawr.Core.Models;

namespace Rawr.App;

public enum BurstThumbnailMode { HighestRated, FirstChronological }

public enum ClippingMode { Highlights, Shadows, Both }

// When an ML classification pass runs. Auto = automatically after a folder
// loads; Manual = only when the user picks it from the toolbar Analyze menu;
// Off = never (and the toolbar entry is hidden). Applies independently to the
// subject classifier and the closed-eye detector.
public enum ClassificationRunMode { Auto, Manual, Off }

// How a single media category (RAW / JPEG / Video) is handled on card import.
// MainFolder keeps the original flat behaviour (file goes straight into the
// chosen destination). Subfolder routes it into a named subfolder under the
// destination. Skip excludes the whole category from the import.
public enum ImportRouteMode { MainFolder, Subfolder, Skip }

public sealed class ImportTypeRule
{
    public ImportRouteMode Mode { get; set; } = ImportRouteMode.MainFolder;
    public string Subfolder { get; set; } = "";

    public ImportTypeRule Clone() => new() { Mode = Mode, Subfolder = Subfolder };
}

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

    // Sticky global preference for the toolbar "this folder / + subfolders"
    // toggle. Persisted so the user's choice survives folder switches and app
    // restarts.
    public bool IncludeSubfolders { get; set; } = true;

    // User-chosen display order of the QUICK FILTERS sidebar subsections, stored
    // as a list of subsection keys (the Tag on each wrapper in MainWindow.xaml:
    // "Rated", "Flagged", "Exposure", "Subjects", "Faces", "Labelled", "Tags").
    // Empty = keep the XAML default order. Unknown/new keys not in the list fall
    // back to their original XAML position, appended after the ordered ones, so
    // adding a subsection later doesn't require a migration.
    public List<string> QuickFilterOrder { get; set; } = new();

    // User-facing "strictness" (10–100), driven by the Settings slider. Maps
    // onto the adaptive threshold multipliers in FocusPeakingOptions — higher
    // shifts every confidence band up (fewer, sharper peaks).
    public byte FocusPeakingThreshold { get; set; } = 60;

    // Advanced focus-peaking math knobs (operator, mode, multipliers, denoise,
    // cleanup, overlay style). Persisted as a nested object; defaults are tuned
    // and need no UI to be usable — edit settings.json or wire up controls later.
    public FocusPeakingOptions FocusPeaking { get; set; } = new();

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

    // ── Subject classifier (zero-shot CLIP) ──
    // Controls when the tiny image-encoder ONNX runs over each photo to apply
    // coarse SubjectTag flags (person / landscape / food / animal). Auto runs
    // the pass automatically after folder open; Manual waits for the toolbar
    // Analyze menu; Off skips it entirely (previously-classified photos keep
    // their tags so the filter chips stay usable).
    public ClassificationRunMode SubjectClassificationMode { get; set; } = ClassificationRunMode.Auto;

    // Legacy pre-3-mode flag, kept only so settings.json written by older
    // builds migrates cleanly in Load() (true → Auto, false → Off). Null in
    // current files; never written back out (see JsonIgnore below).
    [JsonInclude]
    [JsonPropertyName("SubjectClassificationEnabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacySubjectClassificationEnabled { get; set; }

    // ── Closed-eye detector (YuNet faces + eye-state CNN) ──
    // Controls when the face/closed-eye analysis runs. Auto runs it
    // automatically after folder open; Manual waits for the toolbar Analyze
    // menu; Off skips it (and hides the menu entry).
    public ClassificationRunMode ClosedEyeDetectionMode { get; set; } = ClassificationRunMode.Auto;

    // Top-level softmax probability gate, scaled 0..100 (see SubjectClassifier
    // for the decision rule — categories compete in a temperature-scaled softmax
    // against each other plus a background anchor, rather than each racing an
    // absolute cosine floor). A category is applied when its probability ≥
    // value/100. Lower = more permissive (more tags per photo). 22 is a sensible
    // default; raise it if you see false positives, lower it if subjects are missed.
    // Acts as the fallback default for any group without a per-group override below.
    public byte SubjectTagThreshold { get; set; } = 22;

    // Per-group overrides of SubjectTagThreshold, keyed by the top-level group's
    // SubjectTag name ("Person", "Animal", "Vehicle", "Nature", "Architecture",
    // "Food"). A group absent from the map falls back to SubjectTagThreshold, so
    // the user can demand more certainty for a group the model struggles with
    // (e.g. raise "Animal" to suppress spurious Dog/Bird) without touching the rest.
    public Dictionary<string, byte> SubjectGroupThresholds { get; set; } = new();

    /// <summary>
    /// Effective subject-match threshold (0..100) for a top-level group: the
    /// per-group override when present, otherwise the global
    /// <see cref="SubjectTagThreshold"/>.
    /// </summary>
    public byte GetSubjectGroupThreshold(SubjectTag group) =>
        SubjectGroupThresholds.TryGetValue(group.ToString(), out var v) ? v : SubjectTagThreshold;

    public double DoubleClickZoom { get; set; } = 3.0;
    public int ScrollSpeedPercent { get; set; } = Rawr.App.Controls.ScrollSpeed.DefaultPercent;
    public bool ReverseFilmstripScroll { get; set; } = false;

    // When true, RAWR never decodes the linear RAW sensor data for previews —
    // it sticks with the camera's embedded JPEG. Avoids the colour noise that
    // shows up at zoom on RAW. Side effect: clipping detection (which needs
    // the linear sensor data) won't paint, and exposure compensation falls
    // back to the JPEG path (less accurate at the extremes).
    public bool UseEmbeddedJpegOnly { get; set; } = false;

    // Per-folder disk budget (MB) for the linear-RAW cache (.rawr/cache/*_linearraw.bin).
    // Each buffer is uncompressed 16-bit RGB at ~2400px — roughly the size of the
    // source cRAW itself — so a fully-cached folder's .bin set ≈ the total RAW
    // size. Without a cap below that, the cache necessarily rivals the originals.
    // When the total exceeds this, least-recently-used .bin files are evicted
    // (tiny JPEG thumb/preview files are always kept); evicted photos re-decode
    // (~1-3s) the next time they're visited. 0 disables pruning entirely.
    // 2 GB ≈ ~90 hot photos retained on a 45MP body — tune up for snappier
    // revisits at the cost of disk, down to keep the cache well under the RAWs.
    public int LinearRawCacheBudgetMb { get; set; } = 2048;

    // Step size for the Shift+Left/Right video seek shortcuts.
    public int VideoSeekStepSeconds { get; set; } = 5;

    // When true, selecting a video starts playback immediately. When false, the
    // still thumbnail stays visible until the user presses Space or clicks play.
    public bool AutoPlayVideo { get; set; } = true;

    // Last folder used as the destination of a card import. Empty until the
    // user runs the importer at least once. Used to pre-fill the import dialog.
    public string LastImportDestination { get; set; } = "";

    // Auto-open the import dialog when a camera card is plugged in. Off lets
    // the user trigger import manually via the toolbar button.
    public bool AutoImportOnCardInsert { get; set; } = true;

    // Optional per-type import routing. Default Mode is MainFolder for all, so
    // out of the box every file still lands flat in the chosen destination —
    // the rules only matter once the user opts a category into a subfolder or
    // chooses to skip it. The dialog edits these and they persist on a
    // successful import (same commit point as LastImportDestination).
    public ImportTypeRule ImportRawRule { get; set; } = new() { Subfolder = "RAW" };
    public ImportTypeRule ImportJpegRule { get; set; } = new() { Subfolder = "JPEG" };
    public ImportTypeRule ImportVideoRule { get; set; } = new() { Subfolder = "Video" };

    // Keys are ShortcutAction.Id. Value is a serialized KeySpec ("Ctrl+Shift+X"),
    // or empty string to mean "explicitly unbound". Missing entries fall back to the default.
    public Dictionary<string, string> KeyBindings { get; set; } = new();

    // User-defined keyboard macros. Each binds a single key combo to a sequence
    // of edits (flag, rating, color label, tag) applied to the current selection
    // as one undoable step.
    public List<KeyboardMacro> Macros { get; set; } = new();

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RAWR", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            var json = File.ReadAllText(FilePath);
            var s = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new();

            // Migrate the legacy on/off subject flag to the 3-mode setting. Only
            // applies when an older file actually carried the bool; new files
            // leave it null and keep whatever SubjectClassificationMode they saved.
            if (s.LegacySubjectClassificationEnabled is bool enabled)
            {
                s.SubjectClassificationMode = enabled
                    ? ClassificationRunMode.Auto
                    : ClassificationRunMode.Off;
                s.LegacySubjectClassificationEnabled = null;
            }
            return s;
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
        IncludeSubfolders = IncludeSubfolders,
        QuickFilterOrder = new List<string>(QuickFilterOrder),
        FocusPeakingThreshold = FocusPeakingThreshold,
        FocusPeaking = FocusPeaking.Clone(),
        ClippingMode = ClippingMode,
        ClippingThreshold = ClippingThreshold,
        ClippedAreaThreshold = ClippedAreaThreshold,
        ClosedEyeThreshold = ClosedEyeThreshold,
        SubjectClassificationMode = SubjectClassificationMode,
        ClosedEyeDetectionMode = ClosedEyeDetectionMode,
        SubjectTagThreshold = SubjectTagThreshold,
        SubjectGroupThresholds = new Dictionary<string, byte>(SubjectGroupThresholds),
        DoubleClickZoom = DoubleClickZoom,
        ScrollSpeedPercent = ScrollSpeedPercent,
        ReverseFilmstripScroll = ReverseFilmstripScroll,
        UseEmbeddedJpegOnly = UseEmbeddedJpegOnly,
        LinearRawCacheBudgetMb = LinearRawCacheBudgetMb,
        VideoSeekStepSeconds = VideoSeekStepSeconds,
        AutoPlayVideo = AutoPlayVideo,
        LastImportDestination = LastImportDestination,
        AutoImportOnCardInsert = AutoImportOnCardInsert,
        ImportRawRule = ImportRawRule.Clone(),
        ImportJpegRule = ImportJpegRule.Clone(),
        ImportVideoRule = ImportVideoRule.Clone(),
        KeyBindings = new Dictionary<string, string>(KeyBindings),
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
