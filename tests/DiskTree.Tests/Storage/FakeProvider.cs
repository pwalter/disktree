using System.IO;
using DiskTree.Core.Storage;

namespace DiskTree.Tests.Storage;

/// <summary>
/// An in-memory storage source for provider-independent tests: '/' as separator, case-sensitive names (like S3 keys or
/// an SFTP server), capacity and caps as the test likes. Paths look like "/data/a/b.txt"; "/" is the top.
/// The content must not change while a scan runs (it is read from several worker threads); between scans it may.
/// </summary>
public sealed class FakeProvider : IStorageProvider
{
    private sealed class Dir
    {
        public readonly SortedDictionary<string, Dir> Dirs = new(StringComparer.Ordinal);
        public readonly SortedDictionary<string, FakeFile> Files = new(StringComparer.Ordinal);
        public bool Denied;
        public bool Link;
    }

    public sealed record FakeFile(long Size, long ModifiedTicks, long CreatedTicks, long AccessedTicks, FileAttributes Attributes);

    private readonly Dir _root = new();
    private int _listings;

    public FakeProvider(string id = "fake") => Id = id;

    public string Id { get; }
    public string DisplayName => "Testspeicher";

    public StorageCaps Caps { get; set; } =
        StorageCaps.CaseSensitive | StorageCaps.Hierarchical | StorageCaps.ParallelListing | StorageCaps.LiveFileList;

    public PathOps Paths { get; } = new('/', StringComparison.Ordinal);

    /// <summary>Thread count a scan of this source uses (null = the caller's choice).</summary>
    public int? ScanThreads { get; set; }

    /// <summary>Capacity of "/" (0 = unknown: a scan's tree then starts at the scanned folder).</summary>
    public long Total { get; set; }
    public long Free { get; set; }

    /// <summary>How many folders were listed (all threads).</summary>
    public int Listings => Volatile.Read(ref _listings);

    /// <summary>The timestamps the last listing asked for.</summary>
    public StampSet LastStamps { get; private set; }

    // ------------------------------------------------------------------ building the content

    /// <summary>A file (and its folders). Times default to "now"; created / accessed default to modified.</summary>
    public FakeProvider File(string path, long size, DateTime? modified = null, DateTime? created = null, DateTime? accessed = null,
        FileAttributes attributes = FileAttributes.Normal)
    {
        var (dir, name) = ParentAndName(path);
        long m = (modified ?? DateTime.UtcNow).Ticks;
        dir.Files[name] = new FakeFile(size, m, created?.Ticks ?? m, accessed?.Ticks ?? m, attributes);
        return this;
    }

    public FakeProvider Folder(string path)
    {
        Get(path, create: true);
        return this;
    }

    /// <summary>A folder that cannot be listed (no access).</summary>
    public FakeProvider Denied(string path)
    {
        Get(path, create: true)!.Denied = true;
        return this;
    }

    /// <summary>A folder that is a link to somewhere else (reparse point: not followed).</summary>
    public FakeProvider Link(string path)
    {
        var d = Get(path, create: true)!;
        d.Link = true;
        d.Files["inside-the-link.bin"] = new FakeFile(1 << 30, DateTime.UtcNow.Ticks, DateTime.UtcNow.Ticks, DateTime.UtcNow.Ticks, 0);
        return this;
    }

    public FakeProvider Delete(string path)
    {
        var (dir, name) = ParentAndName(path);
        if (!dir.Files.Remove(name)) dir.Dirs.Remove(name);
        return this;
    }

    private (Dir Dir, string Name) ParentAndName(string path)
    {
        var parts = Paths.Split(path);
        var dir = _root;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (!dir.Dirs.TryGetValue(parts[i], out var next)) dir.Dirs[parts[i]] = next = new Dir();
            dir = next;
        }
        return (dir, parts[^1]);
    }

    private Dir? Get(string path, bool create = false)
    {
        var dir = _root;
        foreach (var part in Paths.Split(path))
        {
            if (!dir.Dirs.TryGetValue(part, out var next))
            {
                if (!create) return null;
                dir.Dirs[part] = next = new Dir();
            }
            dir = next;
        }
        return dir;
    }

    // ------------------------------------------------------------------ IStorageProvider

    public bool TryParse(string text, out StorageLocation location)
    {
        location = default;
        if (string.IsNullOrEmpty(text) || text[0] != '/') return false;
        location = new StorageLocation(this, Normalize(text));
        return true;
    }

    public string Normalize(string path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '/') throw new ArgumentException("Kein Pfad dieses Speichers: " + path);
        return "/" + string.Join('/', Paths.Split(path));
    }

    public bool FolderExists(string path) => Get(path) != null;

    public StorageRoot ResolveRoot(string path)
    {
        TryGetCapacity("/", out long total, out long free);
        return new StorageRoot(Normalize(path), "/", total, free);
    }

    public bool TryGetCapacity(string rootPath, out long total, out long free)
    {
        bool known = Total > 0 && rootPath == "/";
        total = known ? Total : 0;
        free = known ? Free : 0;
        return known;
    }

    public IEnumerable<T> Enumerate<T>(string folder, EntryTransform<T> transform, ListOptions options)
    {
        // Lazy like the disk: nothing happens before the enumeration starts. One listing = one request.
        options.Run?.BeforeRequest();
        Interlocked.Increment(ref _listings);
        LastStamps = options.Stamps;
        var dir = Get(folder) ?? throw new DirectoryNotFoundException("Nicht gefunden: " + folder);
        if (dir.Denied)
        {
            if (options.IgnoreInaccessible) yield break;
            throw new UnauthorizedAccessException("Kein Zugriff: " + folder);
        }

        // Copies, so a test may change the content while a consumer is still enumerating.
        foreach (var (name, d) in dir.Dirs.ToList())
            yield return Make(transform, name, true, d.Link, 0, FileAttributes.Directory | (d.Link ? FileAttributes.ReparsePoint : 0),
                0, 0, 0, options.Stamps);
        foreach (var (name, f) in dir.Files.ToList())
            yield return Make(transform, name, false, false, f.Size, f.Attributes, f.ModifiedTicks, f.CreatedTicks, f.AccessedTicks, options.Stamps);
    }

    private static T Make<T>(EntryTransform<T> transform, string name, bool isDir, bool link, long length, FileAttributes attr,
        long modified, long created, long accessed, StampSet stamps)
    {
        var e = new StorageEntry
        {
            Name = name.AsSpan(),
            IsDirectory = isDir,
            IsReparsePoint = link,
            Length = length,
            Attributes = attr,
            ModifiedTicks = (stamps & StampSet.Modified) != 0 ? modified : 0,
            CreatedTicks = (stamps & StampSet.Created) != 0 ? created : 0,
            AccessedTicks = (stamps & StampSet.Accessed) != 0 ? accessed : 0,
        };
        return transform(in e);
    }

    public Stream OpenRead(string path)
    {
        var (dir, name) = ParentAndName(path);
        return dir.Files.TryGetValue(name, out var f) ? new MemoryStream(new byte[f.Size]) : throw new FileNotFoundException(path);
    }
}
