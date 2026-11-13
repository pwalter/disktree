using DiskTree.Core;
using Xunit;

namespace DiskTree.Tests;

public class CancelledScanTests : TestBase
{
    private static void AssertConsistent(Node root)
    {
        foreach (var n in root.Walk())
        {
            Assert.True(n.IsDone, $"Ordner noch offen (Spinner): {n.FullPath} Open={n.Open}");
            var kids = n.Snapshot();
            Assert.Equal(n.OwnSize + kids.Sum(k => k.Size), n.Size);
            Assert.Equal(n.OwnCount + kids.Sum(k => k.FileCount), n.FileCount);
            // (DirCount is not checked: the placeholder folders above the scanned one are not counted as folders)
        }
    }

    [Fact]
    public async Task AlreadyCancelled_LeavesNoOpenFolders()
    {
        for (int i = 0; i < 20; i++) T.File($"d{i}\\f.txt", 1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var s = new Scanner();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.ScanAsync(T.Path, 2, cts.Token));
        Assert.NotNull(s.Root);
        AssertConsistent(s.Root!);
    }

    [Fact]
    public async Task CancelledDuringScan_LeavesNoOpenFolders_AndConsistentSums()
    {
        // wide and deep enough that the cancel normally hits while many folders still wait in the queue
        for (int i = 0; i < 40; i++)
            for (int j = 0; j < 10; j++)
                T.File($"d{i}\\e{j}\\f.txt", 3);

        for (int round = 0; round < 5; round++)
        {
            using var cts = new CancellationTokenSource();
            var s = new Scanner();
            s.Changed += () => cts.Cancel(); // first progress report: cancel
            Exception? ex = null;
            try { await s.ScanAsync(T.Path, 1, cts.Token); }
            catch (OperationCanceledException e) { ex = e; }
            // (a very fast machine may finish before the first event: then it simply completed)
            _ = ex;
            AssertConsistent(s.Root!);
        }
    }

    [Fact]
    public async Task CancelledRescan_LeavesNoOpenFolders()
    {
        for (int i = 0; i < 30; i++) T.File($"a\\d{i}\\f.txt", 2);
        var s = await ScanT();
        var a = Scan.Node(s, T.Sub("a"));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.RescanAsync(a, 2, cts.Token));
        AssertConsistent(s.Root!);
    }
}
