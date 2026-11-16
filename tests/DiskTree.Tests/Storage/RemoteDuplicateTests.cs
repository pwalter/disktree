using System.IO;
using System.IO.Enumeration;
using DiskTree.Core.Storage;
using DiskTree.Features.Duplicates;
using Xunit;

namespace DiskTree.Tests.Storage;

/// <summary>
/// The duplicate search over a storage source other than the disk (FakeContentProvider: byte ranges, content tags like
/// S3 ETags): listing, both modes, the estimate, the download limit, request counting and cancelling. Plus: the disk
/// lists exactly what it listed before.
/// </summary>
public class RemoteDuplicateTests : TestBase
{
    private const int Block = 64 * 1024;

    private static byte[] Bytes(int length, int seed)
    {
        var b = new byte[length];
        new Random(seed).NextBytes(b);
        return b;
    }

    /// <summary>
    /// /d/a/one.bin = /d/b/two.bin = /d/three.bin (300 KB), /d/other.bin same size other content,
    /// /d/mid1.bin and /d/mid2.bin same first and last 64 KB but another middle (400 KB), /d/s1.bin = /d/s2.bin (50 KB),
    /// /d/s3.bin same size as those but one byte different, /d/unique.bin.
    /// </summary>
    private static T Fill<T>(T p) where T : FakeProvider
    {
        var big = Bytes(300_000, 1);
        p.File("/d/a/one.bin", big).File("/d/b/two.bin", big).File("/d/three.bin", big);
        p.File("/d/other.bin", Bytes(300_000, 2));
        var mid = Bytes(400_000, 3);
        var mid2 = (byte[])mid.Clone();
        mid2[200_000] ^= 0xFF;
        p.File("/d/mid1.bin", mid).File("/d/mid2.bin", mid2);
        var small = Bytes(50_000, 4);
        var small2 = (byte[])small.Clone();
        small2[25_000] ^= 1;
        p.File("/d/s1.bin", small).File("/d/s2.bin", small).File("/d/s3.bin", small2);
        p.File("/d/unique.bin", Bytes(777, 5));
        return p;
    }

    private static async Task<(List<DuplicateGroup> Groups, DuplicateFinder Finder, DupCandidates Candidates, ListRun Run)> Run(
        IStorageProvider p, DupMode mode, string root = "/d", DownloadBudget? budget = null, CancellationToken ct = default,
        long requestLimit = 0, Action<ListRun>? limitReached = null)
    {
        var run = new ListRun(ct, requestLimit);
        if (limitReached != null) run.LimitReached += limitReached;
        var c = await DuplicateFinder.CollectAsync(p, root, 1, run, null);
        var finder = new DuplicateFinder();
        var groups = await finder.FindAsync(c, mode, 3, run, budget, null);
        return (groups, finder, c, run);
    }

    private static string[] Sorted(DuplicateGroup g) => g.Paths.Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public async Task Thorough_finds_exactly_the_identical_files_and_marks_them_certain()
    {
        var p = Fill(new FakeContentProvider());
        var (groups, finder, c, _) = await Run(p, DupMode.Thorough);

        Assert.Equal(0, finder.Errors);
        Assert.Equal(3, c.SameSize.Count); // 300 KB, 400 KB, 50 KB
        Assert.Equal(2, groups.Count);
        Assert.Equal(new[] { "/d/a/one.bin", "/d/b/two.bin", "/d/three.bin" }, Sorted(groups[0]));
        Assert.Equal(600_000, groups[0].Wasted);
        Assert.Equal(new[] { "/d/s1.bin", "/d/s2.bin" }, Sorted(groups[1]));
        Assert.All(groups, g => { Assert.Equal(DupBasis.FullHash, g.Basis); Assert.True(g.IsCertain); });

        // Partial hashes are two range reads of one block for big files, one read of the whole small file; the full hash
        // reads only the files still equal after that (the three copies and mid1/mid2), never other.bin or s3.bin twice.
        var reads = p.Reads;
        Assert.DoesNotContain(reads, r => r.Path == "/d/other.bin" && r.Length > Block);
        Assert.Contains(reads, r => r.Path == "/d/mid1.bin" && r.Length == 400_000);
        Assert.Equal(1, reads.Count(r => r.Path == "/d/s3.bin"));
        Assert.Equal(finder.BytesRead, p.BytesServed);
    }

    [Fact]
    public async Task Fast_without_tags_compares_only_the_ends_and_marks_big_files_probable()
    {
        var p = Fill(new FakeContentProvider());
        var (groups, _, _, _) = await Run(p, DupMode.Fast);

        // Without a full download mid1/mid2 (same ends, other middle) look the same: probable, not certain.
        Assert.Equal(3, groups.Count);
        var three = groups.Single(g => g.Paths.Length == 3);
        Assert.Equal(DupBasis.PartialHash, three.Basis);
        Assert.False(three.IsCertain);
        var mid = groups.Single(g => g.Paths.Contains("/d/mid1.bin"));
        Assert.Equal(new[] { "/d/mid1.bin", "/d/mid2.bin" }, Sorted(mid));
        Assert.False(mid.IsCertain);
        // Small files are read completely: certain even in the fast mode.
        var small = groups.Single(g => g.Paths.Contains("/d/s1.bin"));
        Assert.Equal(DupBasis.FullHash, small.Basis);
        Assert.True(small.IsCertain);

        Assert.All(p.Reads, r => Assert.True(r.Length <= Block || r.Path.StartsWith("/d/s"), $"{r.Path} {r.Length}"));
    }

    [Fact]
    public async Task Strong_tags_decide_without_any_download()
    {
        var p = new FakeDeepProvider();
        var x = Bytes(300_000, 1);
        p.File("/d/x1.bin", x).File("/d/x2.bin", x).File("/d/x3.bin", x).File("/d/y.bin", Bytes(300_000, 2));
        p.Tag("/d/x1.bin", "\"aaaa\"").Tag("/d/x2.bin", "aaaa").Tag("/d/x3.bin", "AAAA").Tag("/d/y.bin", "bbbb");

        var (groups, finder, _, run) = await Run(p, DupMode.Fast);

        var g = Assert.Single(groups);
        Assert.Equal(new[] { "/d/x1.bin", "/d/x2.bin", "/d/x3.bin" }, Sorted(g));
        Assert.Equal(DupBasis.ContentTag, g.Basis);
        Assert.True(g.IsCertain);
        Assert.Empty(p.Reads);
        Assert.Equal(0, finder.BytesRead);
        Assert.Equal(4, p.TagChecks);
        Assert.Equal(1, p.DeepListings);
        Assert.Equal(1 + 4, run.Requests); // one listing, four HEADs
    }

    [Fact]
    public async Task Weak_or_unconfirmed_tags_only_make_candidates()
    {
        var p = new FakeDeepProvider();
        var x = Bytes(300_000, 1);
        var x2 = (byte[])x.Clone();
        x2[150_000] ^= 1; // same ends
        // Multipart ETags (weak): equal tag, but only the partial hash compares them -> probable.
        p.File("/d/m1.bin", x).File("/d/m2.bin", x2);
        p.Tag("/d/m1.bin", "abc-2").Tag("/d/m2.bin", "abc-2");
        // Strong-looking, but the HEAD says it is no MD5 (SSE-KMS): compared by content.
        var k = Bytes(200_000, 7);
        p.File("/d/k1.bin", k).File("/d/k2.bin", k);
        p.Tag("/d/k1.bin", "1111").Tag("/d/k2.bin", "2222")
            .Confirm("/d/k1.bin", "1111", strong: false).Confirm("/d/k2.bin", "2222", strong: false);

        var (groups, _, _, _) = await Run(p, DupMode.Fast);

        Assert.Equal(2, groups.Count);
        Assert.All(groups, g => Assert.Equal(DupBasis.PartialHash, g.Basis));
        Assert.Contains(groups, g => Sorted(g).SequenceEqual(new[] { "/d/m1.bin", "/d/m2.bin" }));
        Assert.Contains(groups, g => Sorted(g).SequenceEqual(new[] { "/d/k1.bin", "/d/k2.bin" }));

        // The thorough mode tells m1 and m2 apart.
        p = new FakeDeepProvider();
        p.File("/d/m1.bin", x).File("/d/m2.bin", x2);
        p.Tag("/d/m1.bin", "abc-2").Tag("/d/m2.bin", "abc-2");
        var (thorough, _, _, _) = await Run(p, DupMode.Thorough);
        Assert.Empty(thorough);
        Assert.Equal(0, p.TagChecks); // the thorough mode does not rely on tags
    }

    [Fact]
    public async Task A_strong_class_meets_an_untagged_copy()
    {
        var p = new FakeDeepProvider();
        var x = Bytes(300_000, 1);
        p.File("/d/a.bin", x).File("/d/b.bin", x).File("/d/c.bin", x).File("/d/other.bin", Bytes(300_000, 9));
        p.Tag("/d/a.bin", "aaaa").Tag("/d/b.bin", "aaaa").Tag("/d/other.bin", "cccc"); // c.bin: no tag

        var (groups, _, _, _) = await Run(p, DupMode.Fast);

        var g = Assert.Single(groups);
        Assert.Equal(new[] { "/d/a.bin", "/d/b.bin", "/d/c.bin" }, Sorted(g));
        Assert.Equal(DupBasis.PartialHash, g.Basis); // c.bin is only known by its ends
        // c.bin and one file of each tag class were compared; b.bin needed no read.
        Assert.DoesNotContain(p.Reads, r => r.Path == "/d/b.bin");
    }

    [Fact]
    public async Task The_estimate_matches_what_the_modes_really_ask()
    {
        var p = Fill(new FakeDeepProvider());
        p.Tag("/d/a/one.bin", "e1").Tag("/d/b/two.bin", "e1").Tag("/d/three.bin", "e1").Tag("/d/other.bin", "e2");

        var run = new ListRun();
        var c = await DuplicateFinder.CollectAsync(p, "/d", 1, run, null);
        var e = DuplicateFinder.Estimate(c);
        Assert.Equal(3, e.Groups);
        Assert.Equal(9, e.Files);
        Assert.Equal(4L * 2 * Block + 2L * 2 * Block + 3 * 50_000, e.PartialBytes);
        Assert.Equal(4L * 300_000 + 2 * 400_000, e.FullBytesWorst);
        Assert.Equal(4, e.TagChecks);
        // Fast: 4 HEADs; the 300 KB files are all tagged (no partial hash), mid1/mid2 and the three small ones are hashed.
        Assert.Equal(2L * 2 * Block + 3 * 50_000, e.FastBytes);

        long before = run.Requests;
        var finder = new DuplicateFinder();
        await finder.FindAsync(c, DupMode.Fast, 2, run, null, null);
        Assert.Equal(e.FastRequests, run.Requests - before);
        Assert.Equal(e.FastBytes, finder.BytesRead);

        var run2 = new ListRun();
        var c2 = await DuplicateFinder.CollectAsync(p, "/d", 1, run2, null);
        before = run2.Requests;
        var thorough = new DuplicateFinder();
        await thorough.FindAsync(c2, DupMode.Thorough, 2, run2, null, null);
        Assert.True(run2.Requests - before <= e.ThoroughRequestsWorst);
        Assert.True(thorough.BytesRead <= e.ThoroughBytesWorst);
        Assert.True(thorough.BytesRead > e.PartialBytes); // the equal ones were read completely
    }

    [Fact]
    public async Task The_download_limit_asks_and_a_no_stops_further_downloads()
    {
        var p = Fill(new FakeContentProvider());
        int asked = 0;
        // Enough for the partial hashes, not for the full ones.
        var budget = new DownloadBudget(1_000_000, (used, limit) => { Interlocked.Increment(ref asked); return Task.FromResult(false); });
        var (groups, finder, _, _) = await Run(p, DupMode.Thorough, budget: budget);

        Assert.Equal(1, asked);              // asked once, then no further question
        Assert.True(budget.Declined);
        Assert.True(finder.Skipped > 0);
        Assert.True(p.BytesServed <= 1_000_000);
        // What was compared completely is still reported (the small files).
        Assert.Contains(groups, g => g.Paths.Contains("/d/s1.bin"));

        // Yes: the limit is raised and everything is compared.
        p = Fill(new FakeContentProvider());
        asked = 0;
        budget = new DownloadBudget(1_000_000, (used, limit) => { Interlocked.Increment(ref asked); return Task.FromResult(true); });
        (groups, finder, _, _) = await Run(p, DupMode.Thorough, budget: budget);
        Assert.True(asked >= 1);
        Assert.Equal(0, finder.Skipped);
        Assert.Equal(2, groups.Count);
        Assert.True(budget.Limit >= budget.Reserved);
    }

    [Fact]
    public async Task Cancel_ends_a_running_download()
    {
        var p = Fill(new FakeContentProvider());
        p.BlockReads = true;
        using var cts = new CancellationTokenSource();
        var task = Run(p, DupMode.Thorough, ct: cts.Token);
        await p.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Requests_pause_at_the_cost_limit_like_a_scan()
    {
        var p = Fill(new FakeContentProvider());
        int paused = 0;
        var (groups, _, _, run) = await Run(p, DupMode.Thorough, requestLimit: 5, limitReached: r =>
        {
            Interlocked.Increment(ref paused);
            Task.Run(() => r.Resume(r.Requests + 5));
        });
        Assert.Equal(2, groups.Count);
        Assert.True(paused >= 2);
        Assert.True(run.Requests > 10);
    }

    [Fact]
    public async Task Listing_skips_links_counts_unreadable_folders_and_fails_on_an_unreachable_root()
    {
        var p = Fill(new FakeContentProvider());
        p.Link("/d/link").Denied("/d/geheim").File("/d/geheim/x.bin", 10);
        var run = new ListRun();
        var c = await DuplicateFinder.CollectAsync(p, "/d", 1, run, null);
        Assert.DoesNotContain(c.SameSize.SelectMany(g => g), f => f.Path.Contains("link"));
        Assert.Equal(1, c.Errors);
        Assert.True(run.Requests >= 4); // one per folder

        await Assert.ThrowsAnyAsync<IOException>(() => DuplicateFinder.CollectAsync(p, "/gibtsnicht", 1, new ListRun(), null));
    }

    [Fact]
    public async Task Same_files_give_the_same_result_every_time_despite_parallel_listing()
    {
        var p = new FakeContentProvider();
        var x = Bytes(1000, 1);
        for (int i = 0; i < 30; i++) p.File($"/d/f{i % 7}/g{i}.bin", x);
        var first = (await Run(p, DupMode.Thorough)).Groups;
        for (int k = 0; k < 3; k++)
            Assert.Equal(first[0].Paths, (await Run(p, DupMode.Thorough)).Groups[0].Paths);
        Assert.Equal(30, first[0].Paths.Length);
    }

    [Fact]
    public async Task A_source_without_content_access_is_refused()
    {
        var p = Fill(new FakeProvider());
        var c = await DuplicateFinder.CollectAsync(p, "/d", 1, new ListRun(), null);
        await Assert.ThrowsAsync<NotSupportedException>(() => new DuplicateFinder().FindAsync(c, DupMode.Fast, 2, new ListRun(), null, null));
    }

    // ------------------------------------------------------------------ the disk stays as it was

    [Fact]
    public async Task Disk_lists_the_same_files_in_the_same_order_as_a_recursive_FileSystemEnumerable()
    {
        var data = Bytes(100, 1);
        foreach (var rel in new[] { "z.bin", "a.bin", @"sub\x.bin", @"sub\deeper\y.bin", @"b\c\d.bin", @"b\e.bin", @"sub2\q.bin" })
        {
            var path = T.Sub(rel.Split('\\'));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, data);
        }
        var hidden = T.Sub("hidden.bin");
        File.WriteAllBytes(hidden, data);
        File.SetAttributes(hidden, FileAttributes.Hidden);
        Directory.CreateDirectory(T.Sub("target"));
        File.WriteAllBytes(T.Sub("target", "t.bin"), data);
        bool linked = TryJunction(T.Sub("junction"), T.Sub("target"));

        var expected = new FileSystemEnumerable<string>(T.Path, (ref FileSystemEntry e) => e.ToFullPath(),
            new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory,
        }.ToList();

        var c = await DuplicateFinder.CollectAsync(DiskProvider.Instance, T.Path, 1, new ListRun(), null);
        var g = Assert.Single(c.SameSize);
        Assert.Equal(expected, g.Select(f => f.Path).ToList());
        Assert.Contains(hidden, expected);
        if (linked) Assert.DoesNotContain(g, f => f.Path.Contains("junction"));
        Assert.Equal(0, c.Errors);
        Assert.All(g, f => Assert.Null(f.Tag));

        // Through the old entry point: one group with every copy, compared completely.
        var groups = await new DuplicateFinder().FindAsync(T.Path, 1, 4, null, CancellationToken.None);
        var all = Assert.Single(groups);
        Assert.Equal(expected.Count, all.Paths.Length);
        Assert.Equal(DupBasis.FullHash, all.Basis);
        Assert.True(all.IsCertain);
    }

    [Fact]
    public async Task Disk_root_that_does_not_exist_is_one_error_and_no_result()
    {
        var finder = new DuplicateFinder();
        var groups = await finder.FindAsync(T.Sub("gibt-es-nicht"), 1, 2, null, CancellationToken.None);
        Assert.Empty(groups);
        Assert.Equal(1, finder.Errors);
    }

    private static bool TryJunction(string link, string target)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            proc.WaitForExit(10_000);
            return Directory.Exists(link);
        }
        catch
        {
            return false;
        }
    }
}
