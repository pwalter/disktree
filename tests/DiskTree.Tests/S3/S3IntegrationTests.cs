using System.IO;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using DiskTree.Core;
using DiskTree.Core.Storage;
using DiskTree.Features.Storage.S3;
using Xunit;

namespace DiskTree.Tests.S3;

/// <summary>
/// Runs only when a local S3-compatible server is configured, otherwise skipped (CI has no Docker):
/// <code>
/// docker run -d --name dt-s3-test -p 19300:9000 -e RUSTFS_ACCESS_KEY=dtadmin -e RUSTFS_SECRET_KEY=dtsecret123 rustfs/rustfs:latest /data
/// $env:DISKTREE_S3_TEST_ENDPOINT = "http://127.0.0.1:19300"
/// $env:DISKTREE_S3_TEST_ACCESS_KEY = "dtadmin"; $env:DISKTREE_S3_TEST_SECRET_KEY = "dtsecret123"
/// dotnet test tests/DiskTree.Tests --filter S3IntegrationTests
/// docker rm -f -v dt-s3-test
/// </code>
/// </summary>
public sealed class S3IntegrationFactAttribute : FactAttribute
{
    public S3IntegrationFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(S3IntegrationTests.EndpointVar)))
            Skip = $"Kein lokaler S3-Server konfiguriert ({S3IntegrationTests.EndpointVar}).";
    }
}

public sealed class S3IntegrationTests : IDisposable
{
    public const string EndpointVar = "DISKTREE_S3_TEST_ENDPOINT";

    private static string Endpoint => Environment.GetEnvironmentVariable(EndpointVar) ?? "";
    private static string AccessKey => Environment.GetEnvironmentVariable("DISKTREE_S3_TEST_ACCESS_KEY") ?? "";
    private static string SecretKey => Environment.GetEnvironmentVariable("DISKTREE_S3_TEST_SECRET_KEY") ?? "";

    private readonly string _bucket = "dt-it-" + Guid.NewGuid().ToString("N")[..12];
    private readonly ISecretStore _oldStore = SourceSecrets.Store;
    private readonly MemorySecretStore _store = new();
    private readonly SourceProfile _profile;
    private AmazonS3Client? _admin;

    public S3IntegrationTests()
    {
        S3Storage.Register();
        SourceSecrets.Store = _store;
        _profile = new SourceProfile
        {
            Kind = "s3", Name = "Integration", Endpoint = Endpoint, PathStyle = true, Auth = SourceAuth.AccessKey,
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

    private async Task Fill()
    {
        await Admin.PutBucketAsync(_bucket);
        async Task Put(string key, int size) =>
            await Admin.PutObjectAsync(new PutObjectRequest { BucketName = _bucket, Key = key, InputStream = new MemoryStream(new byte[size]) });
        await Put("top.bin", 100);
        await Put("a/", 0);
        await Put("a/x.bin", 1000);
        await Put("a/b/y.bin", 2000);
        await Put("ordner mit leer/äöü ß.txt", 50);
        // (RustFS rejects keys with empty parts like "e//f.bin"; those are covered by the in-memory tests.)
        for (int i = 0; i < 1100; i++) await Put($"many/f{i:D4}", 1);
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

    private static long Expected => 100 + 1000 + 2000 + 50 + 1100;

    [S3IntegrationFact]
    public async Task Scan_per_folder_and_flat_against_a_real_server()
    {
        await Fill();
        Assert.True(StorageProviders.TryResolve($"s3://{_bucket}/", out var loc));
        Assert.Equal(_profile.Id, loc.Provider.Profile?.Id);
        Assert.True(loc.Provider.FolderExists(loc.Path));
        Assert.True(loc.Provider.FolderExists(loc.Path + "/a"));
        Assert.False(loc.Provider.FolderExists(loc.Path + "/gibtsnicht"));

        var s = new Scanner { Provider = loc.Provider };
        await s.ScanAsync(loc.Path, 32, CancellationToken.None);
        Assert.Equal(0, s.Errors);
        Assert.Equal(Expected, s.Root!.Size);
        Assert.Equal(1104, s.Root.FileCount);
        Assert.Equal(3000, s.FindNode($"s3://{_bucket}/a")!.Size);
        Assert.Equal(50, s.FindNode($"s3://{_bucket}/ordner mit leer")!.Size);
        Assert.Equal(6, s.Requests); // root, a, a/b, ordner, many (2 pages)

        var flatProfile = _profile.Clone();
        flatProfile.Listing = SourceListing.Flat;
        SourceProfiles.Set([flatProfile]);
        Assert.True(StorageProviders.TryResolve($"s3://{_bucket}", out var flatLoc));
        var flat = new Scanner { Provider = flatLoc.Provider };
        await flat.ScanAsync(flatLoc.Path, 32, CancellationToken.None);
        Assert.Equal(Expected, flat.Root!.Size);
        Assert.Equal(1104, flat.Root.FileCount);
        Assert.Equal(2, flat.Requests);

        var ok = await S3SourceKind.Instance.TestAsync(_profile, null, null, CancellationToken.None);
        Assert.Contains(_bucket, ok);
    }

    [S3IntegrationFact]
    public async Task Wrong_secret_and_unreachable_server_are_readable_errors()
    {
        await Fill();
        var wrong = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => S3SourceKind.Instance.TestAsync(_profile, "falsch", null, CancellationToken.None));
        Assert.DoesNotContain("falsch", wrong.Message);

        var down = _profile.Clone();
        down.Endpoint = "http://127.0.0.1:1";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAnyAsync<IOException>(() => S3SourceKind.Instance.TestAsync(down, null, null, CancellationToken.None));
        Console.WriteLine($"unreachable after {sw.ElapsedMilliseconds} ms: {ex.Message}");
        Assert.Contains("nicht erreichbar", ex.Message);

        // A scan with the wrong secret: the folder counts as not readable.
        SourceSecrets.Write(_profile, SecretSlot.Secret, "falsch");
        SourceProfiles.Set([_profile]);
        Assert.True(StorageProviders.TryResolve($"s3://{_bucket}", out var loc));
        Assert.Throws<UnauthorizedAccessException>(() => loc.Provider.FolderExists(loc.Path));
        var s = new Scanner { Provider = loc.Provider };
        await s.ScanAsync(loc.Path, 4, CancellationToken.None);
        Assert.Equal(1, s.Errors);
    }

    [S3IntegrationFact]
    public async Task Cancel_reaches_the_running_requests()
    {
        await Fill();
        Assert.True(StorageProviders.TryResolve($"s3://{_bucket}", out var loc));
        using var cts = new CancellationTokenSource();
        var s = new Scanner { Provider = loc.Provider };
        cts.CancelAfter(5);
        var started = DateTime.UtcNow;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.ScanAsync(loc.Path, 32, cts.Token));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
        Assert.True(s.Root!.IsDone);
    }
}
