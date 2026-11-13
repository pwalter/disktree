using DiskTree.Core;
using DiskTree.Core.Storage;

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
    /// <summary>Where the tree was scanned from: file search lists its folders there, hit paths follow its rules.</summary>
    public IStorageProvider Provider { get; init; } = DiskProvider.Instance;
}
