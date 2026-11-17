namespace DiskTree.Features.Storage.S3;

/// <summary>
/// One object of a listing; <paramref name="ETag"/> as the server sends it (with quotes), null if none.
/// <paramref name="Owner"/> only with <see cref="IS3Client.ListWithOwnersAsync"/> (and only if the server sends it).
/// </summary>
public readonly record struct S3ObjectInfo(string Key, long Size, long ModifiedTicks, string? StorageClass = null, string? ETag = null,
    S3Owner? Owner = null);

/// <summary>
/// An S3 account as owner: the canonical user id (64 hex digits on AWS) and, where the server still sends it, a display
/// name (AWS stopped sending display names in 2025; MinIO and others send one).
/// </summary>
public sealed record S3Owner(string Id, string? DisplayName = null);

/// <summary>
/// One grant of an ACL. <paramref name="Type"/>: "CanonicalUser", "Group" (<paramref name="Id"/> is the group URI) or
/// "AmazonCustomerByEmail" (<paramref name="Id"/> is the address). <paramref name="Permission"/>: FULL_CONTROL, READ,
/// WRITE, READ_ACP, WRITE_ACP.
/// </summary>
public sealed record S3Grant(string Type, string Id, string? DisplayName, string Permission);

/// <summary>GetBucketAcl: the bucket's owner and its grants.</summary>
public sealed record S3Acl(S3Owner? Owner, IReadOnlyList<S3Grant> Grants);

/// <summary>Block Public Access of a bucket (the four switches).</summary>
public sealed record S3PublicAccessBlock(bool BlockPublicAcls, bool IgnorePublicAcls, bool BlockPublicPolicy, bool RestrictPublicBuckets)
{
    public bool All => BlockPublicAcls && IgnorePublicAcls && BlockPublicPolicy && RestrictPublicBuckets;
}

/// <summary>
/// What HeadObject says about an object: size, ETag (with quotes) and its server-side encryption ("AES256", "aws:kms",
/// "aws:kms:dsse"; null = none). <paramref name="CustomerKey"/>: encrypted with a key of the customer (SSE-C).
/// </summary>
public sealed record S3ObjectHead(long Size, string? ETag, string? Encryption, bool CustomerKey = false)
{
    /// <summary>Last-Modified as UTC ticks (0 = unknown).</summary>
    public long ModifiedTicks { get; init; }

    /// <summary>Storage class ("STANDARD_IA", "GLACIER" …); null = STANDARD (S3 sends no header for it).</summary>
    public string? StorageClass { get; init; }

    /// <summary>The KMS key of SSE-KMS / DSSE-KMS (ARN or id), null otherwise.</summary>
    public string? KmsKeyId { get; init; }

    /// <summary>SSE-KMS with an S3 Bucket Key.</summary>
    public bool BucketKey { get; init; }

    /// <summary>An archived object (GLACIER / DEEP_ARCHIVE) with a finished restore: its temporary copy can be read and copied.</summary>
    public bool Restored { get; init; }

    /// <summary>Intelligent-Tiering archive tier ("ARCHIVE_ACCESS", "DEEP_ARCHIVE_ACCESS"): needs a restore first; null = readable.</summary>
    public string? ArchiveStatus { get; init; }

    /// <summary>Content-Type (null = none sent).</summary>
    public string? ContentType { get; init; }

    /// <summary>User metadata (x-amz-meta-*), names in lower case without the prefix.</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    /// <summary>Number of tags (x-amz-tagging-count; 0 = none or not reported).</summary>
    public int TagCount { get; init; }
}

/// <summary>
/// CopyObject inside one bucket. Metadata, Content-Type and tags are copied (MetadataDirective / TaggingDirective COPY);
/// the storage class and the server-side encryption have to be given, or S3 would use STANDARD and the bucket default.
/// </summary>
/// <param name="StorageClass">null = STANDARD.</param>
/// <param name="Encryption">"AES256", "aws:kms", "aws:kms:dsse" or null (bucket default).</param>
/// <param name="KmsKeyId">The KMS key for "aws:kms" / "aws:kms:dsse" (the one of the original).</param>
public sealed record S3CopyRequest(string Bucket, string SourceKey, string TargetKey, string? StorageClass = null,
    string? Encryption = null, string? KmsKeyId = null, bool BucketKey = false);

/// <summary>A key that DeleteObjects could not delete, with the S3 error code ("AccessDenied" …).</summary>
public readonly record struct S3DeleteError(string Key, string Code, string Message);

/// <summary>Versioning of a bucket: off (never on), enabled, or suspended (old versions are kept).</summary>
public enum S3Versioning { Off, Enabled, Suspended }

/// <summary>One page of ListObjectsV2: objects, common prefixes (with delimiter) and the token for the next page (null = last).</summary>
public sealed record S3ListPage(IReadOnlyList<S3ObjectInfo> Objects, IReadOnlyList<string> Prefixes, string? NextToken);

/// <summary>
/// The few S3 calls DiskTree needs, without SDK types in the signature: the provider (loaded at start) only knows
/// this interface, the AWS SDK is loaded when the first client is created (<see cref="AwsS3Client"/>). Tests use an
/// in-memory implementation.
/// <para>
/// Errors arrive already translated: <see cref="UnauthorizedAccessException"/> (sign-in / access denied),
/// <see cref="System.IO.DirectoryNotFoundException"/> (no such bucket), <see cref="System.IO.IOException"/> (network,
/// timeout, other service errors), <see cref="OperationCanceledException"/> only when <c>ct</c> was cancelled.
/// Messages are German and never contain secrets.
/// </para>
/// </summary>
public interface IS3Client
{
    /// <summary>ListObjectsV2 of <paramref name="bucket"/> below <paramref name="prefix"/>; <paramref name="delimiter"/> "/" or null (flat).</summary>
    Task<S3ListPage> ListAsync(string bucket, string prefix, string? delimiter, string? continuationToken, int maxKeys, CancellationToken ct);

    /// <summary>HeadObject: size, ETag and encryption of one object (duplicate search, fast mode).</summary>
    Task<S3ObjectHead> HeadAsync(string bucket, string key, CancellationToken ct);

    /// <summary>
    /// GetObject with a byte range: reads up to <c>buffer.Length</c> bytes of the object from <paramref name="offset"/>
    /// into <paramref name="buffer"/> (memory only) and returns how many arrived (fewer at the end of the object).
    /// </summary>
    Task<int> ReadRangeAsync(string bucket, string key, long offset, Memory<byte> buffer, CancellationToken ct);

    /// <summary>
    /// CopyObject (objects up to 5 GB, inside one bucket). Throws like the other calls; an archived object without
    /// restore comes as <see cref="System.IO.IOException"/> (InvalidObjectState).
    /// </summary>
    Task CopyAsync(S3CopyRequest request, CancellationToken ct);

    /// <summary>
    /// DeleteObjects with up to 1000 keys (quiet): returns the keys that could not be deleted. With versioning only
    /// delete markers are written (no version id is sent). A missing key counts as deleted.
    /// </summary>
    Task<IReadOnlyList<S3DeleteError>> DeleteManyAsync(string bucket, IReadOnlyList<string> keys, CancellationToken ct);

    /// <summary>GetObjectTagging: the tags of an object.</summary>
    Task<IReadOnlyList<KeyValuePair<string, string>>> GetTagsAsync(string bucket, string key, CancellationToken ct);

    /// <summary>PutObjectTagging: replaces the tags of an object (repairs a copy of a server that ignores TaggingDirective COPY).</summary>
    Task PutTagsAsync(string bucket, string key, IReadOnlyList<KeyValuePair<string, string>> tags, CancellationToken ct);

    /// <summary>GetBucketVersioning.</summary>
    Task<S3Versioning> GetVersioningAsync(string bucket, CancellationToken ct);

    /// <summary>PutObject without content (a folder marker "prefix/").</summary>
    Task PutEmptyAsync(string bucket, string key, CancellationToken ct);

    /// <summary>ListObjectsV2 without delimiter and with FetchOwner: every object below the prefix with its owner ("Speicher pro Besitzer").</summary>
    Task<S3ListPage> ListWithOwnersAsync(string bucket, string prefix, string? continuationToken, int maxKeys, CancellationToken ct);

    /// <summary>GetBucketAcl: owner and grants of the bucket.</summary>
    Task<S3Acl> GetBucketAclAsync(string bucket, CancellationToken ct);

    /// <summary>
    /// GetBucketOwnershipControls: "BucketOwnerEnforced" (ACLs off), "BucketOwnerPreferred" or "ObjectWriter";
    /// null if the bucket has no such setting.
    /// </summary>
    Task<string?> GetObjectOwnershipAsync(string bucket, CancellationToken ct);

    /// <summary>GetPublicAccessBlock of the bucket; null if none is set.</summary>
    Task<S3PublicAccessBlock?> GetPublicAccessBlockAsync(string bucket, CancellationToken ct);

    /// <summary>GetBucketPolicyStatus: whether the bucket policy makes the bucket public; null if the bucket has no policy.</summary>
    Task<bool?> GetPolicyIsPublicAsync(string bucket, CancellationToken ct);

    /// <summary>Names of the buckets the credentials can see (connection test without a bucket).</summary>
    Task<IReadOnlyList<string>> ListBucketsAsync(CancellationToken ct);

    /// <summary>A short description of where the client connects ("AWS, Region eu-central-1", "http://localhost:9000").</summary>
    string Describe();
}
