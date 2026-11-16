using System.Diagnostics;
using DiskTree.Core.Storage;
using DiskTree.Features.Duplicates;
using DiskTree.Features.Storage.Sftp;
using Xunit;
using Xunit.Abstractions;

namespace DiskTree.Tests.Storage;

/// <summary>
/// The duplicate search against a real OpenSSH server (atmoz/sftp, see <see cref="SftpTestServer"/>): real duplicates,
/// same size with other content, same first/last 64 KB with another middle, a 6 MB file read in ranges, both modes,
/// cancel during a slow download. The files live in their own folder outside /data (the other tests count /data) and
/// are removed afterwards. Only with DISKTREE_SFTP_TESTS=1.
/// </summary>
[Collection(SftpCollection.Name)]
public sealed class SftpDuplicateIntegrationTests : IDisposable
{
    private readonly SftpTestServer _server;
    private readonly ITestOutputHelper _output;
    private readonly string _dir = "dups-" + Guid.NewGuid().ToString("N")[..8];

    public SftpDuplicateIntegrationTests(SftpTestServer server, ITestOutputHelper output)
    {
        _server = server;
        _output = output;
        if (!SftpTestServer.Enabled) return;
        // /home/tester is the chroot ("/" over SFTP); the folder is created as root and readable for the user.
        server.Sh($$"""
            set -e
            D=/home/{{SftpTestServer.User}}/{{_dir}}
            mkdir -p $D/sub $D/mid $D/big
            head -c 300000 /dev/urandom > $D/a.bin
            cp $D/a.bin $D/b.bin
            cp $D/a.bin $D/sub/c.bin
            head -c 300000 /dev/urandom > $D/other.bin
            head -c 400000 /dev/urandom > $D/mid/one.bin
            cp $D/mid/one.bin $D/mid/two.bin
            printf 'X' | dd of=$D/mid/two.bin bs=1 seek=200000 conv=notrunc 2>/dev/null
            head -c 6000000 /dev/urandom > $D/big/1.bin
            cp $D/big/1.bin $D/big/2.bin
            cp $D/big/1.bin $D/big/3.bin
            printf 'Y' | dd of=$D/big/3.bin bs=1 seek=3000000 conv=notrunc 2>/dev/null
            head -c 50000 /dev/urandom > $D/s1.bin
            cp $D/s1.bin $D/s2.bin
            ln -s a.bin $D/link.bin
            chmod -R a+rX $D
            """);
    }

    public void Dispose()
    {
        if (SftpTestServer.Enabled) _server.Sh($"rm -rf /home/{SftpTestServer.User}/{_dir}");
    }

    private string Root => _server.RootUri + _dir;

    private static string[] Names(DuplicateGroup g, string root) =>
        g.Paths.Select(p => p[(root.Length + 1)..]).Order(StringComparer.Ordinal).ToArray();

    private async Task<(List<DuplicateGroup> Groups, DuplicateFinder Finder, DupEstimate Estimate)> Find(SftpProvider p, DupMode mode)
    {
        var run = new ListRun();
        var c = await DuplicateFinder.CollectAsync(p, Root, 1, run, null);
        var e = DuplicateFinder.Estimate(c);
        var f = new DuplicateFinder();
        var sw = Stopwatch.StartNew();
        var groups = await f.FindAsync(c, mode, 4, run, null, null);
        _output.WriteLine($"{mode}: {sw.ElapsedMilliseconds} ms, {f.BytesRead:N0} Bytes gelesen, {groups.Count} Gruppen, {f.Errors} Fehler");
        Assert.Equal(0, f.Errors);
        return (groups, f, e);
    }

    [SftpFact]
    public async Task Thorough_finds_only_real_duplicates()
    {
        using var p = new SftpProvider(_server.Options());
        var (groups, finder, e) = await Find(p, DupMode.Thorough);

        Assert.Equal(3, groups.Count);
        Assert.All(groups, g => Assert.Equal(DupBasis.FullHash, g.Basis));
        Assert.Contains(groups, g => Names(g, Root).SequenceEqual(new[] { "a.bin", "b.bin", "sub/c.bin" }));
        Assert.Contains(groups, g => Names(g, Root).SequenceEqual(new[] { "big/1.bin", "big/2.bin" }));
        Assert.Contains(groups, g => Names(g, Root).SequenceEqual(new[] { "s1.bin", "s2.bin" }));
        Assert.DoesNotContain(groups, g => g.Paths.Any(x => x.Contains("link") || x.Contains("mid/") || x.EndsWith("other.bin")));
        Assert.True(finder.BytesRead <= e.ThoroughBytesWorst);
        Assert.True(finder.BytesRead > 3 * 6_000_000); // the three big files were read completely
    }

    [SftpFact]
    public async Task Fast_reads_only_the_ends_and_marks_big_files_probable()
    {
        using var p = new SftpProvider(_server.Options());
        var (groups, finder, e) = await Find(p, DupMode.Fast);

        Assert.Equal(4, groups.Count);
        var mid = groups.Single(g => g.Paths.Any(x => x.Contains("mid/")));
        Assert.False(mid.IsCertain); // only the middle differs: cannot be told apart without a full download
        var big = groups.Single(g => g.Paths.Any(x => x.Contains("big/")));
        Assert.Equal(3, big.Paths.Length);
        Assert.Equal(DupBasis.PartialHash, big.Basis);
        var small = groups.Single(g => g.Paths.Any(x => x.EndsWith("s1.bin")));
        Assert.True(small.IsCertain);
        Assert.Equal(e.FastBytesWorst, finder.BytesRead);
        Assert.Equal(0, e.TagChecks);
    }

    [SftpFact]
    public async Task Cancel_ends_a_slow_download()
    {
        // 50 ms each way: reading a 6 MB file takes many seconds.
        using var proxy = new LatencyProxy(_server.Port, TimeSpan.FromMilliseconds(50));
        using var p = new SftpProvider(_server.Options(port: proxy.Port));
        p.Connect(CancellationToken.None);
        using var cts = new CancellationTokenSource();
        using var abort = p.CancelWith(cts.Token);
        var run = new ListRun(cts.Token);
        var c = await DuplicateFinder.CollectAsync(p, $"sftp://{SftpTestServer.User}@{SftpTestServer.Host}:{proxy.Port}/{_dir}/big", 1, run, null);
        var progress = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = new DuplicateFinder().FindAsync(c, DupMode.Thorough, 3, run, null,
            new SyncProgress(x => { if (x.Phase == DupPhase.FullHash && x.BytesDone > 0) progress.TrySetResult(); }));
        await progress.Task.WaitAsync(TimeSpan.FromSeconds(60));
        var sw = Stopwatch.StartNew();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        _output.WriteLine($"cancelled after {sw.ElapsedMilliseconds} ms");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
    }

    private sealed class SyncProgress(Action<DupProgress> h) : IProgress<DupProgress>
    {
        public void Report(DupProgress value) => h(value);
    }
}
