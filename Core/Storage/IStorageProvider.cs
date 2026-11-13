using System.IO;

namespace DiskTree.Core.Storage;

/// <summary>
/// A place that can be scanned: the local file system today (<see cref="DiskProvider"/>), later S3 buckets or SFTP
/// servers. The scanner, the tree, snapshots and reports only talk to this interface and use its
/// <see cref="Paths"/> for everything that takes paths apart.
/// <para>
/// Listing is synchronous and runs on the scanner's worker threads; <see cref="Enumerate{T}"/> calls the transform
/// once per entry while the entry is still in the provider's buffer, so the scan only copies what it keeps (a folder
/// name, the name of a big file) and nothing else.
/// </para>
/// Changing things (delete, move, owners) is not part of this interface yet: those features only work on the disk
/// for now and say so (see <see cref="StorageCaps"/>).
/// </summary>
public interface IStorageProvider
{
    /// <summary>Short stable key, stored in snapshot files ("disk").</summary>
    string Id { get; }

    /// <summary>Name for the user ("Dateisystem").</summary>
    string DisplayName { get; }

    StorageCaps Caps { get; }

    PathOps Paths { get; }

    /// <summary>
    /// Does this provider handle <paramref name="text"/> (typed into the path box, passed on the command line)?
    /// If so, <paramref name="location"/> holds the normalized full path. Never throws.
    /// </summary>
    bool TryParse(string text, out StorageLocation location);

    /// <summary>The normalized full path (for the disk: <see cref="Path.GetFullPath(string)"/>; throws like it for invalid paths).</summary>
    string Normalize(string path);

    /// <summary>
    /// Is there a folder at this path? A remote source that cannot be reached (network, sign-in) throws
    /// <see cref="IOException"/> / <see cref="UnauthorizedAccessException"/> with a message for the user instead of
    /// answering false.
    /// </summary>
    bool FolderExists(string path);

    /// <summary>
    /// Where the tree of a scan of <paramref name="path"/> starts: the top of the hierarchy (the drive) with its
    /// capacity if known. Without capacity the tree starts directly at the scanned folder.
    /// </summary>
    StorageRoot ResolveRoot(string path);

    /// <summary>Total and free bytes of the top of the hierarchy (a drive root); false if unknown.</summary>
    bool TryGetCapacity(string rootPath, out long total, out long free);

    /// <summary>
    /// The entries directly inside a folder (no recursion, "." and ".." left out, hidden and system entries included),
    /// each passed through <paramref name="transform"/>. Lazily: the folder is opened when enumeration starts, and
    /// errors (no access, gone) are thrown from there as <see cref="IOException"/> / <see cref="UnauthorizedAccessException"/>.
    /// </summary>
    IEnumerable<T> Enumerate<T>(string folder, EntryTransform<T> transform, ListOptions options);

    /// <summary>Reads a file's content (only with <see cref="StorageCaps.ContentHash"/>).</summary>
    Stream OpenRead(string path);

    /// <summary>
    /// How many folders a scan lists at the same time with this source, or null for the user's choice (the threads box).
    /// Remote sources set it: S3 profits from many parallel requests, an SFTP server allows only a few connections.
    /// </summary>
    int? ScanThreads => null;

    /// <summary>The saved connection this provider belongs to (null: the disk, or a remote source without a profile).</summary>
    SourceProfile? Profile => null;
}
