using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;
using Amazon.S3;
using Amazon.S3.Model;
using DiskTree.Core.Storage;

namespace DiskTree.Features.Storage.S3;

/// <summary>
/// <see cref="IS3Client"/> over the AWS SDK. The only class (with <see cref="S3Errors"/>) that touches SDK types, so
/// the SDK assemblies are loaded when the first S3 source is used, not at the start of the program.
/// </summary>
internal sealed class AwsS3Client : IS3Client, IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly AmazonS3Client _client;
    private readonly string _where;

    private AwsS3Client(AmazonS3Client client, string where)
    {
        _client = client;
        _where = where;
    }

    /// <summary>
    /// A client for a saved profile (null: no profile, the AWS default credential chain). <paramref name="secret"/> is
    /// the secret access key for <see cref="SourceAuth.AccessKey"/> (read from the Credential Manager by the caller).
    /// </summary>
    public static IS3Client Create(SourceProfile? profile, string? secret)
    {
        var config = new AmazonS3Config
        {
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            Timeout = RequestTimeout,
            MaxErrorRetry = 2,
            ForcePathStyle = profile?.PathStyle ?? false,
        };

        AWSCredentials? credentials = null;
        string? profileRegion = null;
        switch (profile?.Auth ?? SourceAuth.Default)
        {
            case SourceAuth.AccessKey:
                if (string.IsNullOrWhiteSpace(profile!.UserName))
                    throw new UnauthorizedAccessException("Für diese Quelle ist keine Access-Key-ID eingetragen.");
                if (string.IsNullOrEmpty(secret))
                    throw new UnauthorizedAccessException(
                        $"Für die Quelle „{profile.Name}“ ist kein Secret-Access-Key gespeichert (Quelle bearbeiten und den Schlüssel eingeben).");
                credentials = new BasicAWSCredentials(profile.UserName.Trim(), secret);
                break;
            case SourceAuth.NamedProfile:
                var chain = new CredentialProfileStoreChain();
                var name = profile!.AwsProfile.Trim();
                if (!chain.TryGetAWSCredentials(name, out credentials))
                    throw new UnauthorizedAccessException($"Das AWS-Profil „{name}“ wurde in ~/.aws/config bzw. ~/.aws/credentials nicht gefunden.");
                if (chain.TryGetProfile(name, out var cp)) profileRegion = cp.Region?.SystemName;
                break;
        }

        var region = FirstNonEmpty(profile?.Region, profileRegion,
            Environment.GetEnvironmentVariable("AWS_REGION"), Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION"));
        string where;
        if (!string.IsNullOrWhiteSpace(profile?.Endpoint))
        {
            // S3-compatible server: the region only matters for the signature.
            config.ServiceURL = profile.Endpoint.Trim();
            config.AuthenticationRegion = region ?? "us-east-1";
            where = profile.Endpoint.Trim();
        }
        else
        {
            // AWS: buckets of other regions are redirected by the SDK (bucket region lookup).
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(region ?? "us-east-1");
            where = "AWS, Region " + (region ?? "us-east-1");
        }

        var client = credentials != null ? new AmazonS3Client(credentials, config) : new AmazonS3Client(config);
        return new AwsS3Client(client, where);
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    public string Describe() => _where;

    public async Task<S3ListPage> ListAsync(string bucket, string prefix, string? delimiter, string? continuationToken, int maxKeys,
        CancellationToken ct)
    {
        try
        {
            var r = await _client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucket,
                Prefix = prefix,
                Delimiter = delimiter,
                ContinuationToken = continuationToken,
                MaxKeys = maxKeys,
            }, ct).ConfigureAwait(false);

            var objects = r.S3Objects is { } os
                ? os.Select(o => new S3ObjectInfo(o.Key, o.Size ?? 0, o.LastModified is { } m ? m.ToUniversalTime().Ticks : 0,
                    o.StorageClass?.Value)).ToList()
                : new List<S3ObjectInfo>();
            var prefixes = r.CommonPrefixes ?? new List<string>();
            return new S3ListPage(objects, prefixes, r.IsTruncated == true ? r.NextContinuationToken : null);
        }
        catch (Exception ex) when (S3Errors.Translate(ex, ct, bucket) is { } mapped)
        {
            throw mapped;
        }
    }

    public async Task<IReadOnlyList<string>> ListBucketsAsync(CancellationToken ct)
    {
        try
        {
            var r = await _client.ListBucketsAsync(new ListBucketsRequest(), ct).ConfigureAwait(false);
            return r.Buckets?.Select(b => b.BucketName).ToList() ?? new List<string>();
        }
        catch (Exception ex) when (S3Errors.Translate(ex, ct, null) is { } mapped)
        {
            throw mapped;
        }
    }

    public void Dispose() => _client.Dispose();
}

/// <summary>Turns SDK / network errors into the exceptions the scanner counts as "nicht lesbar", with German messages.</summary>
public static class S3Errors
{
    private static readonly HashSet<string> AuthCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "AccessDenied", "InvalidAccessKeyId", "SignatureDoesNotMatch", "ExpiredToken", "InvalidToken", "TokenRefreshRequired",
        "AllAccessDisabled", "AccountProblem", "InvalidSecurity", "UnauthorizedAccess", "InvalidClientTokenId",
    };

    /// <summary>
    /// The exception to throw instead of <paramref name="ex"/>, or null to let it pass (cancellation by
    /// <paramref name="ct"/> and exceptions that are already translated).
    /// </summary>
    public static Exception? Translate(Exception ex, CancellationToken ct, string? bucket)
    {
        if (ex is OperationCanceledException && ct.IsCancellationRequested) return null; // Esc: stays a cancellation
        if (ex is UnauthorizedAccessException or DirectoryNotFoundException) return null; // already translated
        string b = bucket != null ? $"„{bucket}“" : "";

        switch (ex)
        {
            case OperationCanceledException:
                return new IOException("Zeitüberschreitung: der S3-Server hat nicht rechtzeitig geantwortet.", ex);
            case AmazonServiceException s:
                var code = s.ErrorCode ?? "";
                if (code.Equals("NoSuchBucket", StringComparison.OrdinalIgnoreCase))
                    return new DirectoryNotFoundException($"Den Bucket {b} gibt es nicht.", ex);
                if (AuthCodes.Contains(code) || s.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                    return new UnauthorizedAccessException(AuthText(code, b), ex);
                if (code.Equals("PermanentRedirect", StringComparison.OrdinalIgnoreCase) || s.StatusCode == HttpStatusCode.MovedPermanently)
                    return new IOException($"Der Bucket {b} liegt in einer anderen Region: bitte die Region der Quelle eintragen.", ex);
                return new IOException($"S3-Fehler{(code.Length > 0 ? " " + code : "")} ({(int)s.StatusCode}): {s.Message}", ex);
        }

        if (Find<AuthenticationException>(ex) is { } tls)
            return new IOException("Sichere Verbindung fehlgeschlagen (Zertifikat/TLS): " + tls.Message, ex);
        if (Find<SocketException>(ex) is { } sock)
            return new IOException("Der S3-Server ist nicht erreichbar: " + sock.Message, ex);
        if (Find<HttpRequestException>(ex) is { } http)
            return new IOException("Der S3-Server ist nicht erreichbar: " + http.Message, ex);
        if (ex is AmazonClientException c)
        {
            if (c.Message.Contains("credential", StringComparison.OrdinalIgnoreCase))
                return new UnauthorizedAccessException("Keine gültigen AWS-Zugangsdaten gefunden: " + c.Message, ex);
            return new IOException("S3: " + c.Message, ex);
        }
        if (ex is IOException io) return new IOException("Die Verbindung zum S3-Server wurde unterbrochen: " + io.Message, ex);
        if (ex is TimeoutException) return new IOException("Zeitüberschreitung bei der Verbindung zum S3-Server.", ex);
        return null; // bugs stay bugs
    }

    private static string AuthText(string code, string bucket) => code switch
    {
        "InvalidAccessKeyId" or "InvalidClientTokenId" => "Die Access-Key-ID ist dem S3-Server unbekannt.",
        "SignatureDoesNotMatch" => "Der Secret-Access-Key passt nicht zur Access-Key-ID (Signatur abgelehnt).",
        "ExpiredToken" or "TokenRefreshRequired" => "Die Anmeldung ist abgelaufen (Token/SSO-Sitzung erneuern).",
        _ => $"Zugriff verweigert{(bucket.Length > 0 ? " auf " + bucket : "")}{(code.Length > 0 ? $" ({code})" : "")}.",
    };

    private static T? Find<T>(Exception? ex) where T : Exception
    {
        for (; ex != null; ex = ex.InnerException)
            if (ex is T t) return t;
        return null;
    }
}
