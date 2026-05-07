using Rawr.Core.Models;

namespace Rawr.App.Shortcuts;

/// User-defined keyboard macro. One key combo applies a set of edits
/// (flag, rating, color label, tag) to the current selection in a single
/// undoable step. Persisted in AppSettings.Macros.
public sealed class KeyboardMacro
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string KeyBinding { get; set; } = "";
    public CullFlag? SetFlag { get; set; }
    public int? SetRating { get; set; }
    public ColorLabel? SetColorLabel { get; set; }
    public string? TagName { get; set; }

    public bool HasAnyAction =>
        SetFlag.HasValue
        || SetRating.HasValue
        || SetColorLabel.HasValue
        || !string.IsNullOrWhiteSpace(TagName);
}
