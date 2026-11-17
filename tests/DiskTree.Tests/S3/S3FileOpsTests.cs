using System.IO;
using DiskTree.Core;
using DiskTree.Core.Storage;
using DiskTree.Features.FileOperations;
using DiskTree.Features.Storage.S3;
using Xunit;

namespace DiskTree.Tests.S3;

/// <summary>
/// Delete, rename and move on S3 against the in-memory server: DeleteObjects in batches, folders as prefixes (markers
/// included), copy before delete, what a copy keeps, refusals (archive, &gt; 5 GB, SSE-C), errors per object, cancel,
/// versioning note, confirmation, costs through the ListRun, and the tree after each action equals a fresh scan.
/// </summary>
[Collection(DiskTree.Tests.Storage.SourceStateCollection.Name)]
public sealed class S3FileOpsTests : IDisposable
{
    private static readonly DateTime Old = new(2023, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly Func<SourceProfile?, string?, IS3Client> _oldFactory = S3Provider.ClientFactory;
    private readonly MemoryS3Client _server = new();

    public S3FileOpsTests()
    {
        S3Storage.Register();
        S3Provider.ClientFactory = (_, _) => _server;
        SourceProfiles.Set([]);
    }

    public void Dispose()
    {
        S3Provider.ClientFactory = _oldFactory;
        SourceProfiles.Set([]);
    }

    private static S3Provider Provider => S3Provider.For(null);

    private static S3Writer Writer(ListRun? run = null, Action<long, long>? progress = null) =>
        (S3Writer)Provider.Bind(run ?? new ListRun(), progress);

    private MemoryS3Client Sample() => _server
        .Put("bk", "readme.txt", 10, Old)
        .Put("bk", "a/", 0, Old)                 // marker of "a"
        .Put("bk", "a/x.bin", 2_000_000, Old)
        .Put("bk", "a/y.txt", 50, Old)
        .Put("bk", "a/tief/", 0, Old)            // marker of "a/tief"
        .Put("bk", "a/tief/z.iso", 5_000_000, Old)
        .Put("bk", "a", 7, Old)                  // a file next to the folder "a"
        .Put("bk", "ab/neben.txt", 3, Old)       // shares the prefix "a" but not "a/"
        .Put("bk", "b/", 0, Old)
        .Put("bk", "b/1.txt", 1, Old)
        .Put("bk", "solo/einzig.bin", 99, Old);  // a folder without marker

    private static async Task<Scanner> ScanOf(string path = "s3://bk")
    {
        var s = new Scanner { Provider = Provider };
        await s.ScanAsync(path, 4, CancellationToken.None);
        return s;
    }

    private static OpItem Item(string path, bool dir, long size = 0) => new(path, dir, size, -1, S3PathOps.Instance);

    private static List<OpItem> Fresh(IStorageWriter w, Scanner s, params OpItem[] items) =>
        RemoteFileOperations.Refresh(w, items, s.Ages, new OpResult(), null, CancellationToken.None);

    private static async Task AssertSameAsFreshScan(Scanner s)
    {
        var fresh = await ScanOf(s.Root!.FullPath);
        TreeAssert.Equal(fresh.Root!, s.Root!);
        Assert.Equal(fresh.TopFiles().Select(f => (f.Path, f.Size)).OrderBy(x => x.Path, StringComparer.Ordinal),
            s.TopFiles().Select(f => (f.Path, f.Size)).OrderBy(x => x.Path, StringComparer.Ordinal));
    }

    private static PermanentDeletePlan Confirmed(IReadOnlyList<OpItem> items)
    {
        var plan = new PermanentDeletePlan(items, "S3 (Test)");
        plan.Confirm();
        return plan;
    }

    // ------------------------------------------------------------------ caps

    [Fact]
    public void S3_offers_delete_move_rename_and_duplicates_get_check_marks()
    {
        var p = Provider;
        Assert.True(StorageFeatures.IsRemoteWritable(p));
        Assert.True(StorageFeatures.Has(p, StorageFeature.Delete)); // what DuplicatesPanel asks for the check boxes
        Assert.False(StorageFeatures.Has(p, StorageFeature.Trash));
        Assert.False(StorageFeatures.Has(p, StorageFeature.Zip));
        Assert.False(StorageFeatures.Has(p, StorageFeature.Owner));
        Assert.IsAssignableFrom<IRunBoundWriter>(p);
        Assert.IsAssignableFrom<ICopyingWriter>(Writer());
    }

    // ------------------------------------------------------------------ delete

    [Fact]
    public void Delete_without_confirmation_sends_nothing()
    {
        Sample();
        var plan = new PermanentDeletePlan([Item("s3://bk/readme.txt", false, 10), Item("s3://bk/a", true)], "S3");
        int before = _server.Requests;
        Assert.Throws<InvalidOperationException>(() => RemoteFileOperations.Delete(Writer(), plan, null, CancellationToken.None));
        Assert.Equal(before, _server.Requests);
        Assert.Empty(_server.Ops);
    }

    [Fact]
    public async Task Many_files_go_in_batches_of_1000()
    {
        for (int i = 0; i < 2500; i++) _server.Put("bk", $"viel/f{i:D4}.bin", 2, Old);
        _server.Put("bk", "viel/bleibt.txt", 1, Old);
        var s = await ScanOf();
        var w = Writer();
        int before = _server.Requests;
        var items = Fresh(w, s, Enumerable.Range(0, 2500).Select(i => Item($"s3://bk/viel/f{i:D4}.bin", false, 2)).ToArray());
        Assert.Equal(3, _server.Requests - before);               // looked up by listing the folder (3 pages), not 2500 HEADs
        Assert.All(items, i => Assert.True(i.AgeBucket >= 0));
        var res = RemoteFileOperations.Delete(w, Confirmed(items), null, CancellationToken.None);

        Assert.Equal(2500, res.Done.Count);
        Assert.Equal(3, _server.DeleteRequests);                 // 1000 + 1000 + 500
        Assert.Equal(["viel/bleibt.txt"], _server.Keys("bk"));
        Assert.Empty(res.Vanished);                               // "bleibt.txt" keeps the folder
        TreeUpdate.Removed(s, res.Done);
        await AssertSameAsFreshScan(s);
    }

    [Fact]
    public async Task A_folder_goes_with_all_keys_and_markers_but_not_its_neighbours()
    {
        Sample();
        for (int i = 0; i < 1500; i++) _server.Put("bk", $"a/viel/{i:D4}", 1, Old);
        var s = await ScanOf();
        var w = Writer();
        var items = Fresh(w, s, Item("s3://bk/a", true));
        Assert.True(items.Single().IsDir);                        // the folder, not the file "a" next to it
        var res = RemoteFileOperations.Delete(w, Confirmed(items), null, CancellationToken.None);

        Assert.Single(res.Done);
        Assert.DoesNotContain(_server.Keys("bk"), k => k.StartsWith("a/", StringComparison.Ordinal));
        Assert.True(_server.Has("bk", "a"));                      // the file "a" stays
        Assert.True(_server.Has("bk", "ab/neben.txt"));
        Assert.Equal(2, _server.DeleteRequests);                  // 1505 keys: two pages, two DeleteObjects
        TreeUpdate.Removed(s, res.Done);
        TreeUpdate.Vanished(s, res.Vanished);
        await AssertSameAsFreshScan(s);
    }

    [Fact]
    public async Task Access_denied_is_collected_per_object_the_rest_goes()
    {
        Sample();
        _server.DenyDelete.Add("a/tief/z.iso");
        var s = await ScanOf();
        var w = Writer();
        var items = Fresh(w, s, Item("s3://bk/a", true), Item("s3://bk/readme.txt", false, 10));
        var res = RemoteFileOperations.Delete(w, Confirmed(items), null, CancellationToken.None);

        Assert.Equal(["s3://bk/readme.txt"], res.Done.Select(i => i.Path));
        Assert.Contains(res.Failed, f => f.Path == "s3://bk/a/tief/z.iso" && f.Error.Contains("AccessDenied"));
        Assert.Equal(["s3://bk/a"], res.Partial.Select(i => i.Path));
        Assert.True(_server.Has("bk", "a/tief/z.iso"));
        Assert.False(_server.Has("bk", "a/x.bin"));

        TreeUpdate.Removed(s, res.Done);
        foreach (var part in res.Partial) await s.RescanAsync(s.FindNode(part.Path)!, 2, CancellationToken.None);
        await AssertSameAsFreshScan(s);
    }

    [Fact]
    public async Task Deleting_the_last_object_of_a_folder_without_marker_removes_the_folder()
    {
        Sample();
        var s = await ScanOf();
        var w = Writer();
        var res = RemoteFileOperations.Delete(w, Confirmed(Fresh(w, s, Item("s3://bk/solo/einzig.bin", false, 99))), null, CancellationToken.None);
        Assert.Equal(["s3://bk/solo"], res.Vanished);
        TreeUpdate.Removed(s, res.Done);
        TreeUpdate.Vanished(s, res.Vanished);
        Assert.Null(s.FindNode("s3://bk/solo"));
        await AssertSameAsFreshScan(s);

        // With a marker the folder stays (empty).
        var res2 = RemoteFileOperations.Delete(w, Confirmed(Fresh(w, s, Item("s3://bk/b/1.txt", false, 1))), null, CancellationToken.None);
        Assert.Empty(res2.Vanished);
        TreeUpdate.Removed(s, res2.Done);
        await AssertSameAsFreshScan(s);
    }

    [Theory]
    [InlineData(S3Versioning.Enabled, "Löschmarkierungen", "aktiv")]
    [InlineData(S3Versioning.Suspended, "Löschmarkierungen", "ausgesetzt")]
    public void Versioned_buckets_say_only_delete_markers_are_written_and_old_versions_stay(S3Versioning state, string word, string how)
    {
        Sample();
        _server.Versioning["bk"] = state;
        var w = Writer();
        var note = w.DescribeDelete(["s3://bk/readme.txt"], CancellationToken.None);
        Assert.Contains(word, note.Highlight);
        Assert.Contains(how, note.Highlight);
        Assert.Contains("Versionen", note.Highlight);
        Assert.Contains("bleiben erhalten", note.Highlight);
        Assert.Contains("Speicher", note.Highlight);
        Assert.Contains("löscht keine Versionen", note.Text);

        var plan = new PermanentDeletePlan([Item("s3://bk/readme.txt", false, 10)], "S3") { SourceNote = note };
        var text = plan.Describe();
        Assert.Contains("löscht keine Versionen", text);
        Assert.DoesNotContain("Diese Quelle hat keinen Papierkorb", text);

        plan.Confirm();
        var res = RemoteFileOperations.Delete(w, plan, null, CancellationToken.None);
        Assert.Single(res.Done);
        Assert.Equal(1, _server.DeleteMarkers);
        Assert.Equal(1, _server.OldVersions["bk/readme.txt"]); // the old version is kept – no version is ever deleted
    }

    [Fact]
    public void Unversioned_or_unknown_versioning_is_said_too()
    {
        Sample();
        var off = Writer().DescribeDelete(["s3://bk/a"], CancellationToken.None);
        Assert.Null(off.Highlight);
        Assert.Contains("nicht versioniert", off.Text);
        Assert.Contains("endgültig", off.Text);

        _server.DenyVersioning.Add("bk");
        var unknown = Writer().DescribeDelete(["s3://bk/a"], CancellationToken.None);
        Assert.Contains("ließ sich nicht prüfen", unknown.Text);
        Assert.Contains("Löschmarkierungen", unknown.Text);
    }

    // ------------------------------------------------------------------ copy + delete

    [Fact]
    public async Task Moving_a_file_copies_first_checks_and_then_deletes_keeping_class_tags_metadata_and_kms()
    {
        Sample();
        _server.With("bk", "a/x.bin", x =>
        {
            x.StorageClass = "STANDARD_IA";
            x.Encryption = "aws:kms";
            x.KmsKeyId = "arn:aws:kms:eu-central-1:1:key/abc";
            x.BucketKey = true;
            x.ContentType = "application/x-test";
            x.Metadata["herkunft"] = "scanner";
            x.Tags["projekt"] = "dt";
        });
        var s = await ScanOf();
        var w = Writer();
        var items = Fresh(w, s, Item("s3://bk/a/x.bin", false, 2_000_000));
        var res = RemoteFileOperations.Move(w, S3PathOps.Instance, items, "s3://bk/b", ConflictPolicy.Ask, null, CancellationToken.None);

        Assert.Equal("s3://bk/b/x.bin", res.NewPaths["s3://bk/a/x.bin"]);
        int copy = _server.Ops.IndexOf("copy a/x.bin -> b/x.bin"), delete = _server.Ops.IndexOf("delete a/x.bin");
        Assert.True(copy >= 0 && delete > copy, string.Join(" | ", _server.Ops));
        Assert.False(_server.Has("bk", "a/x.bin"));
        var moved = _server.ExtraOf("bk", "b/x.bin");
        Assert.Equal("STANDARD_IA", moved.StorageClass);
        Assert.Equal("aws:kms", moved.Encryption);
        Assert.Equal("arn:aws:kms:eu-central-1:1:key/abc", moved.KmsKeyId);
        Assert.True(moved.BucketKey);
        Assert.Equal("application/x-test", moved.ContentType);
        Assert.Equal("scanner", moved.Metadata["herkunft"]);
        Assert.Equal("dt", moved.Tags["projekt"]);

        // The copy is new: its age bucket follows; the tree equals a fresh scan.
        Assert.True(res.NewTicks.ContainsKey("s3://bk/b/x.bin"));
        TreeUpdate.Removed(s, res.Done, movedTo: res.NewPaths);
        TreeUpdate.Vanished(s, res.Vanished);
        Assert.Null(TreeUpdate.Arrived(s, "s3://bk/b", res));
        await AssertSameAsFreshScan(s);
    }

    [Fact]
    public void A_failed_copy_leaves_the_original_alone()
    {
        Sample();
        _server.DenyCopy.Add("a/y.txt");
        var w = Writer();
        var res = RemoteFileOperations.Move(w, S3PathOps.Instance, [Item("s3://bk/a/y.txt", false, 50), Item("s3://bk/readme.txt", false, 10)],
            "s3://bk/b", ConflictPolicy.Skip, null, CancellationToken.None);
        Assert.Contains(res.Failed, f => f.Path == "s3://bk/a/y.txt" && f.Error.Contains("AccessDenied"));
        Assert.Equal(["s3://bk/readme.txt"], res.Done.Select(i => i.Path));   // the rest goes on
        Assert.True(_server.Has("bk", "a/y.txt"));
        Assert.False(_server.Has("bk", "b/y.txt"));
        Assert.DoesNotContain(_server.Ops, o => o.StartsWith("delete a/y.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void A_copy_that_does_not_match_is_removed_and_the_original_stays()
    {
        Sample();
        _server.BreakCopy.Add("a/y.txt");
        var res = RemoteFileOperations.Move(Writer(), S3PathOps.Instance, [Item("s3://bk/a/y.txt", false, 50)], "s3://bk/b",
            ConflictPolicy.Skip, null, CancellationToken.None);
        Assert.Contains("stimmt nicht", res.Failed.Single().Error);
        Assert.Contains("wieder entfernt", res.Failed.Single().Error);
        Assert.True(_server.Has("bk", "a/y.txt"));
        Assert.False(_server.Has("bk", "b/y.txt"));
    }

    [Fact]
    public void A_storage_class_the_server_drops_counts_as_a_wrong_copy()
    {
        Sample();
        _server.With("bk", "a/y.txt", x => x.StorageClass = "ONEZONE_IA");
        _server.DropClass.Add("a/y.txt");
        var res = RemoteFileOperations.Move(Writer(), S3PathOps.Instance, [Item("s3://bk/a/y.txt", false, 50)], "s3://bk/b",
            ConflictPolicy.Skip, null, CancellationToken.None);
        Assert.Contains("Storage-Klasse STANDARD statt ONEZONE_IA", res.Failed.Single().Error);
        Assert.True(_server.Has("bk", "a/y.txt"));
        Assert.False(_server.Has("bk", "b/y.txt"));
    }

    [Fact]
    public void An_original_that_cannot_be_deleted_gets_its_copy_removed()
    {
        Sample();
        _server.DenyDelete.Add("a/y.txt");
        var res = RemoteFileOperations.Move(Writer(), S3PathOps.Instance, [Item("s3://bk/a/y.txt", false, 50)], "s3://bk/b",
            ConflictPolicy.Skip, null, CancellationToken.None);
        Assert.Contains("Kopie wurde wieder entfernt", res.Failed.Single().Error);
        Assert.True(_server.Has("bk", "a/y.txt"));
        Assert.False(_server.Has("bk", "b/y.txt"));
        Assert.Empty(res.Done);
    }

    [Fact]
    public void Archived_SSE_C_and_too_big_objects_are_refused_with_a_reason()
    {
        Sample();
        _server.Put("bk", "arch/kalt.bin", 100, Old).With("bk", "arch/kalt.bin", x => x.StorageClass = "GLACIER");
        _server.Put("bk", "arch/tief.bin", 100, Old).With("bk", "arch/tief.bin", x => x.StorageClass = "DEEP_ARCHIVE");
        _server.Put("bk", "arch/zurueck.bin", 100, Old).With("bk", "arch/zurueck.bin", x => { x.StorageClass = "GLACIER"; x.Restored = true; });
        _server.Put("bk", "arch/it.bin", 100, Old).With("bk", "arch/it.bin", x => { x.StorageClass = "INTELLIGENT_TIERING"; x.ArchiveStatus = "ARCHIVE_ACCESS"; });
        _server.Put("bk", "arch/geheim.bin", 100, Old).With("bk", "arch/geheim.bin", x => x.CustomerKey = true);
        _server.Put("bk", "arch/riesig.iso", 6L << 30, Old);

        var items = new[] { "kalt.bin", "tief.bin", "zurueck.bin", "it.bin", "geheim.bin", "riesig.iso" }
            .Select(n => Item("s3://bk/arch/" + n, false, 100)).ToList();
        var res = RemoteFileOperations.Move(Writer(), S3PathOps.Instance, items, "s3://bk/b", ConflictPolicy.Skip, null, CancellationToken.None);

        string Error(string name) => res.Failed.Single(f => f.Path == "s3://bk/arch/" + name).Error;
        Assert.Contains("wiederhergestellt", Error("kalt.bin"));
        Assert.Contains("DEEP_ARCHIVE", Error("tief.bin"));
        Assert.Contains("Intelligent-Tiering", Error("it.bin"));
        Assert.Contains("SSE-C", Error("geheim.bin"));
        Assert.Contains("5 GB", Error("riesig.iso"));
        Assert.Contains("Multipart", Error("riesig.iso"));
        Assert.Equal(["s3://bk/arch/zurueck.bin"], res.Done.Select(i => i.Path)); // restored: copyable, stays GLACIER
        Assert.Equal("GLACIER", _server.ExtraOf("bk", "b/zurueck.bin").StorageClass);
        Assert.Equal(1, _server.Copies);                                          // nothing else was even tried
        foreach (var n in new[] { "kalt.bin", "tief.bin", "it.bin", "geheim.bin", "riesig.iso" }) Assert.True(_server.Has("bk", "arch/" + n));
    }

    [Fact]
    public void A_folder_with_an_object_over_5_GB_or_archived_is_not_moved_at_all()
    {
        Sample();
        _server.Put("bk", "gross/klein.bin", 1, Old).Put("bk", "gross/riesig.iso", 6L << 30, Old);
        _server.Put("bk", "kalt/a.bin", 1, Old).Put("bk", "kalt/b.bin", 1, Old).With("bk", "kalt/b.bin", x => x.StorageClass = "GLACIER");
        var w = Writer();
        var plan = new CopyPlan([Item("s3://bk/gross", true), Item("s3://bk/kalt", true)], true, "S3", w);
        plan.Confirm();
        var res = RemoteFileOperations.Move(w, S3PathOps.Instance, [Item("s3://bk/gross", true), Item("s3://bk/kalt", true)], "s3://bk/b",
            ConflictPolicy.Skip, null, CancellationToken.None, plan);
        Assert.Contains(res.Failed, f => f.Path == "s3://bk/gross" && f.Error.Contains("nichts wurde verändert") && f.Error.Contains("5 GB"));
        Assert.Contains(res.Failed, f => f.Path == "s3://bk/kalt" && f.Error.Contains("GLACIER"));
        Assert.Empty(res.Partial);
        Assert.Equal(0, _server.Copies);
    }

    // ------------------------------------------------------------------ folders

    [Fact]
    public void Folders_need_a_confirmed_copy_plan()
    {
        Sample();
        var w = Writer();
        var items = new[] { Item("s3://bk/a", true) };
        Assert.Throws<InvalidOperationException>(() =>
            RemoteFileOperations.Move(w, S3PathOps.Instance, items, "s3://bk/b", ConflictPolicy.Skip, null, CancellationToken.None));
        var rules = new RenameRules(S3PathOps.Instance, new Dictionary<string, IReadOnlyCollection<string>> { ["s3://bk"] = w.ListNames("s3://bk", default) });
        var plans = RemoteFileOperations.PlanSingle(items[0], "A", rules);
        var unconfirmed = new CopyPlan(items, false, "S3", w);
        Assert.Throws<InvalidOperationException>(() => RemoteFileOperations.Rename(w, plans, CancellationToken.None, unconfirmed));
        Assert.Equal(0, _server.Copies);

        // Files alone need no plan.
        var res = RemoteFileOperations.Move(w, S3PathOps.Instance, [Item("s3://bk/readme.txt", false, 10)], "s3://bk/b",
            ConflictPolicy.Skip, null, CancellationToken.None);
        Assert.Single(res.Done);
    }

    [Fact]
    public void The_copy_plan_says_objects_data_requests_and_not_atomic()
    {
        var w = Writer();
        var plan = new CopyPlan([Item("s3://bk/a", true), Item("s3://bk/readme.txt", false, 10)], true, "S3 „Test“", w,
            i => i.Path == "s3://bk/a" ? (2500, 3, 7_000_000) : null);
        Assert.Equal(2501, plan.Files + plan.FolderObjects);
        Assert.Equal(7_000_010, plan.Bytes);
        Assert.True(plan.Cost.Copy >= 2501);
        Assert.True(plan.Cost.Head >= 2 * 2501);
        var text = plan.Describe();
        Assert.Contains("ca. 2.501", text);
        Assert.Contains("COPY (zählt wie PUT)", text);
        Assert.Contains("Nicht atomar", text);
        Assert.Contains("teils am alten, teils am neuen Ort", text);
        Assert.Contains("ACL", text);
        Assert.Contains("Storage-Klasse", text);
        Assert.Contains("S3 „Test“", text);
        Assert.False(plan.IsConfirmed);
        Assert.True(CopyPlan.Needed(w, plan.Items));
        Assert.False(CopyPlan.Needed(w, [Item("s3://bk/readme.txt", false)]));
    }

    [Fact]
    public async Task Renaming_a_folder_copies_every_key_with_markers_and_the_tree_equals_a_fresh_scan()
    {
        Sample();
        var s = await ScanOf();
        var w = Writer();
        var items = Fresh(w, s, Item("s3://bk/a", true));
        var rules = new RenameRules(S3PathOps.Instance, new Dictionary<string, IReadOnlyCollection<string>> { ["s3://bk"] = w.ListNames("s3://bk", default) });
        Assert.True(rules.Exists("s3://bk", "a"));
        var plans = RemoteFileOperations.PlanSingle(items[0], "A", rules);  // case only: a new name on S3
        Assert.True(plans[0].Ok);
        var plan = new CopyPlan(items, false, "S3", w, i => s.FindNode(i.Path) is { } n ? (n.FileCount, n.DirCount, n.Size) : null);
        plan.Confirm();

        var res = RemoteFileOperations.Rename(w, plans, CancellationToken.None, plan);

        Assert.Equal("s3://bk/A", res.NewPaths["s3://bk/a"]);
        Assert.Equal(["A/", "A/tief/", "A/tief/z.iso", "A/x.bin", "A/y.txt"],
            _server.Keys("bk").Where(k => k.StartsWith("A/", StringComparison.Ordinal)));
        Assert.DoesNotContain(_server.Keys("bk"), k => k.StartsWith("a/", StringComparison.Ordinal));
        Assert.True(_server.Has("bk", "a"));                       // the file "a" next to it is untouched
        Assert.True(_server.Ops.FindIndex(o => o.StartsWith("delete", StringComparison.Ordinal)) > _server.Ops.FindLastIndex(o => o.StartsWith("copy", StringComparison.Ordinal)));

        TreeUpdate.Renamed(s, res);
        Assert.Contains(s.TopFiles(), f => f.Path == "s3://bk/A/tief/z.iso");
        await AssertSameAsFreshScan(s);                            // ages: all copies are new
    }

    [Fact]
    public async Task Moving_a_big_folder_deletes_originals_in_batches_and_costs_about_the_estimate()
    {
        for (int i = 0; i < 2500; i++) _server.Put("bk", $"quelle/f{i:D4}", 1, Old);
        _server.Put("bk", "ziel/", 0, Old);
        var s = await ScanOf();
        var run = new ListRun();
        long lastObjects = 0;
        var w = Writer(run, (o, _) => lastObjects = o);
        var items = Fresh(w, s, Item("s3://bk/quelle", true));
        var plan = new CopyPlan(items, true, "S3", w, i => s.FindNode(i.Path) is { } n ? (n.FileCount, n.DirCount, n.Size) : null);
        plan.Confirm();
        long before = run.Requests;
        int serverBefore = _server.Requests;

        var res = RemoteFileOperations.Move(w, S3PathOps.Instance, items, "s3://bk/ziel", ConflictPolicy.Skip, null, CancellationToken.None, plan);

        Assert.Single(res.Done);
        Assert.Equal(2500, lastObjects);
        Assert.Equal(3, _server.DeleteRequests);
        Assert.Equal(2500, _server.Copies);
        long used = run.Requests - before;
        Assert.Equal(_server.Requests - serverBefore, used);       // every request went through the run
        Assert.InRange(used, plan.Cost.Total - 10, plan.Cost.Total + 10);
        Assert.DoesNotContain(_server.Keys("bk"), k => k.StartsWith("quelle/", StringComparison.Ordinal));
        Assert.Equal(2500, _server.Keys("bk").Count(k => k.StartsWith("ziel/quelle/", StringComparison.Ordinal)));

        TreeUpdate.Removed(s, res.Done, movedTo: res.NewPaths);
        TreeUpdate.Vanished(s, res.Vanished);
        var again = TreeUpdate.Arrived(s, "s3://bk/ziel", res);
        Assert.NotNull(again);                                      // a folder arrived: the destination is scanned again
        await s.RescanAsync(again!, 2, CancellationToken.None);
        await AssertSameAsFreshScan(s);
    }

    [Fact]
    public async Task Cancel_in_the_middle_leaves_every_object_complete_at_exactly_one_place()
    {
        for (int i = 0; i < 30; i++) _server.Put("bk", $"q/f{i:D2}", 10, Old);
        _server.Put("bk", "z/", 0, Old);
        var s = await ScanOf();
        using var cts = new CancellationTokenSource();
        int copies = 0;
        _server.AfterCopy = _ => { if (++copies == 7) cts.Cancel(); };
        var w = Writer(new ListRun(cts.Token));
        var items = Fresh(w, s, Item("s3://bk/q", true));
        var plan = new CopyPlan(items, true, "S3", w);
        plan.Confirm();

        var res = RemoteFileOperations.Move(w, S3PathOps.Instance, items, "s3://bk/z", ConflictPolicy.Skip, null, cts.Token, plan);

        Assert.True(res.Cancelled);
        Assert.Equal(["s3://bk/q"], res.Partial.Select(i => i.Path));
        Assert.Contains(res.Failed, f => f.Error.Contains("Abgebrochen: 7 von 30"));
        var keys = _server.Keys("bk");
        Assert.Equal(7, keys.Count(k => k.StartsWith("z/q/", StringComparison.Ordinal)));
        Assert.Equal(23, keys.Count(k => k.StartsWith("q/", StringComparison.Ordinal)));
        for (int i = 0; i < 30; i++)
            Assert.True(keys.Contains($"q/f{i:D2}") ^ keys.Contains($"z/q/f{i:D2}"), $"f{i:D2} liegt nicht an genau einem Ort");

        // Both places are scanned again: the tree equals a fresh scan.
        await s.RescanAsync(s.FindNode("s3://bk/q")!, 2, CancellationToken.None);
        await s.RescanAsync(s.FindNode("s3://bk/z")!, 2, CancellationToken.None);
        await AssertSameAsFreshScan(s);
    }

    [Fact]
    public void Copy_errors_inside_a_folder_are_collected_and_the_folder_is_partial()
    {
        Sample();
        _server.DenyCopy.Add("a/y.txt");
        var w = Writer();
        var items = new[] { Item("s3://bk/a", true) };
        var plan = new CopyPlan(items, true, "S3", w);
        plan.Confirm();
        var res = RemoteFileOperations.Move(w, S3PathOps.Instance, items, "s3://bk/b", ConflictPolicy.Skip, null, CancellationToken.None, plan);
        Assert.Empty(res.Done);
        Assert.Equal(["s3://bk/a"], res.Partial.Select(i => i.Path));
        Assert.Contains(res.Failed, f => f.Path == "s3://bk/a/y.txt" && f.Error.Contains("AccessDenied"));
        Assert.True(_server.Has("bk", "a/y.txt"));
        Assert.True(_server.Has("bk", "b/a/x.bin"));
        Assert.False(_server.Has("bk", "a/x.bin"));
    }

    // ------------------------------------------------------------------ conflicts, names, folders

    [Fact]
    public async Task Conflicts_skip_keep_both_or_replace_case_sensitively()
    {
        Sample();
        _server.Put("bk", "b/readme.txt", 777, Old).Put("bk", "b/A.bin", 5, Old).Put("bk", "c/A.bin", 6, Old).Put("bk", "c/a.bin", 8, Old);
        var s = await ScanOf();
        var w = Writer();

        var res = RemoteFileOperations.Move(w, S3PathOps.Instance, Fresh(w, s, Item("s3://bk/readme.txt", false, 10)), "s3://bk/b",
            ConflictPolicy.Ask, null, CancellationToken.None);
        Assert.Single(res.Skipped);
        Assert.Equal(777, _server.Get("bk", "b/readme.txt").Size);

        res = RemoteFileOperations.Move(w, S3PathOps.Instance, Fresh(w, s, Item("s3://bk/readme.txt", false, 10)), "s3://bk/b",
            ConflictPolicy.Rename, null, CancellationToken.None);
        Assert.Equal("s3://bk/b/readme (2).txt", res.NewPaths["s3://bk/readme.txt"]);
        TreeUpdate.Removed(s, res.Done, movedTo: res.NewPaths);
        Assert.Null(TreeUpdate.Arrived(s, "s3://bk/b", res));
        await AssertSameAsFreshScan(s);

        // "a.bin" and "A.bin" are two names: moving c/a.bin to b is no conflict with b/A.bin.
        res = RemoteFileOperations.Move(w, S3PathOps.Instance, Fresh(w, s, Item("s3://bk/c/a.bin", false, 8)), "s3://bk/b",
            ConflictPolicy.Skip, null, CancellationToken.None);
        Assert.Equal("s3://bk/b/a.bin", res.NewPaths["s3://bk/c/a.bin"]);
        Assert.Equal(5, _server.Get("bk", "b/A.bin").Size);

        // Replace: the copy overwrites the old file, then the original goes.
        res = RemoteFileOperations.Move(w, S3PathOps.Instance, Fresh(w, s, Item("s3://bk/c/A.bin", false, 6)), "s3://bk/b",
            ConflictPolicy.Replace, null, CancellationToken.None);
        Assert.True(res.Replaced);
        Assert.Equal(6, _server.Get("bk", "b/A.bin").Size);
        Assert.False(_server.Has("bk", "c/A.bin"));
    }

    [Fact]
    public void The_writer_never_replaces_unasked()
    {
        Sample();
        _server.Put("bk", "b/readme.txt", 777, Old);
        var w = Writer();
        var ex = Assert.Throws<IOException>(() => w.Move("s3://bk/readme.txt", "s3://bk/b", null, replace: false, CancellationToken.None));
        Assert.Contains("existiert schon", ex.Message);
        Assert.Throws<IOException>(() => w.Move("s3://bk/b", "s3://bk", "a", replace: false, CancellationToken.None)); // folder "a/" exists
        Assert.Throws<IOException>(() => w.Move("s3://bk/a", "s3://bk/a/tief", null, false, CancellationToken.None));
        Assert.Throws<IOException>(() => w.Move("s3://bk/readme.txt", "s3://anderer", null, false, CancellationToken.None));
        Assert.Equal(0, _server.Copies);
    }

    [Fact]
    public void Stat_names_and_new_folders()
    {
        Sample();
        var w = Writer();
        Assert.Equal(new StorageItemInfo(false, false, 7, Old.Ticks), w.Stat("s3://bk/a", default)); // the file wins
        Assert.True(w.Stat("s3://bk/ab", default)!.Value.IsDirectory);
        Assert.True(w.Stat("s3://bk", default)!.Value.IsDirectory);
        Assert.Null(w.Stat("s3://bk/gibtsnicht", default));
        Assert.Null(w.Stat("s3://nobucket", default));
        Assert.Equal(new[] { "a", "ab", "b", "readme.txt", "solo" }.Order(StringComparer.Ordinal), w.ListNames("s3://bk", default).Order(StringComparer.Ordinal));

        RemoteFileOperations.CreateFolders(w, S3PathOps.Instance, "s3://bk/neu/tiefer", CancellationToken.None);
        Assert.Equal(["put neu/", "put neu/tiefer/"], _server.Ops);
        Assert.True(w.Stat("s3://bk/neu/tiefer", default)!.Value.IsDirectory);
    }

    [Fact]
    public void Typed_destinations_stay_in_the_bucket()
    {
        var p = Provider;
        Assert.Equal("s3://bk", RemoteFileOperations.TopOf(p.Paths, "s3://bk/a/b"));
        Assert.Equal("s3://bk/x/y", RemoteFileOperations.ResolveTarget("/x/y/", p, "s3://bk", out _));
        Assert.Equal("s3://bk/x", RemoteFileOperations.ResolveTarget("s3://bk/x", p, "s3://bk", out _));
        Assert.Null(RemoteFileOperations.ResolveTarget("s3://anderer/x", p, "s3://bk", out var error));
        Assert.Contains("desselben Buckets", error);
        Assert.Null(RemoteFileOperations.ResolveTarget("x", p, "s3://bk", out error));
        Assert.Contains("in diesem Bucket", error);
    }

    // ------------------------------------------------------------------ costs

    [Fact]
    public void The_cost_limit_pauses_an_action_until_it_is_resumed()
    {
        for (int i = 0; i < 5; i++) _server.Put("bk", $"p/f{i}", 1, Old);
        var run = new ListRun(CancellationToken.None, requestLimit: 4);
        int paused = 0;
        run.LimitReached += r => { paused++; Task.Run(async () => { await Task.Delay(50); r.Resume(); }); };
        var res = RemoteFileOperations.Move(Writer(run), S3PathOps.Instance,
            Enumerable.Range(0, 5).Select(i => Item($"s3://bk/p/f{i}", false, 1)).ToList(), "s3://bk", ConflictPolicy.Skip, null, CancellationToken.None);
        Assert.Equal(1, paused);
        Assert.Equal(5, res.Done.Count);
        Assert.Equal(_server.Requests, run.Requests);
    }

    [Fact]
    public void A_cancelled_run_sends_no_further_requests()
    {
        Sample();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var run = new ListRun(cts.Token);
        int before = _server.Requests;
        var plan = Confirmed([Item("s3://bk/readme.txt", false, 10), Item("s3://bk/b/1.txt", false, 1)]);
        var res = RemoteFileOperations.Delete(Writer(run), plan, null, cts.Token);
        Assert.True(res.Cancelled);
        Assert.Empty(res.Done);
        Assert.Equal(before, _server.Requests);
    }
}
