using System.IO;
using DiskTree.Core;

namespace DiskTree.Features.Search;

/// <summary>One search result: plain data. How it looks in the list is up to <see cref="SearchHitRow"/>.</summary>
public sealed record SearchHit(string Path, string Name, string Folder, long Size, bool IsDir, Node Node)
{
    // Filled for file hits only (the folder search works on the scanned tree, which has no per-folder dates).
    public long Modified { get; init; }
    public long Created { get; init; }
    public long Accessed { get; init; }
    public FileAttributes Attributes { get; init; }

    /// <summary>The timestamp the date filter used, so the list shows the date that made the file match.</summary>
    public AgeStamp? ShownStamp { get; init; }
    public bool ShowAttributes { get; init; }
}
