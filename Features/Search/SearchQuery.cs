using DiskTree.Core;

namespace DiskTree.Features.Search;

public sealed class SearchQuery
{
    public required Node Scope { get; init; }
    public string Text { get; init; } = "";
    public long MinSize { get; init; }
    /// <summary>Lower-case extensions including the dot (".mp4"); empty = any.</summary>
    public string[] Extensions { get; init; } = [];
    /// <summary>Search files on disk instead of folders in the scanned tree.</summary>
    public bool Files { get; init; }
    /// <summary>Date / attribute conditions (files only); null = none.</summary>
    public FileFilter? Filter { get; init; }
}
