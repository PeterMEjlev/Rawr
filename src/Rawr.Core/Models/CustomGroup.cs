using CommunityToolkit.Mvvm.ComponentModel;

namespace Rawr.Core.Models;

public sealed partial class PhotoTag : ObservableObject
{
    public int Id { get; init; }

    [ObservableProperty] private string _name = "";

    /// <summary>
    /// Number of photos in the current folder that have this tag assigned.
    /// Maintained by the view model after each filter pass; pure UI data.
    /// </summary>
    [ObservableProperty] private int _count;

    /// <summary>
    /// True for tags managed by RAWR itself (e.g. the auto-generated HDR tag).
    /// System tags can't be renamed or deleted from the UI and are auto-restored
    /// if the underlying detection still applies.
    /// </summary>
    public bool IsSystem { get; init; }

    /// <summary>
    /// Optional hex color (e.g. "#FF7A00") used to render this tag instead of the
    /// default tag-pill color. Currently only system tags set this.
    /// </summary>
    public string? Color { get; init; }
}
