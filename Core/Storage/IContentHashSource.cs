using System.IO;

namespace DiskTree.Core.Storage;

/// <summary>What a content tag from a listing (S3: the ETag) says about the content, before anything is asked.</summary>
public enum ContentTagKind
{
    /// <summary>No usable tag: only reading the content can tell.</summary>
    None,
    /// <summary>Equal tags make a candidate, nothing more (S3: multipart ETag "…-N", which depends on the part size).</summary>
    Weak,
    /// <summary>
    /// Probably a hash of the content (S3: single-part ETag = MD5). <see cref="IContentHashSource.TryGetContentTag"/>
    /// confirms it (S3: no SSE-KMS / SSE-C, whose ETags are no MD5).
    /// </summary>
    Strong,
}

/// <summary>
/// Optional for remote sources whose file contents the duplicate search may read (S3, SFTP). Kept out of
/// <see cref="IStorageProvider"/>: the disk reads its files directly, other sources do not offer this at all.
/// <para>
/// Every request goes through the <see cref="ListRun"/> of the search: it is counted (and paused at the cost limit,
/// <see cref="ListRun.BeforeRequest"/>), and its <see cref="ListRun.Cancellation"/> ends running downloads.
/// Contents are only handed out as streams to be hashed in memory; nothing is written to disk.
/// </para>
/// </summary>
public interface IContentHashSource
{
    /// <summary>What a listed tag says without a request (see <see cref="ContentTagKind"/>).</summary>
    ContentTagKind ClassifyTag(string? listedTag);

    /// <summary>Requests <see cref="TryGetContentTag"/> sends for a tag classified <see cref="ContentTagKind.Strong"/> (S3: one HEAD).</summary>
    int TagRequests { get; }

    /// <summary>
    /// The content tag of a file as the source confirms it (S3: HEAD → ETag and encryption). False: the source has no
    /// tag for it (then only reading tells). <paramref name="isStrong"/>: equal strong tags of files of the same size
    /// mean equal content. Errors (gone, no access, network) are thrown as <see cref="IOException"/> /
    /// <see cref="UnauthorizedAccessException"/>.
    /// </summary>
    bool TryGetContentTag(string path, string? listedTag, ListRun run, out string tag, out bool isStrong);

    /// <summary>
    /// <paramref name="length"/> bytes of the file from <paramref name="offset"/> on, as a read-only stream (read it with
    /// the run's token). The stream ends early if the file is shorter.
    /// </summary>
    Stream OpenRead(string path, long offset, long length, ListRun run);

    /// <summary>Requests one <see cref="OpenRead"/> of <paramref name="length"/> bytes costs (for the estimate; 0 = no per-request costs).</summary>
    long RequestsFor(long length);
}

/// <summary>One file of a deep listing (all levels below a folder).</summary>
/// <param name="Path">Full path in the provider's form.</param>
/// <param name="Length">Size in bytes.</param>
/// <param name="ContentTag">The listing's content tag (S3: ETag), null if none.</param>
public readonly record struct DeepFile(string Path, long Length, string? ContentTag);

/// <summary>
/// Optional: a source that lists all files below a folder more cheaply than folder by folder (S3: ListObjectsV2
/// without delimiter, one request per 1000 objects for the whole subtree). Links are left out.
/// </summary>
public interface IDeepListing
{
    /// <summary>
    /// Every file below <paramref name="folder"/>, lazily. <see cref="ListOptions.Run"/> counts the requests and cancels.
    /// Errors are thrown as <see cref="IOException"/> / <see cref="UnauthorizedAccessException"/>.
    /// </summary>
    IEnumerable<DeepFile> ListFiles(string folder, ListOptions options);
}
