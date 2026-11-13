using DiskTree.Core;
using DiskTree.Ui;
using Xunit;

namespace DiskTree.Tests;

public class NodeVmFilesTests : TestBase
{
    private static async Task<NodeVM> WaitForSettled(NodeVM files)
    {
        for (int i = 0; i < 200; i++)
        {
            var c = files.Children.ToArray();
            if (!(c.Length == 1 && c[0].Name == "Lade…")) return files;
            await Task.Delay(25);
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
