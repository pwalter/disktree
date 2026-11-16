using System.IO;
using DiskTree.Core.Storage;
using DiskTree.Features.Duplicates;
using DiskTree.Features.Storage.S3;
using Xunit;

namespace DiskTree.Tests.S3;

/// <summary>The duplicate search over the S3 provider against the in-memory server: ETags, HEAD, ranged GETs, flat listing.</summary>
[Collection(DiskTree.Tests.Storage.SourceStateCollection.Name)]
public sealed class S3DuplicateTests : IDisposable
{
    private readonly Func<SourceProfile?, string?, IS3Client> _oldFactory = S3Provider.ClientFactory;
    private readonly MemoryS3Client _server = new();

    public S3DuplicateTests()
    {
        S3Storage.Register();
        S3Provider.ClientFactory = (_, _) => _server;
        SourceProfiles.Set([]);
    }

    public void Dispose()
    {
        S3Provider.ClientFactory = _oldFactory;
        SourceProfiles.Set([]);
    }

    private static S3Provider P => S3Provider.For(null);

    private static byte[] Bytes(int length, int seed)
    {
        var b = new byte[length];
        new Random(seed).NextBytes(b);
        return b;
    }

    [Theory]
    [InlineData("\"0123456789abcdef0123456789ABCDEF\"", ContentTagKind.Strong)]
    [InlineData("0123456789abcdef0123456789abcdef", ContentTagKind.Strong)]
    [InlineData("\"0123456789abcdef0123456789abcdef-12\"", ContentTagKind.Weak)]
    [InlineData("0123456789abcdef0123456789abcdef-", ContentTagKind.None)]
    [InlineData("abc", ContentTagKind.None)]
    [InlineData("", ContentTagKind.None)]
    [InlineData(null, ContentTagKind.None)]
    public void ETags_are_classified(string? etag, ContentTagKind kind) => Assert.Equal(kind, P.ClassifyTag(etag));

    [Fact]
    public void Keys_of_file_paths()
    {
        Assert.Equal(("bk", "a/x.bin"), S3Provider.KeyOf("s3://bk/a/x.bin"));
        Assert.Equal(("bk", "x"), S3Provider.KeyOf("s3://bk/x"));
        Assert.Equal(("bk", "a/"), S3Provider.KeyOf("s3://bk/a/" + S3Path.EmptySegment));
        Assert.Equal(("bk", "ä ö/ü.txt"), S3Provider.KeyOf("s3://bk/ä ö/ü.txt"));
        Assert.Throws<IOException>(() => S3Provider.KeyOf("s3://bk"));
    }

    private void Fill()
    {
        var big = Bytes(300_000, 1);
        var mid = Bytes(400_000, 3);
        var mid2 = (byte[])mid.Clone();
        mid2[200_000] ^= 0xFF;
        _server.PutContent("bk", "a/one.bin", big)
            .PutContent("bk", "b/two.bin", big)
            .PutContent("bk", "three.bin", big)
            .PutContent("bk", "other.bin", Bytes(300_000, 2))
            // multipart uploads of the same content with another part size: different weak ETags
            .PutContent("bk", "mp/one.bin", mid, "\"11111111111111111111111111111111-2\"")
            .PutContent("bk", "mp/two.bin", mid, "\"22222222222222222222222222222222-3\"")
            .PutContent("bk", "mp/mid.bin", mid2, "\"33333333333333333333333333333333-2\"")
            .PutContent("bk", "kms/a.bin", Bytes(200_000, 7), "\"44444444444444444444444444444444\"", "aws:kms")
            .PutContent("bk", "kms/b.bin", Bytes(200_000, 7), "\"55555555555555555555555555555555\"", "aws:kms")
            .Put("bk", "a/", 0);
    }

    private async Task<(List<DuplicateGroup> Groups, DuplicateFinder Finder, DupCandidates C, ListRun Run, DupEstimate E)> Find(DupMode mode)
    {
        var run = new ListRun();
        var c = await DuplicateFinder.CollectAsync(P, "s3://bk", 1, run, null);
        var e = DuplicateFinder.Estimate(c);
        var f = new DuplicateFinder();
        var groups = await f.FindAsync(c, mode, 4, run, null, null);
        return (groups, f, c, run, e);
    }

    [Fact]
    public async Task Fast_mode_uses_ETags_and_HEAD_and_reads_only_the_rest()
    {
        Fill();
        var (groups, finder, c, run, e) = await Find(DupMode.Fast);

        Assert.Equal(1, c.ListRequests); // one flat listing, no delimiter
        Assert.Contains(_server.Calls, x => x.Delimiter == null);
        Assert.Equal(3, c.SameSize.Count);

        // Single-part, no KMS: same MD5 ETag -> certain, nothing downloaded for them.
        var big = groups.Single(g => g.Paths.Contains("s3://bk/three.bin"));
        Assert.Equal(new[] { "s3://bk/a/one.bin", "s3://bk/b/two.bin", "s3://bk/three.bin" }, big.Paths.Order(StringComparer.Ordinal));
        Assert.Equal(DupBasis.ContentTag, big.Basis);

        // Multipart: only the ends compared -> all three probable (mid.bin differs only in the middle).
        var mp = groups.Single(g => g.Paths.Contains("s3://bk/mp/one.bin"));
        Assert.Equal(3, mp.Paths.Length);
        Assert.False(mp.IsCertain);

        // SSE-KMS: the ETags are no MD5; compared by content (probable).
        var kms = groups.Single(g => g.Paths.Contains("s3://bk/kms/a.bin"));
        Assert.Equal(DupBasis.PartialHash, kms.Basis);

        Assert.Equal(6, _server.Heads);      // strong-looking ETags: 4 plain + 2 KMS
        Assert.Equal(e.TagChecks, _server.Heads);
        Assert.Equal(5 * 2, _server.Gets);    // first and last 64 KB of mp/* and kms/*
        Assert.Equal(finder.BytesRead, _server.BytesSent);
        Assert.Equal(1 + 6 + 10, run.Requests);
        Assert.True(run.Requests - 1 <= e.FastRequestsWorst);
    }

    [Fact]
    public async Task Thorough_mode_downloads_and_tells_the_multipart_files_apart()
    {
        Fill();
        var (groups, finder, _, run, e) = await Find(DupMode.Thorough);

        Assert.Equal(3, groups.Count);
        Assert.All(groups, g => Assert.Equal(DupBasis.FullHash, g.Basis));
        var mp = groups.Single(g => g.Paths.Contains("s3://bk/mp/one.bin"));
        Assert.Equal(new[] { "s3://bk/mp/one.bin", "s3://bk/mp/two.bin" }, mp.Paths.Order(StringComparer.Ordinal));
        Assert.Equal(0, _server.Heads);
        Assert.True(finder.BytesRead <= e.ThoroughBytesWorst);
        Assert.True(run.Requests - 1 <= e.ThoroughRequestsWorst);
    }

    [Fact]
    public async Task Big_objects_are_read_in_ranged_parts()
    {
        var x = Bytes(S3Provider.ReadChunk * 2 + 12345, 9);
        _server.PutContent("bk", "big/a.bin", x, "\"a-2\"").PutContent("bk", "big/b.bin", x, "\"b-3\"");
        var (groups, finder, _, _, e) = await Find(DupMode.Thorough);

        var g = Assert.Single(groups);
        Assert.Equal(2, g.Paths.Length);
        // per file: 2 partial reads + 3 parts of the full read
        Assert.Equal(2 * (2 + 3), _server.Gets);
        Assert.Equal(e.ThoroughRequestsWorst, _server.Gets);
        Assert.Equal(2L * (2 * 65536 + x.Length), finder.BytesRead);
    }

    [Fact]
    public async Task Cancel_ends_a_running_GET()
    {
        Fill();
        _server.BlockGets = true;
        using var cts = new CancellationTokenSource();
        var run = new ListRun(cts.Token);
        var c = await DuplicateFinder.CollectAsync(P, "s3://bk", 1, run, null);
        var task = new DuplicateFinder().FindAsync(c, DupMode.Thorough, 2, run, null, null);
        await _server.GetStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Requests_count_into_the_cost_limit()
    {
        Fill();
        var run = new ListRun(CancellationToken.None, requestLimit: 3);
        var paused = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        run.LimitReached += r => paused.TrySetResult(r.Requests);
        var c = await DuplicateFinder.CollectAsync(P, "s3://bk", 1, run, null);
        var task = new DuplicateFinder().FindAsync(c, DupMode.Fast, 2, run, null, null);
        Assert.Equal(3, await paused.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        await Task.Delay(200);
        Assert.False(task.IsCompleted); // waits for the decision
        Assert.True(run.IsPaused);
        run.Resume();
        var groups = await task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(3, groups.Count);
    }

    [Fact]
    public async Task Generic_provider_resolves_the_profile_for_contents()
    {
        Fill();
        var run = new ListRun();
        var c = await DuplicateFinder.CollectAsync(S3Provider.Generic, "s3://bk/a", 1, run, null);
        Assert.Empty(c.SameSize); // only one file below a/
        var all = await DuplicateFinder.CollectAsync(S3Provider.Generic, "s3://bk", 1, run, null);
        var groups = await new DuplicateFinder().FindAsync(all, DupMode.Fast, 2, run, null, null);
        Assert.Equal(3, groups.Count);
    }

    [Fact]
    public async Task Marker_objects_with_content_and_empty_parts_keep_their_tree_paths()
    {
        var x = Bytes(1000, 1);
        _server.PutContent("bk", "m/", x).PutContent("bk", "e//f.bin", x).PutContent("bk", "z.bin", x).Put("bk", "empty/", 0);
        var (groups, _, c, _, _) = await Find(DupMode.Thorough);
        var g = Assert.Single(groups);
        Assert.Equal(new[] { "s3://bk/e/" + S3Path.EmptySegment + "/f.bin", "s3://bk/m/" + S3Path.EmptySegment, "s3://bk/z.bin" },
            g.Paths.Order(StringComparer.Ordinal));
        Assert.Equal(3, c.FilesListed);
    }
}
