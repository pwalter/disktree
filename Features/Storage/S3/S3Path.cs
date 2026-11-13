using System.IO;
using DiskTree.Core.Storage;

namespace DiskTree.Features.Storage.S3;

/// <summary>
/// Paths of S3 objects as the tree carries them: "s3://bucket/a/b" (no separator at the end, '/' between the parts,
/// case-sensitive). A folder is a key prefix: "s3://bucket/a/b" lists the keys below "a/b/", the bucket itself
/// ("s3://bucket") the keys below "". Keys may contain empty parts ("a//b", "/x"); such a part becomes the name
/// <see cref="EmptySegment"/> (shown as "(leer)") so every folder still has a path of its own.
/// </summary>
public static class S3Path
{
    public const string Scheme = "s3://";

    /// <summary>Name of an empty key part ("a//b"). The invisible word joiner keeps it apart from a real folder "(leer)".</summary>
    public const string EmptySegment = "(leer)⁠";

    public static bool HasScheme(string? text) =>
        text != null && text.TrimStart().TrimStart('"').StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// "S3://bucket/a//b/" gives "s3://bucket/a/(leer)/b": scheme in lower case, one separator at the end dropped (it
    /// only says "folder"), empty parts named. Text without bucket gives "s3://". Throws <see cref="ArgumentException"/>
    /// for text that is no S3 path.
    /// </summary>
    public static string Normalize(string text)
    {
        var t = text.Trim().Trim('"').Trim();
        if (!t.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Kein S3-Pfad (s3://bucket/…): " + text);
        var rest = t[Scheme.Length..];
        if (rest.EndsWith('/')) rest = rest[..^1];
        int slash = rest.IndexOf('/');
        var bucket = slash < 0 ? rest : rest[..slash];
        if (bucket.Length == 0) return Scheme;
        if (bucket.Any(char.IsWhiteSpace) || bucket.Contains('\\'))
            throw new ArgumentException("Ungültiger Bucket-Name: " + bucket);
        if (slash < 0) return Scheme + bucket;

        var parts = rest[(slash + 1)..].Split('/');
        return Scheme + bucket + "/" + string.Join('/', parts.Select(Encode));
    }

    /// <summary>Bucket and key prefix of a normalized folder path ("s3://b/a/(leer)" gives "b", "a//"); bucket "" for "s3://".</summary>
    public static (string Bucket, string Prefix) Split(string path)
    {
        if (!path.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Kein S3-Pfad: " + path);
        var rest = path[Scheme.Length..];
        int slash = rest.IndexOf('/');
        if (slash < 0) return (rest, "");
        var parts = rest[(slash + 1)..].Split('/');
        return (rest[..slash], string.Join('/', parts.Select(Decode)) + "/");
    }

    /// <summary>The bucket a path lies in ("" for "s3://").</summary>
    public static string BucketOf(string path) => Split(path).Bucket;

    /// <summary>A key part as it appears in the tree ("" becomes <see cref="EmptySegment"/>).</summary>
    public static string Encode(string part) => part.Length == 0 ? EmptySegment : part;

    /// <summary>A name of the tree as a key part (<see cref="EmptySegment"/> becomes "").</summary>
    public static string Decode(string name) => name == EmptySegment ? "" : name;
}

/// <summary>
/// Path rules for S3: '/' and ordinal comparison like the base class; "s3://bucket" is a root (no parent, its name is
/// the bucket), joining never drops an empty name (it becomes <see cref="S3Path.EmptySegment"/>).
/// </summary>
public sealed class S3PathOps : PathOps
{
    public static readonly S3PathOps Instance = new();

    private S3PathOps() : base('/', StringComparison.Ordinal) { }

    private static bool IsRoot(string t) =>
        t.StartsWith(S3Path.Scheme, StringComparison.OrdinalIgnoreCase) && t.IndexOf('/', S3Path.Scheme.Length) < 0;

    public override string Join(string parent, string name) => base.Join(parent, S3Path.Encode(name));

    public override string NameOf(string path)
    {
        var t = path.TrimEnd('/');
        if (IsRoot(t)) return t[Math.Min(t.Length, S3Path.Scheme.Length)..];
        return t[(t.LastIndexOf('/') + 1)..];
    }

    public override string? ParentOf(string path)
    {
        var t = path.TrimEnd('/');
        if (IsRoot(t)) return null;
        int i = t.LastIndexOf('/');
        return i < 0 ? null : t[..i];
    }
}
