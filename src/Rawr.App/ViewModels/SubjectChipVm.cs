using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Rawr.Core.Models;

namespace Rawr.App.ViewModels;

/// <summary>
/// One leaf chip in the sidebar Subjects subsection (e.g. Dog under Animal).
/// <see cref="Count"/> and <see cref="IsActive"/> are refreshed by the view model
/// (<c>RefreshSubjectChips</c>); the rest is static layout metadata.
/// </summary>
public sealed partial class SubjectChipVm : ObservableObject
{
    public SubjectTag Tag { get; }
    public string Glyph { get; }
    public string Label { get; }

    [ObservableProperty] private int _count;
    [ObservableProperty] private bool _isActive;

    public SubjectChipVm(SubjectTag tag, string glyph, string label)
    {
        Tag = tag;
        Glyph = glyph;
        Label = label;
    }
}

/// <summary>
/// A group header chip plus its (possibly empty) leaf chips. Standalone
/// categories — Person, Food, Architecture — are groups with no leaves and
/// therefore aren't expandable. Clicking the header filters on the group tag;
/// thanks to the hybrid rollup that matches every photo carrying any leaf too.
/// </summary>
public sealed partial class SubjectGroupVm : ObservableObject
{
    public SubjectTag Tag { get; }
    public string Glyph { get; }
    public string Label { get; }
    public ObservableCollection<SubjectChipVm> Leaves { get; }
    public bool HasLeaves => Leaves.Count > 0;

    [ObservableProperty] private int _count;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isExpanded;

    public SubjectGroupVm(SubjectTag tag, string glyph, string label, IEnumerable<SubjectChipVm> leaves)
    {
        Tag = tag;
        Glyph = glyph;
        Label = label;
        Leaves = new ObservableCollection<SubjectChipVm>(leaves);
    }
}

/// <summary>
/// Presentation metadata for the subject taxonomy (glyph + friendly label per
/// tag) and the factory that turns <see cref="SubjectTaxonomy.Groups"/> into the
/// bindable group/leaf chips. Kept here, in the App layer, so Rawr.Core stays
/// UI-free.
/// </summary>
public static class SubjectChipCatalog
{
    private static readonly IReadOnlyDictionary<SubjectTag, string> Glyphs = new Dictionary<SubjectTag, string>
    {
        [SubjectTag.Person]       = "🧑",
        [SubjectTag.Animal]       = "🐾",
        [SubjectTag.Dog]          = "🐕",
        [SubjectTag.Cat]          = "🐈",
        [SubjectTag.Bird]         = "🐦",
        [SubjectTag.Horse]        = "🐎",
        [SubjectTag.Wildlife]     = "🦌",
        [SubjectTag.Vehicle]      = "🚗",
        [SubjectTag.Car]          = "🚙",
        [SubjectTag.Plane]        = "✈",
        [SubjectTag.Bike]         = "🚲",
        [SubjectTag.Boat]         = "⛵",
        [SubjectTag.Train]        = "🚆",
        [SubjectTag.Nature]       = "🏞",
        [SubjectTag.Mountain]     = "⛰",
        [SubjectTag.Forest]       = "🌲",
        [SubjectTag.Water]        = "🌊",
        [SubjectTag.Beach]        = "🏖",
        [SubjectTag.Sky]          = "🌅",
        [SubjectTag.Architecture] = "🏛",
        [SubjectTag.Food]         = "🍽",
    };

    // Labels that read better than the bare enum name. Anything absent falls
    // back to the enum name.
    private static readonly IReadOnlyDictionary<SubjectTag, string> Labels = new Dictionary<SubjectTag, string>
    {
        [SubjectTag.Water] = "Water / sea",
        [SubjectTag.Sky]   = "Sky / sunset",
    };

    private static string GlyphFor(SubjectTag tag) => Glyphs.TryGetValue(tag, out var g) ? g : "•";
    private static string LabelFor(SubjectTag tag) => Labels.TryGetValue(tag, out var l) ? l : tag.ToString();

    /// <summary>Build the bindable group/leaf chips in taxonomy order.</summary>
    public static ObservableCollection<SubjectGroupVm> BuildGroups()
    {
        var groups = new ObservableCollection<SubjectGroupVm>();
        foreach (var def in SubjectTaxonomy.Groups)
        {
            var leaves = new List<SubjectChipVm>(def.Leaves.Count);
            foreach (var leaf in def.Leaves)
                leaves.Add(new SubjectChipVm(leaf, GlyphFor(leaf), LabelFor(leaf)));
            groups.Add(new SubjectGroupVm(def.Group, GlyphFor(def.Group), LabelFor(def.Group), leaves));
        }
        return groups;
    }
}
