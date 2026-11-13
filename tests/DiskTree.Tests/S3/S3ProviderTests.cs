using System.IO;
using System.IO.Compression;
using System.Runtime.Loader;
using DiskTree.Core;
using DiskTree.Core.Snapshots;
using DiskTree.Core.Storage;
using DiskTree.Features.Storage.S3;
using Xunit;

namespace DiskTree.Tests.S3;

/// <summary>The S3 provider against an in-memory server: paths, listing, scan, errors, cancellation, profiles, snapshots.</summary>
public sealed class S3ProviderTests : IDisposable
{
    private readonly Func<SourceProfile?, string?, IS3Client> _oldFactory = S3Provider.ClientFactory;
    private readonly MemoryS3Client _server = new();
    private readonly List<(SourceProfile? Profile, string? Secret)> _created = new();

    public S3ProviderTests()
    {
        S3Storage.Register();
        S3Provider.ClientFactory = (p, secret) =>
        {
            lock (_created) _created.Add((p, secret));
            return _server;
        };
        SourceProfiles.Set([]); // new version: no cached clients of other tests
    }

    public void Dispose()
    {
        S3Provider.ClientFactory = _oldFactory;
        SourceProfiles.PreferredId = null;
        SourceProfiles.Set([]);
    }

    private static S3Provider Unbound => S3Provider.For(null);

    private MemoryS3Client Sample() => _server
        .Put("bk", "top.bin", 1)
        .Put("bk", "a/", 0)                       // folder marker: not a file
        .Put("bk", "a/x.bin", 10)
        .Put("bk", "a/b/y.bin", 20)
        .Put("bk", "empty/", 0)                    // only a marker: an empty folder
        .Put("bk", "c d/ä ö.txt", 5)
        .Put("bk", "e//f.bin", 7)                  // empty key part
        .Put("bk", "/lead.bin", 3)                 // leading slash
        .Put("bk", "m/", 4);                       // a "marker" with content counts

    // ------------------------------------------------------------------ paths

    [Theory]
    [InlineData("s3://bk", "s3://bk")]
    [InlineData("S3://bk/", "s3://bk")]
    [InlineData("s3://bk/a/b/", "s3://bk/a/b")]
    [InlineData(" \"s3://bk/a\" ", "s3://bk/a")]
    [InlineData("s3://bk/a//b", "s3://bk/a/(leer)\u2060/b")]
    [InlineData("s3://bk//x", "s3://bk/(leer)\u2060/x")]
    [InlineData("s3://bk/a//", "s3://bk/a/(leer)\u2060")]
    [InlineData("s3://Bk/Ä Ö", "s3://Bk/Ä Ö")]
    [InlineData("s3://", "s3://")]
    public void Normalize(string text, string expected) => Assert.Equal(expected, S3Path.Normalize(text));

    [Theory]
    [InlineData("s3://bk", "bk", "")]
    [InlineData("s3://bk/a/b", "bk", "a/b/")]
    [InlineData("s3://bk/a/(leer)\u2060", "bk", "a//")]
    [InlineData("s3://bk/(leer)\u2060", "bk", "/")]
    public void Split_gives_bucket_and_key_prefix(string path, string bucket, string prefix) =>
        Assert.Equal((bucket, prefix), S3Path.Split(path));

    [Fact]
    public void PathOps_know_the_bucket_root()
    {
        var p = S3PathOps.Instance;
        Assert.Null(p.ParentOf("s3://bk"));
        Assert.Equal("s3://bk", p.ParentOf("s3://bk/a"));
        Assert.Equal("s3://bk/a", p.ParentOf("s3://bk/a/b.txt"));
        Assert.Equal("bk", p.NameOf("s3://bk"));
        Assert.Equal("b.txt", p.NameOf("s3://bk/a/b.txt"));
        Assert.Equal("s3://bk/a", p.Join("s3://bk", "a"));
        Assert.Equal("s3://bk/a/" + S3Path.EmptySegment, p.Join("s3://bk/a", ""));
        Assert.True(p.IsUnder("s3://bk", "s3://bk/a"));
        Assert.False(p.IsUnder("s3://bk/a", "s3://bk/ab"));
        Assert.False(p.IsSameOrUnder("s3://bk/a", "s3://bk/A")); // case-sensitive
        Assert.False(p.IsSameOrUnder("s3://bk", "s3://bk2/a"));
    }

    [Fact]
    public void TryParse_only_takes_s3_paths()
    {
        Assert.False(S3Provider.Generic.TryParse(@"C:\Daten", out _));
        Assert.False(S3Provider.Generic.TryParse("", out _));
        Assert.True(S3Provider.Generic.TryParse("S3://bk/x/", out var loc));
        Assert.Equal("s3://bk/x", loc.Path);
        Assert.Equal("s3", loc.Provider.Id);
        Assert.Null(loc.Provider.Profile);

        Assert.True(StorageProviders.TryResolve("s3://bk/y", out var viaRegistry));
        Assert.Equal("s3", viaRegistry.Provider.Id);
        Assert.True(StorageProviders.TryResolve(@"C:\x", out var disk));
        Assert.Same(DiskProvider.Instance, disk.Provider);
    }

    [Fact]
    public void Caps_and_threads()
    {
        var p = Unbound;
        Assert.Equal(StorageCaps.CaseSensitive | StorageCaps.ParallelListing | StorageCaps.CostsPerRequest, p.Caps);
        Assert.Equal(32, ((IStorageProvider)p).ScanThreads);
        Assert.False(p.TryGetCapacity("s3://bk", out _, out _));
        var root = p.ResolveRoot("s3://bk/a/");
        Assert.Equal("s3://bk/a", root.Target);
        Assert.Equal("s3://bk/a", root.Top);
        Assert.False(root.HasCapacity);
        Assert.Throws<NotSupportedException>(() => p.OpenRead("s3://bk/top.bin"));
    }

    // ------------------------------------------------------------------ listing

    private static List<(string Name, bool Dir, long Size, long Ticks)> List(IStorageProvider p, string folder, ListOptions? o = null) =>
        p.Enumerate(folder, (in StorageEntry e) => (e.Name.ToString(), e.IsDirectory, e.Length, e.ModifiedTicks), o ?? ListOptions.Strict).ToList();

    [Fact]
    public void Lists_one_level_with_markers_empty_parts_and_umlauts()
    {
        Sample();
        var root = List(Unbound, "s3://bk");
        Assert.Equal(
            new[] { S3Path.EmptySegment, "a", "c d", "e", "empty", "m" }.Order(StringComparer.Ordinal),
            root.Where(e => e.Dir).Select(e => e.Name).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { ("top.bin", 1L) }, root.Where(e => !e.Dir).Select(e => (e.Name, e.Size)));

        var a = List(Unbound, "s3://bk/a");
        Assert.Equal(new[] { ("b", true), ("x.bin", false) }, a.Select(e => (e.Name, e.Dir)).Order());
        Assert.Empty(List(Unbound, "s3://bk/empty"));
        Assert.Equal(new[] { (S3Path.EmptySegment, 4L) }, List(Unbound, "s3://bk/m").Select(e => (e.Name, e.Size)));
        Assert.Equal(new[] { ("ä ö.txt", 5L) }, List(Unbound, "s3://bk/c d").Select(e => (e.Name, e.Size)));
        Assert.Equal(new[] { "f.bin" }, List(Unbound, "s3://bk/e/" + S3Path.EmptySegment).Select(e => e.Name));
        Assert.Equal(new[] { "lead.bin" }, List(Unbound, "s3://bk/" + S3Path.EmptySegment).Select(e => e.Name));
    }

    [Fact]
    public void Only_the_modified_time_is_filled_in()
    {
        var when = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        _server.Put("bk", "f.bin", 1, when);
        var e = Assert.Single(List(Unbound, "s3://bk"));
        Assert.Equal(when.Ticks, e.Ticks);
        var none = Assert.Single(List(Unbound, "s3://bk", ListOptions.Strict with { Stamps = StampSet.None }));
        Assert.Equal(0, none.Ticks);
    }

    [Fact]
    public void Listing_is_lazy_and_pages_by_1000()
    {
        for (int i = 0; i < 2500; i++) _server.Put("bk", $"big/f{i:D5}.bin", 1);
        var entries = Unbound.Enumerate("s3://bk/big", (in StorageEntry e) => e.Length, ListOptions.Strict);
        Assert.Equal(0, _server.Requests);
        Assert.Equal(2500, entries.Count());
        Assert.Equal(3, _server.Requests);
    }

    [Fact]
    public void Errors_are_mapped_or_skipped()
    {
        Sample();
        _server.Failures["bk/a/"] = new UnauthorizedAccessException("Zugriff verweigert");
        Assert.Throws<UnauthorizedAccessException>(() => List(Unbound, "s3://bk/a"));
        Assert.Empty(List(Unbound, "s3://bk/a", ListOptions.Lenient));
        Assert.Throws<DirectoryNotFoundException>(() => List(Unbound, "s3://nope"));
        Assert.Throws<IOException>(() => List(Unbound, "s3://")); // no bucket
    }

    [Fact]
    public void FolderExists()
    {
        Sample();
        Assert.True(Unbound.FolderExists("s3://bk"));
        Assert.True(Unbound.FolderExists("s3://bk/a"));
        Assert.True(Unbound.FolderExists("s3://bk/empty/"));
        Assert.True(Unbound.FolderExists("s3://bk/e/" + S3Path.EmptySegment));
        Assert.False(Unbound.FolderExists("s3://bk/A"));
        Assert.False(Unbound.FolderExists("s3://bk/top.bin"));
        Assert.False(Unbound.FolderExists("s3://nope"));
        var ex = Assert.Throws<IOException>(() => Unbound.FolderExists("s3://"));
        Assert.Contains("Bucket", ex.Message);

        _server.Failures["bk/"] = new UnauthorizedAccessException("Zugriff verweigert");
        Assert.Throws<UnauthorizedAccessException>(() => Unbound.FolderExists("s3://bk"));
        _server.Failures["bk/"] = new IOException("Der S3-Server ist nicht erreichbar");
        Assert.Throws<IOException>(() => Unbound.FolderExists("s3://bk"));
    }

    // ------------------------------------------------------------------ scan

    private static async Task<Scanner> Scan(IStorageProvider p, string path, CancellationToken ct = default)
    {
        var s = new Scanner { Provider = p };
        await s.ScanAsync(path, 8, ct);
        return s;
    }

    [Fact]
    public async Task Scan_builds_the_tree_from_prefixes()
    {
        Sample();
        var s = await Scan(Unbound, "s3://bk");
        var root = s.Root!;
        Assert.Equal("s3://bk", root.FullPath);
        Assert.Equal(1 + 10 + 20 + 5 + 7 + 3 + 4, root.Size);
        Assert.Equal(7, root.FileCount);
        Assert.Equal(0, s.Errors);
        Assert.Equal(s.Requests, _server.Requests);
        Assert.Equal(9, s.Requests); // bk, a, a/b, empty, c d, e, e//, /, m … one LIST per folder
        Assert.Equal(20, s.FindNode("s3://bk/a/b")!.Size);
        Assert.Equal(7, s.FindNode("s3://bk/e/" + S3Path.EmptySegment)!.Size);
        Assert.Equal(3, s.FindNode("s3://bk/" + S3Path.EmptySegment)!.Size);
        Assert.Equal("c d", s.FindNode("s3://bk/c d")!.Name);
        Assert.Null(s.FindNode("s3://bk/A"));
    }

    [Fact]
    public async Task Scan_of_a_prefix_starts_there()
    {
        Sample();
        var s = await Scan(Unbound, "s3://bk/a/");
        Assert.Equal("s3://bk/a", s.Root!.FullPath);
        Assert.Equal(30, s.Root.Size);
        Assert.Equal(0, s.ExpectedBytes); // unknown total: indeterminate progress like UNC
    }

    [Fact]
    public async Task Denied_folders_count_as_not_readable()
    {
        Sample();
        _server.Failures["bk/a/"] = new UnauthorizedAccessException("Zugriff verweigert");
        _server.Failures["bk/c d/"] = new IOException("Zeitüberschreitung");
        var s = await Scan(Unbound, "s3://bk");
        Assert.Equal(2, s.Errors);
        Assert.Equal(1 + 7 + 3 + 4, s.Root!.Size);
    }

    [Fact]
    public async Task Cancel_stops_a_running_request()
    {
        Sample();
        _server.BlockOn = "bk/a/";
        using var cts = new CancellationTokenSource();
        var scan = Scan(Unbound, "s3://bk", cts.Token);
        await _server.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        var ex = await Record.ExceptionAsync(() => scan.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.IsAssignableFrom<OperationCanceledException>(ex);
    }

    [Fact]
    public async Task Request_limit_pauses_the_scan()
    {
        for (int i = 0; i < 30; i++) _server.Put("bk", $"d{i:D2}/f.bin", 1);
        var s = new Scanner { Provider = Unbound, RequestLimit = 5 };
        var reached = new TaskCompletionSource<ListRun>();
        s.RequestLimitReached += r => reached.TrySetResult(r);
        var scan = s.ScanAsync("s3://bk", 8, CancellationToken.None);
        var run = await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(100);
        Assert.Equal(5, _server.Requests);
        run.Resume();
        await scan.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(31, s.Requests);
        Assert.Equal(30, s.Root!.FileCount);
    }

    [Fact]
    public async Task Many_folders_and_pages()
    {
        for (int d = 0; d < 20; d++)
            for (int i = 0; i < 150; i++) _server.Put("bk", $"p/d{d:D2}/f{i:D3}", 2);
        for (int i = 0; i < 1500; i++) _server.Put("bk", $"p/flat{i:D4}", 1);
        var s = await Scan(Unbound, "s3://bk/p");
        Assert.Equal(20 * 150 + 1500, s.Root!.FileCount);
        Assert.Equal(20 * 150 * 2 + 1500, s.Root.Size);
        Assert.Equal(2 + 20, s.Requests); // p (1520 entries: 2 pages) + one per folder
    }

    // ------------------------------------------------------------------ flat mode

    [Fact]
    public async Task Flat_listing_gives_the_same_tree_with_fewer_requests()
    {
        Sample();
        for (int d = 0; d < 20; d++)
            for (int i = 0; i < 100; i++) _server.Put("bk", $"many/d{d:D2}/f{i:D3}", 3);
        var live = await Scan(Unbound, "s3://bk");
        int liveRequests = _server.Requests;

        var profile = new SourceProfile { Kind = "s3", Name = "flach", Listing = SourceListing.Flat };
        SourceProfiles.Set([profile]);
        var before = _server.Requests;
        var flat = await Scan(S3Provider.For(SourceProfiles.All[0]), "s3://bk");
        int flatRequests = _server.Requests - before;

        Assert.Equal(3, flatRequests);        // 2.009 objects / 1.000 per page
        Assert.Equal(flatRequests, flat.Requests);
        Assert.Equal(30, liveRequests);     // 9 folders of the sample + many + 20
        Assert.Equal(live.Root!.Size, flat.Root!.Size);
        Assert.Equal(live.Root.FileCount, flat.Root.FileCount);
        Assert.All(live.Root.Walk(), n =>
        {
            var other = flat.FindNode(n.FullPath);
            Assert.NotNull(other);
            Assert.Equal(n.Size, other!.Size);
            Assert.Equal(n.FileCount, other.FileCount);
        });
        Assert.All(_server.Calls.Skip(liveRequests), c => Assert.Null(c.Delimiter));
    }

    [Fact]
    public async Task Flat_rescan_lists_again()
    {
        Sample();
        SourceProfiles.Set([new SourceProfile { Kind = "s3", Name = "flach", Listing = SourceListing.Flat }]);
        var p = S3Provider.For(SourceProfiles.All[0]);
        var s = await Scan(p, "s3://bk");
        _server.Put("bk", "a/b/new.bin", 100);
        var node = s.FindNode("s3://bk/a")!;
        await s.RescanAsync(node, 4, CancellationToken.None);
        Assert.Equal(130, s.FindNode("s3://bk/a")!.Size);
        Assert.Equal(1, s.Requests);
    }

    // ------------------------------------------------------------------ profiles

    [Fact]
    public void Paths_are_bound_to_the_matching_profile()
    {
        var any = new SourceProfile { Kind = "s3", Name = "alle", Endpoint = "http://a:9000" };
        var bk = new SourceProfile { Kind = "s3", Name = "bk", Endpoint = "http://b:9000", Path = "s3://bk" };
        var other = new SourceProfile { Kind = "sftp", Name = "fremd", Path = "s3://bk/a" };
        SourceProfiles.Set([any, bk, other]);

        Assert.True(StorageProviders.TryResolve("s3://bk/a/b", out var loc));
        Assert.Equal("bk", loc.Provider.Profile?.Name);
        Assert.True(StorageProviders.TryResolve("s3://zz", out loc));
        Assert.Equal("alle", loc.Provider.Profile?.Name);
        Assert.Same(loc.Provider, S3Provider.For(SourceProfiles.All[0]));      // cached
        SourceProfiles.PreferredId = any.Id;
        Assert.True(StorageProviders.TryResolve("s3://bk", out loc));
        Assert.Equal("alle", loc.Provider.Profile?.Name);
    }

    [Fact]
    public void The_registered_provider_resolves_the_profile_per_path()
    {
        Sample();
        var bk = new SourceProfile { Kind = "s3", Name = "bk", Path = "s3://bk" };
        SourceProfiles.Set([bk]);
        Assert.Same(S3Provider.Generic, StorageProviders.Get("s3"));
        Assert.Equal(2, List(S3Provider.Generic, "s3://bk/a").Count);
        Assert.Equal(bk.Id, _created.Last().Profile?.Id);
        Assert.Null(_created.Last().Secret); // the stored secret is read by the real factory, not passed around
    }

    [Fact]
    public void Changing_profiles_creates_a_new_client()
    {
        var p = new SourceProfile { Kind = "s3", Name = "x" };
        SourceProfiles.Set([p]);
        var first = S3Provider.For(SourceProfiles.All[0]);
        Assert.Same(first, S3Provider.For(SourceProfiles.All[0]));
        S3SourceKind.Instance.Forget(p.Id);
        Assert.NotSame(first, S3Provider.For(SourceProfiles.All[0]));
        var second = S3Provider.For(SourceProfiles.All[0]);
        SourceProfiles.Set([p]);
        Assert.NotSame(second, S3Provider.For(SourceProfiles.All[0]));
    }

    // ------------------------------------------------------------------ snapshot

    [Fact]
    public async Task Snapshot_keeps_provider_and_paths_but_no_connection_data()
    {
        Sample();
        var profile = new SourceProfile
        {
            Kind = "s3", Name = "Geheimes Profil", Endpoint = "http://endpoint-xyz:9000", Auth = SourceAuth.AccessKey,
            UserName = "AKIDXYZ", Path = "s3://bk",
        };
        SourceProfiles.Set([profile]);
        Assert.True(StorageProviders.TryResolve("s3://bk", out var loc));
        var s = await Scan(loc.Provider, loc.Path);

        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "s3.disktree");
        SnapshotStore.Save(file, s, DateTime.UtcNow);
        string json;
        using (var gz = new GZipStream(File.OpenRead(file), CompressionMode.Decompress))
        using (var r = new StreamReader(gz))
            json = r.ReadToEnd();
        Assert.Contains("\"provider\":\"s3\"", json.Replace(" ", ""));
        Assert.Contains("s3://bk", json);
        Assert.DoesNotContain("endpoint-xyz", json);
        Assert.DoesNotContain("AKIDXYZ", json);
        Assert.DoesNotContain("Geheimes", json);

        var loaded = SnapshotStore.Load(file);
        Assert.Same(S3Provider.Generic, loaded.Provider);
        var again = Scanner.FromSnapshot(loaded);
        Assert.Equal(s.Root!.Size, again.Root!.Size);
        Assert.Equal(20, again.FindNode("s3://bk/a/b")!.Size);
        Assert.NotNull(again.FindNode("s3://bk/e/" + S3Path.EmptySegment));
    }

    // ------------------------------------------------------------------ lazy loading

    private sealed class RecordingContext() : AssemblyLoadContext("s3-lazy-check", isCollectible: true)
    {
        public readonly List<string> Requested = new();

        protected override System.Reflection.Assembly? Load(System.Reflection.AssemblyName name)
        {
            lock (Requested) Requested.Add(name.Name ?? "");
            return null; // the default context loads it
        }
    }

    [Fact]
    public void Registering_and_parsing_does_not_load_the_AWS_SDK()
    {
        // A second copy of DiskTree.dll in its own load context: every assembly it needs is requested from it.
        var ctx = new RecordingContext();
        try
        {
            var asm = ctx.LoadFromAssemblyPath(typeof(S3Provider).Assembly.Location);
            asm.GetType("DiskTree.Features.Storage.S3.S3Storage")!.GetMethod("Register")!.Invoke(null, null);
            var registry = asm.GetType("DiskTree.Core.Storage.StorageProviders")!;
            var args = new object?[] { "s3://bucket/x", null };
            Assert.True((bool)registry.GetMethod("TryResolve")!.Invoke(null, args)!);
            lock (ctx.Requested)
                Assert.DoesNotContain(ctx.Requested, n => n.StartsWith("AWSSDK", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            ctx.Unload();
        }
    }
}
