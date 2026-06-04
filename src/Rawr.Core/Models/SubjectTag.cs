namespace Rawr.Core.Models;

/// <summary>
/// Coarse subject categories assigned by the zero-shot CLIP-style classifier.
/// Stored as a bitmask on <see cref="PhotoItem.SubjectTags"/>: a photo can carry
/// several tags (e.g. a person on a landscape). <c>None</c> = classifier ran but
/// no category cleared the score threshold; a null <c>SubjectTags?</c> = not
/// classified yet.
///
/// The bit values are persisted to SQLite. Existing values must never be
/// reassigned — append new categories at the next free bit.
/// </summary>
[Flags]
public enum SubjectTag
{
    None      = 0,
    Person    = 1 << 0,
    Landscape = 1 << 1,
    Food      = 1 << 2,
    Animal    = 1 << 3,
}
