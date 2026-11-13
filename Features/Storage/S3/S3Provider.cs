using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using DiskTree.Core.Storage;

namespace DiskTree.Features.Storage.S3;

/// <summary>
/// Amazon S3 and S3-compatible servers (MinIO, RustFS, SeaweedFS, Ceph …) as a storage source. Folders are key
/// prefixes (see <see cref="S3Path"/>); listing a folder is one ListObjectsV2 with delimiter "/" per 1000 entries,
/// sent from the scanner's worker threads (<see cref="ScanThreads"/> of them at once), so the tree grows live.
/// A profile with <see cref="SourceListing.Flat"/> lists everything below the scanned folder in one go instead
/// (1 request per 1000 objects) and serves the folders of that scan from memory.
/// <para>
/// One instance per saved profile (<see cref="For"/>), plus <see cref="Generic"/>: the instance registered in
/// <see cref="StorageProviders"/> (snapshot files, <see cref="StorageProviders.TryResolve"/>), which looks up the
/// profile for every path it is given. No AWS SDK type is touched before the first request (lazy loading).
/// </para>
/// </summary>
public sealed class S3Provider : IStorageProvider
{
    public const string ProviderId = "s3";

    /// <summary>Folders listed at the same time: requests are cheap to run in parallel, latency dominates.</summary>
    public const int Threads = 32;

    /// <summary>Entries per ListObjectsV2 page (the S3 maximum).</summary>
    public const int PageSize = 1000;

    private const string FlatKey = "s3flat|";

    /// <summary>
    /// Creates the client of a profile (null = no profile: AWS default credential chain). The second argument is a secret
    /// typed into the dialog (connection test); null = read the stored one. Tests replace this with an in-memory client.
    /// </summary>
    public static Func<SourceProfile?, string?, IS3Client> ClientFactory { get; set; } = CreateAwsClient;

    // Its own method so the AWS SDK is only loaded when a client is really created.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IS3Client CreateAwsClient(SourceProfile? profile, string? secret) =>
        AwsS3Client.Create(profile, secret ?? (profile?.Auth == SourceAuth.AccessKey ? SourceSecrets.Read(profile) : null));

    /// <summary>The registered provider: binds every path to its saved profile when it is used.</summary>
    public static readonly S3Provider Generic = new(null, resolving: true, version: 0);

    private static readonly ConcurrentDictionary<string, S3Provider> Bound = new();

    /// <summary>The provider of a saved profile (null: without profile). Cached until the profiles change.</summary>
    public static S3Provider For(SourceProfile? profile)
    {
        var key = profile?.Id ?? "";
        int version = SourceProfiles.Version;
        if (Bound.TryGetValue(key, out var p) && p._version == version) return p;
        var fresh = new S3Provider(profile?.Clone(), resolving: false, version);
        Bound[key] = fresh;
        return fresh;
    }

    /// <summary>Drops the cached provider (and its client) of a profile after it was changed or deleted.</summary>
    public static void Forget(string profileId) => Bound.TryRemove(profileId, out _);

    /// <summary>The saved profile a scan of <paramref name="path"/> uses (see <see cref="SourceProfiles.Match"/>).</summary>
    public static SourceProfile? MatchProfile(string path) =>
        SourceProfiles.Match(ProviderId, path, S3PathOps.Instance, ScopeOf);

    /// <summary>What a profile covers: its bucket/prefix, or "" (any bucket).</summary>
    public static string? ScopeOf(SourceProfile p)
    {
        if (string.IsNullOrWhiteSpace(p.Path)) return "";
        try
        {
            var n = S3Path.Normalize(S3Path.HasScheme(p.Path) ? p.Path : S3Path.Scheme + p.Path.Trim().TrimStart('/'));
            return n == S3Path.Scheme ? "" : n;
        }
        catch (ArgumentException) { return null; } // a broken path fits nothing
    }

    private readonly SourceProfile? _profile;
    private readonly bool _resolving;
    private readonly int _version;
    private readonly object _clientLock = new();
    private IS3Client? _client;

    private S3Provider(SourceProfile? profile, bool resolving, int version)
    {
        _profile = profile;
        _resolving = resolving;
        _version = version;
    }

    public string Id => ProviderId;
    public string DisplayName => "S3";

    public StorageCaps Caps => StorageCaps.CaseSensitive | StorageCaps.ParallelListing | StorageCaps.CostsPerRequest;

    public PathOps Paths => S3PathOps.Instance;

    public int? ScanThreads => Threads;

    public SourceProfile? Profile => _profile;

    /// <summary>The provider that really talks to the server for this path (this one, or the bound one for <see cref="Generic"/>).</summary>
    public S3Provider Resolve(string path) => _resolving ? For(MatchProfile(path)) : this;

    private bool Flat => _profile?.Listing == SourceListing.Flat;

    private IS3Client Client
    {
        get
        {
            lock (_clientLock) return _client ??= ClientFactory(_profile, null);
        }
    }

    // ------------------------------------------------------------------ paths

    public bool TryParse(string text, out StorageLocation location)
    {
        location = default;
        if (!S3Path.HasScheme(text)) return false;
        try
        {
            var path = S3Path.Normalize(text);
            location = new StorageLocation(For(MatchProfile(path)), path);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public string Normalize(string path) => S3Path.Normalize(path);

    private static (string Bucket, string Prefix) SplitChecked(string folder)
    {
        var (bucket, prefix) = S3Path.Split(S3Path.Normalize(folder));
        if (bucket.Length == 0) throw new IOException("Bitte einen Bucket angeben, z. B. s3://bucket/ordner.");
        return (bucket, prefix);
    }

    public StorageRoot ResolveRoot(string path)
    {
        var n = S3Path.Normalize(path);
        return new StorageRoot(n, n, 0, 0); // no capacity: the tree starts at the scanned prefix
    }

    public bool TryGetCapacity(string rootPath, out long total, out long free)
    {
        total = free = 0;
        return false;
    }

    public Stream OpenRead(string path) => throw new NotSupportedException("S3-Objekte werden nicht gelesen.");

    // ------------------------------------------------------------------ listing

    public bool FolderExists(string path)
    {
        var target = Resolve(path);
        var (bucket, prefix) = SplitChecked(path);
        try
        {
            var page = target.Client.ListAsync(bucket, prefix, "/", null, 1, CancellationToken.None).GetAwaiter().GetResult();
            return prefix.Length == 0 || page.Objects.Count > 0 || page.Prefixes.Count > 0;
        }
        catch (DirectoryNotFoundException)
        {
            return false; // no such bucket
        }
    }

    public IEnumerable<T> Enumerate<T>(string folder, EntryTransform<T> transform, ListOptions options) =>
        Resolve(folder).List(folder, transform, options);

    private IEnumerable<T> List<T>(string folder, EntryTransform<T> transform, ListOptions options)
    {
        // Like the disk: nothing happens before the enumeration starts.
        var (bucket, prefix) = SplitChecked(folder);
        var stamps = options.Stamps;

        if (Flat && options.Run is { } run && TakeFlat(run, folder, bucket, prefix, options) is { } dir)
        {
            foreach (var name in dir.Dirs)
                yield return Make(transform, name, true, 0, 0, stamps);
            foreach (var f in dir.Files)
                yield return Make(transform, f.Name, false, f.Size, f.Ticks, stamps);
            yield break;
        }

        string? token = null;
        do
        {
            var page = Fetch(bucket, prefix, "/", token, options);
            if (page == null) yield break;

            foreach (var p in page.Prefixes)
            {
                if (p.Length <= prefix.Length || !p.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var rel = p[prefix.Length..];
                if (rel.EndsWith('/')) rel = rel[..^1];
                if (rel.Contains('/')) continue; // not a direct child (does not happen with delimiter "/")
                yield return Make(transform, S3Path.Encode(rel), true, 0, 0, stamps);
            }
            foreach (var o in page.Objects)
            {
                if (!o.Key.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var name = o.Key[prefix.Length..];
                if (name.Contains('/')) continue;
                if (name.Length == 0)
                {
                    // The folder marker "prefix/" (created by consoles for empty folders): not a file. One with content
                    // still counts, as a file without a name.
                    if (o.Size == 0) continue;
                    name = S3Path.EmptySegment;
                }
                yield return Make(transform, name, false, o.Size, o.ModifiedTicks, stamps);
            }
            token = page.NextToken;
        } while (token != null);
    }

    /// <summary>One request; errors are swallowed (null) if the listing ignores unreadable folders.</summary>
    private S3ListPage? Fetch(string bucket, string prefix, string? delimiter, string? token, ListOptions options)
    {
        options.Run?.BeforeRequest();
        try
        {
            return Client.ListAsync(bucket, prefix, delimiter, token, PageSize, options.Cancellation).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (options.IgnoreInaccessible && ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static T Make<T>(EntryTransform<T> transform, string name, bool isDir, long length, long modified, StampSet stamps)
    {
        var e = new StorageEntry
        {
            Name = name.AsSpan(),
            IsDirectory = isDir,
            Length = length,
            Attributes = isDir ? FileAttributes.Directory : FileAttributes.Normal,
            ModifiedTicks = (stamps & StampSet.Modified) != 0 ? modified : 0,
        };
        return transform(in e);
    }

    // ------------------------------------------------------------------ flat listing

    private sealed record FlatFile(string Name, long Size, long Ticks);

    private sealed class FlatDir
    {
        public readonly List<string> Dirs = new();
        public readonly HashSet<string> DirSet = new(StringComparer.Ordinal);
        public readonly List<FlatFile> Files = new();
    }

    /// <summary>Everything below one folder, listed in one go; each folder is handed out once and then forgotten.</summary>
    private sealed class FlatSession(string root)
    {
        public string Root { get; } = root;
        private readonly Dictionary<string, FlatDir> _dirs = new(StringComparer.Ordinal) { [""] = new FlatDir() };

        public FlatDir Get(string rel)
        {
            if (!_dirs.TryGetValue(rel, out var d)) _dirs[rel] = d = new FlatDir();
            return d;
        }

        public void Add(string relKey, long size, long ticks)
        {
            var parts = relKey.Split('/');
            string dirRel = "";
            for (int i = 0; i < parts.Length - 1; i++)
            {
                var name = S3Path.Encode(parts[i]);
                var parent = Get(dirRel);
                if (parent.DirSet.Add(name)) parent.Dirs.Add(name);
                dirRel = dirRel.Length == 0 ? name : dirRel + "/" + name;
                Get(dirRel);
            }
            var file = parts[^1];
            if (file.Length == 0)
            {
                if (size == 0) return; // folder marker
                file = S3Path.EmptySegment;
            }
            Get(dirRel).Files.Add(new FlatFile(file, size, ticks));
        }

        public FlatDir? Take(string folder)
        {
            var rel = folder.Length == Root.Length ? "" : folder[(Root.Length + 1)..];
            lock (_dirs)
            {
                if (!_dirs.Remove(rel, out var d)) return null;
                return d;
            }
        }
    }

    /// <summary>
    /// The folder's entries from this run's flat listing: the first folder of a run (the scanned one) lists everything
    /// below it; its descendants are served from memory. Null if the folder was already handed out (then it is listed
    /// live again).
    /// </summary>
    private FlatDir? TakeFlat(ListRun run, string folder, string bucket, string prefix, ListOptions options)
    {
        var paths = S3PathOps.Instance;
        foreach (var kv in run.State)
        {
            if (kv.Key.StartsWith(FlatKey, StringComparison.Ordinal) && kv.Value is FlatSession s && paths.IsSameOrUnder(s.Root, folder))
                return s.Take(folder);
        }

        var session = new FlatSession(S3Path.Normalize(folder));
        string? token = null;
        do
        {
            var page = Fetch(bucket, prefix, null, token, options);
            if (page == null) break;
            foreach (var o in page.Objects)
                if (o.Key.StartsWith(prefix, StringComparison.Ordinal))
                    session.Add(o.Key[prefix.Length..], o.Size, o.ModifiedTicks);
            token = page.NextToken;
        } while (token != null);

        run.State.TryAdd(FlatKey + session.Root, session);
        return session.Take(session.Root);
    }
}
