using System.IO;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using DiskTree.Core;
using DiskTree.Core.Storage;
using DiskTree.Features.FileOperations;
using DiskTree.Features.Storage.S3;
using Xunit;
using Xunit.Abstractions;

namespace DiskTree.Tests.S3;

/// <summary>
/// Delete, rename and move against a real S3-compatible server (own local container, see <see cref="S3IntegrationTests"/>
/// for the variables): files and folders, conflicts, versioning (if the server has it), cancel. After every action the
/// tree equals a fresh scan of the server.
/// </summary>
[Collection(DiskTree.Tests.Storage.SourceStateCollection.Name)]
public sealed class S3FileOpsIntegrationTests : IDisposable
{
    private static string Endpoint => Environment.GetEnvironmentVariable(S3IntegrationTests.EndpointVar) ?? "";
    private static string AccessKey => Environment.GetEnvironmentVariable("DISKTREE_S3_TEST_ACCESS_KEY") ?? "";
    private static string SecretKey => Environment.GetEnvironmentVariable("DISKTREE_S3_TEST_SECRET_KEY") ?? "";

    private readonly ITestOutputHelper _out;
    private readonly string _bucket = "dt-ops-" + Guid.NewGuid().ToString("N")[..12];
    private readonly ISecretStore _oldStore = SourceSecrets.Store;
    private readonly MemorySecretStore _store = new();
    private readonly SourceProfile _profile;
    private AmazonS3Client? _admin;

    public S3FileOpsIntegrationTests(ITestOutputHelper output)
    {
        _out = output;
        S3Storage.Register();
        SourceSecrets.Store = _store;
        _profile = new SourceProfile
        {
            Kind = "s3", Name = "Integration", Endpoint = Endpoint, PathStyle = true, Auth = SourceAuth.AccessKey,
            UserName = AccessKey, Path = "s3://" + _bucket,
        };
        SourceSecrets.Write(_profile, SecretSlot.Secret, SecretKey);
        SourceProfiles.Set([_profile]);
    }

    private AmazonS3Client Admin => _admin ??= new AmazonS3Client(new BasicAWSCredentials(AccessKey, SecretKey),
        new AmazonS3Config
        {
            ServiceURL = Endpoint, ForcePathStyle = true, AuthenticationRegion = "us-east-1",
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        });

    public void Dispose()
    {
        try
        {
            if (_admin != null)
            {
                // All versions and delete markers, then the bucket.
                string? keyMarker = null, versionMarker = null;
                while (true)
                {
                    var page = _admin.ListVersionsAsync(new ListVersionsRequest { BucketName = _bucket, KeyMarker = keyMarker, VersionIdMarker = versionMarker })
                        .GetAwaiter().GetResult();
                    foreach (var v in page.Versions ?? [])
                        _admin.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucket, Key = v.Key, VersionId = v.VersionId }).GetAwaiter().GetResult();
                    if (page.IsTruncated != true) break;
                    keyMarker = page.NextKeyMarker;
                    versionMarker = page.NextVersionIdMarker;
                }
                string? token = null;
                do
                {
                    var page = _admin.ListObjectsV2Async(new ListObjectsV2Request { BucketName = _bucket, ContinuationToken = token }).GetAwaiter().GetResult();
                    foreach (var o in page.S3Objects ?? []) _admin.DeleteObjectAsync(_bucket, o.Key).GetAwaiter().GetResult();
                    token = page.IsTruncated == true ? page.NextContinuationToken : null;
                } while (token != null);
                _admin.DeleteBucketAsync(_bucket).GetAwaiter().GetResult();
                _admin.Dispose();
            }
        }
        catch (Exception ex) { _out.WriteLine("Aufräumen: " + ex.Message); }
        SourceSecrets.Store = _oldStore;
        SourceProfiles.Set([]);
    }

    private async Task Put(string key, int size, string? contentType = null, Dictionary<string, string>? meta = null,
        List<Tag>? tags = null)
    {
        var r = new PutObjectRequest { BucketName = _bucket, Key = key, InputStream = new MemoryStream(new byte[size]) };
        if (contentType != null) r.ContentType = contentType;
        if (meta != null) foreach (var (k, v) in meta) r.Metadata.Add(k, v);
        if (tags != null) r.TagSet = tags;
        await Admin.PutObjectAsync(r);
    }

    private async Task Fill()
    {
        await Admin.PutBucketAsync(_bucket);
        await Put("readme.txt", 10, "text/plain", new() { ["herkunft"] = "test" }, [new Tag { Key = "projekt", Value = "dt" }]);
        await Put("a/", 0);
        await Put("a/x.bin", 1000);
        await Put("a/tief/z.bin", 2000);
        await Put("b/", 0);
        await Put("b/1.txt", 1);
        await Put("solo/einzig.bin", 5);
    }

    private IStorageProvider Provider
    {
        get
        {
            Assert.True(StorageProviders.TryResolve($"s3://{_bucket}", out var loc));
            return loc.Provider;
        }
    }

    private S3Writer Writer(ListRun? run = null, Action<long, long>? progress = null) =>
        (S3Writer)((IRunBoundWriter)Provider).Bind(run ?? new ListRun(), progress);

    private async Task<Scanner> ScanOf()
    {
        var s = new Scanner { Provider = Provider };
        await s.ScanAsync($"s3://{_bucket}", 8, CancellationToken.None);
        Assert.Equal(0, s.Errors);
        return s;
    }

    private async Task AssertSameAsFreshScan(Scanner s)
    {
        var fresh = await ScanOf();
        TreeAssert.Equal(fresh.Root!, s.Root!);
        Assert.Equal(fresh.TopFiles().Select(f => (f.Path, f.Size)).OrderBy(x => x.Path, StringComparer.Ordinal),
            s.TopFiles().Select(f => (f.Path, f.Size)).OrderBy(x => x.Path, StringComparer.Ordinal));
    }

    private string P(string rel) => $"s3://{_bucket}/{rel}";

    private static OpItem Item(string path, bool dir, long size = 0) => new(path, dir, size, -1, S3PathOps.Instance);

    private async Task<bool> Exists(string key)
    {
        try
        {
            await Admin.GetObjectMetadataAsync(_bucket, key);
            return true;
        }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return false; }
    }

    private async Task<List<string>> Keys(string prefix = "")
    {
        var keys = new List<string>();
        string? token = null;
        do
        {
            var page = await Admin.ListObjectsV2Async(new ListObjectsV2Request { BucketName = _bucket, Prefix = prefix, ContinuationToken = token });
            keys.AddRange((page.S3Objects ?? []).Select(o => o.Key));
            token = page.IsTruncated == true ? page.NextContinuationToken : null;
        } while (token != null);
        return keys;
    }

    [S3IntegrationFact]
    public async Task Rename_and_move_a_file_keep_type_metadata_and_tags_and_the_tree_equals_a_fresh_scan()
    {
        await Fill();
        var s = await ScanOf();
        var w = Writer();
        var before = await Admin.GetObjectTaggingAsync(new GetObjectTaggingRequest { BucketName = _bucket, Key = "readme.txt" });
        _out.WriteLine("Tags vorher: " + string.Join(",", (before.Tagging ?? []).Select(t => t.Key + "=" + t.Value)));
        var rules = new RenameRules(S3PathOps.Instance, new Dictionary<string, IReadOnlyCollection<string>> { [P("").TrimEnd('/')] = w.ListNames(P("").TrimEnd('/'), default) });
        var items = RemoteFileOperations.Refresh(w, [Item(P("readme.txt"), false, 10)], s.Ages, new OpResult(), null, default);
        var res = RemoteFileOperations.Rename(w, RemoteFileOperations.PlanSingle(items[0], "Readme.md", rules), default);
        Assert.Single(res.Done);
        TreeUpdate.Renamed(s, res);
        await AssertSameAsFreshScan(s);

        var head = await Admin.GetObjectMetadataAsync(_bucket, "Readme.md");
        Assert.Equal("text/plain", head.Headers.ContentType);
        Assert.Equal("test", head.Metadata["x-amz-meta-herkunft"]);
        var tags = await Admin.GetObjectTaggingAsync(new GetObjectTaggingRequest { BucketName = _bucket, Key = "Readme.md" });
        _out.WriteLine("Tags nachher: " + string.Join(",", (tags.Tagging ?? []).Select(t => t.Key + "=" + t.Value)));
        if (before.Tagging is { Count: > 0 }) Assert.Contains(tags.Tagging ?? [], t => t.Key == "projekt" && t.Value == "dt");
        Assert.False(await Exists("readme.txt"));

        // Move into another folder: the last object of "solo" (no marker) – the folder disappears.
        items = RemoteFileOperations.Refresh(w, [Item(P("solo/einzig.bin"), false, 5)], s.Ages, new OpResult(), null, default);
        res = RemoteFileOperations.Move(w, S3PathOps.Instance, items, P("b"), ConflictPolicy.Ask, null, default);
        Assert.Single(res.Done);
        Assert.Equal([P("solo")], res.Vanished);
        TreeUpdate.Removed(s, res.Done, movedTo: res.NewPaths);
        TreeUpdate.Vanished(s, res.Vanished);
        Assert.Null(TreeUpdate.Arrived(s, P("b"), res));
        await AssertSameAsFreshScan(s);
    }

    [S3IntegrationFact]
    public async Task Rename_move_and_delete_a_folder_with_markers_and_more_than_1000_objects()
    {
        await Fill();
        await Parallel.ForEachAsync(Enumerable.Range(0, 1050), new ParallelOptions { MaxDegreeOfParallelism = 16 },
            async (i, _) => await Put($"a/viel/{i:D4}", 1));
        var s = await ScanOf();
        var run = new ListRun();
        var w = Writer(run);

        // Rename "a" → "a2" (copies + delete in batches).
        var items = RemoteFileOperations.Refresh(w, [Item(P("a"), true)], s.Ages, new OpResult(), null, default);
        var plan = new CopyPlan(items, false, "S3", w, i => s.FindNode(i.Path) is { } n ? (n.FileCount, n.DirCount, n.Size) : null);
        plan.Confirm();
        var rules = new RenameRules(S3PathOps.Instance, new Dictionary<string, IReadOnlyCollection<string>> { [P("").TrimEnd('/')] = w.ListNames(P("").TrimEnd('/'), default) });
        long before = run.Requests;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var res = RemoteFileOperations.Rename(w, RemoteFileOperations.PlanSingle(items[0], "a2", rules), default, plan);
        _out.WriteLine($"Umbenennen von 1053 Objekten: {sw.ElapsedMilliseconds} ms, {run.Requests - before} Anfragen (geschätzt {plan.Cost.Total})");
        Assert.Single(res.Done);
        Assert.Empty(await Keys("a/"));
        var moved = await Keys("a2/");
        Assert.Equal(1050 + 3, moved.Count);   // the marker "a2/", x.bin, tief/z.bin and the 1050
        Assert.Contains("a2/", moved);
        TreeUpdate.Renamed(s, res);
        await AssertSameAsFreshScan(s);

        // Move "a2" into "b".
        items = RemoteFileOperations.Refresh(w, [Item(P("a2"), true)], s.Ages, new OpResult(), null, default);
        plan = new CopyPlan(items, true, "S3", w);
        plan.Confirm();
        res = RemoteFileOperations.Move(w, S3PathOps.Instance, items, P("b"), ConflictPolicy.Ask, null, default, plan);
        Assert.Single(res.Done);
        TreeUpdate.Removed(s, res.Done, movedTo: res.NewPaths);
        TreeUpdate.Vanished(s, res.Vanished);
        var again = TreeUpdate.Arrived(s, P("b"), res);
        await s.RescanAsync(again!, 4, default);
        await AssertSameAsFreshScan(s);

        // Delete it for good.
        items = RemoteFileOperations.Refresh(w, [Item(P("b/a2"), true)], s.Ages, new OpResult(), null, default);
        var del = new PermanentDeletePlan(items, "S3");
        del.Confirm();
        var gone = RemoteFileOperations.Delete(w, del, null, default);
        Assert.Single(gone.Done);
        Assert.Empty(await Keys("b/a2/"));
        Assert.True(await Exists("b/1.txt"));
        TreeUpdate.Removed(s, gone.Done);
        TreeUpdate.Vanished(s, gone.Vanished);
        await AssertSameAsFreshScan(s);
    }

    [S3IntegrationFact]
    public async Task Conflicts_never_replace_unasked()
    {
        await Fill();
        await Put("b/readme.txt", 777);
        var s = await ScanOf();
        var w = Writer();
        var items = RemoteFileOperations.Refresh(w, [Item(P("readme.txt"), false, 10)], s.Ages, new OpResult(), null, default);

        var res = RemoteFileOperations.Move(w, S3PathOps.Instance, items, P("b"), ConflictPolicy.Skip, null, default);
        Assert.Single(res.Skipped);
        Assert.Throws<IOException>(() => w.Move(P("readme.txt"), P("b"), null, replace: false, default));
        Assert.Equal(777, (await Admin.GetObjectMetadataAsync(_bucket, "b/readme.txt")).ContentLength);

        res = RemoteFileOperations.Move(w, S3PathOps.Instance, items, P("b"), ConflictPolicy.Rename, null, default);
        Assert.Equal(P("b/readme (2).txt"), res.NewPaths[P("readme.txt")]);
        TreeUpdate.Removed(s, res.Done, movedTo: res.NewPaths);
        Assert.Null(TreeUpdate.Arrived(s, P("b"), res));
        await AssertSameAsFreshScan(s);

        // Replace (asked for): the copy overwrites the old file, then the original goes.
        await Put("a/readme.txt", 3);
        s = await ScanOf();
        items = RemoteFileOperations.Refresh(w, [Item(P("b/readme.txt"), false, 777)], s.Ages, new OpResult(), null, default);
        res = RemoteFileOperations.Move(w, S3PathOps.Instance, items, P("a"), ConflictPolicy.Replace, null, default);
        Assert.Single(res.Done);
        Assert.True(res.Replaced);
        Assert.Equal(777, (await Admin.GetObjectMetadataAsync(_bucket, "a/readme.txt")).ContentLength);
        Assert.False(await Exists("b/readme.txt"));
    }

    [S3IntegrationFact]
    public async Task Versioning_is_reported_and_only_delete_markers_are_written()
    {
        await Fill();
        try
        {
            await Admin.PutBucketVersioningAsync(new PutBucketVersioningRequest
            {
                BucketName = _bucket, VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled },
            });
        }
        catch (AmazonS3Exception ex)
        {
            _out.WriteLine("Server ohne Versionierung: " + ex.Message);
            return;
        }
        await Put("v/datei.txt", 4);
        await Put("v/datei.txt", 5); // a second version

        var w = Writer();
        var note = w.DescribeDelete([P("v/datei.txt")], default);
        _out.WriteLine(note.Highlight ?? note.Text);
        Assert.Contains("Löschmarkierungen", note.Highlight);

        var plan = new PermanentDeletePlan([Item(P("v/datei.txt"), false, 5)], "S3") { SourceNote = note };
        plan.Confirm();
        var res = RemoteFileOperations.Delete(w, plan, null, default);
        Assert.Single(res.Done);
        Assert.False(await Exists("v/datei.txt"));
        var versions = await Admin.ListVersionsAsync(new ListVersionsRequest { BucketName = _bucket, Prefix = "v/" });
        var all = versions.Versions ?? [];
        _out.WriteLine(string.Join(", ", all.Select(v => $"{v.Key} {v.VersionId} marker={v.IsDeleteMarker}")));
        Assert.Equal(2, all.Count(v => v.IsDeleteMarker != true));   // both old versions stay
        Assert.Equal(1, all.Count(v => v.IsDeleteMarker == true));
    }

    [S3IntegrationFact]
    public async Task Cancel_while_moving_a_folder_leaves_every_object_at_exactly_one_place()
    {
        await Fill();
        await Parallel.ForEachAsync(Enumerable.Range(0, 60), new ParallelOptions { MaxDegreeOfParallelism = 16 },
            async (i, _) => await Put($"q/f{i:D2}", 3));
        var s = await ScanOf();
        using var cts = new CancellationTokenSource();
        var w = Writer(new ListRun(cts.Token), (objects, _) => { if (objects >= 10) cts.Cancel(); });
        var items = RemoteFileOperations.Refresh(w, [Item(P("q"), true)], s.Ages, new OpResult(), null, default);
        var plan = new CopyPlan(items, true, "S3", w);
        plan.Confirm();

        var res = RemoteFileOperations.Move(w, S3PathOps.Instance, items, P("b"), ConflictPolicy.Skip, null, cts.Token, plan);

        Assert.True(res.Cancelled);
        Assert.Equal([P("q")], res.Partial.Select(i => i.Path));
        _out.WriteLine(string.Join("\n", res.Failed.Select(f => f.Error)));
        var keys = (await Keys()).ToHashSet(StringComparer.Ordinal);
        int atNew = 0;
        for (int i = 0; i < 60; i++)
        {
            bool old = keys.Contains($"q/f{i:D2}"), now = keys.Contains($"b/q/f{i:D2}");
            Assert.True(old ^ now, $"f{i:D2}: alt={old}, neu={now}");
            if (now) atNew++;
        }
        Assert.InRange(atNew, 10, 11);

        await s.RescanAsync(s.FindNode(P("q"))!, 4, default);
        await s.RescanAsync(s.FindNode(P("b"))!, 4, default);
        await AssertSameAsFreshScan(s);
    }

    [S3IntegrationFact]
    public async Task Storage_class_is_kept_if_the_server_knows_it()
    {
        await Fill();
        try
        {
            await Admin.PutObjectAsync(new PutObjectRequest
            {
                BucketName = _bucket, Key = "klasse/x.bin", InputStream = new MemoryStream(new byte[3]), StorageClass = S3StorageClass.ReducedRedundancy,
            });
        }
        catch (AmazonS3Exception ex)
        {
            _out.WriteLine("Server ohne Storage-Klassen: " + ex.Message);
            return;
        }
        var cls = (await Admin.GetObjectMetadataAsync(_bucket, "klasse/x.bin")).StorageClass?.Value;
        _out.WriteLine("Klasse laut Server: " + (cls ?? "(keine)"));
        var w = Writer();
        var res = RemoteFileOperations.Move(w, S3PathOps.Instance, [Item(P("klasse/x.bin"), false, 3)], P("b"), ConflictPolicy.Skip, null, default);
        _out.WriteLine(string.Join("\n", res.Failed.Select(f => f.Error)));
        if (res.Done.Count == 1)
            Assert.Equal(cls, (await Admin.GetObjectMetadataAsync(_bucket, "b/x.bin")).StorageClass?.Value);
        else
            Assert.True(await Exists("klasse/x.bin")); // refused: the original stays
    }
}
