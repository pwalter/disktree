using DiskTree.Core;
using DiskTree.Core.Storage;
using DiskTree.Infrastructure;
using Xunit;

namespace DiskTree.Tests.Storage;

/// <summary>ListRun (cancellation, request counting, cost limit), saved profiles and their secrets.</summary>
public class SourceInfraTests
{
    private static FakeProvider Tree(int folders)
    {
        var p = new FakeProvider();
        for (int i = 0; i < folders; i++) p.File($"/data/d{i}/f.bin", 10);
        return p;
    }

    [Fact]
    public void ListRun_counts_requests_and_pauses_at_the_limit()
    {
        var run = new ListRun(default, requestLimit: 3);
        int raised = 0;
        run.LimitReached += _ => raised++;
        run.BeforeRequest();
        run.BeforeRequest();
        Assert.False(run.IsPaused);
        run.BeforeRequest();
        Assert.True(run.IsPaused);
        Assert.Equal(1, raised);
        Assert.Equal(3, run.Requests);

        // The next request waits until the user decided.
        var next = Task.Run(run.BeforeRequest);
        Assert.False(next.Wait(150));
        run.Resume(nextLimit: 0);
        Assert.True(next.Wait(5000));
        Assert.Equal(4, run.Requests);
        Assert.False(run.IsPaused);
    }

    [Fact]
    public void ListRun_cancel_releases_a_paused_request()
    {
        using var cts = new CancellationTokenSource();
        var run = new ListRun(cts.Token, requestLimit: 1);
        run.BeforeRequest();
        Assert.True(run.IsPaused);
        var next = Task.Run(run.BeforeRequest);
        cts.Cancel();
        var ex = Assert.ThrowsAny<AggregateException>(() => next.Wait(5000));
        Assert.IsAssignableFrom<OperationCanceledException>(ex.InnerException);
    }

    [Fact]
    public async Task Scan_counts_one_request_per_listing_of_a_remote_source()
    {
        var p = Tree(5);
        var scanner = new Scanner { Provider = p };
        await scanner.ScanAsync("/data", 4, CancellationToken.None);
        Assert.Equal(6, scanner.Requests);          // /data + 5 folders
        Assert.Equal(p.Listings, scanner.Requests);
        Assert.Equal(5, scanner.Root!.FileCount);
    }

    [Fact]
    public async Task Scan_pauses_at_the_request_limit_and_goes_on_after_resume()
    {
        var p = Tree(20);
        var scanner = new Scanner { Provider = p, RequestLimit = 5 };
        var reached = new TaskCompletionSource<ListRun>();
        scanner.RequestLimitReached += r => reached.TrySetResult(r);
        var scan = scanner.ScanAsync("/data", 4, CancellationToken.None);

        var run = await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(100);
        Assert.True(run.IsPaused);
        Assert.Equal(5, scanner.Requests);          // nothing more while paused
        Assert.False(scan.IsCompleted);

        run.Resume();
        await scan.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(21, scanner.Requests);
        Assert.Equal(20, scanner.Root!.FileCount);
    }

    [Fact]
    public async Task Cancelling_a_paused_scan_ends_it_as_cancelled()
    {
        var p = Tree(20);
        using var cts = new CancellationTokenSource();
        var scanner = new Scanner { Provider = p, RequestLimit = 3 };
        scanner.RequestLimitReached += _ => cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scanner.ScanAsync("/data", 4, cts.Token));
        Assert.Equal(3, scanner.Requests);
        Assert.True(scanner.Root!.IsDone); // no spinner left behind
    }

    [Fact]
    public async Task Disk_scan_sends_no_requests()
    {
        using var dir = new TempDir();
        dir.File("a.bin", 10);
        var scanner = new Scanner();
        await scanner.ScanAsync(dir.Path, 2, CancellationToken.None);
        Assert.Equal(0, scanner.Requests);
    }

    // ------------------------------------------------------------------ profiles

    private static SourceProfile Profile(string kind, string path, string name = "") =>
        new() { Kind = kind, Path = path, Name = name };

    [Fact]
    public void Match_takes_the_longest_scope_then_the_preferred_one()
    {
        var paths = new PathOps('/', StringComparison.Ordinal);
        var any = Profile("x", "", "any");
        var bucket = Profile("x", "x://b", "bucket");
        var prefix = Profile("x", "x://b/pre", "prefix");
        var other = Profile("x", "x://c", "other");
        var foreign = Profile("y", "", "foreign");
        try
        {
            SourceProfiles.Set([any, bucket, prefix, other, foreign]);
            string? Scope(SourceProfile p) => p.Path;
            SourceProfile? M(string path) => SourceProfiles.Match("x", path, paths, Scope);

            Assert.Equal("prefix", M("x://b/pre/sub")?.Name);
            Assert.Equal("bucket", M("x://b/prefix2")?.Name);   // boundary at the separator
            Assert.Equal("bucket", M("x://b")?.Name);
            Assert.Equal("other", M("x://c/d")?.Name);
            Assert.Equal("any", M("x://z")?.Name);
            Assert.Null(SourceProfiles.Match("z", "x://b", paths, Scope));

            SourceProfiles.PreferredId = SourceProfiles.All.Single(p => p.Name == "any").Id;
            Assert.Equal("any", M("x://b/pre/sub")?.Name);
            SourceProfiles.PreferredId = SourceProfiles.All.Single(p => p.Name == "other").Id;
            Assert.Equal("prefix", M("x://b/pre/sub")?.Name);     // preferred, but its scope does not fit
        }
        finally
        {
            SourceProfiles.PreferredId = null;
            SourceProfiles.Set([]);
        }
    }

    [Fact]
    public void Set_keeps_copies_and_bumps_the_version()
    {
        var p = Profile("x", "x://a", "one");
        int v = SourceProfiles.Version;
        try
        {
            SourceProfiles.Set([p]);
            p.Name = "changed";
            Assert.Equal("one", SourceProfiles.All[0].Name);
            Assert.True(SourceProfiles.Version > v);
        }
        finally { SourceProfiles.Set([]); }
    }

    [Fact]
    public void Profiles_are_saved_in_the_settings_without_secrets()
    {
        using var dir = new TempDir();
        var file = System.IO.Path.Combine(dir.Path, "settings.json");
        var profile = new SourceProfile
        {
            Name = "Backup", Kind = "s3", Endpoint = "http://localhost:9000", Region = "eu-central-1", PathStyle = true,
            Auth = SourceAuth.AccessKey, UserName = "AKIDEXAMPLE", Path = "s3://bucket/pre",
        };
        var store = new MemorySecretStore();
        var old = SourceSecrets.Store;
        SourceSecrets.Store = store;
        try
        {
            SourceSecrets.Write(profile, SecretSlot.Secret, "geheim-123");
            var s = new AppSettings();
            s.Sources.Add(profile);
            System.IO.File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(s));

            var text = System.IO.File.ReadAllText(file);
            Assert.DoesNotContain("geheim", text);
            var back = AppSettings.LoadFrom(file);
            var p = Assert.Single(back.Sources);
            Assert.Equal(profile.Id, p.Id);
            Assert.Equal("http://localhost:9000", p.Endpoint);
            Assert.True(p.PathStyle);
            Assert.Equal(SourceAuth.AccessKey, p.Auth);
            Assert.Equal("geheim-123", SourceSecrets.Read(p));

            SourceSecrets.Write(profile, SecretSlot.Passphrase, "pp");
            SourceSecrets.DeleteAll(p);
            Assert.Null(SourceSecrets.Read(p));
            Assert.Null(SourceSecrets.Read(p, SecretSlot.Passphrase));
            Assert.Empty(store.Targets);
        }
        finally { SourceSecrets.Store = old; }
    }

    [Fact]
    public void Old_settings_without_sources_load_with_defaults()
    {
        using var dir = new TempDir();
        var file = System.IO.Path.Combine(dir.Path, "settings.json");
        System.IO.File.WriteAllText(file, "{\"LastPath\":\"C:\\\\\",\"Threads\":8}");
        var s = AppSettings.LoadFrom(file);
        Assert.Empty(s.Sources);
        Assert.Equal(100_000, s.RequestWarnThreshold);
        Assert.Equal(8, s.Threads);
    }

    [Fact]
    public void Credential_manager_round_trip()
    {
        // The real Windows Credential Manager of the test user, with a target nobody else uses; removed again.
        var target = $"DiskTree-Test:{Guid.NewGuid():N}";
        var store = CredentialStore.Instance;
        try
        {
            Assert.False(store.TryRead(target, out _));
            store.Write(target, "user", "pässwört-€");
            Assert.True(store.TryRead(target, out var s));
            Assert.Equal("pässwört-€", s);
            store.Write(target, "user", "neu");
            Assert.True(store.TryRead(target, out s));
            Assert.Equal("neu", s);
        }
        finally
        {
            store.Delete(target);
        }
        Assert.False(store.TryRead(target, out _));
        store.Delete(target); // deleting twice is fine
    }

    [Fact]
    public void Secret_targets_follow_the_profile_id()
    {
        Assert.Equal("DiskTree:abc", SourceSecrets.Target("abc"));
        Assert.Equal("DiskTree:abc:passphrase", SourceSecrets.Target("abc", SecretSlot.Passphrase));
    }
}
