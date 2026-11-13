using DiskTree.Core;
using DiskTree.Ui;
using Xunit;

namespace DiskTree.Tests;

public class NodeVmFilesTests : TestBase
{
    // In the app the file list is filled on the UI thread. In a test there is no dispatcher, so the loading
    // continuation runs on a pool thread and can clear/fill Children while we read it: take snapshots defensively
    // and only accept a state that stays the same for a moment.
    private static async Task<NodeVM> WaitForSettled(NodeVM files)
    {
        string[]? last = null;
        for (int i = 0; i < 400; i++)
        {
            await Task.Delay(25);
            string[] names;
            try
            {
                var c = files.Children.ToArray();
                if (c.Any(x => x is null)) { last = null; continue; }
                names = c.Select(x => x.Name).ToArray();
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or IndexOutOfRangeException)
            {
                last = null;
                continue;
            }

            bool loading = names.Length == 0 || (names.Length == 1 && names[0] == "Lade…");
            if (!loading && last != null && last.SequenceEqual(names)) return files;
            last = loading ? null : names;
        }
        return files;
    }

    private static NodeVM FilesRow(string folder)
    {
        var dirNode = new Node { Name = "x", FullPath = folder, Scanned = true, OwnCount = 1, FileCount = 1 };
        var dir = new NodeVM(dirNode, Kind.Dir, null, 0);
        return new NodeVM(new Node { FullPath = folder, FileCount = 1 }, Kind.Files, dir, 1);
    }

    [Fact]
    public async Task FilesRow_OfMissingFolder_ShowsMessageInsteadOfStayingOnLoading()
    {
        var files = FilesRow(T.Sub("gone", "folder"));
        files.IsExpanded = true;
        await WaitForSettled(files);

        var row = Assert.Single(files.Children);
        Assert.Equal(Kind.Dummy, row.Kind);
        Assert.Equal("Dateien nicht lesbar", row.Name);
    }

    [Fact]
    public async Task FilesRow_OfExistingFolder_ListsFilesLargestFirst()
    {
        T.File(@"d\small.txt", 1);
        T.File(@"d\big.txt", 50);
        var files = FilesRow(T.Sub("d"));
        files.IsExpanded = true;
        await WaitForSettled(files);

        Assert.Equal(new[] { "big.txt", "small.txt" }, files.Children.Select(c => c.Name).ToArray());
    }
}
