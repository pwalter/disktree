using System.IO;
using DiskTree.Core;
using DiskTree.Core.Storage;
using DiskTree.Features.FileOperations;
using Xunit;

namespace DiskTree.Tests.Storage;

/// <summary>
/// Delete for good, rename and move on a writable source without recycle bin (FakeStore, case-sensitive like SFTP):
/// confirmation, conflicts, errors per item, cancel, and the tree after each action equals a fresh scan.
/// </summary>
public class RemoteFileOpsTests
{
    private static readonly DateTime Old = DateTime.UtcNow.AddYears(-2);
    private static readonly DateTime Recent = DateTime.UtcNow.AddDays(-3);

    private static FakeStore Sample()
    {
        var p = new FakeStore();
        p.File("/d/readme.txt", 10, Recent)
            .File("/d/bild.jpg", 3_000_000, Old)
            .File("/d/a/x.bin", 2_000_000, Recent)
            .File("/d/a/y.txt", 50, Old)
            .File("/d/a/tief/z.iso", 5_000_000, Old)
            .File("/d/b/1.txt", 1, Recent)
            .File("/d/b/2.txt", 2, Recent)
            .Folder("/d/leer");
        return p;
    }

    private static async Task<Scanner> ScanOf(FakeStore p, string path = "/d")
    {
        var s = new Scanner { Provider = p };
        await s.ScanAsync(path, 2, CancellationToken.None);
        return s;
    }

    private static OpItem Item(FakeStore p, string path, bool dir, long size = 0) => new(path, dir, size, -1, p.Paths);

    private static List<OpItem> Fresh(FakeStore p, Scanner s, params OpItem[] items) =>
        RemoteFileOperations.Refresh(p, items, s.Ages, new OpResult(), null, CancellationToken.None);

    private static async Task AssertSameAsFreshScan(FakeStore p, Scanner s)
    {
        var fresh = await ScanOf(p);
        TreeAssert.Equal(fresh.Root!, s.Root!);
        Assert.Equal(fresh.TopFiles().Select(f => (f.Path, f.Size)), s.TopFiles().Select(f => (f.Path, f.Size)));
    }

    // ------------------------------------------------------------------ delete for good

    [Fact]
    public void Delete_without_confirmation_touches_nothing()
    {
        var p = Sample();
        var plan = new PermanentDeletePlan([Item(p, "/d/readme.txt", false, 10), Item(p, "/d/a", true)], "Testspeicher");
        Assert.False(plan.IsConfirmed);
        Assert.Throws<InvalidOperationException>(() => RemoteFileOperations.Delete(p, plan, null, CancellationToken.None));
        Assert.Empty(p.Log);
        Assert.NotNull(p.Stat("/d/readme.txt", CancellationToken.None));
        Assert.NotNull(p.Stat("/d/a/tief/z.iso", CancellationToken.None));
    }

    [Fact]
    public void Delete_plan_text_says_permanent_with_counts_examples_and_place()
    {
        var p = Sample();
        var items = new[] { Item(p, "/d/a", true), Item(p, "/d/readme.txt", false, 10) };
        var plan = new PermanentDeletePlan(items, "SFTP tester@nas „Backup“",
            i => i.Path == "/d/a" ? (3, 1, 7_000_050) : null);
        Assert.Equal(4, plan.Files);
        Assert.Equal(2, plan.Folders);
        Assert.Equal(7_000_060, plan.Bytes);
        var text = plan.Describe();
        Assert.Contains("endgültig gelöscht", text);
        Assert.Contains("2 Elemente (1 Datei, 1 Ordner)", text);
        Assert.Contains("4 Dateien in 2 Ordnern", text);
        Assert.Contains("  a/", text);
        Assert.Contains("  readme.txt", text);
        Assert.Contains("SFTP tester@nas „Backup“", text);
        Assert.Contains("Ort: /d", text);
        Assert.Contains("keinen Papierkorb", text);
        Assert.Contains("nicht rückgängig", PermanentDeletePlan.Title);

        var unknown = new PermanentDeletePlan([Item(p, "/d/x", true, 5)], "S");
        Assert.True(unknown.CountsUnknown);
        Assert.Contains("teils unbekannt", unknown.Describe());
    }

    [Fact]
    public async Task Delete_files_and_folders_for_good_tree_equals_fresh_scan()
    {
        var p = Sample();
        var s = await ScanOf(p);
        var items = Fresh(p, s, Item(p, "/d/a", true), Item(p, "/d/bild.jpg", false, 3_000_000), Item(p, "/d/b/1.txt", false, 1));
        Assert.All(items.Where(i => !i.IsDir), i => Assert.True(i.AgeBucket >= 0));
        var plan = new PermanentDeletePlan(items, "Testspeicher");
        plan.Confirm();

        long lastFiles = 0;
        var res = RemoteFileOperations.Delete(p, plan, (f, _) => lastFiles = f, CancellationToken.None);

        Assert.Equal(3, res.Done.Count);
        Assert.Empty(res.Failed);
        Assert.Equal(5, lastFiles); // x.bin, y.txt, z.iso, bild.jpg, 1.txt
        Assert.Null(p.Stat("/d/a", CancellationToken.None));
        Assert.Null(p.Stat("/d/bild.jpg", CancellationToken.None));
        Assert.NotNull(p.Stat("/d/b/2.txt", CancellationToken.None));

        TreeUpdate.Removed(s, res.Done);
        await AssertSameAsFreshScan(p, s);
    }

    [Fact]
    public async Task Delete_collects_errors_per_entry_and_leaves_the_rest()
    {
        var p = Sample();
        p.Protected.Add("/d/a/tief/z.iso");   // no permission
        var s = await ScanOf(p);
        var items = Fresh(p, s, Item(p, "/d/a", true), Item(p, "/d/readme.txt", false, 10));
        var plan = new PermanentDeletePlan(items, "Testspeicher");
        plan.Confirm();

        var res = RemoteFileOperations.Delete(p, plan, null, CancellationToken.None);

        Assert.Equal(["/d/readme.txt"], res.Done.Select(i => i.Path));
        Assert.Contains(res.Failed, f => f.Path == "/d/a/tief/z.iso" && f.Error.Contains("Kein Zugriff"));
        Assert.Equal(["/d/a"], res.Partial.Select(i => i.Path));
        Assert.NotNull(p.Stat("/d/a/tief/z.iso", CancellationToken.None));
        Assert.Null(p.Stat("/d/a/x.bin", CancellationToken.None));   // the rest went

        // The partly deleted folder is scanned again: then the tree equals a fresh scan.
        TreeUpdate.Removed(s, res.Done);
        foreach (var part in res.Partial) await s.RescanAsync(s.FindNode(part.Path)!, 2, CancellationToken.None);
        await AssertSameAsFreshScan(p, s);
    }

    [Fact]
    public async Task Delete_can_be_cancelled_in_the_middle()
    {
        var p = new FakeStore();
        for (int i = 0; i < 20; i++) p.File($"/d/viel/f{i:00}.txt", 100);
        p.File("/d/danach.txt", 5);
        var s = await ScanOf(p);
        using var cts = new CancellationTokenSource();
        p.CancelAfterDeletes = (5, cts);
        var plan = new PermanentDeletePlan(Fresh(p, s, Item(p, "/d/viel", true), Item(p, "/d/danach.txt", false, 5)), "T");
        plan.Confirm();

        var res = RemoteFileOperations.Delete(p, plan, null, cts.Token);

        Assert.True(res.Cancelled);
        Assert.Empty(res.Done);
        Assert.Equal(["/d/viel"], res.Partial.Select(i => i.Path));
        Assert.NotNull(p.Stat("/d/danach.txt", CancellationToken.None));   // not reached
        Assert.Equal(5, p.Log.Count);

        await s.RescanAsync(s.FindNode("/d/viel")!, 2, CancellationToken.None);
        await AssertSameAsFreshScan(p, s);
    }

    // ------------------------------------------------------------------ rename

    private static RenameRules Rules(FakeStore p, params string[] folders) =>
        new(p.Paths, folders.ToDictionary(f => f, f => p.ListNames(f, CancellationToken.None)));

    [Fact]
    public void Rename_checks_names_case_sensitively()
    {
        var p = new FakeStore();
        p.File("/d/a.txt", 1).File("/d/B.txt", 2).File("/d/b.txt", 3).File("/d/.bashrc", 4);
        var rules = Rules(p, "/d");
        Assert.True(rules.CaseSensitive);

        // Only the case changes: a new name on a case-sensitive source.
        var single = RemoteFileOperations.PlanSingle(Item(p, "/d/a.txt", false), "A.txt", rules)[0];
        Assert.True(single.Ok);
        Assert.Equal("/d/A.txt", single.NewPath);

        // "b.txt" exists next to "B.txt": taken.
        Assert.Equal("Name existiert schon", RemoteFileOperations.PlanSingle(Item(p, "/d/B.txt", false), "b.txt", rules)[0].Problem);
        Assert.Equal("Ungültige Zeichen (/)", RemoteFileOperations.PlanSingle(Item(p, "/d/a.txt", false), "x/y", rules)[0].Problem);
        Assert.Equal("Ungültiger Name", RemoteFileOperations.PlanSingle(Item(p, "/d/a.txt", false), "..", rules)[0].Problem);
        // What Windows forbids is fine on POSIX.
        Assert.True(RemoteFileOperations.PlanSingle(Item(p, "/d/a.txt", false), "a:b?.txt.", rules)[0].Ok);
        Assert.True(RemoteFileOperations.PlanSingle(Item(p, "/d/a.txt", false), "a.txt", rules)[0].Unchanged);

        // Dot files have no extension; the pattern keeps real extensions.
        var plans = RemoteFileOperations.PlanPattern([Item(p, "/d/.bashrc", false), Item(p, "/d/a.txt", false)], "{name}_{n:2}", 1, rules);
        Assert.Equal([".bashrc_01", "a_02.txt"], plans.Select(x => x.NewName));

        // Search / replace case-sensitive: only "B".
        var repl = RemoteFileOperations.PlanReplace([Item(p, "/d/B.txt", false), Item(p, "/d/b.txt", false)], "B", "c", false, rules);
        Assert.Equal(["c.txt", "b.txt"], repl.Select(x => x.NewName));
        Assert.True(repl[0].Ok);
        Assert.True(repl[1].Unchanged);

        // Two items to the same new name in one folder.
        var twice = RemoteFileOperations.PlanPattern([Item(p, "/d/B.txt", false), Item(p, "/d/b.txt", false)], "neu", 1, rules);
        Assert.All(twice, x => Assert.Equal("Name kommt mehrfach vor", x.Problem));
    }

    [Fact]
    public async Task Rename_files_and_folders_tree_equals_fresh_scan()
    {
        var p = Sample();
        var s = await ScanOf(p);
        var items = Fresh(p, s, Item(p, "/d/a", true), Item(p, "/d/bild.jpg", false, 3_000_000), Item(p, "/d/readme.txt", false, 10));
        var rules = Rules(p, "/d");
        var plans = new List<RenamePlan>
        {
            RemoteFileOperations.PlanSingle(items[0], "A", rules)[0],              // case only
            RemoteFileOperations.PlanSingle(items[1], "bild.png", rules)[0],       // type stays "Bilder"
            RemoteFileOperations.PlanSingle(items[2], "readme.md", rules)[0],
        };
        Assert.All(plans, x => Assert.True(x.Ok, x.Problem));

        var res = RemoteFileOperations.Rename(p, plans, CancellationToken.None);
        Assert.Equal(3, res.Done.Count);
        Assert.Equal("/d/A", res.NewPaths["/d/a"]);
        Assert.NotNull(p.Stat("/d/A/tief/z.iso", CancellationToken.None));
        Assert.Null(p.Stat("/d/a", CancellationToken.None));

        TreeUpdate.Renamed(s, res);
        Assert.Equal("/d/A/tief", s.FindNode("/d/A/tief")!.FullPath);
        Assert.Contains(s.TopFiles(), f => f.Path == "/d/A/tief/z.iso");
        await AssertSameAsFreshScan(p, s);
    }

    [Fact]
    public void Rename_refuses_a_name_that_appeared_meanwhile()
    {
        var p = new FakeStore();
        p.File("/d/a.txt", 1);
        var rules = Rules(p, "/d");
        var plan = RemoteFileOperations.PlanSingle(Item(p, "/d/a.txt", false, 1), "b.txt", rules)[0];
        p.File("/d/b.txt", 9);   // after the check

        var res = RemoteFileOperations.Rename(p, [plan], CancellationToken.None);
        Assert.Empty(res.Done);
        Assert.Contains("existiert schon", res.Failed[0].Error);
        Assert.Equal(9, p.Stat("/d/b.txt", CancellationToken.None)!.Value.Length);
    }

    // ------------------------------------------------------------------ move

    [Fact]
    public async Task Move_files_into_a_scanned_folder_counts_them_in()
    {
        var p = Sample();
        var s = await ScanOf(p);
        var items = Fresh(p, s, Item(p, "/d/readme.txt", false, 10), Item(p, "/d/bild.jpg", false, 3_000_000));
        var res = RemoteFileOperations.Move(p, p.Paths, items, "/d/b", ConflictPolicy.Ask, null, CancellationToken.None);
        Assert.Equal(2, res.Done.Count);
        Assert.Equal("/d/b/bild.jpg", res.NewPaths["/d/bild.jpg"]);

        TreeUpdate.Removed(s, res.Done, movedTo: res.NewPaths);
        Assert.Null(TreeUpdate.Arrived(s, "/d/b", res)); // plain files: counted in, no rescan
        await AssertSameAsFreshScan(p, s);
    }

    [Fact]
    public async Task Move_a_folder_rescans_the_destination_and_equals_a_fresh_scan()
    {
        var p = Sample();
        var s = await ScanOf(p);
        var res = RemoteFileOperations.Move(p, p.Paths, Fresh(p, s, Item(p, "/d/a/tief", true)), "/d/b", ConflictPolicy.Ask, null, CancellationToken.None);
        Assert.Single(res.Done);

        TreeUpdate.Removed(s, res.Done, movedTo: res.NewPaths);
        var again = TreeUpdate.Arrived(s, "/d/b", res);
        Assert.Same(s.FindNode("/d/b"), again);
        await s.RescanAsync(again!, 2, CancellationToken.None);
        await AssertSameAsFreshScan(p, s);
    }

    [Fact]
    public async Task Move_conflicts_skip_keep_both_or_replace_for_good()
    {
        var p = Sample();
        p.File("/d/b/readme.txt", 777, Old).File("/d/b/bild.jpg", 5, Old).File("/d/b/x.bin", 1, Old).Folder("/d/b/a");
        var s = await ScanOf(p);

        // Skip (no question asked without a callback).
        var res = RemoteFileOperations.Move(p, p.Paths, Fresh(p, s, Item(p, "/d/readme.txt", false, 10)), "/d/b", ConflictPolicy.Ask, null, CancellationToken.None);
        Assert.Single(res.Skipped);
        Assert.Equal(777, p.Stat("/d/b/readme.txt", CancellationToken.None)!.Value.Length);

        // Ask: keep both for this one and all further ones.
        var asked = new List<string>();
        res = RemoteFileOperations.Move(p, p.Paths, Fresh(p, s, Item(p, "/d/readme.txt", false, 10), Item(p, "/d/bild.jpg", false, 3_000_000)), "/d/b",
            ConflictPolicy.Ask, (item, target) => { asked.Add(target); return (ConflictPolicy.Rename, true); }, CancellationToken.None);
        Assert.Equal(["/d/b/readme.txt"], asked);   // the second conflict follows "für alle"
        Assert.Equal("/d/b/readme (2).txt", res.NewPaths["/d/readme.txt"]);
        Assert.Equal("/d/b/bild (2).jpg", res.NewPaths["/d/bild.jpg"]);
        TreeUpdate.Removed(s, res.Done, movedTo: res.NewPaths);
        Assert.Null(TreeUpdate.Arrived(s, "/d/b", res));
        await AssertSameAsFreshScan(p, s);

        // Replace: file over file in one step, a folder over a folder after deleting the old one for good.
        res = RemoteFileOperations.Move(p, p.Paths, Fresh(p, s, Item(p, "/d/a/x.bin", false, 2_000_000), Item(p, "/d/a", true)), "/d/b",
            ConflictPolicy.Replace, null, CancellationToken.None);
        Assert.Equal(2, res.Done.Count);
        Assert.True(res.Replaced);
        Assert.Contains("move /d/a/x.bin -> /d/b/x.bin (replace)", p.Log);
        Assert.Contains("delete /d/b/a", p.Log);
        Assert.Equal(2_000_000, p.Stat("/d/b/x.bin", CancellationToken.None)!.Value.Length);
        Assert.NotNull(p.Stat("/d/b/a/tief/z.iso", CancellationToken.None));
        TreeUpdate.Removed(s, res.Done, movedTo: res.NewPaths);
        var again = TreeUpdate.Arrived(s, "/d/b", res);
        Assert.NotNull(again);
        await s.RescanAsync(again!, 2, CancellationToken.None);
        await AssertSameAsFreshScan(p, s);
    }

    [Fact]
    public void Move_refuses_into_itself_and_reports_errors_per_item()
    {
        var p = Sample();
        p.Protected.Add("/d/b/1.txt");
        var res = RemoteFileOperations.Move(p, p.Paths,
            [Item(p, "/d/a", true), Item(p, "/d/b/1.txt", false, 1), Item(p, "/d/b/2.txt", false, 2)],
            "/d/a/tief", ConflictPolicy.Skip, null, CancellationToken.None);
        Assert.Contains(res.Failed, f => f.Path == "/d/a" && f.Error.Contains("in sich selbst"));
        Assert.Contains(res.Failed, f => f.Path == "/d/b/1.txt" && f.Error.Contains("Kein Zugriff"));
        Assert.Equal(["/d/b/2.txt"], res.Done.Select(i => i.Path));

        var missing = RemoteFileOperations.Move(p, p.Paths, [Item(p, "/d/readme.txt", false)], "/d/gibtsnicht", ConflictPolicy.Skip, null, CancellationToken.None);
        Assert.Contains("nicht vorhanden", missing.Failed.Single().Error);
        Assert.NotNull(p.Stat("/d/readme.txt", CancellationToken.None));
    }

    [Fact]
    public void Moving_into_a_new_folder_creates_it_like_mkdir_p()
    {
        var p = Sample();
        RemoteFileOperations.CreateFolders(p, p.Paths, "/d/neu/tiefer", CancellationToken.None);
        Assert.Equal(["mkdir /d/neu", "mkdir /d/neu/tiefer"], p.Log);
        Assert.Throws<IOException>(() => RemoteFileOperations.CreateFolders(p, p.Paths, "/d/readme.txt/x", CancellationToken.None));
    }

    [Fact]
    public void Normalize_is_case_sensitive_and_drops_what_lies_in_a_selected_folder()
    {
        var p = new FakeStore();
        var list = RemoteFileOperations.Normalize(
        [
            Item(p, "/d/a", true), Item(p, "/d/A", true), Item(p, "/d/a/x.txt", false), Item(p, "/d/ab", false), Item(p, "/d/a", true),
        ], p.Paths);
        Assert.Equal(["/d/A", "/d/a", "/d/ab"], list.Select(i => i.Path));
        Assert.True(RemoteFileOperations.IsRoot(Item(p, "/", true)));
    }

    [Fact]
    public void Typed_destinations_must_be_on_the_same_server()
    {
        var sftp = DiskTree.Features.Storage.Sftp.SftpSource.Generic;
        const string top = "sftp://tester@nas:2222/";
        Assert.Equal(top, RemoteFileOperations.TopOf(sftp.Paths, "sftp://tester@nas:2222/data/a/b"));
        Assert.Equal("sftp://tester@nas:2222/data/x", RemoteFileOperations.ResolveTarget(" /data/x/ ", sftp, top, out _));
        Assert.Equal("sftp://tester@nas:2222/data/x", RemoteFileOperations.ResolveTarget("sftp://tester@nas:2222/data/x", sftp, top, out _));
        Assert.Null(RemoteFileOperations.ResolveTarget("sftp://tester@anderer/data", sftp, top, out var error));
        Assert.Contains("demselben Server", error);
        Assert.Null(RemoteFileOperations.ResolveTarget("data/x", sftp, top, out error));
        Assert.Contains("vollständigen Pfad", error);
        Assert.Null(RemoteFileOperations.ResolveTarget("  ", sftp, top, out error));
        Assert.Null(RemoteFileOperations.ResolveTarget(@"C:\temp", sftp, top, out _));
    }

    [Fact]
    public void Duplicates_of_a_writable_source_get_check_marks()
    {
        var g = new DiskTree.Features.Duplicates.DuplicateGroup(10, ["/d/a", "/d/b"]);
        var sftp = new DiskTree.Features.Duplicates.DupGroupVM(g, new FakeStore().Paths, remote: true, canCheck: true);
        Assert.All(sftp.Files, f => Assert.Equal(System.Windows.Visibility.Visible, f.CheckVisibility));
        Assert.Equal(System.Windows.Visibility.Visible, sftp.BadgeVisibility);
        var s3 = new DiskTree.Features.Duplicates.DupGroupVM(g, new FakeStore().Paths, remote: true);
        Assert.All(s3.Files, f => Assert.Equal(System.Windows.Visibility.Collapsed, f.CheckVisibility));
    }

    [Fact]
    public void Refresh_skips_what_is_gone_and_reads_the_age_bucket()
    {
        var p = Sample();
        var res = new OpResult(p.Paths.Comparer);
        var ages = new AgeClassifier(AgeStamp.Modified, DateTime.UtcNow);
        var ticks = new Dictionary<string, long>();
        var list = RemoteFileOperations.Refresh(p, [Item(p, "/d/bild.jpg", false, 3_000_000), Item(p, "/d/weg.txt", false), Item(p, "/d/a", false)],
            ages, res, ticks, CancellationToken.None);
        Assert.Equal(["/d/bild.jpg", "/d/a"], list.Select(i => i.Path));
        Assert.Equal(ages.Of(Old.Ticks), list[0].AgeBucket);
        Assert.True(list[1].IsDir);          // the source says what it is
        Assert.Equal("nicht mehr vorhanden", res.Skipped.Single().Reason);
        Assert.Equal(Old.Ticks, ticks["/d/bild.jpg"]);
    }
}
