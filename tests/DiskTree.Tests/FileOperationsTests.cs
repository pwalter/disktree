using System.IO;
using System.IO.Compression;
using Xunit;

namespace DiskTree.Tests;

public class FileOperationsTests : TestBase
{
    // The recycle bin is a shell feature; it may be missing (service accounts, some CI images). Tests that need it
    // are skipped (return early) when a probe file cannot be recycled.
    private static readonly Lazy<bool> RecycleWorks = new(() =>
    {
        var probe = Path.Combine(Path.GetTempPath(), "disktree-recycle-probe-" + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(probe, "x");
        try { return FileOperations.Recycle(probe) == null && !File.Exists(probe); }
        catch { return false; }
        finally { try { File.Delete(probe); } catch { } }
    });

    private OpItem F(string rel, long size = 0)
    {
        var p = T.File(rel, size);
        return new OpItem(p, false, size);
    }

    private OpItem D(string rel) => new(T.Dir(rel), true, 0);

    private static string Text(string path) => File.ReadAllText(path);

    // ------------------------------------------------------------------ selection

    [Fact]
    public void Normalize_DropsDuplicatesAndItemsInsideSelectedFolders_AndSorts()
    {
        var items = new[]
        {
            new OpItem(@"C:\data\b.txt", false, 1),
            new OpItem(@"C:\data\sub", true, 0),
            new OpItem(@"C:\DATA\SUB\inner.txt", false, 1),
            new OpItem(@"C:\data\sub\deeper", true, 0),
            new OpItem(@"C:\data\a.txt", false, 1),
            new OpItem(@"C:\data\b.txt", false, 1),
            new OpItem(@"C:\data\sub\", true, 0),
            new OpItem(@"C:\data\subtle.txt", false, 1), // similar prefix: stays
        };
        var r = FileOperations.Normalize(items);
        Assert.Equal(new[] { @"C:\data\a.txt", @"C:\data\b.txt", @"C:\data\sub", @"C:\data\subtle.txt" },
            r.Select(i => i.Path.TrimEnd('\\')).ToArray());
    }

    [Fact]
    public void OpItem_NameParentAndRoot()
    {
        var i = new OpItem(@"C:\a\b\f.txt", false, 5);
        Assert.Equal("f.txt", i.Name);
        Assert.Equal(@"C:\a\b", i.Parent);
        Assert.Equal("b", new OpItem(@"C:\a\b\", true, 0).Name);
        Assert.True(FileOperations.IsRoot(new OpItem(@"C:\", true, 0)));
        Assert.False(FileOperations.IsRoot(i));
    }

    [Fact]
    public void UniqueName_CountsUpFromTwo()
    {
        T.File("a.txt");
        Assert.Equal("a (2).txt", FileOperations.UniqueName(T.Path, "a.txt"));
        T.File("a (2).txt");
        Assert.Equal("a (3).txt", FileOperations.UniqueName(T.Path, "a.txt"));
        T.Dir("dir");
        Assert.Equal("dir (2)", FileOperations.UniqueName(T.Path, "dir"));
        Assert.Equal("free (2).txt", FileOperations.UniqueName(T.Path, "free.txt")); // always a suffix, even if the plain name is free
    }

    // ------------------------------------------------------------------ recycle

    [Fact]
    public void Recycle_FileAndFolder()
    {
        if (!RecycleWorks.Value) return;
        var f = T.File("r.txt", 5);
        var d = T.Dir("rd");
        T.File(@"rd\x.txt", 3);

        Assert.Null(FileOperations.Recycle(f));
        Assert.False(File.Exists(f));
        Assert.Null(FileOperations.Recycle(d));
        Assert.False(Directory.Exists(d));
    }

    [Fact]
    public void RecycleAll_CollectsDoneAndFailed()
    {
        if (!RecycleWorks.Value) return;
        var ok = F("ok.txt");
        var missing = new OpItem(T.Sub("missing.txt"), false, 0);

        var res = FileOperations.RecycleAll(new[] { ok, missing });

        Assert.Contains(ok, res.Done);
        Assert.False(File.Exists(ok.Path));
        Assert.Single(res.Failed);
        Assert.Equal(missing.Path, res.Failed[0].Path);
    }

    // ------------------------------------------------------------------ move

    [Fact]
    public void Move_FileAndFolder_IntoNewDestination()
    {
        var f = F(@"src\f.txt", 4);
        File.WriteAllText(f.Path, "data");
        var d = D(@"src\folder");
        T.File(@"src\folder\inner.txt", 2);
        var dest = T.Sub("dest", "new");

        var res = FileOperations.Move(new[] { f, d }, dest, ConflictPolicy.Ask, null);

        Assert.Empty(res.Failed);
        Assert.Equal(2, res.Done.Count);
        Assert.Equal("data", Text(Path.Combine(dest, "f.txt")));
        Assert.True(File.Exists(Path.Combine(dest, "folder", "inner.txt")));
        Assert.False(File.Exists(f.Path));
        Assert.False(Directory.Exists(d.Path));
        Assert.Equal(Path.Combine(dest, "f.txt"), res.NewPaths[f.Path]);
        Assert.Equal(Path.Combine(dest, "folder"), res.NewPaths[d.Path]);
        Assert.False(res.Replaced);
        Assert.Equal(4, res.DoneBytes);
    }

    [Fact]
    public void Move_ConflictSkip_LeavesBothFiles()
    {
        var src = F(@"src\f.txt"); File.WriteAllText(src.Path, "new");
        File.WriteAllText(T.File(@"dest\f.txt"), "old");

        var res = FileOperations.Move(new[] { src }, T.Sub("dest"), ConflictPolicy.Skip, null);

        Assert.Empty(res.Done);
        Assert.Single(res.Skipped);
        Assert.Contains("vorhanden", res.Skipped[0].Reason);
        Assert.Equal("old", Text(T.Sub("dest", "f.txt")));
        Assert.Equal("new", Text(src.Path));
    }

    [Fact]
    public void Move_ConflictRename_KeepsBoth()
    {
        var src = F(@"src\f.txt"); File.WriteAllText(src.Path, "new");
        File.WriteAllText(T.File(@"dest\f.txt"), "old");

        var res = FileOperations.Move(new[] { src }, T.Sub("dest"), ConflictPolicy.Rename, null);

        Assert.Single(res.Done);
        Assert.Equal("old", Text(T.Sub("dest", "f.txt")));
        Assert.Equal("new", Text(T.Sub("dest", "f (2).txt")));
        Assert.Equal(T.Sub("dest", "f (2).txt"), res.NewPaths[src.Path]);
        Assert.False(res.Replaced);
    }

    [Fact]
    public void Move_ConflictReplace_OverwritesAndFlagsReplaced()
    {
        if (!RecycleWorks.Value) return;
        var src = F(@"src\f.txt"); File.WriteAllText(src.Path, "new");
        File.WriteAllText(T.File(@"dest\f.txt"), "old");

        var res = FileOperations.Move(new[] { src }, T.Sub("dest"), ConflictPolicy.Replace, null);

        Assert.Empty(res.Failed);
        Assert.Single(res.Done);
        Assert.True(res.Replaced);
        Assert.Equal("new", Text(T.Sub("dest", "f.txt")));
        Assert.False(File.Exists(src.Path));
    }

    [Fact]
    public void Move_Ask_UsesCallbackAndRemembersForAll()
    {
        var items = new[] { F(@"src\a.txt"), F(@"src\b.txt"), F(@"src\c.txt") };
        foreach (var i in items) { File.WriteAllText(i.Path, "new"); File.WriteAllText(T.File(@"dest\" + i.Name), "old"); }

        int asked = 0;
        var res = FileOperations.Move(items, T.Sub("dest"), ConflictPolicy.Ask, (item, target) =>
        {
            asked++;
            Assert.Equal(T.Sub("dest", item.Name), target);
            return (ConflictPolicy.Rename, true);
        });

        Assert.Equal(1, asked); // "for all" answered the rest
        Assert.Equal(3, res.Done.Count);
        Assert.True(File.Exists(T.Sub("dest", "a (2).txt")));
        Assert.True(File.Exists(T.Sub("dest", "b (2).txt")));
        Assert.True(File.Exists(T.Sub("dest", "c (2).txt")));
    }

    [Fact]
    public void Move_AskWithoutCallback_Skips()
    {
        var src = F(@"src\f.txt");
        T.File(@"dest\f.txt");
        var res = FileOperations.Move(new[] { src }, T.Sub("dest"), ConflictPolicy.Ask, null);
        Assert.Single(res.Skipped);
        Assert.True(File.Exists(src.Path));
    }

    [Fact]
    public void Move_ToItsOwnFolder_IsSkipped()
    {
        var src = F(@"same\f.txt");
        var res = FileOperations.Move(new[] { src }, T.Sub("same"), ConflictPolicy.Rename, null);
        Assert.Single(res.Skipped);
        Assert.Empty(res.Done);
        Assert.True(File.Exists(src.Path));
    }

    [Fact]
    public void Move_FolderIntoItself_Fails()
    {
        var d = D("outer");
        var res = FileOperations.Move(new[] { d }, T.Sub("outer", "inner"), ConflictPolicy.Skip, null);
        Assert.Single(res.Failed);
        Assert.Contains("sich selbst", res.Failed[0].Error);
        Assert.True(Directory.Exists(d.Path));
    }

    [Fact]
    public void Move_MissingSource_FailsOnlyThatItem()
    {
        var good = F(@"src\g.txt");
        var gone = new OpItem(T.Sub("src", "gone.txt"), false, 0);
        var res = FileOperations.Move(new[] { gone, good }, T.Sub("dest"), ConflictPolicy.Skip, null);
        Assert.Single(res.Failed);
        Assert.Equal(gone.Path, res.Failed[0].Path);
        Assert.Single(res.Done);
        Assert.True(File.Exists(T.Sub("dest", "g.txt")));
    }

    [Fact]
    public void Move_LockedFile_FailsButContinues()
    {
        var locked = F(@"src\locked.txt");
        var free = F(@"src\free.txt");
        using var hold = new FileStream(locked.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var res = FileOperations.Move(new[] { locked, free }, T.Sub("dest"), ConflictPolicy.Skip, null);

        Assert.Single(res.Failed);
        Assert.Equal(free, Assert.Single(res.Done));
    }

    // ------------------------------------------------------------------ zip

    private static List<string> Entries(string zip)
    {
        using var z = ZipFile.OpenRead(zip);
        return z.Entries.Select(e => e.FullName).OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    [Fact]
    public void Zip_FilesAndFolders()
    {
        var f = F(@"src\a.txt"); File.WriteAllText(f.Path, "hello");
        var d = D(@"src\docs");
        T.File(@"src\docs\x.pdf", 10);
        T.File(@"src\docs\deep\y.txt", 20);
        T.Dir(@"src\docs\emptydir");
        var zip = T.Sub("out", "archive.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(zip)!);

        var res = FileOperations.Zip(new[] { f, d }, zip);

        Assert.Empty(res.Failed);
        Assert.Equal(2, res.Done.Count);
        Assert.Equal(new[] { "a.txt", "docs/", "docs/deep/", "docs/deep/y.txt", "docs/emptydir/", "docs/x.pdf" }, Entries(zip));
        using var z = ZipFile.OpenRead(zip);
        using var r = new StreamReader(z.GetEntry("a.txt")!.Open());
        Assert.Equal("hello", r.ReadToEnd());
        Assert.False(File.Exists(zip + ".dt_tmp"));
    }

    [Fact]
    public void Zip_SameNamesFromDifferentFolders_GetSuffix()
    {
        var a = F(@"one\same.txt"); var b = F(@"two\same.txt");
        var zip = T.Sub("z.zip");
        var res = FileOperations.Zip(new[] { a, b }, zip);
        Assert.Empty(res.Failed);
        Assert.Equal(new[] { "same (2).txt", "same.txt" }, Entries(zip));
    }

    [Fact]
    public void Zip_LeavesTheArchiveItselfOut()
    {
        var d = D("folder");
        T.File(@"folder\a.txt", 3);
        var zip = T.Sub("folder", "inside.zip");

        var res = FileOperations.Zip(new[] { d }, zip);

        Assert.Empty(res.Failed);
        Assert.Equal(new[] { "folder/", "folder/a.txt" }, Entries(zip));
    }

    [Fact]
    public void Zip_UnreadableFile_IsReportedAndItemNotDone()
    {
        var ok = F(@"src\ok.txt");
        var locked = F(@"src\locked.txt");
        var zip = T.Sub("z.zip");
        using var hold = new FileStream(locked.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var res = FileOperations.Zip(new[] { ok, locked }, zip);

        Assert.Equal(ok, Assert.Single(res.Done));
        Assert.Equal(locked.Path, Assert.Single(res.Failed).Path);
        Assert.Equal(new[] { "ok.txt" }, Entries(zip));
    }

    [Fact]
    public void Zip_ExistingArchive_IsReplaced()
    {
        if (!RecycleWorks.Value) return;
        var f = F(@"src\new.txt");
        var zip = T.Sub("z.zip");
        File.WriteAllText(zip, "not really a zip");

        var res = FileOperations.Zip(new[] { f }, zip);

        Assert.Empty(res.Failed);
        Assert.Equal(new[] { "new.txt" }, Entries(zip));
    }

    [Fact]
    public void Zip_UnwritableTarget_NothingIsDone()
    {
        var f = F(@"src\a.txt");
        var res = FileOperations.Zip(new[] { f }, T.Sub("missing-folder", "z.zip"));
        Assert.Empty(res.Done);
        Assert.Single(res.Failed);
        Assert.Contains("ZIP-Datei konnte nicht erstellt werden", res.Failed[0].Error);
    }

    // ------------------------------------------------------------------ rename

    [Fact]
    public void PlanPattern_NameCounterAndDate_KeepExtension()
    {
        var a = F("one.jpg"); var b = F("two.JPG");
        var day = new DateTime(2024, 2, 29);
        File.SetLastWriteTime(a.Path, day);
        File.SetLastWriteTime(b.Path, day);

        var plans = FileOperations.PlanPattern(new[] { a, b }, "{date}_{name}_{n:3}", 7);

        Assert.Equal(new[] { "2024-02-29_one_007.jpg", "2024-02-29_two_008.JPG" }, plans.Select(p => p.NewName).ToArray());
        Assert.All(plans, p => Assert.True(p.Ok));
        Assert.Equal(T.Sub("2024-02-29_one_007.jpg"), plans[0].NewPath);
    }

    [Fact]
    public void PlanPattern_PlainCounterAndCaseInsensitiveTokens()
    {
        var a = F("a.txt"); var b = F("b.txt");
        var plans = FileOperations.PlanPattern(new[] { a, b }, "Doc-{N}-{NAME}", 1);
        Assert.Equal(new[] { "Doc-1-a.txt", "Doc-2-b.txt" }, plans.Select(p => p.NewName).ToArray());
    }

    [Fact]
    public void PlanPattern_FolderGetsNoExtension()
    {
        var d = D("my.folder");
        var plans = FileOperations.PlanPattern(new[] { d }, "{name}-x", 1);
        Assert.Equal("my.folder-x", plans[0].NewName);
    }

    [Fact]
    public void PlanPattern_SameNameTwice_IsAProblem()
    {
        var plans = FileOperations.PlanPattern(new[] { F("a.txt"), F("b.txt") }, "same", 1);
        Assert.All(plans, p => Assert.Equal("Name kommt mehrfach vor", p.Problem));
    }

    [Fact]
    public void PlanReplace_FindReplace_IgnoreCase_OnlyInStem()
    {
        var items = new[] { F("Foto_alt_1.jpg"), F("FOTO_ALT_2.jpg"), F("alt.alt") };
        var cs = FileOperations.PlanReplace(items, "alt", "neu", ignoreCase: false);
        Assert.Equal(new[] { "Foto_neu_1.jpg", "FOTO_ALT_2.jpg", "neu.alt" }, cs.Select(p => p.NewName).ToArray());
        Assert.True(cs[1].Unchanged);

        var ci = FileOperations.PlanReplace(items.Take(2).ToArray(), "alt", "neu", ignoreCase: true);
        Assert.Equal(new[] { "Foto_neu_1.jpg", "FOTO_neu_2.jpg" }, ci.Select(p => p.NewName).ToArray());

        var nothing = FileOperations.PlanReplace(items, "", "x", true);
        Assert.All(nothing, p => Assert.True(p.Unchanged));
    }

    [Fact]
    public void PlanSingle_TrimsAndKeepsWhatTheUserTyped()
    {
        var p = Assert.Single(FileOperations.PlanSingle(F("old.txt"), "  new name.md "));
        Assert.Equal("new name.md", p.NewName);
        Assert.True(p.Ok);
    }

    [Theory]
    [InlineData("", "Name ist leer")]
    [InlineData("   ", "Name ist leer")]
    [InlineData("a|b.txt", "Ungültige Zeichen")]
    [InlineData("a?b", "Ungültige Zeichen")]
    [InlineData("a\\b", "Ungültige Zeichen")]
    [InlineData("name.", "Endet mit Punkt oder Leerzeichen")]
    public void Validate_FindsInvalidNames(string newName, string problem)
    {
        var plans = new List<RenamePlan> { new(F("x.txt"), newName, null) };
        // PlanSingle trims; Validate itself does not, so test it directly
        FileOperations.Validate(plans);
        Assert.Equal(problem, plans[0].Problem);
        Assert.False(plans[0].Ok);
    }

    [Fact]
    public void Validate_NameTakenOnDisk_IsAProblem_ButCaseOnlyChangeIsFine()
    {
        var a = F("a.txt"); F("taken.txt");
        Assert.Equal("Name existiert schon", FileOperations.PlanSingle(a, "taken.txt")[0].Problem);
        Assert.Equal("Name existiert schon", FileOperations.PlanSingle(a, "TAKEN.txt")[0].Problem);
        Assert.Null(FileOperations.PlanSingle(a, "A.TXT")[0].Problem); // same file, different case
        Assert.True(FileOperations.PlanSingle(a, "a.txt")[0].Unchanged);
    }

    [Fact]
    public void Rename_RenamesFilesAndFolders_AndReportsNewPaths()
    {
        var f = F("old.txt"); File.WriteAllText(f.Path, "c");
        var d = D("olddir"); T.File(@"olddir\in.txt", 1);

        var plans = new[] { new RenamePlan(f, "new.txt", null), new RenamePlan(d, "newdir", null) };
        var res = FileOperations.Rename(plans);

        Assert.Empty(res.Failed);
        Assert.Equal(2, res.Done.Count);
        Assert.Equal("c", Text(T.Sub("new.txt")));
        Assert.True(File.Exists(T.Sub("newdir", "in.txt")));
        Assert.Equal(T.Sub("new.txt"), res.NewPaths[f.Path]);
        Assert.False(File.Exists(f.Path));
    }

    [Fact]
    public void Rename_SkipsUnchanged_AndFailsProblems_ButGoesOn()
    {
        var same = F("same.txt"); var bad = F("bad.txt"); var good = F("good.txt");
        var plans = FileOperations.PlanPattern(new[] { same, bad, good }, "{name}", 1);
        plans[1] = plans[1] with { NewName = "bad?.txt", Problem = "Ungültige Zeichen" };
        plans[2] = plans[2] with { NewName = "better.txt" };

        var res = FileOperations.Rename(plans);

        Assert.Single(res.Skipped);
        Assert.Single(res.Failed);
        Assert.Equal(good, Assert.Single(res.Done));
        Assert.True(File.Exists(T.Sub("better.txt")));
        Assert.True(File.Exists(bad.Path));
    }

    [Fact]
    public void Rename_LockedFile_Fails()
    {
        var f = F("held.txt");
        using var hold = new FileStream(f.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var res = FileOperations.Rename(FileOperations.PlanSingle(f, "other.txt"));
        Assert.Single(res.Failed);
        Assert.True(File.Exists(f.Path));
    }

    [Fact]
    public void Rename_SwapNames_IsBlockedByValidation()
    {
        // a -> b and b -> a would need a temporary name; both new names exist on disk, so both plans are rejected
        var a = F("a.txt"); var b = F("b.txt");
        var plans = new List<RenamePlan> { new(a, "b.txt", null), new(b, "a.txt", null) };
        FileOperations.Validate(plans);
        Assert.All(plans, p => Assert.Equal("Name existiert schon", p.Problem));
    }

    // ------------------------------------------------------------------ tree bookkeeping

    [Fact]
    public async Task RenameNode_RepathsTheWholeSubtree()
    {
        T.File(@"old\a\f.txt", 1);
        T.File(@"old\b\g.txt", 1);
        var s = await ScanT();
        var node = Scan.Node(s, T.Sub("old"));

        FileOperations.RenameNode(node, "renamed");

        Assert.Equal("renamed", node.Name);
        Assert.Equal(T.Sub("renamed"), node.FullPath, ignoreCase: true);
        Assert.Equal(T.Sub("renamed", "a"), node.Snapshot().First(c => c.Name == "a").FullPath, ignoreCase: true);
        Assert.NotNull(s.FindNode(T.Sub("renamed", "b")));
        Assert.Null(s.FindNode(T.Sub("old")));
    }
}
