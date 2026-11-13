using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Amazon.Runtime;
using Amazon.S3;
using DiskTree.Core.Storage;
using DiskTree.Features.Storage.S3;
using Xunit;

namespace DiskTree.Tests.S3;

/// <summary>Translation of SDK errors and the "Quelle" side of S3 (fields, checks, connection test).</summary>
public sealed class S3KindAndErrorTests : IDisposable
{
    private readonly Func<SourceProfile?, string?, IS3Client> _oldFactory = S3Provider.ClientFactory;

    public void Dispose()
    {
        S3Provider.ClientFactory = _oldFactory;
        SourceProfiles.Set([]);
    }

    private static AmazonS3Exception Service(string code, HttpStatusCode status) =>
        new($"message {code}", ErrorType.Sender, code, "req", status);

    [Theory]
    [InlineData("AccessDenied", HttpStatusCode.Forbidden)]
    [InlineData("InvalidAccessKeyId", HttpStatusCode.Forbidden)]
    [InlineData("SignatureDoesNotMatch", HttpStatusCode.Forbidden)]
    [InlineData("ExpiredToken", HttpStatusCode.BadRequest)]
    [InlineData("Whatever", HttpStatusCode.Unauthorized)]
    public void Auth_errors_become_access_denied(string code, HttpStatusCode status) =>
        Assert.IsType<UnauthorizedAccessException>(S3Errors.Translate(Service(code, status), default, "bk"));

    [Fact]
    public void Missing_bucket_and_other_service_errors()
    {
        var nb = S3Errors.Translate(Service("NoSuchBucket", HttpStatusCode.NotFound), default, "bk");
        Assert.IsType<DirectoryNotFoundException>(nb);
        Assert.Contains("„bk“", nb!.Message);
        Assert.IsType<IOException>(S3Errors.Translate(Service("InternalError", HttpStatusCode.InternalServerError), default, "bk"));
        Assert.Contains("andere", S3Errors.Translate(Service("PermanentRedirect", HttpStatusCode.MovedPermanently), default, "bk")!.Message);
    }

    [Fact]
    public void Network_errors_and_timeouts_are_IO_errors()
    {
        var refused = new HttpRequestException("Connection refused", new SocketException((int)SocketError.ConnectionRefused));
        Assert.IsType<IOException>(S3Errors.Translate(refused, default, "bk"));
        Assert.IsType<IOException>(S3Errors.Translate(new AmazonClientException("wrapped", refused), default, "bk"));
        var timeout = S3Errors.Translate(new TaskCanceledException("timeout"), CancellationToken.None, "bk");
        Assert.IsType<IOException>(timeout);
        Assert.Contains("Zeitüberschreitung", timeout!.Message);
        Assert.IsType<UnauthorizedAccessException>(
            S3Errors.Translate(new AmazonClientException("Unable to find credentials"), default, null));
    }

    [Fact]
    public void Cancellation_by_the_user_stays_a_cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Null(S3Errors.Translate(new TaskCanceledException(), cts.Token, "bk"));
        Assert.Null(S3Errors.Translate(new UnauthorizedAccessException("x"), default, "bk"));
        Assert.Null(S3Errors.Translate(new InvalidOperationException("bug"), default, "bk")); // bugs are not hidden
    }

    // ------------------------------------------------------------------ kind

    private static readonly S3SourceKind Kind = S3SourceKind.Instance;

    [Fact]
    public void Fields_depend_on_the_sign_in_mode()
    {
        Assert.Equal("s3", Kind.Id);
        Assert.Equal("s3://", Kind.Scheme);
        Assert.Equal(SourceAuth.Default, Kind.AuthModes[0]);
        Assert.NotNull(Kind.Label(SourceField.Endpoint, SourceAuth.Default));
        Assert.Null(Kind.Label(SourceField.UserName, SourceAuth.Default));
        Assert.Null(Kind.Label(SourceField.Secret, SourceAuth.NamedProfile));
        Assert.NotNull(Kind.Label(SourceField.Secret, SourceAuth.AccessKey));
        Assert.NotNull(Kind.Label(SourceField.AwsProfile, SourceAuth.NamedProfile));
        Assert.Null(Kind.Label(SourceField.KeyFile, SourceAuth.AccessKey));
        Assert.Null(Kind.Label(SourceField.Port, SourceAuth.AccessKey));
        Assert.Equal("s3", Kind.NewProfile().Kind);
    }

    [Fact]
    public void Validate()
    {
        SourceProfile P() => new() { Kind = "s3", Name = "x", Auth = SourceAuth.AccessKey, UserName = "id" };
        Assert.Null(Kind.Validate(P()));
        Assert.NotNull(Kind.Validate(new SourceProfile { Kind = "s3", Name = " " }));
        var p = P(); p.Endpoint = "localhost:9000"; Assert.NotNull(Kind.Validate(p));
        p.Endpoint = "http://localhost:9000"; Assert.Null(Kind.Validate(p));
        p.Region = "EU Central"; Assert.NotNull(Kind.Validate(p));
        p.Region = "eu-central-1"; Assert.Null(Kind.Validate(p));
        p.UserName = ""; Assert.NotNull(Kind.Validate(p));
        var named = new SourceProfile { Kind = "s3", Name = "n", Auth = SourceAuth.NamedProfile };
        Assert.NotNull(Kind.Validate(named));
        named.AwsProfile = "dev"; Assert.Null(Kind.Validate(named));
        var path = P(); path.Path = "s3://bad bucket/x"; Assert.NotNull(Kind.Validate(path));
    }

    [Theory]
    [InlineData("", "s3://")]
    [InlineData("s3://", "s3://")]
    [InlineData("bk", "s3://bk")]
    [InlineData("bk/pre/", "s3://bk/pre")]
    [InlineData("S3://bk/pre", "s3://bk/pre")]
    public void PathOf(string path, string expected) =>
        Assert.Equal(expected, Kind.PathOf(new SourceProfile { Kind = "s3", Path = path }));

    [Fact]
    public async Task Connection_test_uses_the_typed_secret()
    {
        var server = new MemoryS3Client().Put("bk", "a/x", 1).Bucket("zwei");
        string? usedSecret = null;
        S3Provider.ClientFactory = (_, secret) => { usedSecret = secret; return server; };

        var p = new SourceProfile { Kind = "s3", Name = "t", Path = "s3://bk/a" };
        var ok = await Kind.TestAsync(p, "getippt", null, CancellationToken.None);
        Assert.Contains("„bk“", ok);
        Assert.Equal("getippt", usedSecret);

        var empty = await Kind.TestAsync(new SourceProfile { Kind = "s3", Path = "s3://bk/leer" }, null, null, CancellationToken.None);
        Assert.Contains("keine Objekte", empty);
        Assert.Null(usedSecret);

        var all = await Kind.TestAsync(new SourceProfile { Kind = "s3" }, "", null, CancellationToken.None);
        Assert.Contains("2 Bucket", all);

        var missing = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Kind.TestAsync(new SourceProfile { Kind = "s3", Path = "s3://nope" }, null, null, CancellationToken.None));
        Assert.Contains("nope", missing.Message);

        server.Failures["bk/"] = new UnauthorizedAccessException("Der Secret-Access-Key passt nicht");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => Kind.TestAsync(new SourceProfile { Kind = "s3", Path = "s3://bk" }, "falsch", null, CancellationToken.None));
    }

    [Fact]
    public void Access_key_profile_without_stored_secret_explains_what_is_missing()
    {
        var old = SourceSecrets.Store;
        SourceSecrets.Store = new MemorySecretStore();
        try
        {
            var p = new SourceProfile { Kind = "s3", Name = "ohne", Auth = SourceAuth.AccessKey, UserName = "id", Endpoint = "http://127.0.0.1:1" };
            var ex = Assert.Throws<UnauthorizedAccessException>(() => _oldFactory(p, null));
            Assert.Contains("Secret", ex.Message);
        }
        finally { SourceSecrets.Store = old; }
    }
}
