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

    /// <summary>CopyObject of up to 5 GB runs on the server and may take minutes: its own client with a longer timeout.</summary>
    private static readonly TimeSpan CopyTimeout = TimeSpan.FromMinutes(15);

    private readonly AmazonS3Client _client;
    private readonly Lazy<AmazonS3Client> _copyClient;
    private readonly string _where;

    private AwsS3Client(AmazonS3Client client, Func<AmazonS3Client> copyClient, string where)
    {
        _client = client;
        _copyClient = new Lazy<AmazonS3Client>(copyClient);
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
        var creds = credentials;
        AmazonS3Client CopyClient()
        {
            var c = new AmazonS3Config
            {
                RequestChecksumCalculation = config.RequestChecksumCalculation,
                ResponseChecksumValidation = config.ResponseChecksumValidation,
                Timeout = CopyTimeout,
                MaxErrorRetry = config.MaxErrorRetry,
                ForcePathStyle = config.ForcePathStyle,
            };
            if (config.ServiceURL != null)
            {
                c.ServiceURL = config.ServiceURL;
                c.AuthenticationRegion = config.AuthenticationRegion;
            }
            else c.RegionEndpoint = config.RegionEndpoint;
            return creds != null ? new AmazonS3Client(creds, c) : new AmazonS3Client(c);
        }
        return new AwsS3Client(client, CopyClient, where);
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
                    o.StorageClass?.Value, o.ETag)).ToList()
                : new List<S3ObjectInfo>();
            var prefixes = r.CommonPrefixes ?? new List<string>();
            return new S3ListPage(objects, prefixes, r.IsTruncated == true ? r.NextContinuationToken : null);
        }
        catch (Exception ex) when (S3Errors.Translate(ex, ct, bucket) is { } mapped)
        {
            throw mapped;
        }
    }

    public async Task<S3ListPage> ListWithOwnersAsync(string bucket, string prefix, string? continuationToken, int maxKeys, CancellationToken ct)
    {
        try
        {
            var r = await _client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucket,
                Prefix = prefix,
                ContinuationToken = continuationToken,
                MaxKeys = maxKeys,
                FetchOwner = true,
            }, ct).ConfigureAwait(false);

            var objects = r.S3Objects is { } os
                ? os.Select(o => new S3ObjectInfo(o.Key, o.Size ?? 0, o.LastModified is { } m ? m.ToUniversalTime().Ticks : 0,
                    o.StorageClass?.Value, o.ETag, OwnerOf(o.Owner))).ToList()
                : new List<S3ObjectInfo>();
            return new S3ListPage(objects, [], r.IsTruncated == true ? r.NextContinuationToken : null);
        }
        catch (Exception ex) when (S3Errors.Translate(ex, ct, bucket) is { } mapped)
        {
            throw mapped;
        }
    }

    private static S3Owner? OwnerOf(Amazon.S3.Model.Owner? o) =>
        o == null || (string.IsNullOrEmpty(o.Id) && string.IsNullOrEmpty(o.DisplayName))
            ? null
            : new S3Owner(o.Id ?? "", string.IsNullOrWhiteSpace(o.DisplayName) ? null : o.DisplayName);

    public async Task<S3Acl> GetBucketAclAsync(string bucket, CancellationToken ct)
    {
        try
        {
            var r = await _client.GetBucketAclAsync(new GetBucketAclRequest { BucketName = bucket }, ct).ConfigureAwait(false);
            var grants = r.Grants?.Where(g => g.Grantee != null).Select(g => new S3Grant(
                g.Grantee.Type?.Value ?? "",
                g.Grantee.CanonicalUser ?? g.Grantee.URI ?? g.Grantee.EmailAddress ?? "",
                string.IsNullOrWhiteSpace(g.Grantee.DisplayName) ? null : g.Grantee.DisplayName,
                g.Permission?.Value ?? "")).ToList() ?? [];
            return new S3Acl(OwnerOf(r.Owner), grants);
        }
        catch (Exception ex) when (S3Errors.Translate(ex, ct, bucket) is { } mapped)
        {
            throw mapped;
        }
    }

    public async Task<string?> GetObjectOwnershipAsync(string bucket, CancellationToken ct)
    {
        try
        {
            var r = await _client.GetBucketOwnershipControlsAsync(new GetBucketOwnershipControlsRequest { BucketName = bucket }, ct)
                .ConfigureAwait(false);
            return r.OwnershipControls?.Rules?.FirstOrDefault()?.ObjectOwnership?.Value;
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode == "OwnershipControlsNotFoundError")
        {
            return null;
        }
        catch (Exception ex) when (S3Errors.Translate(ex, ct, bucket) is { } mapped)
        {
            throw mapped;
        }
    }

    public async Task<S3PublicAccessBlock?> GetPublicAccessBlockAsync(string bucket, CancellationToken ct)
    {
        try
        {
            var r = await _client.GetPublicAccessBlockAsync(new GetPublicAccessBlockRequest { BucketName = bucket }, ct).ConfigureAwait(false);
            var c = r.PublicAccessBlockConfiguration;
            return c == null ? null : new S3PublicAccessBlock(c.BlockPublicAcls == true, c.IgnorePublicAcls == true,
                c.BlockPublicPolicy == true, c.RestrictPublicBuckets == true);
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode == "NoSuchPublicAccessBlockConfiguration")
        {
            return null;
        }
        catch (Exception ex) when (S3Errors.Translate(ex, ct, bucket) is { } mapped)
        {
            throw mapped;
        }
    }

    public async Task<bool?> GetPolicyIsPublicAsync(string bucket, CancellationToken ct)
    {
        try
        {
            var r = await _client.GetBucketPolicyStatusAsync(new GetBucketPolicyStatusRequest { BucketName = bucket }, ct).ConfigureAwait(false);
            return r.PolicyStatus?.IsPublic == true;
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode == "NoSuchBucketPolicy")
        {
            return null;
        }
        catch (Exception ex) when (S3Errors.Translate(ex, ct, bucket) is { } mapped)
        {
            throw mapped;
        }
    }

    public async Task<S3ObjectHead> HeadAsync(string bucket, string key, CancellationToken ct)
    {
        try
        {
            var r = await _client.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = key }, ct)
                .ConfigureAwait(false);
            var sse = r.ServerSideEncryptionMethod?.Value;
            var cls = r.StorageClass?.Value;
            var archive = r.ArchiveStatus?.Value;
            return new S3ObjectHead(r.ContentLength, r.ETag,
                string.IsNullOrEmpty(sse) || sse.Equals("None", StringComparison.OrdinalIgnoreCase) ? null : sse,
                r.ServerSideEncryptionCustomerMethod != null && r.ServerSideEncryptionCustomerMethod != ServerSideEncryptionCustomerMethod.None)
            {
                ModifiedTicks = r.LastModified is { } m ? m.ToUniversalTime().Ticks : 0,
                StorageClass = string.IsNullOrEmpty(cls) ? null : cls,
                KmsKeyId = string.IsNullOrEmpty(r.ServerSideEncryptionKeyManagementServiceKeyId) ? null : r.ServerSideEncryptionKeyManagementServiceKeyId,
                BucketKey = r.BucketKeyEnabled == true,
                Restored = r.RestoreInProgress == false && r.RestoreExpiration != null,
                ArchiveStatus = string.IsNullOrEmpty(archive) ? null : archive,
                ContentType = string.IsNullOrEmpty(r.Headers?.ContentType) ? null : r.Headers.ContentType,
                Metadata = r.Metadata?.Keys.ToDictionary(
                    k => (k.StartsWith("x-amz-meta-", StringComparison.OrdinalIgnoreCase) ? k[11..] : k).ToLowerInvariant(),
                    k => r.Metadata[k] ?? "", StringComparer.Ordinal),
                TagCount = r.TagsCount ?? 0,
            };
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.BadRequest && !ct.IsCancellationRequested
                                           && (string.IsNullOrEmpty(ex.ErrorCode) || ex.ErrorCode == "BadRequest"))
        {
            // HEAD of an object encrypted with a key of the customer (SSE-C), sent without that key.
            throw new S3CustomerKeyException(S3Errors.CustomerKeyText, ex);
        }
        catch (Exception ex) when (S3Errors.Translate(ex, ct, bucket) is { } mapped)
        {
            throw mapped;
        }
    }

    public async Task<int> ReadRangeAsync(string bucket, string key, long offset, Memory<byte> buffer, CancellationToken ct)
    {
        if (buffer.Length == 0) return 0;
        try
        {
            using var r = await _client.GetObjectAsync(new GetObjectRequest
            {
                BucketName = bucket,
                Key = key,
                ByteRange = new ByteRange(offset, offset + buffer.Length - 1),
            }, ct).ConfigureAwait(false);
            int total = 0;
            while (total < buffer.Length)
            {
                int n = await r.ResponseStream.ReadAsync(buffer[total..], ct).ConfigureAwait(false);
                if (n <= 0) break;
                total += n;
            }
            return total;
        }
        catch (Exception ex) when (S3Errors.Translate(ex, ct, bucket) is { } mapped)
        {
            throw mapped;
        }
    }

    public async Task CopyAsync(S3CopyRequest request, CancellationToken ct)
    {
        var r = new CopyObjectRequest
        {
            SourceBucket = request.Bucket,
            SourceKey = request.SourceKey,
            DestinationBucket = request.Bucket,
            DestinationKey = request.TargetKey,
            MetadataDirective = S3MetadataDirective.COPY,
            TaggingDirective = TaggingDirective.COPY,
        };
        // Without these S3 would write STANDARD and the bucket's default encryption.
        if (request.StorageClass != null) r.StorageClass = S3StorageClass.FindValue(request.StorageClass);
        if (request.Encryption != null)
        {
            r.ServerSideEncryptionMethod = ServerSideEncryptionMethod.FindValue(request.Encryption);
            if (request.KmsKeyId != null) r.ServerSideEncryptionKeyManagementServiceKeyId = request.KmsKeyId;
            if (request.BucketKey) r.BucketKeyEnabled = true;
        }
        try
        {
            await _copyClient.Value.CopyObjectAsync(r, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (S3Errors.Translate(ex, ct, request.Bucket) is { } mapped)
        {
            throw mapped;
        }
    }

    public async Task<IReadOnlyList<S3DeleteError>> DeleteManyAsync(string bucket, IReadOnlyList<string> keys, CancellationToken ct)
    {
        if (keys.Count == 0) return [];
        var r = new DeleteObjectsRequest
        {
            BucketName = bucket,
            Quiet = true,
            Objects = keys.Select(k => new KeyVersion { Key = k }).ToList(),
        };
        try
        {
            var resp = await _client.DeleteObjectsAsync(r, ct).ConfigureAwait(false);
            return Errors(resp);
        }
        catch (DeleteObjectsException ex) when (ex.Response != null)
        {
            return Errors(ex.Response); // some keys failed: the SDK throws, its response says which
        }
        catch (Exception ex) when (S3Errors.Translate(ex, ct, bucket) is { } mapped)
        {
            throw mapped;
        }

        static IReadOnlyList<S3DeleteError> Errors(DeleteObjectsResponse resp) =>
            resp.DeleteErrors?.Select(e => new S3DeleteError(e.Key, e.Code ?? "", e.Message ?? "")).ToList() ?? [];
    }

    public async Task<IReadOnlyList<KeyValuePair<string, string>>> GetTagsAsync(string bucket, string key, CancellationToken ct)
    {
        try
        {
            var r = await _client.GetObjectTaggingAsync(new GetObjectTaggingRequest { BucketName = bucket, Key = key }, ct).ConfigureAwait(false);
            return r.Tagging?.Select(t => new KeyValuePair<string, string>(t.Key, t.Value ?? "")).ToList() ?? [];
        }
        catch (Exception ex) when (S3Errors.Translate(ex, ct, bucket) is { } mapped)
        {
            throw mapped;
        }
    }

    public async Task PutTagsAsync(string bucket, string key, IReadOnlyList<KeyValuePair<string, string>> tags, CancellationToken ct)
    {
        try
        {
            await _client.PutObjectTaggingAsync(new PutObjectTaggingRequest
            {
                BucketName = bucket,
                Key = key,
                Tagging = new Tagging { TagSet = tags.Select(t => new Tag { Key = t.Key, Value = t.Value }).ToList() },
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (S3Errors.Translate(ex, ct, bucket) is { } mapped)
        {
            throw mapped;
        }
    }

    public async Task<S3Versioning> GetVersioningAsync(string bucket, CancellationToken ct)
    {
        try
        {
            var r = await _client.GetBucketVersioningAsync(new GetBucketVersioningRequest { BucketName = bucket }, ct).ConfigureAwait(false);
            var status = r.VersioningConfig?.Status?.Value;
            if (string.Equals(status, VersionStatus.Enabled.Value, StringComparison.OrdinalIgnoreCase)) return S3Versioning.Enabled;
            if (string.Equals(status, VersionStatus.Suspended.Value, StringComparison.OrdinalIgnoreCase)) return S3Versioning.Suspended;
            return S3Versioning.Off;
        }
        catch (Exception ex) when (S3Errors.Translate(ex, ct, bucket) is { } mapped)
        {
            throw mapped;
        }
    }

    public async Task PutEmptyAsync(string bucket, string key, CancellationToken ct)
    {
        try
        {
            await _client.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "" }, ct).ConfigureAwait(false);
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

    public void Dispose()
    {
        _client.Dispose();
        if (_copyClient.IsValueCreated) _copyClient.Value.Dispose();
    }
}

/// <summary>Turns SDK / network errors into the exceptions the scanner counts as "nicht lesbar", with German messages.</summary>
public static class S3Errors
{
    /// <summary>HEAD answered 400 without details: the object is encrypted with a key of the customer (SSE-C).</summary>
    public const string CustomerKeyText =
        "Das Objekt lässt sich ohne seinen Schlüssel nicht lesen (400): es ist vermutlich mit einem eigenen Schlüssel des Kunden " +
        "verschlüsselt (SSE-C). Ohne diesen Schlüssel kann DiskTree es weder prüfen noch kopieren.";

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
                if (code.Equals("NoSuchKey", StringComparison.OrdinalIgnoreCase)
                    || (s.StatusCode == HttpStatusCode.NotFound && (code.Length == 0 || code.Equals("NotFound", StringComparison.OrdinalIgnoreCase))))
                    return new FileNotFoundException("Das Objekt gibt es nicht (mehr).", ex);
                if (code.Equals("InvalidObjectState", StringComparison.OrdinalIgnoreCase))
                    return new IOException("Das Objekt ist archiviert (Glacier/Deep Archive bzw. Archiv-Stufe): es muss erst " +
                                           "wiederhergestellt werden, bevor es kopiert werden kann (InvalidObjectState).", ex);
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
