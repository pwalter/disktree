using System.Diagnostics;
using System.IO;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using DiskTree.Core.Storage;
using DiskTree.Features.Duplicates;
using DiskTree.Features.Storage.S3;
using Xunit;
using Xunit.Abstractions;

namespace DiskTree.Tests.S3;

/// <summary>
/// The duplicate search against a local S3-compatible server (see <see cref="S3IntegrationTests"/> for the variables):
/// single-part uploads (ETag = MD5), multipart uploads of the same content with other part sizes (weak ETags), same size
/// with other content, same ends with another middle, a 12 MB object read in ranged parts, both modes, cancel.
/// </summary>
[Collection(DiskTree.Tests.Storage.SourceStateCollection.Name)]
public sealed class S3DuplicateIntegrationTests : IDisposable
{
    private static string Endpoint => Environment.GetEnvironmentVariable(S3IntegrationTests.EndpointVar) ?? "";
    private static string AccessKey => Environment.GetEnvironmentVariable("DISKTREE_S3_TEST_ACCESS_KEY") ?? "";
    private static string SecretKey => Environment.GetEnvironmentVariable("DISKTREE_S3_TEST_SECRET_KEY") ?? "";

    private readonly ITestOutputHelper _output;
    private readonly string _bucket = "dt-dup-" + Guid.NewGuid().ToString("N")[..12];
    private readonly ISecretStore _oldStore = SourceSecrets.Store;
    private readonly MemorySecretStore _store = new();
    private readonly SourceProfile _profile;
    private AmazonS3Client? _admin;

    public S3DuplicateIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        S3Storage.Register();
        SourceSecrets.Store = _store;
        _profile = new SourceProfile
        {
            Kind = "s3", Name = "Duplikate", Endpoint = Endpoint, PathStyle = true, Auth = SourceAuth.AccessKey,
            UserName = AccessKey, Path = "s3://" + _bucket,
        };
        SourceSecrets.Write(_profile, SecretSlot.Secret, SecretKey);
        SourceProfiles.Set([_profile]);
    }

    private AmazonS3Client Admin => _admin ??= new AmazonS3Client(new BasicAWSCredentials(AccessKey, SecretKey),
        new AmazonS3Config
        {
            ServiceURL = Endpoint, ForcePathStyle = true, AuthenticationRegion = "us-east-1",
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        });

    private static byte[] Bytes(int length, int seed)
    {
        var b = new byte[length];
        new Random(seed).NextBytes(b);
        return b;
    }

    private Task Put(string key, byte[] data) =>
        Admin.PutObjectAsync(new PutObjectRequest { BucketName = _bucket, Key = key, InputStream = new MemoryStream(data) });

    private async Task PutMultipart(string key, byte[] data, int partSize)
    {
        var init = await Admin.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = _bucket, Key = key });
        var parts = new List<PartETag>();
        for (int i = 0, n = 1; i < data.Length; i += partSize, n++)
        {
            int len = Math.Min(partSize, data.Length - i);
            var r = await Admin.UploadPartAsync(new UploadPartRequest
            {
                BucketName = _bucket, Key = key, UploadId = init.UploadId, PartNumber = n, PartSize = len,
                InputStream = new MemoryStream(data, i, len),
            });
            parts.Add(new PartETag(n, r.ETag));
        }
        await Admin.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = _bucket, Key = key, UploadId = init.UploadId, PartETags = parts,
        });
    }

    private async Task Fill()
    {
        await Admin.PutBucketAsync(_bucket);
        var x = Bytes(300_000, 1);
        await Put("a.bin", x);
        await Put("ordner/b.bin", x);
        await Put("ordner mit leer/c äöü.bin", x);
        await Put("other.bin", Bytes(300_000, 2));
        var mid = Bytes(400_000, 3);
        var mid2 = (byte[])mid.Clone();
        mid2[200_000] ^= 0xFF;
        await Put("mid/one.bin", mid);
        await Put("mid/two.bin", mid2);
        var big = Bytes(12_000_000, 4);
        await PutMultipart("mp/five.bin", big, 5 << 20);   // ETag "…-3"
        await PutMultipart("mp/six.bin", big, 6 << 20);    // ETag "…-2": same content, other tag
        var big2 = (byte[])big.Clone();
        big2[6_000_000] ^= 1;
        await PutMultipart("mp/other.bin", big2, 5 << 20); // same ends, other middle
        await Put("single/big.bin", big);                  // single-part copy of the multipart content
        await Put("s1.bin", Bytes(50_000, 5));
        await Put("s2.bin", Bytes(50_000, 5));
    }

    public void Dispose()
    {
        try
        {
            if (_admin != null)
            {
                string? token = null;
                do
                {
                    var page = _admin.ListObjectsV2Async(new ListObjectsV2Request { BucketName = _bucket, ContinuationToken = token }).GetAwaiter().GetResult();
                    foreach (var o in page.S3Objects ?? []) _admin.DeleteObjectAsync(_bucket, o.Key).GetAwaiter().GetResult();
                    token = page.IsTruncated == true ? page.NextContinuationToken : null;
                } while (token != null);
                _admin.DeleteBucketAsync(_bucket).GetAwaiter().GetResult();
                _admin.Dispose();
            }
        }
        catch { /* the container is thrown away anyway */ }
        SourceSecrets.Store = _oldStore;
        SourceProfiles.Set([]);
    }

    private string Root => "s3://" + _bucket;

    private string[] Names(DuplicateGroup g) => g.Paths.Select(p => p[(Root.Length + 1)..]).Order(StringComparer.Ordinal).ToArray();

    private async Task<(List<DuplicateGroup> Groups, DuplicateFinder Finder, DupCandidates C, DupEstimate E, ListRun Run)> Find(DupMode mode,
        CancellationToken ct = default)
    {
        Assert.True(StorageProviders.TryResolve(Root, out var loc));
        var run = new ListRun(ct);
        var c = await DuplicateFinder.CollectAsync(loc.Provider, loc.Path, 1, run, null);
        var e = DuplicateFinder.Estimate(c);
        var f = new DuplicateFinder();
        var sw = Stopwatch.StartNew();
        var groups = await f.FindAsync(c, mode, 8, run, null, null);
        _output.WriteLine($"{mode}: {sw.ElapsedMilliseconds} ms, {f.BytesRead:N0} Bytes, {run.Requests} Anfragen, {groups.Count} Gruppen");
        foreach (var g in groups) _output.WriteLine($"  {g.Basis}: {string.Join(", ", Names(g))}");
        return (groups, f, c, e, run);
    }

    [S3IntegrationFact]
    public async Task ETags_of_the_server_are_classified_as_expected()
    {
        await Fill();
        Assert.True(StorageProviders.TryResolve(Root, out var loc));
        var p = (S3Provider)loc.Provider;
        var files = p.ListFiles(loc.Path, new ListOptions { Run = new ListRun() }).ToDictionary(f => f.Path[(Root.Length + 1)..]);
        foreach (var (k, f) in files) _output.WriteLine($"{k}: {f.ContentTag}");
        Assert.Equal(ContentTagKind.Strong, p.ClassifyTag(files["a.bin"].ContentTag));
        Assert.Equal(ContentTagKind.Weak, p.ClassifyTag(files["mp/five.bin"].ContentTag));
        Assert.Equal(ContentTagKind.Weak, p.ClassifyTag(files["mp/six.bin"].ContentTag));
        Assert.NotEqual(S3Provider.NormalizeETag(files["mp/five.bin"].ContentTag), S3Provider.NormalizeETag(files["mp/six.bin"].ContentTag));
        Assert.True(p.TryGetContentTag(files["a.bin"].Path, files["a.bin"].ContentTag, new ListRun(), out var tag, out bool strong));
        Assert.True(strong);
        Assert.Equal(S3Provider.NormalizeETag(files["a.bin"].ContentTag), tag);
    }

    [S3IntegrationFact]
    public async Task Fast_mode_against_a_real_server()
    {
        await Fill();
        var (groups, finder, c, e, run) = await Find(DupMode.Fast);

        Assert.Equal(1, c.ListRequests);
        // Single-part copies: same MD5 ETag, certain without download.
        var x = groups.Single(g => g.Paths.Any(p => p.EndsWith("/a.bin")));
        Assert.Equal(new[] { "a.bin", "ordner mit leer/c äöü.bin", "ordner/b.bin" }, Names(x));
        Assert.Equal(DupBasis.ContentTag, x.Basis);
        // mid/one and mid/two: different single-part ETags -> different, without a download.
        Assert.DoesNotContain(groups, g => g.Paths.Any(p => p.Contains("/mid/")));
        // The multipart files (weak tags) and the single-part copy of the same content: only the ends are compared.
        var big = groups.Single(g => g.Paths.Any(p => p.Contains("/mp/")));
        Assert.Equal(new[] { "mp/five.bin", "mp/other.bin", "mp/six.bin", "single/big.bin" }, Names(big));
        Assert.False(big.IsCertain);
        Assert.Contains(groups, g => Names(g).SequenceEqual(new[] { "s1.bin", "s2.bin" }) && g.IsCertain);
        Assert.Equal(e.FastBytes, finder.BytesRead);
        Assert.Equal(e.FastRequests, run.Requests - c.ListRequests);
    }

    [S3IntegrationFact]
    public async Task Thorough_mode_against_a_real_server()
    {
        await Fill();
        var (groups, finder, c, e, run) = await Find(DupMode.Thorough);

        Assert.All(groups, g => Assert.Equal(DupBasis.FullHash, g.Basis));
        Assert.Contains(groups, g => Names(g).SequenceEqual(new[] { "a.bin", "ordner mit leer/c äöü.bin", "ordner/b.bin" }));
        // Same content uploaded as multipart (two part sizes) and single part: one group; "other" differs in the middle.
        Assert.Contains(groups, g => Names(g).SequenceEqual(new[] { "mp/five.bin", "mp/six.bin", "single/big.bin" }));
        Assert.DoesNotContain(groups, g => g.Paths.Any(p => p.Contains("/mid/") || p.EndsWith("mp/other.bin")));
        Assert.Equal(3, groups.Count);
        Assert.True(finder.BytesRead <= e.ThoroughBytesWorst);
        Assert.True(run.Requests - c.ListRequests <= e.ThoroughRequestsWorst);
        // 12 MB objects are read in parts of 4 MB: 3 ranged GETs each for the full hash.
        Assert.True(run.Requests - c.ListRequests >= 4 * 3);
    }

    [S3IntegrationFact]
    public async Task Cancel_ends_the_downloads()
    {
        await Fill();
        using var cts = new CancellationTokenSource();
        Assert.True(StorageProviders.TryResolve(Root, out var loc));
        var run = new ListRun(cts.Token);
        var c = await DuplicateFinder.CollectAsync(loc.Provider, loc.Path, 1, run, null);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = new DuplicateFinder().FindAsync(c, DupMode.Thorough, 2, run, null,
            new SyncProgress(p => { if (p.Phase == DupPhase.FullHash) started.TrySetResult(); }));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var sw = Stopwatch.StartNew();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
    }

    private sealed class SyncProgress(Action<DupProgress> h) : IProgress<DupProgress>
    {
        public void Report(DupProgress value) => h(value);
    }
}
