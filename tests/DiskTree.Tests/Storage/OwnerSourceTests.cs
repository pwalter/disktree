using DiskTree.Core.Storage;
using DiskTree.Features.Owner;
using DiskTree.Features.Storage.S3;
using DiskTree.Features.Storage.Sftp;
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
    public void Sftp_sources_get_owners_rights_and_file_actions_but_no_trash_zip_or_shell()
    {
        var sftp = SftpSource.Generic;
        foreach (var f in new[] { StorageFeature.Owner, StorageFeature.Delete, StorageFeature.Move, StorageFeature.Rename })
            Assert.True(StorageFeatures.Has(sftp, f), f.ToString());
        Assert.True(sftp.Caps.HasFlag(StorageCaps.PosixPermissions));
        foreach (var f in new[] { StorageFeature.Trash, StorageFeature.Zip, StorageFeature.ShellMenu, StorageFeature.Explorer })
            Assert.False(StorageFeatures.Has(sftp, f), f.ToString());
        Assert.Contains("keinen Papierkorb", StorageFeatures.WhyNot(sftp, StorageFeature.Trash));
        Assert.Same(sftp, OwnerSources.For(sftp));
        Assert.True(StorageFeatures.IsRemoteWritable(sftp));
        Assert.Equal("Rechte (POSIX)", sftp.RightsTitle);
        Assert.False(sftp.SamplingHelps);
        // The read part did not change.
        Assert.Equal(SftpSource.SftpCaps, sftp.Caps & ~SftpSource.WriteCaps);
    }

    [Fact]
    public void Owner_names_of_passwd_group_and_getent_output()
    {
        var passwd = "root:x:0:0:root:/root:/bin/sh\n# Kommentar\n\ntester:x:1001:100::/home/tester:\nkaputt\nanna:x:1002:100:Anna A.:/home/anna:/bin/sh\r\nanna2:x:1002:100::/:/bin/sh\n";
        Assert.Equal([(0, "root"), (1001, "tester"), (1002, "anna"), (1002, "anna2")], SftpOwnerNames.Parse(passwd));
        Assert.False(SftpOwnerNames.LooksValid("This service allows sftp connections only.\n"));
        Assert.False(SftpOwnerNames.LooksValid(null));

        var names = new SftpOwnerNames();
        names.Load(OwnerNameSource.Getent, passwd, "users:x:100:\nadm:x:4:root\n");
        Assert.Equal("anna", names.User(1002)); // the first name of an id wins
        Assert.Equal("uid 1004", names.User(1004));
        Assert.Equal("users (gid 100)", names.GroupWithId(100));
        Assert.Equal("gid 7", names.GroupWithId(7));
        Assert.Equal("tester (uid 1001)", names.UserWithId(1001));

        // Missing ids are asked for once.
        var (u, g) = names.TakeMissing([1001, 1004, 1004, 1005], [100, 7]);
        Assert.Equal([1004, 1005], u);
        Assert.Equal([7], g);
        Assert.Equal((Array.Empty<int>(), Array.Empty<int>()), names.TakeMissing([1004], [7]));
        names.Add("ldapuser:*:1004:100::/:/bin/sh\n", null);
        Assert.Equal("ldapuser", names.User(1004));
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
