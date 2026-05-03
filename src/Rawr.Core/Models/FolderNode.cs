using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Rawr.Core.Services;

namespace Rawr.Core.Models;

/// <summary>
/// One node in the folder-browser TreeView. Children load lazily the first
/// time the node is expanded; an empty placeholder child is added at
/// construction so WPF renders an expand chevron without us pre-scanning
/// the whole subtree.
/// </summary>
public sealed partial class FolderNode : ObservableObject
{
    public string Name { get; }
    public string FullPath { get; }
    public bool IsPlaceholder { get; init; }

    /// <summary>
    /// Count of supported image/video files in this folder (non-recursive).
    /// Computed once at construction. Zero for the placeholder, drives, or
    /// inaccessible paths.
    /// </summary>
    public int MediaFileCount { get; }

    public ObservableCollection<FolderNode> Children { get; } = [];

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;

    private bool _hasLoadedChildren;

    public FolderNode(string name, string fullPath)
    {
        Name = name;
        FullPath = fullPath;
        if (!string.IsNullOrEmpty(fullPath))
        {
            MediaFileCount = FolderScanner.CountSupportedFiles(fullPath);
            if (DirectoryHasSubfolders(fullPath))
                Children.Add(new FolderNode(string.Empty, string.Empty) { IsPlaceholder = true });
        }
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !_hasLoadedChildren)
            LoadChildren();
    }

    /// <summary>
    /// Re-enumerates this folder's children from disk and expands the node so
    /// the refreshed list is visible. Call after creating/deleting subfolders.
    /// </summary>
    public void RefreshChildren()
    {
        if (string.IsNullOrEmpty(FullPath)) return;
        LoadChildren();
        IsExpanded = true;
    }

    private void LoadChildren()
    {
        _hasLoadedChildren = true;
        Children.Clear();
        if (string.IsNullOrEmpty(FullPath)) return;

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(FullPath)
                .Where(d => !IsHiddenFromTree(d))
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                Children.Add(new FolderNode(Path.GetFileName(dir), dir));
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }

    private static bool DirectoryHasSubfolders(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path).Any(d => !IsHiddenFromTree(d));
        }
        catch
        {
            return false;
        }
    }

    // RAWR's per-folder metadata directory (.rawr) is internal bookkeeping; users
    // should never see or navigate into it from the folder tree.
    private static bool IsHiddenFromTree(string directoryPath) =>
        string.Equals(Path.GetFileName(directoryPath), ".rawr", StringComparison.OrdinalIgnoreCase);
}
