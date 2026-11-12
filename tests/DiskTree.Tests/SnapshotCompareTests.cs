using System.IO;
using Xunit;

namespace DiskTree.Tests;

public class SnapshotCompareTests : TestBase
{
    [Fact]
    public async Task Compare_GrownShrunkNewRemoved_SortedByChange()
    {
        T.File(@"grow\a.bin", 100);
        T.File(@"shrink\b.bin", 1000);
        T.File(@"same\c.bin", 50);
        T.File(@"gone\d.bin", 300);
        T.File(@"gone\deep\e.bin", 200);
        var before = await ScanT();

        T.File(@"grow\a2.bin", 400);       // +400
        File.Delete(T.Sub("shrink", "b.bin"));
        T.File(@"shrink\b.bin", 900);      // -100
        Directory.Delete(T.Sub("gone"), true); // -500, one row
        T.File(@"fresh\x\f.bin", 700);     // +700, one row for 'fresh'
        var after = await ScanT();

        var r = SnapshotCompare.Compare(before.ScanTarget!, after.ScanTarget!);

        Assert.Equal(T.Path, r.CommonPath, ignoreCase: true);
        Assert.Equal(1650, r.OldTotal);
        Assert.Equal(1650 + 400 - 100 - 500 + 700, r.NewTotal);

        var byName = r.Rows.ToDictionary(x => x.Name == "" ? "root" : x.Name);
        Assert.Equal(DiffKind.Grown, byName["grow"].Kind);
        Assert.Equal(100, byName["grow"].OldSize);
        Assert.Equal(500, byName["grow"].NewSize);
        Assert.Equal(400, byName["grow"].Delta);
        Assert.Equal(DiffKind.Shrunk, byName["shrink"].Kind);
        Assert.Equal(-100, byName["shrink"].Delta);
        Assert.Equal(DiffKind.Removed, byName["gone"].Kind);
        Assert.Equal(500, byName["gone"].OldSize);
        Assert.Equal(0, byName["gone"].NewSize);
        Assert.Equal(2, byName["gone"].OldFiles);
        Assert.Equal(DiffKind.New, byName["fresh"].Kind);
        Assert.Equal(700, byName["fresh"].NewSize);
        Assert.False(byName.ContainsKey("same"));
        Assert.False(byName.ContainsKey("deep")); // only the top of a removed subtree is listed
        Assert.False(byName.ContainsKey("x"));

        // biggest absolute change first; the scanned folder itself (net +500) is a row too
        var deltas = r.Rows.Select(x => Math.Abs(x.Delta)).ToList();
        Assert.Equal(deltas.OrderByDescending(d => d), deltas);
        Assert.Equal("fresh", r.Rows[0].Name);
    }

    [Fact]
    public async Task Compare_IdenticalScans_NoRows()
    {
        T.File(@"a\x.bin", 10);
        var r = SnapshotCompare.Compare((await ScanT()).ScanTarget!, (await ScanT()).ScanTarget!);
        Assert.Empty(r.Rows);
        Assert.Equal(r.OldTotal, r.NewTotal);
    }

    [Fact]
    public async Task Compare_OnlyTheOverlappingAreaIsCompared()
    {
        T.File(@"a\x.bin", 10);
        T.File(@"b\y.bin", 20);
        var whole = await ScanT();                       // older, bigger scan
        T.File(@"a\more.bin", 5);
        var part = await Scan.Run(T.Sub("a"));            // newer, covers only 'a'

        var r = SnapshotCompare.Compare(whole.ScanTarget!, part.ScanTarget!);
        Assert.Equal(T.Sub("a"), r.CommonPath, ignoreCase: true);
        Assert.Equal(10, r.OldTotal);
        Assert.Equal(15, r.NewTotal);
        var row = Assert.Single(r.Rows);
        Assert.Equal(DiffKind.Grown, row.Kind);

        // and the other way round (older = part, newer = whole)
        var r2 = SnapshotCompare.Compare(part.ScanTarget!, whole.ScanTarget!);
        Assert.Equal(T.Sub("a"), r2.CommonPath, ignoreCase: true);
        Assert.Equal(15, r2.OldTotal);
        Assert.Equal(10, r2.NewTotal);
    }

    [Fact]
    public async Task Compare_DisjointScans_Throw()
    {
        T.File(@"a\x.bin", 10);
        T.File(@"b\y.bin", 20);
        var a = await Scan.Run(T.Sub("a"));
        var b = await Scan.Run(T.Sub("b"));
        Assert.Throws<InvalidOperationException>(() => SnapshotCompare.Compare(a.ScanTarget!, b.ScanTarget!));
    }

    [Fact]
    public async Task Compare_SiblingWithSamePrefix_IsNotOverlap()
    {
        T.File(@"foo\x.bin", 10);
        T.File(@"foobar\y.bin", 20);
        var a = await Scan.Run(T.Sub("foo"));
        var b = await Scan.Run(T.Sub("foobar"));
        Assert.Throws<InvalidOperationException>(() => SnapshotCompare.Compare(a.ScanTarget!, b.ScanTarget!));
    }

    [Fact]
    public async Task Compare_WorksOnLoadedSnapshots()
    {
        using var store = new TempDir(); // snapshot files must not lie inside the scanned folder
        T.File(@"a\x.bin", 10);
        var s1 = await ScanT();
        var f1 = Path.Combine(store.Path, "1.disktree");
        SnapshotStore.Save(f1, s1, DateTime.UtcNow);
        T.File(@"a\y.bin", 90);
        var s2 = await ScanT();
        var f2 = Path.Combine(store.Path, "2.disktree");
        SnapshotStore.Save(f2, s2, DateTime.UtcNow);

        var r = SnapshotCompare.Compare(SnapshotStore.Load(f1).Target, SnapshotStore.Load(f2).Target);
        Assert.Equal(10, r.OldTotal);
        Assert.Equal(100, r.NewTotal);
        Assert.Contains(r.Rows, x => x.Name == "a" && x.Delta == 90);
    }

    [Fact]
    public void DiffRow_DeltaIsNewMinusOld() =>
        Assert.Equal(-30, new DiffRow(@"C:\x", "x", DiffKind.Shrunk, 50, 20, 1, 1).Delta);
}
