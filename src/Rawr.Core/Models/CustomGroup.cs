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
}
