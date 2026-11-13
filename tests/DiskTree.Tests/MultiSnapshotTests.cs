using System.IO;
using System.IO.Compression;
using System.Text;
using DiskTree.Core;
using DiskTree.Core.Snapshots;
using Xunit;

namespace DiskTree.Tests;

public class MultiSnapshotTests : TestBase
{
    private string SnapFile => Path.Combine(T.Path, "multi.disktree");

    [Fact]
    public async Task RoundTrip_MultiScan_StaysMulti_AndFindsNodes()
    {
        T.File(@"one\a.txt", 100);
        T.File(@"one\sub\b.txt", 200);
        T.File(@"two\c.docx", 400);
        var s = new Scanner();
        await s.ScanManyAsync(new[] { T.Sub("one"), T.Sub("two") }, 2, CancellationToken.None);
        SnapshotStore.Save(SnapFile, s, DateTime.UtcNow);

        var loaded = SnapshotStore.Load(SnapFile);
        Assert.True(loaded.IsMulti);
        var snap = Scanner.FromSnapshot(loaded);
        Assert.True(snap.IsMulti);
        Assert.Same(snap.Root, snap.FindNode(""));
        Assert.Equal(300, snap.FindNode(T.Sub("one"))!.Size);
        Assert.Equal(200, snap.FindNode(T.Sub("one", "sub"))!.Size);
        Assert.Equal(400, snap.FindNode(T.Sub("two"))!.Size);
        Assert.Null(snap.FindNode(T.Sub("three")));
        Assert.Equal(700, snap.Root!.Size);
    }

    [Fact]
    public async Task SingleScan_IsNotMulti_AndWritesNoMultiField()
    {
        T.File(@"scan\f.txt", 5);
        var s = await Scan.Run(T.Sub("scan"));
        SnapshotStore.Save(SnapFile, s, DateTime.UtcNow);

        Assert.False(SnapshotStore.Load(SnapFile).IsMulti);
        using var gz = new GZipStream(File.OpenRead(SnapFile), CompressionMode.Decompress);
        using var r = new StreamReader(gz, Encoding.UTF8);
        Assert.DoesNotContain("multi", r.ReadLine());
    }

    [Fact]
    public void OldFileWithoutField_LoadsAsNotMulti_AndUnknownValueIsTolerated()
    {
        void Write(string header)
        {
            using var fs = File.Create(SnapFile);
            using var gz = new GZipStream(fs, CompressionLevel.Fastest);
            gz.Write(new UTF8Encoding(false).GetBytes(header + "\n{\"k\":\"n\",\"p\":-1,\"n\":\"D:\\\\\",\"fp\":\"D:\\\\\",\"s\":1}\n"));
        }
        const string h = "{\"format\":\"DiskTree-Snapshot\",\"version\":1,\"scannedAt\":\"2026-03-04T05:06:07.0000000Z\",\"target\":\"D:\\\\\"";
        Write(h + "}");
        Assert.False(SnapshotStore.Load(SnapFile).IsMulti);
        Write(h + ",\"multi\":\"weird\"}");
        Assert.False(SnapshotStore.Load(SnapFile).IsMulti);
        Write(h + ",\"multi\":true}");
        Assert.True(SnapshotStore.Load(SnapFile).IsMulti);
    }
}
