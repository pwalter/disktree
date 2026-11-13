using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using DiskTree.Core;
using DiskTree.Core.Snapshots;
using DiskTree.Core.Storage;
using DiskTree.Features.Reports;
using DiskTree.Features.Search;
using DiskTree.Features.Treemap;
using Xunit;

namespace DiskTree.Tests.Storage;

/// <summary>
/// The scan and everything built on its tree, run against <see cref="FakeProvider"/> ('/' separator, case-sensitive
/// names): nothing below may assume Windows paths or the local disk.
/// </summary>
public class ProviderScanTests : TestBase
{
    private static FakeProvider Sample()
    {
        var now = DateTime.UtcNow;
        return new FakeProvider()
            .File("/data/readme.txt", 10, now.AddDays(-2))
            .File("/data/A/film.mp4", 3 << 20, now.AddMonths(-6))   // big enough for the largest-files list
            .File("/data/a/klein.mp4", 100, now.AddYears(-3))       // "a" and "A" are two folders here
            .File("/data/a/b/c/tief.pdf", 200, now.AddYears(-10))
            .File("/data/node_modules/x.js", 5000)
            .Link("/data/verweis")
            .Denied("/data/geheim")
            .Folder("/data/leer")
            .File("/other/z.bin", 7);
    }

    private static async Task<Scanner> ScanFake(FakeProvider p, string path = "/data", ExcludeRules? excludes = null,
        AgeStamp stamp = AgeStamp.Modified, int threads = 4)
    {
        var s = new Scanner { Provider = p, Excludes = excludes, AgeStamp = stamp };
        await s.ScanAsync(path, threads, CancellationToken.None);
        return s;
    }

    // ------------------------------------------------------------------ scanner

    [Fact]
    public async Task Scan_WithoutCapacity_StartsAtTheFolder_AndSumsUp()
    {
        var s = await ScanFake(Sample());

        var root = s.Root!;
        Assert.Same(root, s.ScanTarget);
        Assert.Equal("/data", root.FullPath);
        Assert.Equal(0, root.DriveTotal);
        Assert.Equal(0, s.ExpectedBytes);
        Assert.Equal(10 + (3 << 20) + 100 + 200 + 5000, root.Size); // the link is not followed
        Assert.Equal(5, root.FileCount);
        Assert.Equal(1, s.Errors); // "geheim"
        Assert.True(root.IsDone);

        var names = root.Snapshot().Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "A", "a", "geheim", "leer", "node_modules" }, names);
        Assert.Equal("/data/a/b/c", Scan.Node(s, "/data/a/b/c").FullPath);
    }

    [Fact]
    public async Task Scan_WithCapacity_HangsBelowPlaceholderChain()
    {
        var p = Sample();
        p.Total = 1_000_000_000;
        p.Free = 400_000_000;

        var s = await ScanFake(p, "/data/a/b");

        Assert.Equal("/", s.Root!.FullPath);
        Assert.True(s.Root.PartialDrive);
        Assert.Equal(1_000_000_000, s.Root.DriveTotal);
        Assert.Equal(0, s.ExpectedBytes); // only part of the "drive"
        Assert.Equal("/data/a/b", s.ScanTarget!.FullPath);
        Assert.Equal(new[] { "/", "/data", "/data/a", "/data/a/b" },
            new[] { s.Root, s.Root.Snapshot()[0], s.Root.Snapshot()[0].Snapshot()[0], s.ScanTarget }.Select(n => n.FullPath));
        Assert.Equal(200, s.Root.Size);

        var whole = await ScanFake(p, "/");
        Assert.Same(whole.Root, whole.ScanTarget);
        Assert.False(whole.Root!.PartialDrive);
        Assert.Equal(600_000_000, whole.ExpectedBytes);
    }

    [Fact]
    public async Task FindNode_IsCaseSensitive_AndStopsAtSeparator()
    {
        var p = Sample().Folder("/data/ab");
        var s = await ScanFake(p);

        Assert.NotSame(s.FindNode("/data/A"), s.FindNode("/data/a"));
        Assert.Equal("A", s.FindNode("/data/A")!.Name);
        Assert.Null(s.FindNode("/DATA/a"));
        Assert.Null(s.FindNode("/data/a/B"));
        Assert.Equal("ab", s.FindNode("/data/ab/")!.Name);
        Assert.Same(s.Root, s.FindNode("/data"));
        Assert.Null(s.FindNode("/other"));
    }

    [Fact]
    public async Task TopFiles_UseTheProvidersSeparator()
    {
        var s = await ScanFake(Sample());
        var top = Assert.Single(s.TopFiles());
        Assert.Equal("/data/A/film.mp4", top.Path);

        s.RenameTop("/data/A", "/data/B");
        Assert.Equal("/data/B/film.mp4", Assert.Single(s.TopFiles()).Path);
        s.RemoveTop("/data/b"); // other case: a different folder here
        Assert.Single(s.TopFiles());
        s.RemoveTop("/data/B");
        Assert.Empty(s.TopFiles());
    }

    [Fact]
    public async Task TypesAndAges_ComeFromTheListing()
    {
        var s = await ScanFake(Sample());
        var a = Scan.Node(s, "/data/a");

        int video = FileTypes.CategoryOf("x.mp4");
        Assert.Contains(a.OwnTypes!, t => t.Cat == video && t.Size == 100 && t.Count == 1);
        // own files of "a": 100 bytes 3 years old (bucket "2 – 5 Jahre"); below it 200 bytes 10 years old
        Assert.Equal(new long[] { 0, 0, 0, 100, 200 }, a.AgeSub);
        Assert.Equal(StampSet.Modified, ((FakeProvider)s.Provider).LastStamps);
    }

    [Fact]
    public async Task AgeStamp_Created_ReadsOnlyThatTimestamp()
    {
        var now = DateTime.UtcNow;
        var p = new FakeProvider().File("/d/f.txt", 50, modified: now, created: now.AddYears(-6));

        var s = await ScanFake(p, "/d", stamp: AgeStamp.Created);

        Assert.Equal(new long[] { 0, 0, 0, 0, 50 }, s.Root!.AgeSub);
        Assert.Equal(StampSet.Created, p.LastStamps);
    }

    [Fact]
    public async Task ExcludedByName_IsCountedWithProviderPath()
    {
        var s = await ScanFake(Sample(), excludes: new ExcludeRules(new[] { "node_modules" }));

        Assert.Null(s.FindNode("/data/node_modules"));
        Assert.Equal(1, s.Excluded);
        Assert.Equal(new[] { "/data/node_modules" }, s.ExcludedPaths());
        Assert.Equal(1, s.Root!.Excluded);
    }

    [Fact]
    public async Task ScanMany_TakesSeveralProviderPaths()
    {
        var p = Sample();
        var s = new Scanner { Provider = p };
        await s.ScanManyAsync(new[] { "/data/a", "/other/" }, 2, CancellationToken.None);

        Assert.True(s.IsMulti);
        Assert.Equal(new[] { "/data/a", "/other" }, s.Root!.Snapshot().Select(c => c.FullPath));
        Assert.Equal(300 + 7, s.Root.Size);
        Assert.Equal("c", s.FindNode("/data/a/b/c")!.Name);
    }

    [Fact]
    public async Task Rescan_AfterChange_EqualsFreshScan()
    {
        var p = Sample();
        var s = await ScanFake(p);
        p.File("/data/a/b/neu.docx", 1234).Delete("/data/a/klein.mp4").Folder("/data/a/b/frisch");

        await s.RescanAsync(Scan.Node(s, "/data/a"), 2, CancellationToken.None);

        TreeAssert.Equal((await ScanFake(p)).Root!, s.Root!);
    }

    [Fact]
    public async Task Scan_ListsEveryFolderOnce_AndNotTheLink()
    {
        var p = Sample();
        await ScanFake(p);
        // data, A, a, a/b, a/b/c, geheim, leer, node_modules (the link is not listed)
        Assert.Equal(8, p.Listings);
    }

    // ------------------------------------------------------------------ tree edits

    [Fact]
    public async Task TreeEditor_RenameFolder_UsesProviderPaths()
    {
        var s = await ScanFake(Sample());
        var a = Scan.Node(s, "/data/a");

        TreeEditor.RenameFolder(a, "x", s.Paths);

        Assert.Equal("/data/x", a.FullPath);
        Assert.Equal("/data/x/b/c", Scan.Node(s, "/data/x/b/c").FullPath);
        Assert.Null(s.FindNode("/data/a"));
    }

    [Fact]
    public async Task TreeEditor_RemoveFolderAndFile_EqualFreshScan()
    {
        var p = Sample();
        var s = await ScanFake(p);

        TreeEditor.RemoveFolder(Scan.Node(s, "/data/a/b"));
        p.Delete("/data/a/b");
        var dir = Scan.Node(s, "/data");
        TreeEditor.RemoveFile(dir, "readme.txt", 10, s.Ages.Of(DateTime.UtcNow.AddDays(-2).Ticks));
        p.Delete("/data/readme.txt");

        TreeAssert.Equal((await ScanFake(p)).Root!, s.Root!);
    }

    // ------------------------------------------------------------------ snapshots

    private string SnapFile => Path.Combine(T.Path, "fake.disktree");

    private static string HeaderOf(string file)
    {
        using var fs = File.OpenRead(file);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var r = new StreamReader(gz, Encoding.UTF8);
        return r.ReadLine()!;
    }

    [Fact]
    public async Task Snapshot_RoundTrip_KeepsProviderAndPaths()
    {
        var p = Sample();
        p.Total = 10_000_000;
        p.Free = 1_000_000;
        StorageProviders.Register(p);
        try
        {
            var s = await ScanFake(p, "/data/a");
            SnapshotStore.Save(SnapFile, s, DateTime.UtcNow);
            Assert.Contains("\"provider\":\"fake\"", HeaderOf(SnapFile));

            var loaded = SnapshotStore.Load(SnapFile);
            Assert.Same(p, loaded.Provider);
            TreeAssert.Equal(s.Root!, loaded.Root);
            Assert.Equal("/data/a", loaded.Target.FullPath);
            Assert.Equal("/data/a/b/c", loaded.Target.Snapshot().Single(c => c.Name == "b").Snapshot()[0].FullPath);

            var again = Scanner.FromSnapshot(loaded);
            Assert.Same(p, again.Provider);
            Assert.Equal("c", again.FindNode("/data/a/b/c")!.Name);
            Assert.Null(again.FindNode("/data/A"));
        }
        finally
        {
            StorageProviders.Unregister(p.Id);
        }
    }

    [Fact]
    public async Task Snapshot_UnknownProvider_IsRefused()
    {
        var p = Sample();
        var s = await ScanFake(p);
        SnapshotStore.Save(SnapFile, s, DateTime.UtcNow); // "fake" is not registered

        var ex = Assert.Throws<InvalidDataException>(() => SnapshotStore.Load(SnapFile));
        Assert.Contains("fake", ex.Message);
    }

    [Fact]
    public async Task Snapshot_OfDiskScan_HasNoProviderField()
    {
        T.File(@"scan\f.txt", 10);
        var s = await Scan.Run(T.Sub("scan"));
        SnapshotStore.Save(SnapFile, s, DateTime.UtcNow);

        Assert.DoesNotContain("provider", HeaderOf(SnapFile));
        Assert.Same(DiskProvider.Instance, SnapshotStore.Load(SnapFile).Provider);
    }

    [Fact]
    public async Task Compare_IsCaseSensitiveWithProviderPaths()
    {
        var p = Sample();
        var before = await ScanFake(p);
        p.File("/data/a/neu.bin", 1000).Delete("/data/A");
        var after = await ScanFake(p);

        var r = SnapshotCompare.Compare(before.ScanTarget!, after.ScanTarget!, p.Paths);

        Assert.Same(p.Paths, r.Paths);
        Assert.Contains(r.Rows, x => x.Path == "/data/A" && x.Kind == DiffKind.Removed);
        Assert.Contains(r.Rows, x => x.Path == "/data/a" && x.Kind == DiffKind.Grown);

        var part = SnapshotCompare.Compare(before.ScanTarget!, Scan.Node(after, "/data/a"), p.Paths);
        Assert.Equal("/data/a", part.CommonPath);
    }

    // ------------------------------------------------------------------ tree rows

    [Fact]
    public async Task FilesRow_ListsFromTheProviderOfTheRootRow()
    {
        var p = new FakeProvider().File("/d/klein.txt", 1).File("/d/gross.txt", 50).Folder("/d/sub");
        var dirNode = new Node { Name = "d", FullPath = "/d", Scanned = true, OwnCount = 2, FileCount = 2 };
        var root = new DiskTree.Ui.NodeVM(dirNode, DiskTree.Ui.Kind.Dir, null, 0, p);
        var files = new DiskTree.Ui.NodeVM(new Node { FullPath = "/d", FileCount = 2 }, DiskTree.Ui.Kind.Files, root, 1);

        files.IsExpanded = true;
        string[] names = [];
        for (int i = 0; i < 200; i++)
        {
            await Task.Delay(25);
            try { names = files.Children.ToArray().Where(c => c != null).Select(c => c.Path).ToArray(); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or IndexOutOfRangeException) { continue; }
            if (names.Length == 2) break;
        }

        Assert.Equal(new[] { "/d/gross.txt", "/d/klein.txt" }, names); // largest first, the folder is no file
    }

    // ------------------------------------------------------------------ search, treemap, export, report

    [Fact]
    public async Task Search_FoldersAndFiles_GoThroughTheProvider()
    {
        var p = Sample();
        var s = await ScanFake(p);

        var folders = SearchEngine.Run(new SearchQuery { Scope = s.Root!, Text = "b", Provider = p }, null, CancellationToken.None);
        var b = Assert.Single(folders.Hits);
        Assert.Equal("/data/a/b", b.Path);
        Assert.Equal("/data/a", b.Folder);

        int before = p.Listings;
        var files = SearchEngine.Run(new SearchQuery { Scope = s.Root!, Files = true, Extensions = [".mp4"], Provider = p },
            null, CancellationToken.None);
        Assert.Equal(new[] { "/data/A/film.mp4", "/data/a/klein.mp4" }, files.Hits.Select(h => h.Path));
        Assert.Equal("/data/A", files.Hits[0].Folder);
        Assert.True(p.Listings > before);
        Assert.Equal(StampSet.All, p.LastStamps); // the date filter may need any of them
    }

    [Fact]
    public async Task Treemap_SizesOfTheTree_TileTheArea()
    {
        var s = await ScanFake(Sample());
        var root = s.Root!;

        var sizes = root.Snapshot().Select(c => (double)c.Size).Append(root.OwnSize).OrderByDescending(x => x).ToList();
        Assert.Equal(root.Size, (long)sizes.Sum());

        var rects = TreemapLayout.Squarify(sizes, new Rect(0, 0, 400, 300));
        Assert.Equal(400 * 300, rects.Where(r => !r.IsEmpty).Sum(r => r.Width * r.Height), 3);
    }

    [Fact]
    public async Task ExportAndReport_SplitPathsWithProviderRules()
    {
        var s = await ScanFake(Sample());

        var csv = Path.Combine(T.Path, "top.csv");
        Exporter.WriteTopFiles(csv, s.TopFiles(), s.Paths);
        var line = File.ReadAllLines(csv)[1];
        Assert.StartsWith("1;film.mp4;/data/A;/data/A/film.mp4;", line);

        var data = ReportBuilder.Build(s, interim: false);
        Assert.Same(s.Paths, data.Paths);
        Assert.Equal("/data", data.Title);
        var html = ReportHtml.Render(data);
        Assert.Contains("<td>film.mp4</td><td class=\"p\">/data/A</td>", html);
        Assert.Contains(data.Folders, f => f.Path == "/data/A");

        var tree = Path.Combine(T.Path, "tree.csv");
        Exporter.WriteTree(tree, s.Root!);
        Assert.Contains(File.ReadAllLines(tree), l => l.Contains(";/data/a/b/c;"));
    }
}
