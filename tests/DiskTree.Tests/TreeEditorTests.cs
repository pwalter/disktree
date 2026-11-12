using System.IO;
using DiskTree.Core;
using Xunit;

namespace DiskTree.Tests;

/// <summary>TreeEditor: after every change the tree must have the numbers of a fresh scan of the changed disk.</summary>
public class TreeEditorTests : TestBase
{
    [Fact]
    public async Task AddToChain_AddsToTheNodeAndAllParents()
    {
        T.File(@"a\b\x.txt", 10);
        var s = await ScanT();
        var b = Scan.Node(s, T.Sub("a", "b"));
        long rootSize = s.Root!.Size, rootFiles = s.Root.FileCount, rootDirs = s.Root.DirCount;

        TreeEditor.AddToChain(b, 5, 2, 1);

        Assert.Equal(15, b.Size);
        Assert.Equal(rootSize + 5, s.Root.Size);
        Assert.Equal(rootFiles + 2, s.Root.FileCount);
        Assert.Equal(rootDirs + 1, s.Root.DirCount);
    }

    [Fact]
    public async Task RemoveFolder_WithoutAnyRow_MatchesFreshScan()
    {
        T.File(@"keep\k.txt", 10, Ago(days: 1));
        T.File(@"gone\g.txt", 20, Ago(years: 7));
        T.File(@"gone\sub\h.docx", 30, Ago(months: 6));
        var s = await ScanT();

        var gone = Scan.Node(s, T.Sub("gone"));
        Directory.Delete(T.Sub("gone"), true);
        TreeEditor.RemoveFolder(gone);

        Assert.Null(s.FindNode(T.Sub("gone")));
        TreeAssert.Equal((await ScanT()).Root!, s.Root!);
    }

    [Fact]
    public async Task RemoveFolder_OnTheRoot_DoesNothing()
    {
        T.File(@"a\x.txt", 10);
        var s = await ScanT();
        long before = s.Root!.Size;

        TreeEditor.RemoveFolder(s.Root);

        Assert.Equal(before, s.Root.Size);
    }

    [Fact]
    public async Task RemoveFile_WithoutAgeBucket_LeavesAgeNumbersAlone()
    {
        T.File(@"a\x.txt", 10, Ago(years: 7));
        T.File(@"a\y.txt", 20, Ago(days: 1));
        var s = await ScanT();
        var a = Scan.Node(s, T.Sub("a"));
        var ownAges = a.OwnAges!.ToArray();
        var subAges = a.AgeSub!.ToArray();

        TreeEditor.RemoveFile(a, "x.txt", 10);

        Assert.Equal(20, a.Size);
        Assert.Equal(1, a.OwnCount);
        Assert.Equal(ownAges, a.OwnAges);
        Assert.Equal(subAges, a.AgeSub);
    }

    [Fact]
    public async Task AddFile_ToAFolderWithoutFiles_CreatesTypesAndAges()
    {
        T.File(@"a\b\x.txt", 10, Ago(days: 1));
        var s = await ScanT();
        var a = Scan.Node(s, T.Sub("a"));
        Assert.Null(a.OwnTypes);

        var f = T.File(@"a\n.pdf", 700, Ago(years: 3));
        TreeEditor.AddFile(a, "n.pdf", 700, s.Ages.OfFile(f));

        TreeAssert.Equal((await ScanT()).Root!, s.Root!);
    }

    [Fact]
    public async Task RenameFile_ChangingTheType_MatchesFreshScan()
    {
        T.File(@"a\x.txt", 10, Ago(years: 3));
        T.File(@"a\y.txt", 20, Ago(days: 1));
        var s = await ScanT();
        var a = Scan.Node(s, T.Sub("a"));
        int bucket = s.Ages.OfFile(T.Sub("a", "x.txt"));

        File.Move(T.Sub("a", "x.txt"), T.Sub("a", "x.pdf"));
        TreeEditor.RenameFile(a, "x.txt", "x.pdf", 10, bucket);

        TreeAssert.Equal((await ScanT()).Root!, s.Root!);
    }

    [Fact]
    public async Task RenameFolder_RepathsAndMatchesFreshScan()
    {
        T.File(@"old\f.txt", 10, Ago(days: 1));
        T.File(@"old\sub\g.txt", 20, Ago(years: 6));
        var s = await ScanT();
        var node = Scan.Node(s, T.Sub("old"));

        Directory.Move(T.Sub("old"), T.Sub("new"));
        TreeEditor.RenameFolder(node, "new");

        Assert.Equal(T.Sub("new", "sub"), Scan.Node(s, T.Sub("new", "sub")).FullPath, ignoreCase: true);
        TreeAssert.Equal((await ScanT()).Root!, s.Root!);
    }

    [Fact]
    public async Task Move_RemoveAtSourceAndAddAtDestination_MatchesFreshScan()
    {
        T.File(@"src\m.docx", 300, Ago(years: 2));
        T.File(@"src\stay.txt", 5, Ago(days: 2));
        T.Dir("dst");
        var s = await ScanT();
        var src = Scan.Node(s, T.Sub("src"));
        var dst = Scan.Node(s, T.Sub("dst"));
        int bucket = s.Ages.OfFile(T.Sub("src", "m.docx"));

        File.Move(T.Sub("src", "m.docx"), T.Sub("dst", "m.docx"));
        TreeEditor.RemoveFile(src, "m.docx", 300, bucket);
        TreeEditor.AddFile(dst, "m.docx", 300, bucket);

        TreeAssert.Equal((await ScanT()).Root!, s.Root!);
    }

    [Fact]
    public async Task ClearForRescan_TakesTheSubtreeOutOfAllParents_AndEmptiesIt()
    {
        T.File(@"a\x.txt", 10, Ago(days: 1));
        T.File(@"a\b\y.docx", 20, Ago(years: 7));
        T.File(@"other\z.txt", 40, Ago(days: 3));
        var s = await ScanT();
        var a = Scan.Node(s, T.Sub("a"));
        var parent = a.Parent!;
        long parentSize = parent.Size, parentFiles = parent.FileCount, parentDirs = parent.DirCount;

        TreeEditor.ClearForRescan(a);

        Assert.Equal(parentSize - 30, parent.Size);
        Assert.Equal(parentFiles - 2, parent.FileCount);
        Assert.Equal(parentDirs - 1, parent.DirCount); // 'b' only: 'a' itself stays a folder of the tree
        Assert.Equal(0, a.Size);
        Assert.Equal(0, a.OwnCount);
        Assert.Null(a.OwnTypes);
        Assert.Null(a.OwnAges);
        Assert.Empty(a.Snapshot());
        Assert.False(a.Scanned);
        Assert.False(a.IsDone);
        Assert.All(a.AgeSub!, v => Assert.Equal(0, v));
    }

    [Fact]
    public async Task ClearForRescan_CorrectsOpenOfTheParents()
    {
        T.File(@"a\x.txt", 10);
        var s = await ScanT();
        var a = Scan.Node(s, T.Sub("a"));
        Assert.Equal(0, s.Root!.Open);

        // A cancelled scan left unfinished folders behind: the node and all parents still count them.
        a.Open = 3;
        for (var n = a.Parent; n != null; n = n.Parent) n.Open += 3;

        TreeEditor.ClearForRescan(a);

        Assert.Equal(1, a.Open);
        for (var n = a.Parent; n != null; n = n.Parent) Assert.Equal(1, n.Open);
    }
}
