namespace DiskTree.Core.Storage;

/// <summary>
/// What a storage source can do. The UI and the features ask for these instead of assuming a local disk, so a source
/// without e.g. owners or a recycle bin can be scanned without the matching commands failing.
/// </summary>
[Flags]
public enum StorageCaps : long
{
    None = 0,

    // ---- what the scan can learn
    /// <summary>Total and free space of the top of the tree are known (drive): percentages refer to the whole drive.</summary>
    Capacity = 1L << 0,
    /// <summary>Entries have a creation time.</summary>
    CreatedTime = 1L << 1,
    /// <summary>Entries have a last-access time.</summary>
    AccessedTime = 1L << 2,
    /// <summary>Entries carry Windows file attributes (hidden, system, read-only, …).</summary>
    Attributes = 1L << 3,
    /// <summary>Folders are real objects (not only prefixes of keys, as in S3).</summary>
    Hierarchical = 1L << 4,
    /// <summary>Junctions / symbolic links exist and are skipped by the scan.</summary>
    ReparsePoints = 1L << 5,
    /// <summary>Names differ by case ("a" and "A" are two entries).</summary>
    CaseSensitive = 1L << 6,
    /// <summary>Several folders may be listed at the same time (many worker threads are fine).</summary>
    ParallelListing = 1L << 7,
    /// <summary>Listing a folder is cheap enough to do on demand (the "Dateien" row, file search).</summary>
    LiveFileList = 1L << 8,
    /// <summary>Every request costs money (cloud storage): avoid listing things twice.</summary>
    CostsPerRequest = 1L << 9,
    /// <summary>Objects have a storage class (e.g. S3 STANDARD / GLACIER).</summary>
    StorageClasses = 1L << 10,

    // ---- what the features may do
    /// <summary>File contents can be read (duplicate search hashes them).</summary>
    ContentHash = 1L << 16,
    /// <summary>Owners can be read (the "Besitzer" tab).</summary>
    Owner = 1L << 17,
    /// <summary>Owners / access lists can be changed.</summary>
    Acl = 1L << 18,
    Delete = 1L << 19,
    /// <summary>Deleting can go to a recycle bin.</summary>
    Trash = 1L << 20,
    Move = 1L << 21,
    Rename = 1L << 22,
    ZipCreate = 1L << 23,
    /// <summary>The Windows shell (Explorer context menu, open, properties) knows the paths.</summary>
    ShellMenu = 1L << 24,
}
