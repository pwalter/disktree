using System.Diagnostics;
using System.IO;
using Xunit;

namespace DiskTree.Tests;

public class ScannerTests : TestBase
{
    // T\r.txt 50, T\hidden.txt 7 (hidden), T\a\w.docx 100, T\a\p.pdf 200, T\a\b\v.mp4 1000, T\c (empty)
    private void BuildBasicTree()
    {
        T.File("r.txt", 50);
        File.SetAttributes(T.File("hidden.txt", 7), FileAttributes.Hidden);
        T.File(@"a\w.docx", 100);
        T.File(@"a\p.pdf", 200);
        T.File(@"a\b\v.mp4", 1000);
        T.Dir("c");
    }

    [Fact]
    public async Task Scan_SumsSizesAndCounts()
    {
        BuildBasicTree();
        var s = await ScanT();

        var target = s.ScanTarget!;
        Assert.Equal(T.Path, target.FullPath, ignoreCase: true);
        Assert.Equal(1357, target.Size);
        Assert.Equal(5, target.FileCount);
        Assert.Equal(3, target.DirCount); // a, b, c
        Assert.Equal(57, target.OwnSize);
        Assert.Equal(2, target.OwnCount);

        var a = Scan.Node(s, T.Sub("a"));
        Assert.Equal(1300, a.Size);
        Assert.Equal(3, a.FileCount);
        Assert.Equal(1, a.DirCount);
        Assert.Equal(300, a.OwnSize);

        var b = Scan.Node(s, T.Sub("a", "b"));
        Assert.Equal(1000, b.Size);
        Assert.Equal(1, b.FileCount);
        Assert.True(b.IsDone);

        var c = Scan.Node(s, T.Sub("c"));
        Assert.Equal(0, c.Size);
        Assert.False(c.HasChildren);

        Assert.True(target.IsDone);
        Assert.Equal(1357, s.Bytes);
        Assert.Equal(5, s.Files);
        Assert.Equal(0, s.Errors);
    }

    [Fact]
    public async Task Scan_TreeStartsAtDriveAndChainLeadsToTarget()
    {
        T.File(@"x\y.txt", 10);
        var s = await ScanT();

        var drive = s.Root!;
        Assert.Equal(Path.GetPathRoot(T.Path), drive.FullPath, ignoreCase: true);
        Assert.True(drive.DriveTotal > 0);
        Assert.True(drive.PartialDrive);
        Assert.Same(s.ScanTarget, s.FindNode(T.Path));
        Assert.Equal(10, drive.Size);
        Assert.Equal(0, s.ExpectedBytes); // partial drive: no expectation
        Assert.False(s.IsMulti);
        Assert.False(s.IsSnapshot);
    }

    [Fact]
    public async Task Scan_FindNode_IsCaseInsensitiveAndNullForUnknown()
    {
        T.File(@"Alpha\Beta\f.txt", 1);
        var s = await ScanT();

        Assert.NotNull(s.FindNode(T.Sub("alpha", "BETA")));
        Assert.Null(s.FindNode(T.Sub("alpha", "gamma")));
        Assert.Null(s.FindNode(@"Q:\nope\"));
    }

    [Fact]
    public async Task Scan_FileTypeAggregatesPerFolder()
    {
        T.File(@"a\one.docx", 100);
        T.File(@"a\two.DOCX", 50);
        T.File(@"a\p.pdf", 200);
        T.File(@"a\noext", 7);
        T.File(@"a\weird.zzz", 3);
        var s = await ScanT();

        var a = Scan.Node(s, T.Sub("a"));
        var types = a.OwnTypes!.ToDictionary(t => t.Cat);
        int word = Array.FindIndex(FileTypes.All, c => c.Name == "Word-Dokumente");
        int pdf = Array.FindIndex(FileTypes.All, c => c.Name == "PDF");
        Assert.Equal(new TypeStat(word, 150, 2), types[word]);
        Assert.Equal(new TypeStat(pdf, 200, 1), types[pdf]);
        Assert.Equal(new TypeStat(FileTypes.Other, 10, 2), types[FileTypes.Other]);
        Assert.Equal(3, types.Count);
        Assert.Equal(360, types.Values.Sum(t => t.Size));
    }

    [Fact]
    public async Task Scan_AgeBuckets_ByLastWriteTime()
    {
        T.File(@"d\new.txt", 10, Ago(days: 1));        // < 1 month
        T.File(@"d\half.txt", 20, Ago(months: 6));     // 1 month - 1 year
        T.File(@"d\mid.txt", 40, Ago(months: 18));     // 1 - 2 years
        T.File(@"d\old.txt", 80, Ago(years: 3));       // 2 - 5 years
        T.File(@"d\ancient.txt", 160, Ago(years: 10)); // > 5 years
        T.File(@"d\ancient2.txt", 320, Ago(years: 12));
        var s = await ScanT();

        var d = Scan.Node(s, T.Sub("d"));
        Assert.Equal(new long[] { 10, 20, 40, 80, 480, 1, 1, 1, 1, 2 }, d.OwnAges);
        Assert.Equal(new long[] { 10, 20, 40, 80, 480 }, d.AgeSub);
        Assert.Equal(d.AgeSub, s.ScanTarget!.AgeSub);
        Assert.Equal(d.AgeSub, s.Root!.AgeSub);

        // "old" threshold 24 months = buckets 3 and 4
        Assert.Equal(560, AgeBuckets.OldSize(d));
        Assert.Equal(560, AgeBuckets.OldOwnSize(d));
    }

    [Fact]
    public async Task Scan_AgeBuckets_RollUpToParents()
    {
        T.File(@"p\x.txt", 5, Ago(years: 10));
        T.File(@"p\q\y.txt", 7, Ago(days: 2));
        var s = await ScanT();

        var p = Scan.Node(s, T.Sub("p"));
        Assert.Equal(new long[] { 7, 0, 0, 0, 5 }, p.AgeSub);
        Assert.Equal(new long[] { 0, 0, 0, 0, 5, 0, 0, 0, 0, 1 }, p.OwnAges);
        Assert.Equal(new long[] { 7, 0, 0, 0, 0, 1, 0, 0, 0, 0 }, Scan.Node(s, T.Sub("p", "q")).OwnAges);
    }

    [Fact]
    public async Task Scan_AgeStampCreated_UsesCreationTime()
    {
        T.File("old-by-modification.txt", 100, Ago(years: 10)); // created just now
        AgeStamps.Preferred = AgeStamp.Created;
        var s = await ScanT();

        Assert.Equal(AgeStamp.Created, AgeStamps.Current);
        Assert.Equal(new long[] { 100, 0, 0, 0, 0 }, s.ScanTarget!.AgeSub);
    }

    [Fact]
    public async Task Scan_IncludesHiddenFiles_AndSkipsJunctions()
    {
        using var other = new TempDir();
        other.File("huge.bin", 5000);
        T.File("normal.txt", 10);
        var link = T.Sub("link");
        if (!TryCreateJunction(link, other.Path)) return; // no junction support here: nothing to check

        var s = await ScanT();
        Assert.Equal(10, s.ScanTarget!.Size);
        Assert.Empty(s.ScanTarget.Snapshot());
    }

    private static bool TryCreateJunction(string link, string target)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            p.WaitForExit(10000);
            return p.ExitCode == 0 && Directory.Exists(link);
        }
        catch { return false; }
    }

    [Fact]
    public async Task Scan_Exclusions_ByNameWildcardAndPath()
    {
        T.File("keep.txt", 1);
        T.File(@"node_modules\big.js", 500);
        T.File(@"node_modules\deep\x.js", 500);
        T.File(@"a\cache1\y.dat", 300);
        T.File(@"a\cache12\kept.dat", 4);          // 'cache?' must not match cache12
        T.File(@"logs.tmp\z.txt", 10);
        T.File(@"b\Temp\t.txt", 20);
        T.File(@"a\ok.txt", 2);

        var rules = new ExcludeRules(new[] { "NODE_MODULES", "cache?", "*.tmp", Path.Combine(T.Path, "b", "Temp") });
        var s = await ScanT(rules);

        var t = s.ScanTarget!;
        Assert.Equal(1 + 4 + 2, t.Size);
        Assert.Equal(4, s.Excluded);
        Assert.Equal(4, t.Excluded);
        Assert.Equal(4, Scan.Node(s, T.Path).Excluded);
        Assert.Equal(1, Scan.Node(s, T.Sub("a")).Excluded);
        Assert.Null(s.FindNode(T.Sub("node_modules")));
        Assert.NotNull(s.FindNode(T.Sub("a", "cache12")));

        var paths = s.ExcludedPaths();
        Assert.Equal(4, paths.Length);
        Assert.Contains(T.Sub("a", "cache1"), paths, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Scan_WithoutRules_ExcludesNothing()
    {
        T.File(@"node_modules\x.js", 5);
        var s = await Scan.Run(T.Path, ExcludeRules.None);
        Assert.Equal(5, s.ScanTarget!.Size);
        Assert.Equal(0, s.Excluded);
    }

    [Fact]
    public async Task Scan_Cancelled_Throws()
    {
        for (int i = 0; i < 20; i++) T.File($"d{i}\\f.txt", 1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var s = new Scanner();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.ScanAsync(T.Path, 2, cts.Token));
    }

    [Fact]
    public async Task Scan_ChangedEvent_IsCoalescedUntilAcknowledged()
    {
        T.File(@"a\f.txt", 1);
        var s = new Scanner();
        int fired = 0;
        s.Changed += () => Interlocked.Increment(ref fired);
        await s.ScanAsync(T.Path, 2, CancellationToken.None);
        Assert.Equal(1, fired);
        s.Acknowledge();
        // After acknowledging, the next change may fire again.
        await s.RescanAsync(s.ScanTarget!, 2, CancellationToken.None);
        Assert.Equal(2, fired);
    }

    // ------------------------------------------------------------------ top files

    [Fact]
    public async Task Scan_TopFiles_ListsFilesFromOneMegabyte_Descending()
    {
        const int MB = 1 << 20;
        T.File(@"a\big.bin", 3 * MB);
        T.File(@"a\mid.bin", 2 * MB);
        T.File(@"small.bin", 1000);
        T.File("b.bin", MB);
        var s = await ScanT();

        var top = s.TopFiles();
        Assert.Equal(new[] { 3L * MB, 2L * MB, MB }, top.Select(f => f.Size));
        Assert.Equal(T.Sub("a", "big.bin"), top[0].Path, ignoreCase: true);
    }

    [Fact]
    public async Task Scan_TopFiles_RemoveTopAndRenameTop()
    {
        const int MB = 1 << 20;
        T.File(@"a\big.bin", 3 * MB);
        T.File(@"a\sub\mid.bin", 2 * MB);
        T.File(@"other.bin", MB);
        var s = await ScanT();

        s.RenameTop(T.Sub("a"), T.Sub("renamed"));
        Assert.Contains(s.TopFiles(), f => f.Path.Equals(T.Sub("renamed", "sub", "mid.bin"), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(s.TopFiles(), f => f.Path.Equals(T.Sub("renamed", "big.bin"), StringComparison.OrdinalIgnoreCase));

        s.RemoveTop(T.Sub("renamed"));
        var left = Assert.Single(s.TopFiles());
        Assert.Equal(T.Sub("other.bin"), left.Path, ignoreCase: true);
    }

    // ------------------------------------------------------------------ rescan

    [Fact]
    public async Task Rescan_Subfolder_EqualsFreshScan()
    {
        BuildBasicTree();
        T.File(@"a\old.txt", 33, Ago(years: 6));
        var s = await ScanT();

        // Change the disk below 'a': add, delete, new subfolder.
        T.File(@"a\added.pdf", 1234, Ago(months: 3));
        File.Delete(T.Sub("a", "b", "v.mp4"));
        T.File(@"a\new\n.docx", 77, Ago(days: 3));

        var a = Scan.Node(s, T.Sub("a"));
        await s.RescanAsync(a, 4, CancellationToken.None);

        var fresh = await ScanT();
        TreeAssert.Equal(fresh.Root!, s.Root!);
        Assert.Equal(s.ScanTarget!.Size, fresh.ScanTarget!.Size);
        Assert.True(a.IsDone);
        Assert.True(s.Root!.IsDone);
    }

    [Fact]
    public async Task Rescan_Whole_EqualsFreshScan()
    {
        BuildBasicTree();
        var s = await ScanT();
        T.File(@"zzz\n.txt", 99);
        File.Delete(T.Sub("r.txt"));

        await s.RescanAsync(s.ScanTarget!, 4, CancellationToken.None);
        TreeAssert.Equal((await ScanT()).Root!, s.Root!);
    }

    [Fact]
    public async Task Rescan_AppliesCurrentExclusions()
    {
        T.File(@"a\skip\big.bin", 900);
        T.File(@"a\ok.txt", 1);
        var s = await ScanT();
        Assert.Equal(901, s.ScanTarget!.Size);

        s.Excludes = new ExcludeRules(new[] { "skip" });
        await s.RescanAsync(Scan.Node(s, T.Sub("a")), 2, CancellationToken.None);
        Assert.Equal(1, s.ScanTarget.Size);
        Assert.Equal(1, s.Root!.Excluded);
    }

    [Fact]
    public async Task Rescan_BeforeAnyScan_Throws()
    {
        var s = new Scanner();
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.RescanAsync(new Node(), 1, CancellationToken.None));
    }

    // ------------------------------------------------------------------ deletions

    [Fact]
    public async Task RemoveFile_MatchesFreshScanAfterDelete()
    {
        T.File(@"a\w.docx", 100, Ago(years: 4));
        T.File(@"a\p.pdf", 200, Ago(days: 1));
        T.File(@"a\b\v.mp4", 1000, Ago(months: 6));
        var s = await ScanT();

        var a = Scan.Node(s, T.Sub("a"));
        int bucket = AgeBuckets.OfFile(T.Sub("a", "w.docx"));
        Assert.Equal(3, bucket);
        File.Delete(T.Sub("a", "w.docx"));
        a.RemoveFile("w.docx", 100, bucket);

        TreeAssert.Equal((await ScanT()).Root!, s.Root!);
    }

    [Fact]
    public async Task RemoveFile_LastOfItsType_DropsTheTypeEntry()
    {
        T.File(@"a\only.pdf", 10);
        T.File(@"a\other.txt", 5);
        var s = await ScanT();
        var a = Scan.Node(s, T.Sub("a"));

        a.RemoveFile("only.pdf", 10, AgeBuckets.OfFile(T.File(@"a\only.pdf", 10)));
        Assert.Single(a.OwnTypes!);
        Assert.Equal(5, a.Size);
        Assert.Equal(1, a.OwnCount);
    }

    [Fact]
    public async Task RemoveFolder_ViaNodeVM_MatchesFreshScan()
    {
        T.File(@"keep\k.txt", 10, Ago(days: 1));
        T.File(@"gone\g.txt", 20, Ago(years: 7));
        T.File(@"gone\sub\h.docx", 30, Ago(months: 6));
        var s = await ScanT();

        var target = s.ScanTarget!;
        var gone = Scan.Node(s, T.Sub("gone"));
        var rootVm = new NodeVM(target, Kind.Dir, null, 0);
        var goneVm = new NodeVM(gone, Kind.Dir, rootVm, 1);
        rootVm.Children.Add(goneVm);

        Directory.Delete(T.Sub("gone"), true);
        goneVm.RemoveFromTree();

        Assert.DoesNotContain(goneVm, rootVm.Children);
        TreeAssert.Equal((await ScanT()).Root!, s.Root!);
    }

    [Fact]
    public async Task RemoveFile_ViaNodeVM_MatchesFreshScan()
    {
        T.File(@"a\x.txt", 10, Ago(years: 7));
        T.File(@"a\y.txt", 20, Ago(days: 1));
        var s = await ScanT();

        var aNode = Scan.Node(s, T.Sub("a"));
        var rootVm = new NodeVM(s.ScanTarget!, Kind.Dir, null, 0);
        var aVm = new NodeVM(aNode, Kind.Dir, rootVm, 1);
        var filesVm = new NodeVM(new Node { FullPath = aNode.FullPath }, Kind.Files, aVm, 2);
        var fileVm = new NodeVM(new Node { Name = "x.txt", FullPath = T.Sub("a", "x.txt"), Size = 10, FileCount = 1 }, Kind.File, filesVm, 3);
        filesVm.Children.Add(fileVm);

        int bucket = AgeBuckets.OfFile(T.Sub("a", "x.txt"));
        File.Delete(T.Sub("a", "x.txt"));
        fileVm.RemoveFromTree(bucket);

        TreeAssert.Equal((await ScanT()).Root!, s.Root!);
    }

    [Fact]
    public async Task AddFile_CountsIntoFolderAndParents_LikeAScan()
    {
        T.File(@"a\x.txt", 10, Ago(days: 1));
        var s = await ScanT();
        var a = Scan.Node(s, T.Sub("a"));

        var added = T.File(@"a\new.pdf", 500, Ago(years: 6));
        FileOperations.AddFile(a, "new.pdf", 500, AgeBuckets.OfFile(added));

        TreeAssert.Equal((await ScanT()).Root!, s.Root!);
    }

    // ------------------------------------------------------------------ several paths

    [Fact]
    public async Task ScanMany_CombinesPathsBelowArtificialRoot()
    {
        T.File(@"one\a.txt", 100, Ago(days: 1));
        T.File(@"one\sub\b.txt", 200);
        T.File(@"two\c.docx", 400, Ago(years: 8));
        var s = new Scanner();
        var root = await s.ScanManyAsync(new[] { T.Sub("one"), T.Sub("two") }, 4, CancellationToken.None);

        Assert.True(s.IsMulti);
        Assert.Same(root, s.Root);
        Assert.Same(root, s.ScanTarget);
        Assert.Equal(700, root.Size);
        Assert.Equal(3, root.FileCount);
        Assert.Equal(1, root.DirCount); // 'sub'; the two paths themselves are not counted
        Assert.True(root.IsDone);
        Assert.Equal(2, root.Snapshot().Length);
        Assert.Equal(new long[] { 300, 0, 0, 0, 400 }, root.AgeSub);

        Assert.Equal(300, s.FindNode(T.Sub("one"))!.Size);
        Assert.Equal(200, s.FindNode(T.Sub("one", "sub"))!.Size);
        Assert.Equal(400, s.FindNode(T.Sub("two"))!.Size);
        Assert.Same(root, s.FindNode(""));
        Assert.Null(s.FindNode(T.Sub("three")));
        Assert.Equal(0, s.ExpectedBytes);
    }

    [Fact]
    public async Task ScanMany_Rescan_Works()
    {
        T.File(@"one\a.txt", 100);
        T.File(@"two\c.txt", 400);
        var s = new Scanner();
        await s.ScanManyAsync(new[] { T.Sub("one"), T.Sub("two") }, 2, CancellationToken.None);
        T.File(@"one\more.txt", 50);

        await s.RescanAsync(s.FindNode(T.Sub("one"))!, 2, CancellationToken.None);
        Assert.Equal(550, s.Root!.Size);
        Assert.Equal(3, s.Root.FileCount);
    }

    private static string? UncOf(string localPath)
    {
        var root = Path.GetPathRoot(localPath)!;
        if (root.Length < 3 || root[1] != ':') return null;
        return @"\\localhost\" + root[0] + "$\\" + localPath[root.Length..];
    }

    [UncFact]
    public async Task Scan_UncAdminShare_WhenReachable()
    {
        T.File(@"u\f.txt", 123);
        var unc = UncOf(Path.Combine(T.Path, "u"));
        if (unc == null || !Path.GetPathRoot(T.Path)!.StartsWith("C:", StringComparison.OrdinalIgnoreCase)) return; // temp is not on C:
        Assert.True(Directory.Exists(unc));

        var s = await Scan.Run(unc!);
        Assert.Equal(123, s.ScanTarget!.Size);
        Assert.Equal(1, s.ScanTarget.FileCount);

        var many = new Scanner();
        await many.ScanManyAsync(new[] { unc!, T.Sub("u") }, 2, CancellationToken.None);
        Assert.Equal(246, many.Root!.Size);
    }
}
