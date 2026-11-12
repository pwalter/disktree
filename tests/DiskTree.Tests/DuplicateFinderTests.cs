using System.IO;
using Xunit;

namespace DiskTree.Tests;

public class DuplicateFinderTests : TestBase
{
    private string Write(string rel, byte[] data)
    {
        var p = T.Sub(rel.Split('\\'));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, data);
        return p;
    }

    private static byte[] Bytes(int length, int seed)
    {
        var b = new byte[length];
        new Random(seed).NextBytes(b);
        return b;
    }

    private Task<List<DuplicateGroup>> Find(long minSize = 1, IProgress<DupProgress>? progress = null, CancellationToken ct = default) =>
        new DuplicateFinder().FindAsync(T.Path, minSize, 4, progress, ct);

    [Fact]
    public async Task FindsGroupsOfIdenticalFiles_IgnoresSameSizeDifferentContent()
    {
        var content = Bytes(5000, 1);
        var a = Write(@"a\one.bin", content);
        var b = Write(@"b\deep\two.dat", content);
        var c = Write(@"three.txt", content);
        Write(@"same-size-other.bin", Bytes(5000, 2));
        Write(@"unique.bin", Bytes(777, 3));

        var groups = await Find();

        var g = Assert.Single(groups);
        Assert.Equal(5000, g.Size);
        Assert.Equal(new[] { a, b, c }.OrderBy(x => x, StringComparer.OrdinalIgnoreCase),
                     g.Paths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(10000, g.Wasted);
    }

    [Fact]
    public async Task LargeFiles_DifferingOnlyInTheMiddle_AreNotDuplicates()
    {
        // bigger than two 64 KB blocks, first and last block identical: only the full hash can tell them apart
        var x = Bytes(400_000, 7);
        var y = (byte[])x.Clone();
        y[200_000] ^= 0xFF;
        Write("x.bin", x);
        Write("y.bin", y);
        Write("x-dup.bin", x);

        var groups = await Find();

        var g = Assert.Single(groups);
        Assert.Equal(2, g.Paths.Length);
        Assert.DoesNotContain(g.Paths, p => p.EndsWith("y.bin"));
        Assert.Equal(400_000, g.Wasted);
    }

    [Fact]
    public async Task SmallFilesBelowTwoBlocks_AreComparedCompletely()
    {
        var x = Bytes(100_000, 9); // < 128 KB: partial hash covers the whole file
        var y = (byte[])x.Clone();
        y[50_000] ^= 1;
        Write("p.bin", x);
        Write("q.bin", y);
        Assert.Empty(await Find());
    }

    [Fact]
    public async Task MinSizeAndEmptyFiles()
    {
        Write("e1.txt", Array.Empty<byte>());
        Write("e2.txt", Array.Empty<byte>());
        var small = Bytes(100, 4);
        Write("s1.bin", small);
        Write("s2.bin", small);
        var big = Bytes(10_000, 5);
        Write("b1.bin", big);
        Write("b2.bin", big);

        Assert.Equal(2, (await Find(minSize: 1)).Count);           // empty files never count
        var onlyBig = Assert.Single(await Find(minSize: 1000));
        Assert.Equal(10_000, onlyBig.Size);
        Assert.Empty(await Find(minSize: 1_000_000));
        Assert.Equal(2, (await Find(minSize: 0)).Count);           // 0 is raised to 1
    }

    [Fact]
    public async Task GroupsAreSortedByWastedSpace()
    {
        var a = Bytes(1000, 11);
        var b = Bytes(2000, 12);
        var c = Bytes(500, 13);
        for (int i = 0; i < 2; i++) Write($"a{i}.bin", a);   // wasted 1000
        for (int i = 0; i < 2; i++) Write($"b{i}.bin", b);   // wasted 2000
        for (int i = 0; i < 5; i++) Write($"c{i}.bin", c);   // wasted 2000
        for (int i = 0; i < 2; i++) Write($"d{i}.bin", Bytes(3000, 14)); // wasted 3000

        var groups = await Find();
        Assert.Equal(4, groups.Count);
        Assert.Equal(groups.Select(g => g.Wasted).OrderByDescending(w => w), groups.Select(g => g.Wasted));
        Assert.Equal(3000, groups[0].Wasted);
    }

    [Fact]
    public async Task NoFilesAndNoDuplicates_GiveEmptyResult()
    {
        Assert.Empty(await Find());
        Write("a.bin", Bytes(10, 1));
        Write("b.bin", Bytes(11, 1));
        Assert.Empty(await Find());
    }

    [Fact]
    public async Task ReportsProgressAndFinishesWithDone()
    {
        var content = Bytes(300_000, 21);
        Write("a.bin", content);
        Write("b.bin", content);
        var seen = new List<DupProgress>();
        var progress = new SyncProgress<DupProgress>(p => { lock (seen) seen.Add(p); });

        var groups = await Find(progress: progress);

        Assert.Single(groups);
        Assert.Contains(seen, p => p.Phase == DupPhase.Sizes);
        Assert.Contains(seen, p => p.Phase == DupPhase.PartialHash);
        Assert.Contains(seen, p => p.Phase == DupPhase.FullHash);
        Assert.Equal(DupPhase.Done, seen[^1].Phase);
    }

    [Fact]
    public async Task Cancelled_Throws()
    {
        var content = Bytes(1000, 1);
        Write("a.bin", content);
        Write("b.bin", content);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Find(ct: cts.Token));
    }

    [Fact]
    public async Task LockedFile_IsCountedAsError_NotAsDuplicate()
    {
        var content = Bytes(2000, 31);
        Write("a.bin", content);
        var b = Write("b.bin", content);
        Write("c.bin", content);
        var finder = new DuplicateFinder();
        using var lockFile = new FileStream(b, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var groups = await finder.FindAsync(T.Path, 1, 2, null, CancellationToken.None);

        var g = Assert.Single(groups);
        Assert.Equal(2, g.Paths.Length);
        Assert.DoesNotContain(b, g.Paths, StringComparer.OrdinalIgnoreCase);
        Assert.True(finder.Errors >= 1);
    }

    [Fact]
    public void IsNetworkPath()
    {
        Assert.True(DuplicateFinder.IsNetworkPath(@"\\server\share\x"));
        Assert.False(DuplicateFinder.IsNetworkPath(T.Path));
        Assert.False(DuplicateFinder.IsNetworkPath("::not a path::"));
    }

    private sealed class SyncProgress<TV>(Action<TV> h) : IProgress<TV>
    {
        public void Report(TV value) => h(value);
    }
}
