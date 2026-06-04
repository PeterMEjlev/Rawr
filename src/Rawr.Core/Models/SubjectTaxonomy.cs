namespace Rawr.Core.Models;

/// <summary>
/// The shallow two-level grouping over <see cref="SubjectTag"/>: each group root
/// owns zero or more leaf categories. Standalone categories are modelled as a
/// group with no leaves so a single ordered list drives both the sidebar UI and
/// the leaf→group rollup the classifier applies.
///
/// Display concerns (glyphs, labels) live in the App layer — this stays
/// UI-free so Rawr.Core keeps no presentation dependency.
/// </summary>
public static class SubjectTaxonomy
{
    /// <summary>A group root and the leaf categories it owns (empty for standalones).</summary>
    public sealed record GroupDef(SubjectTag Group, IReadOnlyList<SubjectTag> Leaves);

    /// <summary>
    /// Ordered group definitions. This is the single source of truth for both the
    /// sidebar layout order and the rollup table below.
    /// </summary>
    public static readonly IReadOnlyList<GroupDef> Groups = new[]
    {
        new GroupDef(SubjectTag.Person,       Array.Empty<SubjectTag>()),
        new GroupDef(SubjectTag.Animal,       new[] { SubjectTag.Dog, SubjectTag.Cat, SubjectTag.Bird, SubjectTag.Horse, SubjectTag.Wildlife }),
        new GroupDef(SubjectTag.Vehicle,      new[] { SubjectTag.Car, SubjectTag.Plane, SubjectTag.Bike, SubjectTag.Boat, SubjectTag.Train }),
        new GroupDef(SubjectTag.Nature,       new[] { SubjectTag.Mountain, SubjectTag.Forest, SubjectTag.Water, SubjectTag.Beach, SubjectTag.Sky }),
        new GroupDef(SubjectTag.Architecture, Array.Empty<SubjectTag>()),
        new GroupDef(SubjectTag.Food,         Array.Empty<SubjectTag>()),
    };

    // Precomputed (group bit, OR of its leaf bits) pairs for groups that have
    // leaves. Used by ApplyGroupRollup on the per-photo hot path.
    private static readonly (SubjectTag Group, SubjectTag LeafMask)[] Rollup = BuildRollup();

    private static (SubjectTag, SubjectTag)[] BuildRollup()
    {
        var list = new List<(SubjectTag, SubjectTag)>();
        foreach (var g in Groups)
        {
            if (g.Leaves.Count == 0) continue;
            SubjectTag leafMask = SubjectTag.None;
            foreach (var leaf in g.Leaves) leafMask |= leaf;
            list.Add((g.Group, leafMask));
        }
        return list.ToArray();
    }

    /// <summary>
    /// Hybrid grouping rule: ensure every group bit is set whenever any of its
    /// leaves is. The classifier scores group roots independently too, so this
    /// only adds bits the leaf hits imply — the result is a superset of the
    /// input where each group ⊇ its leaves.
    /// </summary>
    public static SubjectTag ApplyGroupRollup(SubjectTag mask)
    {
        foreach (var (group, leafMask) in Rollup)
            if ((mask & leafMask) != 0) mask |= group;
        return mask;
    }
}
