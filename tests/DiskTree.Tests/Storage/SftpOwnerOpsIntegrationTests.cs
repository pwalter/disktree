using System.IO;
using DiskTree.Core;
using DiskTree.Core.Storage;
using DiskTree.Features.FileOperations;
using DiskTree.Features.Storage.Sftp;
using Xunit;

namespace DiskTree.Tests.Storage;

/// <summary>
/// Owners, rights and file actions against a real SFTP server (own atmoz/sftp container, see <see cref="SftpOpsServer"/>):
/// names by getent, from /etc files in the chroot or not at all; delete for good (links never followed), rename
/// (case-sensitive), move with conflicts, errors for missing rights, cancel; the tree afterwards equals a fresh scan.
/// </summary>
[Collection(SftpOpsCollection.Name)]
public class SftpOwnerOpsIntegrationTests
{
    private readonly SftpOpsServer _s;

    public SftpOwnerOpsIntegrationTests(SftpOpsServer server) => _s = server;

    /// <summary>Files of three owners and two groups, a link, and a folder tester cannot read.</summary>
    private string OwnerData()
    {
        var name = _s.NewFolder("own");
        _s.Sh($"""
            set -e
            cd /home/tester/data/{name}
            mkdir sub geheim
            head -c 1000 /dev/zero > a.bin;  chown 1001:100 a.bin
            head -c 3000 /dev/zero > b.bin;  chown 1002:100 b.bin
            head -c 5000 /dev/zero > sub/c.bin; chown 1004:1005 sub/c.bin
            ln -s ../a.bin sub/link; chown -h 1001:100 sub/link
            head -c 700 /dev/zero > geheim/x.bin
            chown 1001:1005 sub; chmod 2755 sub
            chown 0:0 geheim; chmod 700 geheim
            """);
        return name;
    }

    [SftpFact]
    public void Owner_names_from_the_etc_files_of_the_chroot_when_exec_is_not_allowed()
    {
        var name = OwnerData();
        using var p = _s.Provider("tester");
        var r = p.Aggregate(_s.Uri("tester", "/data/" + name), 0, null, CancellationToken.None);

        Assert.Null(r.Error);
        Assert.False(r.Cancelled);
        Assert.Equal(3, r.Seen);                // the link is not counted
        Assert.Equal(1, r.Failed);              // "geheim"
        Assert.True(r.FailedAreFolders);
        Assert.Equal([("uid 1004", 5000L), ("anna", 3000L), ("tester", 1000L)], r.Rows.Select(x => (x.Owner, x.Bytes)));
        Assert.Equal([("gid 1005", 5000L), ("users", 4000L)], r.RowsBy(OwnerGrouping.Group).Select(x => (x.Owner, x.Bytes)));
        Assert.Equal(OwnerNameSource.EtcFiles, p.OwnerNames().Source);
    }

    [SftpFact]
    public async Task Owner_names_by_getent_over_exec()
    {
        var name = OwnerData();
        using var p = _s.Provider("shelly");
        var r = p.Aggregate(_s.Uri("shelly", $"/home/tester/data/{name}"), 0, null, CancellationToken.None);

        Assert.Equal(OwnerNameSource.Getent, p.OwnerNames().Source);
        Assert.Equal([("uid 1004", 5000L), ("anna", 3000L), ("tester", 1000L)], r.Rows.Select(x => (x.Owner, x.Bytes)));
        Assert.Equal([("projekt", 5000L), ("users", 4000L)], r.RowsBy(OwnerGrouping.Group).Select(x => (x.Owner, x.Bytes)));

        var o = await p.GetOwnerAsync(_s.Uri("shelly", $"/home/tester/data/{name}/sub"), CancellationToken.None);
        Assert.Equal("tester (uid 1001)", o!.Owner);
        Assert.Equal("projekt (gid 1005)", o.Group);
        Assert.Equal("drwxr-sr-x  tester:projekt  (2755)", o.Permissions);
        var rights = p.GetRights(_s.Uri("shelly", $"/home/tester/data/{name}/sub"), CancellationToken.None);
        Assert.Equal("drwxr-sr-x  tester:projekt  (2755)", rights[0]);
        Assert.Contains("Gruppe projekt: auflisten, betreten", rights);
        Assert.Contains("Namen: getent auf dem Server", rights);
    }

    [SftpFact]
    public async Task Without_exec_and_without_etc_files_the_ids_are_shown()
    {
        _s.Sh("""
            set -e
            cd /home/anna/data
            head -c 100 /dev/zero > eigen.txt; chown 1002:100 eigen.txt
            head -c 200 /dev/zero > fremd.txt; chown 1001:1005 fremd.txt
            """);
        using var p = _s.Provider("anna");
        var r = p.Aggregate(_s.Uri("anna", "/data"), 0, null, CancellationToken.None);
        Assert.Equal(OwnerNameSource.Unavailable, p.OwnerNames().Source);
        Assert.Equal([("uid 1001", 200L), ("uid 1002", 100L)], r.Rows.Select(x => (x.Owner, x.Bytes)));
        Assert.Equal(["gid 1005", "gid 100"], r.RowsBy(OwnerGrouping.Group).Select(x => x.Owner));

        var o = await p.GetOwnerAsync(_s.Uri("anna", "/data/fremd.txt"), CancellationToken.None);
        Assert.Equal("uid 1001", o!.Owner);
        Assert.Equal("-rw-r--r--  uid 1001:gid 1005  (0644)", o.Permissions);
        Assert.Contains(p.GetRights(_s.Uri("anna", "/data/fremd.txt"), CancellationToken.None), l => l.Contains("nicht ermittelbar"));
    }

    [SftpFact]
    public void Owner_analysis_of_a_missing_folder_is_an_error_not_a_crash()
    {
        using var p = _s.Provider("tester");
        var r = p.Aggregate(_s.Uri("tester", "/data/gibt-es-nicht"), 0, null, CancellationToken.None);
        Assert.NotNull(r.Error);
        Assert.Empty(r.Rows);
    }

    // ------------------------------------------------------------------ file actions

    [SftpFact]
    public void Delete_removes_a_link_never_its_target()
    {
        var name = _s.NewFolder("link");
        _s.Sh($"""
            set -e
            cd /home/tester/data/{name}
            mkdir drin
            echo wichtig > /home/tester/data/{name}-ziel.txt; chown 1001:100 /home/tester/data/{name}-ziel.txt
            mkdir /home/tester/data/{name}-zielordner; echo x > /home/tester/data/{name}-zielordner/bleibt.txt
            chown -R 1001:100 /home/tester/data/{name}-zielordner
            ln -s ../{name}-ziel.txt link_datei
            ln -s ../../{name}-zielordner drin/link_ordner
            ln -s /nirgendwo drin/kaputt
            chown -h 1001:100 link_datei drin/link_ordner drin/kaputt; chown 1001:100 drin
            """);
        using var p = _s.Provider("tester");
        var info = p.Stat(_s.Uri("tester", $"/data/{name}/link_datei"), CancellationToken.None);
        Assert.True(info!.Value.IsLink);   // lstat: the link itself

        var o1 = p.Delete(_s.Uri("tester", $"/data/{name}/link_datei"), false, null, CancellationToken.None);
        var o2 = p.Delete(_s.Uri("tester", $"/data/{name}/drin"), true, null, CancellationToken.None);

        Assert.True(o1.Complete);
        Assert.True(o2.Complete, string.Join("; ", o2.Failed));
        Assert.Equal(1, o2.Folders);
        Assert.Equal(2, o2.Files);   // two links
        Assert.False(_s.ExistsInContainer($"/home/tester/data/{name}/link_datei"));
        Assert.False(_s.ExistsInContainer($"/home/tester/data/{name}/drin"));
        Assert.True(_s.ExistsInContainer($"/home/tester/data/{name}-ziel.txt"));
        Assert.True(_s.ExistsInContainer($"/home/tester/data/{name}-zielordner/bleibt.txt"));
    }

    [SftpFact]
    public async Task Delete_rename_move_against_the_server_tree_equals_a_fresh_scan()
    {
        var name = _s.NewFolder("ops");
        _s.Sh($"""
            set -e
            cd /home/tester/data/{name}
            mkdir -p a/tief b leer
            head -c 2000000 /dev/zero > a/x.bin
            head -c 50 /dev/zero > a/y.txt
            head -c 3000000 /dev/zero > a/tief/z.iso
            head -c 10 /dev/zero > readme.txt
            echo klein > klein.txt
            echo GROSS > GROSS.txt
            head -c 1 /dev/zero > b/1.txt
            touch -d "2019-05-01 10:00:00" a/y.txt
            chown -R 1001:100 .
            """);
        using var source = _s.Provider("tester");
        var root = _s.Uri("tester", "/data/" + name);
        var s = new Scanner { Provider = source };
        await s.ScanAsync(root, 2, CancellationToken.None);
        var paths = source.Paths;
        OpItem Item(string rel, bool dir, long size = 0) => new(paths.Join(root, rel), dir, size, -1, paths);
        List<OpItem> Fresh(params OpItem[] items) => RemoteFileOperations.Refresh(source, items, s.Ages, new OpResult(paths.Comparer), null, CancellationToken.None);

        // Rename: case only (a new name on a case-sensitive server), a folder, a name that exists with other case.
        var rules = new RenameRules(paths, new Dictionary<string, IReadOnlyCollection<string>> { [root] = source.ListNames(root, CancellationToken.None) });
        var plans = new List<RenamePlan>
        {
            RemoteFileOperations.PlanSingle(Fresh(Item("klein.txt", false, 6))[0], "Klein.txt", rules)[0],
            RemoteFileOperations.PlanSingle(Fresh(Item("a", true))[0], "A", rules)[0],
        };
        Assert.All(plans, x => Assert.True(x.Ok, x.Problem));
        Assert.Equal("Name existiert schon", RemoteFileOperations.PlanSingle(Item("klein.txt", false), "GROSS.txt", rules)[0].Problem);
        var renamed = RemoteFileOperations.Rename(source, plans, CancellationToken.None);
        Assert.Equal(2, renamed.Done.Count);
        TreeUpdate.Renamed(s, renamed);
        Assert.True(_s.ExistsInContainer($"/home/tester/data/{name}/Klein.txt"));
        Assert.False(_s.ExistsInContainer($"/home/tester/data/{name}/klein.txt"));
        await AssertSameAsFreshScan(source, s, root);

        // Move into a folder of the tree: a file (counted in) and a folder (the destination is scanned again).
        var moved = RemoteFileOperations.Move(source, paths, Fresh(Item("readme.txt", false, 10), Item("A/tief", true)),
            paths.Join(root, "b"), ConflictPolicy.Ask, null, CancellationToken.None);
        Assert.Equal(2, moved.Done.Count);
        TreeUpdate.Removed(s, moved.Done, movedTo: moved.NewPaths);
        var again = TreeUpdate.Arrived(s, paths.Join(root, "b"), moved);
        Assert.NotNull(again);
        await s.RescanAsync(again!, 2, CancellationToken.None);
        await AssertSameAsFreshScan(source, s, root);

        // Conflict: replace a file (posix-rename, atomic) and keep both.
        _s.Sh($"cd /home/tester/data/{name} && echo alt > b/y.txt && echo neu > y2.txt && chown 1001:100 b/y.txt y2.txt");
        await s.RescanAsync(s.FindNode(root)!, 2, CancellationToken.None);
        var conflict = RemoteFileOperations.Move(source, paths, Fresh(Item("A/y.txt", false, 50)), paths.Join(root, "b"),
            ConflictPolicy.Replace, null, CancellationToken.None);
        Assert.Single(conflict.Done);
        Assert.True(conflict.Replaced);
        Assert.Equal("50", _s.Sh($"stat -c %s /home/tester/data/{name}/b/y.txt").Trim());

        // Delete for good (confirmed plan): a folder with content and a file.
        var plan = new PermanentDeletePlan(Fresh(Item("A", true), Item("GROSS.txt", false, 6)), source.DisplayName,
            i => s.FindNode(i.Path) is { } n ? (n.FileCount, n.DirCount, n.Size) : null);
        Assert.Equal(3, plan.Files);   // A/x.bin (y.txt moved, tief moved), GROSS.txt … as the scan knows them
        plan.Confirm();
        var deleted = RemoteFileOperations.Delete(source, plan, null, CancellationToken.None);
        Assert.Equal(2, deleted.Done.Count);
        Assert.False(_s.ExistsInContainer($"/home/tester/data/{name}/A"));
        await s.RescanAsync(s.FindNode(root)!, 2, CancellationToken.None);   // after the replace above
        await AssertSameAsFreshScan(source, s, root);
    }

    [SftpFact]
    public async Task Missing_rights_are_collected_per_entry_the_rest_goes()
    {
        var name = _s.NewFolder("rechte");
        _s.Sh($"""
            set -e
            cd /home/tester/data/{name}
            mkdir -p frei gesperrt
            echo 1 > frei/a.txt; echo 2 > frei/b.txt
            echo 3 > gesperrt/c.txt; echo 4 > gesperrt/d.txt
            chown -R 1001:100 .
            chmod 555 gesperrt
            echo r > root.txt; chown 0:0 root.txt; chmod 644 root.txt
            chown 0:0 .; chmod 755 .
            """);
        using var source = _s.Provider("tester");
        var root = _s.Uri("tester", "/data/" + name);
        var s = new Scanner { Provider = source };
        await s.ScanAsync(root, 2, CancellationToken.None);
        var paths = source.Paths;
        OpItem Item(string rel, bool dir, long size = 0) => new(paths.Join(root, rel), dir, size, -1, paths);
        var items = RemoteFileOperations.Refresh(source, [Item("frei", true), Item("gesperrt", true), Item("root.txt", false, 2)], s.Ages,
            new OpResult(paths.Comparer), null, CancellationToken.None);
        var plan = new PermanentDeletePlan(items, "T");
        plan.Confirm();

        var res = RemoteFileOperations.Delete(source, plan, null, CancellationToken.None);

        // "frei" lies in a folder owned by root (755): its content goes, the folder itself cannot be removed.
        Assert.Empty(res.Done);
        Assert.Contains(res.Failed, f => f.Path.EndsWith("/gesperrt/c.txt") && f.Error.Contains("Kein Zugriff"));
        Assert.Contains(res.Failed, f => f.Path.EndsWith("/root.txt"));
        Assert.Contains(res.Failed, f => f.Path.EndsWith("/frei"));
        Assert.Equal(["frei"], res.Partial.Select(i => i.Name));
        Assert.False(_s.ExistsInContainer($"/home/tester/data/{name}/frei/a.txt"));
        Assert.True(_s.ExistsInContainer($"/home/tester/data/{name}/gesperrt/c.txt"));

        TreeUpdate.Removed(s, res.Done);
        foreach (var part in res.Partial) await s.RescanAsync(s.FindNode(part.Path)!, 2, CancellationToken.None);
        await AssertSameAsFreshScan(source, s, root);
    }

    [SftpFact]
    public void Cancel_stops_a_recursive_delete_between_entries()
    {
        var name = _s.NewFolder("abbruch");
        _s.Sh($"cd /home/tester/data/{name} && mkdir viel && i=0; while [ $i -lt 300 ]; do echo $i > viel/f$i.txt; i=$((i+1)); done; chown -R 1001:100 .");
        using var p = _s.Provider("tester");
        using var cts = new CancellationTokenSource();
        long seen = 0;
        Assert.ThrowsAny<OperationCanceledException>(() => p.Delete(_s.Uri("tester", $"/data/{name}/viel"), true, o =>
        {
            seen = o.Files;
            if (o.Files >= 20) cts.Cancel();
        }, cts.Token));
        int left = int.Parse(_s.Sh($"ls /home/tester/data/{name}/viel | wc -l").Trim());
        Assert.InRange(left, 1, 299);
        Assert.True(seen >= 20);
        Assert.True(_s.ExistsInContainer($"/home/tester/data/{name}/viel"));
    }

    [SftpFact]
    public void Rename_and_move_never_replace_without_being_asked()
    {
        var name = _s.NewFolder("ziel");
        _s.Sh($"cd /home/tester/data/{name} && mkdir d && echo eins > a.txt && echo zwei > d/a.txt && echo drei > b.txt && chown -R 1001:100 .");
        using var p = _s.Provider("tester");
        var root = _s.Uri("tester", "/data/" + name);
        Assert.Throws<IOException>(() => p.Rename(root + "/a.txt", "b.txt", CancellationToken.None));
        Assert.Throws<IOException>(() => p.Move(root + "/a.txt", root + "/d", null, false, CancellationToken.None));
        Assert.Throws<IOException>(() => p.Move(root + "/d", root + "/d", null, false, CancellationToken.None));
        Assert.Equal("eins", _s.Sh($"cat /home/tester/data/{name}/a.txt").Trim());
        Assert.Equal("zwei", _s.Sh($"cat /home/tester/data/{name}/d/a.txt").Trim());
        Assert.Equal("drei", _s.Sh($"cat /home/tester/data/{name}/b.txt").Trim());

        // With replace (file over file) it is atomic and the content is the moved one.
        Assert.Equal(root + "/d/a.txt", p.Move(root + "/a.txt", root + "/d", null, true, CancellationToken.None));
        Assert.Equal("eins", _s.Sh($"cat /home/tester/data/{name}/d/a.txt").Trim());

        p.CreateDirectory(root + "/neu", CancellationToken.None);
        Assert.True(_s.ExistsInContainer($"/home/tester/data/{name}/neu"));
        Assert.Contains("neu", p.ListNames(root, CancellationToken.None));
    }

    // ------------------------------------------------------------------ helpers

    private static async Task AssertSameAsFreshScan(IStorageProvider p, Scanner s, string root)
    {
        var fresh = new Scanner { Provider = p };
        await fresh.ScanAsync(root, 2, CancellationToken.None);
        var actual = s.FindNode(root)!;
        TreeAssert.Equal(fresh.FindNode(root)!, actual);
        var paths = p.Paths;
        Assert.Equal(fresh.TopFiles().Where(f => paths.IsUnder(root, f.Path)).Select(f => (f.Path, f.Size)),
            s.TopFiles().Where(f => paths.IsUnder(root, f.Path)).Select(f => (f.Path, f.Size)));
    }
}
