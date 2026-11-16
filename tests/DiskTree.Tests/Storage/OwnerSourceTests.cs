using DiskTree.Core.Storage;
using DiskTree.Features.Owner;
using DiskTree.Features.Storage.S3;
using Xunit;

namespace DiskTree.Tests.Storage;

/// <summary>The "Besitzer" tab behind IOwnerSource: the disk unchanged, POSIX owners for sources that know them.</summary>
public class OwnerSourceTests : TestBase
{
    [Theory]
    [InlineData(0x4000 | 0x1E8, "drwxr-x---", "0750")]
    [InlineData(0x8000 | 0x1A4, "-rw-r--r--", "0644")]
    [InlineData(0xA000 | 0x1FF, "lrwxrwxrwx", "0777")]
    [InlineData(0x8000 | 0x800 | 0x1ED, "-rwsr-xr-x", "4755")]
    [InlineData(0x8000 | 0x800 | 0x1A4, "-rwSr--r--", "4644")]
    [InlineData(0x4000 | 0x400 | 0x1F8, "drwxrws---", "2770")]
    [InlineData(0x4000 | 0x200 | 0x1FF, "drwxrwxrwt", "1777")]
    [InlineData(0x4000 | 0x200 | 0x1FE, "drwxrwxrwT", "1776")]
    [InlineData(0x8000, "----------", "0000")]
    public void Posix_mode_reads_like_ls_and_chmod(int mode, string text, string octal)
    {
        Assert.Equal(text, PosixMode.Format(mode));
        Assert.Equal(octal, PosixMode.Octal(mode));
    }

    [Fact]
    public void Posix_rights_are_described_in_words()
    {
        var lines = PosixMode.Describe(0x4000 | 0x400 | 0x1E8, "pascal", "users");
        Assert.Equal("drwxr-s---  pascal:users  (2750)", lines[0]);
        Assert.Equal("Besitzer pascal: auflisten, anlegen/löschen, betreten", lines[1]);
        Assert.Equal("Gruppe users: auflisten, betreten", lines[2]);
        Assert.Equal("Andere: keine Rechte", lines[3]);
        Assert.Contains(lines, l => l.StartsWith("setgid"));
        Assert.Equal("Besitzer a: lesen, schreiben", PosixMode.Describe(0x8000 | 0x180, "a", "b")[1]);
    }

    [Fact]
    public void Owner_tab_and_file_actions_need_the_caps_and_the_interface()
    {
        var store = new FakeStore();
        Assert.True(StorageFeatures.Has(store, StorageFeature.Owner));
        Assert.True(StorageFeatures.Has(store, StorageFeature.Delete));
        Assert.True(StorageFeatures.Has(store, StorageFeature.Move));
        Assert.True(StorageFeatures.Has(store, StorageFeature.Rename));
        Assert.True(StorageFeatures.IsRemoteWritable(store));
        // Never what uses the Windows shell / System.IO, and no recycle bin.
        Assert.False(StorageFeatures.Has(store, StorageFeature.Trash));
        Assert.False(StorageFeatures.Has(store, StorageFeature.Zip));
        Assert.False(StorageFeatures.Has(store, StorageFeature.ShellMenu));
        Assert.False(StorageFeatures.Has(store, StorageFeature.Explorer));
        Assert.Contains("endgültig", StorageFeatures.WhyNot(store, StorageFeature.Trash));
        Assert.Same(store, OwnerSources.For(store));

        // The interface without the caps does not unlock anything.
        var noCaps = new FakeStore { Caps = StorageCaps.CaseSensitive | StorageCaps.Hierarchical };
        Assert.False(StorageFeatures.Has(noCaps, StorageFeature.Owner));
        Assert.False(StorageFeatures.Has(noCaps, StorageFeature.Delete));
        Assert.False(StorageFeatures.IsRemoteWritable(noCaps));
        Assert.Null(OwnerSources.For(noCaps));

        // The disk: its own Windows owners, recycle bin, not "remote writable".
        Assert.Same(DiskOwnerSource.Instance, OwnerSources.For(DiskProvider.Instance));
        Assert.False(StorageFeatures.IsRemoteWritable(DiskProvider.Instance));
        Assert.Null(StorageFeatures.WhyNot(DiskProvider.Instance, StorageFeature.Trash));
    }

    [Fact]
    public void S3_stays_without_owner_and_file_actions()
    {
        var s3 = S3Provider.Generic;
        foreach (var f in new[] { StorageFeature.Owner, StorageFeature.Delete, StorageFeature.Move, StorageFeature.Rename, StorageFeature.Trash })
            Assert.False(StorageFeatures.Has(s3, f), f.ToString());
        Assert.Null(OwnerSources.For(s3));
        Assert.False(StorageFeatures.IsRemoteWritable(s3));
        Assert.Contains("nur lesender Zugriff", StorageFeatures.WhyNot(s3, StorageFeature.Delete));
    }

    [Fact]
    public void Disk_owner_source_sums_like_the_aggregator()
    {
        T.File("a.bin", 3000);
        T.File("sub\\b.bin", 5000);
        var r = DiskOwnerSource.Instance.Aggregate(T.Path, 0, null, CancellationToken.None);
        var direct = OwnerAggregator.Aggregate(OwnerAggregator.EnumerateFiles(T.Path),
            p => OwnerInfo.GetOwnerSid(p) is { } s ? OwnerInfo.ResolveName(s) : null, 0, 8, null, CancellationToken.None);
        Assert.Equal(direct.Rows, r.Rows);
        Assert.Equal(8000, r.TotalBytes);
        Assert.False(r.FailedAreFolders);
        Assert.Null(r.GroupRows);
        Assert.Same(r.Rows, r.RowsBy(OwnerGrouping.Group)); // the disk has no groups: by user
        Assert.Equal("Rechte (ACL)", DiskOwnerSource.Instance.RightsTitle);
        Assert.True(DiskOwnerSource.Instance.SamplingHelps);
    }

    [Fact]
    public async Task Fake_posix_owners_by_user_and_group()
    {
        var p = new FakeStore();
        p.File("/d/a.bin", 100).File("/d/x/b.bin", 200).File("/d/x/c.bin", 400).Folder("/d/leer");
        p.Own("/d/x/b.bin", 1001, 1000).Own("/d/x/c.bin", 1002, 50).Own("/d/x", 1001, 1000, 0x1E8);
        p.Users[1001] = "anna";

        var r = p.Aggregate("/d", 0, null, CancellationToken.None);
        Assert.Equal(["uid 1002", "anna", "pascal"], r.Rows.Select(x => x.Owner));
        Assert.Equal(["gid 50", "users"], r.RowsBy(OwnerGrouping.Group).Select(x => x.Owner));
        Assert.Equal(300, r.RowsBy(OwnerGrouping.Group)[1].Bytes);
        Assert.Equal(3, r.Seen);
        Assert.True(r.FailedAreFolders);

        var o = await p.GetOwnerAsync("/d/x", CancellationToken.None);
        Assert.Equal("anna", o!.Owner);
        Assert.Equal("users", o.Group);
        Assert.Equal("drwxr-x---  anna:users  (0750)", o.Permissions);
        Assert.Null(await p.GetOwnerAsync("/d/gibtsnicht", CancellationToken.None));
    }
}
