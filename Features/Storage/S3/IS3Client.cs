namespace DiskTree.Features.Storage.S3;

/// <summary>One object of a listing.</summary>
public readonly record struct S3ObjectInfo(string Key, long Size, long ModifiedTicks, string? StorageClass = null);

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

    /// <summary>Names of the buckets the credentials can see (connection test without a bucket).</summary>
    Task<IReadOnlyList<string>> ListBucketsAsync(CancellationToken ct);

    /// <summary>A short description of where the client connects ("AWS, Region eu-central-1", "http://localhost:9000").</summary>
    string Describe();
}
