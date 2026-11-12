using System.IO;
using System.IO.Compression;
using System.Text;
using Xunit;

namespace DiskTree.Tests;

public class SnapshotStoreTests : TestBase
{
    private string SnapFile => Path.Combine(T.Path, "out.disktree");

    private string BuildTree()
    {
        var root = T.Dir("scan");
        File.WriteAllText(Path.Combine(root, "readme.txt"), "x");
        T.File(@"scan\a\w.docx", 100, Ago(years: 4));
        T.File(@"scan\a\p.pdf", 200, Ago(days: 1));
        T.File(@"scan\a\b\v.mp4", 3 << 20, Ago(months: 6));
        T.File(@"scan\skipme\x.bin", 999);
        T.Dir(@"scan\empty");
        return root;
    }

    [Fact]
    public async Task RoundTrip_KeepsNumbersTypesAgesExcludedAndTopFiles()
    {
        var root = BuildTree();
        var s = await Scan.Run(root, new ExcludeRules(new[] { "skipme" }));
        var at = new DateTime(2026, 5, 17, 13, 45, 12, DateTimeKind.Utc);

        SnapshotStore.Save(SnapFile, s, at);
        var loaded = SnapshotStore.Load(SnapFile);

        TreeAssert.Equal(s.Root!, loaded.Root);
        Assert.Equal(s.Root!.FullPath, loaded.Root.FullPath);
        Assert.Equal(s.Root.DriveTotal, loaded.Root.DriveTotal);
        Assert.Equal(s.Root.DriveFree, loaded.Root.DriveFree);
        Assert.True(loaded.Root.PartialDrive);
        Assert.Equal(root, loaded.Target.FullPath, ignoreCase: true);
        Assert.Equal(root, loaded.Info.TargetPath, ignoreCase: true);
        Assert.Equal(1, loaded.Version);
        Assert.Equal(Environment.MachineName, loaded.Info.Machine);
        Assert.Equal(at, loaded.Info.ScannedAt.ToUniversalTime());
        Assert.Equal(s.ExpectedBytes, loaded.ExpectedBytes);
        Assert.Equal(AgeStamp.Modified, loaded.AgeStamp);

        // Excluded counter and age buckets survived explicitly
        Assert.Equal(1, loaded.Target.Excluded);
        Assert.Equal(new long[] { 201, 3 << 20, 0, 100, 0 }, loaded.Target.AgeSub);
        Assert.Equal(s.ScanTarget!.AgeSub, loaded.Target.AgeSub);

        Assert.Equal(s.TopFiles().Select(f => (f.Path, f.Size)), loaded.Top.Select(f => (f.Path, f.Size)));
        Assert.Single(loaded.Top);
    }

    [Fact]
    public async Task RoundTrip_AgeStampKey_IsStored()
    {
        T.File(@"scan\f.txt", 10);
        AgeStamps.Preferred = AgeStamp.Accessed;
        var s = await Scan.Run(T.Sub("scan"));
        SnapshotStore.Save(SnapFile, s, DateTime.UtcNow);
        Assert.Equal(AgeStamp.Accessed, SnapshotStore.Load(SnapFile).AgeStamp);

        AgeStamps.Preferred = AgeStamp.Created;
        s = await Scan.Run(T.Sub("scan"));
        SnapshotStore.Save(SnapFile, s, DateTime.UtcNow);
        Assert.Equal(AgeStamp.Created, SnapshotStore.Load(SnapFile).AgeStamp);
    }

    [Fact]
    public async Task RoundTrip_MultiScan_KeepsArtificialRoot()
    {
        T.File(@"one\a.txt", 100);
        T.File(@"two\b.txt", 200);
        var s = new Scanner();
        await s.ScanManyAsync(new[] { T.Sub("one"), T.Sub("two") }, 2, CancellationToken.None);

        SnapshotStore.Save(SnapFile, s, DateTime.UtcNow);
        var loaded = SnapshotStore.Load(SnapFile);

        TreeAssert.Equal(s.Root!, loaded.Root);
        Assert.Equal("", loaded.Root.FullPath);
        Assert.Equal(300, loaded.Root.Size);
        Assert.Equal(T.Sub("one"), loaded.Root.Snapshot().First(c => c.Name.EndsWith("one")).FullPath, ignoreCase: true);
    }

    [Fact]
    public async Task RoundTrip_UnusualNames()
    {
        T.File(@"scan\Ärger & quote'ü [1] #2\x.txt", 5);
        T.File(@"scan\日本語\f.txt", 6);
        var s = await Scan.Run(T.Sub("scan"));
        SnapshotStore.Save(SnapFile, s, DateTime.UtcNow);
        TreeAssert.Equal(s.Root!, SnapshotStore.Load(SnapFile).Root);
    }

    [Fact]
    public async Task FromSnapshot_BuildsSnapshotScanner()
    {
        const int MB = 1 << 20;
        T.File(@"scan\big.bin", 2 * MB);
        var s = await Scan.Run(T.Sub("scan"));
        var at = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        SnapshotStore.Save(SnapFile, s, at);

        var snap = Scanner.FromSnapshot(SnapshotStore.Load(SnapFile), SnapFile);
        Assert.True(snap.IsSnapshot);
        Assert.Equal(SnapFile, snap.SnapshotFile);
        Assert.Equal(at, snap.ScannedAt!.Value.ToUniversalTime());
        Assert.Equal(2 * MB, snap.ScanTarget!.Size);
        Assert.Equal(2 * MB, Assert.Single(snap.TopFiles()).Size);
        Assert.NotNull(snap.FindNode(T.Sub("scan")));
    }

    [Fact]
    public async Task Save_ReplacesExistingFile_AndLeavesNoTempFile()
    {
        T.File(@"scan\f.txt", 1);
        var s = await Scan.Run(T.Sub("scan"));
        File.WriteAllText(SnapFile, "old garbage");

        SnapshotStore.Save(SnapFile, s, DateTime.UtcNow);

        Assert.False(File.Exists(SnapFile + ".tmp"));
        Assert.Equal(1, SnapshotStore.Load(SnapFile).Root.Size);
    }

    [Fact]
    public void Save_WithoutScan_Throws() =>
        Assert.Throws<InvalidOperationException>(() => SnapshotStore.Save(SnapFile, new Scanner(), DateTime.UtcNow));

    [Fact]
    public async Task File_IsGzipWithOneJsonLinePerFolder()
    {
        T.File(@"scan\a\f.txt", 1);
        var s = await Scan.Run(T.Sub("scan"));
        SnapshotStore.Save(SnapFile, s, DateTime.UtcNow);

        using var gz = new GZipStream(File.OpenRead(SnapFile), CompressionMode.Decompress);
        var lines = new StreamReader(gz).ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("\"format\":\"DiskTree-Snapshot\"", lines[0]);
        Assert.Contains("\"version\":1", lines[0]);
        // one node line per folder of the tree: drive, the folders down to the scanned one, and 'a'
        int nodes = 0;
        for (var stack = new Stack<Node>(new[] { s.Root! }); stack.Count > 0;)
        {
            nodes++;
            foreach (var c in stack.Pop().Snapshot()) stack.Push(c);
        }
        Assert.Equal(nodes, lines.Count(l => l.Contains("\"k\":\"n\"")));
        Assert.Contains("\"n\":\"a\"", string.Join("\n", lines));
    }

    // ------------------------------------------------------------------ tolerant reading

    private string WriteSnapshot(params string[] lines)
    {
        using (var fs = File.Create(SnapFile))
        using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
        {
            var bytes = new UTF8Encoding(false).GetBytes(string.Join("\n", lines) + "\n");
            gz.Write(bytes);
        }
        return SnapFile;
    }

    private const string Header = "{\"format\":\"DiskTree-Snapshot\",\"version\":1,\"scannedAt\":\"2026-03-04T05:06:07.0000000Z\",\"target\":\"D:\\\\Data\",\"machine\":\"PC1\",\"expected\":123}";

    [Fact]
    public void Read_MissingFieldsDefaultToZero_OldFileWithoutAgeStamp()
    {
        WriteSnapshot(Header,
            "{\"k\":\"n\",\"p\":-1,\"n\":\"D:\\\\\",\"fp\":\"D:\\\\\",\"s\":10}",
            "{\"k\":\"n\",\"p\":0,\"n\":\"Data\",\"s\":10,\"f\":1}");

        var l = SnapshotStore.Load(SnapFile);
        Assert.Equal(AgeStamp.Modified, l.AgeStamp);
        Assert.Equal(123, l.ExpectedBytes);
        Assert.Equal("PC1", l.Info.Machine);
        Assert.Equal(@"D:\Data", l.Target.FullPath);
        Assert.Equal(10, l.Root.Size);
        Assert.Equal(0, l.Root.DirCount);
        Assert.Equal(0, l.Target.OwnSize);
        Assert.Null(l.Target.OwnTypes);
        Assert.Null(l.Target.OwnAges);
        Assert.Null(l.Target.AgeSub);
        Assert.True(l.Target.IsDone);
        Assert.Empty(l.Top);
    }

    [Fact]
    public void Read_UnknownFieldsLinesAndCategories_AreIgnoredOrMapped()
    {
        WriteSnapshot(
            Header.Replace("\"version\":1", "\"version\":99").Replace("}", ",\"future\":{\"a\":1}}"),
            "{\"k\":\"n\",\"p\":-1,\"n\":\"D:\\\\\",\"fp\":\"D:\\\\\",\"s\":7,\"os\":7,\"oc\":1,\"newField\":[1,2,3],\"t\":[[\"Unbekannte Kategorie\",7,1],[\"PDF\",0,0],[\"bad\"]]}",
            "{\"k\":\"somethingNew\",\"x\":1}",
            "",
            "[1,2,3]",
            "{\"k\":\"top\",\"p\":\"D:\\\\f.bin\",\"s\":7}",
            "{\"k\":\"top\",\"s\":7}");

        var l = SnapshotStore.Load(SnapFile);
        Assert.Equal(99, l.Version);
        Assert.Equal(7, l.Root.Size);
        var t = Assert.Single(l.Root.OwnTypes!);
        Assert.Equal(FileTypes.Other, t.Cat); // unknown category name falls back to "Sonstige"
        Assert.Equal(7, t.Size);
        var top = Assert.Single(l.Top);
        Assert.Equal(@"D:\f.bin", top.Path);
        // target "D:\Data" does not exist in this file: falls back to the root
        Assert.Same(l.Root, l.Target);
    }

    [Fact]
    public void Read_CategoriesAreMappedByName_NotByIndex()
    {
        WriteSnapshot(Header,
            "{\"k\":\"n\",\"p\":-1,\"n\":\"D:\\\\\",\"fp\":\"D:\\\\\",\"s\":30,\"t\":[[\"PDF\",10,1],[\"word-dokumente\",20,2]]}");
        var l = SnapshotStore.Load(SnapFile);
        var types = l.Root.OwnTypes!.ToDictionary(x => FileTypes.All[x.Cat].Name);
        Assert.Equal(new TypeStat(Array.FindIndex(FileTypes.All, c => c.Name == "PDF"), 10, 1), types["PDF"]);
        Assert.Equal(20, types["Word-Dokumente"].Size);
        Assert.Equal(2, types["Word-Dokumente"].Count);
    }

    [Fact]
    public void Read_IncompleteAgeArray_IsIgnored()
    {
        WriteSnapshot(Header, "{\"k\":\"n\",\"p\":-1,\"n\":\"D:\\\\\",\"fp\":\"D:\\\\\",\"s\":3,\"ag\":[1,2,3]}");
        var l = SnapshotStore.Load(SnapFile);
        Assert.Null(l.Root.OwnAges);
        Assert.Null(l.Root.AgeSub);
    }

    // ------------------------------------------------------------------ errors

    [Fact]
    public void Load_EmptyFile_IsInvalidData()
    {
        File.WriteAllBytes(SnapFile, Array.Empty<byte>());
        Assert.ThrowsAny<Exception>(() => SnapshotStore.Load(SnapFile));
    }

    [Fact]
    public void Load_EmptyGzip_IsInvalidData()
    {
        WriteSnapshot();
        // one empty line: no header
        Assert.Throws<InvalidDataException>(() => SnapshotStore.Load(SnapFile));
    }

    [Fact]
    public void Load_WrongFormat_IsInvalidData()
    {
        WriteSnapshot("{\"format\":\"Something-Else\",\"version\":1}");
        var ex = Assert.Throws<InvalidDataException>(() => SnapshotStore.Load(SnapFile));
        Assert.Contains("keine DiskTree-Scandatei", ex.Message);
    }

    [Fact]
    public void Load_HeaderOnly_HasNoTree()
    {
        WriteSnapshot(Header);
        var ex = Assert.Throws<InvalidDataException>(() => SnapshotStore.Load(SnapFile));
        Assert.Contains("keinen Ordnerbaum", ex.Message);
    }

    [Fact]
    public void Load_BrokenJson_IsReportedAsDamaged()
    {
        WriteSnapshot(Header, "{\"k\":\"n\",\"p\":-1,\"n\":\"D:\\\\\",\"fp\":\"D:\\\\\"", "{broken");
        var ex = Assert.Throws<InvalidDataException>(() => SnapshotStore.Load(SnapFile));
        Assert.Contains("beschädigt", ex.Message);
    }

    [Fact]
    public void Load_BadParentIndex_IsReportedAsDamaged()
    {
        WriteSnapshot(Header,
            "{\"k\":\"n\",\"p\":-1,\"n\":\"D:\\\\\",\"fp\":\"D:\\\\\"}",
            "{\"k\":\"n\",\"p\":5,\"n\":\"x\"}");
        Assert.Throws<InvalidDataException>(() => SnapshotStore.Load(SnapFile));
    }

    [Fact]
    public void Load_PlainTextFile_Fails()
    {
        File.WriteAllText(SnapFile, "this is not gzip at all");
        Assert.ThrowsAny<Exception>(() => SnapshotStore.Load(SnapFile));
    }

    [Fact]
    public void Load_MissingFile_Fails() =>
        Assert.Throws<FileNotFoundException>(() => SnapshotStore.Load(Path.Combine(T.Path, "nope.disktree")));

    [Fact]
    public async Task FindByPath_FindsCaseInsensitive_NullOtherwise()
    {
        T.File(@"scan\Aa\Bb\f.txt", 1);
        var s = await Scan.Run(T.Sub("scan"));
        Assert.NotNull(SnapshotStore.FindByPath(s.Root!, T.Sub("scan", "aa", "BB")));
        Assert.Null(SnapshotStore.FindByPath(s.Root!, T.Sub("scan", "aa", "zz")));
        Assert.Null(SnapshotStore.FindByPath(s.Root!, @"Z:\other"));
    }
}
