using System.Collections;
using System.IO;
using System.IO.Enumeration;

namespace DiskTree.Core.Storage;

/// <summary>
/// The local file system (drives, folders, \\server\share paths): what DiskTree has always scanned. Listing uses
/// <see cref="FileSystemEnumerator{TResult}"/> directly, so an entry is turned into a <see cref="StorageEntry"/> on
/// the stack and handed to the consumer's transform without allocating anything (the name stays a span into the
/// enumerator's buffer).
/// </summary>
public sealed class DiskProvider : IStorageProvider
{
    public const string ProviderId = "disk";

    public static readonly DiskProvider Instance = new();

    private DiskProvider() { }

    public string Id => ProviderId;
    public string DisplayName => "Dateisystem";

    public StorageCaps Caps =>
        StorageCaps.Capacity | StorageCaps.CreatedTime | StorageCaps.AccessedTime | StorageCaps.Attributes
        | StorageCaps.Hierarchical | StorageCaps.ReparsePoints | StorageCaps.ParallelListing | StorageCaps.LiveFileList
        | StorageCaps.ContentHash | StorageCaps.Owner | StorageCaps.Acl | StorageCaps.Delete | StorageCaps.Trash
        | StorageCaps.Move | StorageCaps.Rename | StorageCaps.ZipCreate | StorageCaps.ShellMenu;

    public PathOps Paths => DiskPathOps.Instance;

    public bool TryParse(string text, out StorageLocation location)
    {
        location = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            location = new StorageLocation(this, Path.GetFullPath(text));
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public string Normalize(string path) => Path.GetFullPath(path);

    public bool FolderExists(string path) => Directory.Exists(path);

    public StorageRoot ResolveRoot(string path)
    {
        var full = Path.GetFullPath(path);
        var driveRoot = Path.GetPathRoot(full) ?? full;
        TryGetCapacity(driveRoot, out long total, out long free);
        return new StorageRoot(full, driveRoot, total, free);
    }

    public bool TryGetCapacity(string rootPath, out long total, out long free)
    {
        try
        {
            var di = new DriveInfo(rootPath);
            total = di.TotalSize;
            free = di.AvailableFreeSpace;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // Network paths and the like have no capacity; the tree then just starts at the scanned folder.
            total = free = 0;
            return false;
        }
    }

    public IEnumerable<T> Enumerate<T>(string folder, EntryTransform<T> transform, ListOptions options) =>
        new Listing<T>(folder, transform, options);

    public Stream OpenRead(string path) =>
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);

    private static EnumerationOptions OptionsFor(ListOptions o) => new()
    {
        AttributesToSkip = 0, // include hidden + system
        IgnoreInaccessible = o.IgnoreInaccessible,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    /// <summary>Opens the folder when enumeration starts (like <see cref="FileSystemEnumerable{TResult}"/>).</summary>
    private sealed class Listing<T>(string folder, EntryTransform<T> transform, ListOptions options) : IEnumerable<T>
    {
        public IEnumerator<T> GetEnumerator() => new Enumerator<T>(folder, transform, options);
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class Enumerator<T>(string folder, EntryTransform<T> transform, ListOptions options)
        : FileSystemEnumerator<T>(folder, OptionsFor(options))
    {
        private readonly StampSet _stamps = options.Stamps;

        protected override T TransformEntry(ref FileSystemEntry e)
        {
            var attr = e.Attributes;
            var entry = new StorageEntry
            {
                Name = e.FileName,
                IsDirectory = e.IsDirectory,
                IsReparsePoint = (attr & FileAttributes.ReparsePoint) != 0,
                Length = e.Length,
                Attributes = attr,
                ModifiedTicks = (_stamps & StampSet.Modified) != 0 ? e.LastWriteTimeUtc.UtcTicks : 0,
                CreatedTicks = (_stamps & StampSet.Created) != 0 ? e.CreationTimeUtc.UtcTicks : 0,
                AccessedTicks = (_stamps & StampSet.Accessed) != 0 ? e.LastAccessTimeUtc.UtcTicks : 0,
            };
            return transform(in entry);
        }
    }
}
