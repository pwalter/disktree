using System.IO;
using DiskTree.Core;
using DiskTree.Core.Snapshots;
using Xunit;

namespace DiskTree.Tests;

/// <summary>AgeClassifier: unchangeable per scan, owned by the Scanner, no state shared between scans.</summary>
public class AgeClassifierTests : TestBase
{
    private static readonly DateTime Ref = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void KeepsStampAndReference()
    {
        var c = new AgeClassifier(AgeStamp.Accessed, Ref);
        Assert.Equal(AgeStamp.Accessed, c.Stamp);
        Assert.Equal(Ref, c.ReferenceUtc);
    }

    [Fact]
    public void Of_IsIndependentPerInstance()
    {
        var now = new AgeClassifier(AgeStamp.Modified, Ref);
        var later = new AgeClassifier(AgeStamp.Modified, Ref.AddYears(3));
        long file = Ref.AddMonths(-3).Ticks;

        Assert.Equal(1, now.Of(file));
        Assert.Equal(3, later.Of(file));
        Assert.Equal(1, now.Of(file)); // creating the second one did not change the first
    }

    [Fact]
    public void OfFile_UsesTheStampOfTheInstance()
    {
        var f = T.File("t.txt", 1, Ago(years: 9)); // written 9 years ago, created just now
        Assert.Equal(4, new AgeClassifier(AgeStamp.Modified, DateTime.UtcNow).OfFile(f));
        Assert.Equal(0, new AgeClassifier(AgeStamp.Created, DateTime.UtcNow).OfFile(f));
    }

    [Fact]
    public void OfFile_MissingFile_IsMinusOneOrTheEpochBucket()
    {
        // File.Get...TimeUtc does not throw for a missing file (1601): it lands in the oldest bucket.
        int b = new AgeClassifier(AgeStamp.Modified, DateTime.UtcNow).OfFile(T.Sub("nope.txt"));
        Assert.True(b is -1 or 4);
    }

    [Fact]
    public async Task Scanner_BuildsItsOwnClassifier_FromTheChosenStamp()
    {
        T.File("a.txt", 100, Ago(years: 10)); // created just now

        var modified = await ScanT(stamp: AgeStamp.Modified);
        var created = await ScanT(stamp: AgeStamp.Created);

        Assert.Equal(AgeStamp.Modified, modified.Ages.Stamp);
        Assert.Equal(AgeStamp.Created, created.Ages.Stamp);
        Assert.Equal(new long[] { 0, 0, 0, 0, 100 }, modified.ScanTarget!.AgeSub);
        Assert.Equal(new long[] { 100, 0, 0, 0, 0 }, created.ScanTarget!.AgeSub);
        // the first scan keeps its stamp although a second one with another stamp ran afterwards
        Assert.Equal(AgeStamp.Modified, modified.Ages.Stamp);
        Assert.Equal(4, modified.Ages.OfFile(T.Sub("a.txt")));
        Assert.Equal(0, created.Ages.OfFile(T.Sub("a.txt")));
    }

    [Fact]
    public async Task Scanner_FixesTheReferenceAtScanStart_AndRescanKeepsIt()
    {
        T.File(@"a\x.txt", 10, Ago(days: 1));
        var before = DateTime.UtcNow;
        var s = await ScanT();
        var after = DateTime.UtcNow;
        var ages = s.Ages;

        Assert.InRange(ages.ReferenceUtc, before, after);

        await s.RescanAsync(Scan.Node(s, T.Sub("a")), 2, CancellationToken.None);
        Assert.Same(ages, s.Ages);
    }

    [Fact]
    public async Task Scanner_ChangingTheChoiceAfterTheScan_DoesNotChangeTheTree()
    {
        T.File("a.txt", 100, Ago(years: 10));
        var s = await ScanT(stamp: AgeStamp.Modified);

        s.AgeStamp = AgeStamp.Created; // only applies to the next scan

        Assert.Equal(AgeStamp.Modified, s.Ages.Stamp);
    }

    [Fact]
    public void BeforeAnyScan_AgesFollowsTheChoice()
    {
        var s = new Scanner { AgeStamp = AgeStamp.Accessed };
        Assert.Equal(AgeStamp.Accessed, s.Ages.Stamp);
    }

    [Fact]
    public async Task Snapshot_CarriesItsStampIntoTheScanner()
    {
        T.File(@"scan\f.txt", 10);
        var s = await Scan.Run(T.Sub("scan"), stamp: AgeStamp.Accessed);
        var file = T.Sub("x.disktree");
        SnapshotStore.Save(file, s, DateTime.UtcNow);

        var loaded = Scanner.FromSnapshot(SnapshotStore.Load(file));

        Assert.Equal(AgeStamp.Accessed, loaded.Ages.Stamp);
    }

    [Fact]
    public async Task EditsAfterTheScan_UseTheScansClassifier()
    {
        // The bucket of a file that is deleted later is found with the same stamp the tree was built with.
        T.File(@"a\x.txt", 10, Ago(years: 7));
        T.File(@"a\y.txt", 20, Ago(days: 1));
        var s = await ScanT(stamp: AgeStamp.Modified);
        var a = Scan.Node(s, T.Sub("a"));

        int bucket = s.Ages.OfFile(T.Sub("a", "x.txt"));
        Assert.Equal(4, bucket);
        File.Delete(T.Sub("a", "x.txt"));
        TreeEditor.RemoveFile(a, "x.txt", 10, bucket);

        TreeAssert.Equal((await ScanT()).Root!, s.Root!);
    }
}
