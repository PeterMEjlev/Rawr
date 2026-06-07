namespace Rawr.Core;

/// <summary>
/// Process-wide runtime tunables that need to be read from the low-level
/// projects (<c>Rawr.Core</c>, <c>Rawr.Raw</c>) which can't reference
/// <c>Rawr.App.AppSettings</c>. The app pushes the relevant user settings here
/// (see <c>AppSettings.PushRuntimeTuning</c>) on load and whenever settings are
/// saved, so these stay in sync with the single source of truth in settings.json.
///
/// Fields are <c>volatile</c> because the encoders read them from background
/// (preview-generation / Parallel.ForEach) threads.
/// </summary>
public static class RawrTuning
{
    /// <summary>
    /// JPEG quality (1–100) used when re-encoding cache thumbnails/previews.
    /// Higher = sharper cached previews at the cost of larger <c>.rawr/cache</c>.
    /// </summary>
    public static volatile int CacheJpegQuality = 85;
}
