namespace Rawr.Core.Models;

/// <summary>
/// Subject categories assigned by the zero-shot CLIP-style classifier. Stored as
/// a bitmask on <see cref="PhotoItem.SubjectTags"/>: a photo can carry several
/// tags (e.g. a person on a landscape). <c>None</c> = classifier ran but no
/// category cleared the score threshold; a null <c>SubjectTags?</c> = not
/// classified yet.
///
/// The tags form a shallow two-level taxonomy (see <see cref="SubjectTaxonomy"/>):
/// a few <i>group</i> roots (<see cref="Animal"/>, <see cref="Vehicle"/>,
/// <see cref="Nature"/>) each own a handful of <i>leaf</i> categories, plus some
/// standalone categories (<see cref="Person"/>, <see cref="Food"/>,
/// <see cref="Architecture"/>) with no leaves. The classifier scores every tag —
/// group roots included — against its own text embedding, then rolls leaf hits up
/// into their group (<see cref="SubjectTaxonomy.ApplyGroupRollup"/>) so a group
/// bit is always a superset of its leaves (no "Dog but not Animal").
///
/// The bit values are persisted to SQLite. Existing values must never be
/// reassigned — append new categories at the next free bit. Bit 1 was originally
/// <c>Landscape</c>; it has been repurposed as the near-synonymous <see cref="Nature"/>
/// group root, so photos classified by older builds read back as Nature.
/// </summary>
[Flags]
public enum SubjectTag
{
    None      = 0,

    // ── Standalone categories & group roots (bits 0–5) ──
    Person       = 1 << 0,   // standalone
    Nature       = 1 << 1,   // group root (was "Landscape" — repurposed, same meaning)
    Food         = 1 << 2,   // standalone
    Animal       = 1 << 3,   // group root
    Vehicle      = 1 << 4,   // group root
    Architecture = 1 << 5,   // standalone

    // ── Animal leaves (bits 6–10) ──
    Dog      = 1 << 6,
    Cat      = 1 << 7,
    Bird     = 1 << 8,
    Horse    = 1 << 9,
    Wildlife = 1 << 10,

    // ── Vehicle leaves (bits 11–15) ──
    Car   = 1 << 11,
    Plane = 1 << 12,
    Bike  = 1 << 13,
    Boat  = 1 << 14,
    Train = 1 << 15,

    // ── Nature leaves (bits 16–20) ──
    Mountain = 1 << 16,
    Forest   = 1 << 17,
    Water    = 1 << 18,
    Beach    = 1 << 19,
    Sky      = 1 << 20,
}
