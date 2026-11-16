using DiskTree.Core.Storage;

namespace DiskTree.Features.Storage.Sftp;

/// <summary>
/// Path rules for full SFTP paths ("sftp://user@host/a/b"): '/' as separator, case-sensitive. Joining is the generic
/// rule; taking apart stops at the server part, so the root "sftp://user@host/" has no parent and an empty name, and
/// the parent of "sftp://user@host/a" is the root (with its '/'). Works for any server: snapshots of SFTP scans can
/// be loaded without a connection.
/// </summary>
public sealed class SftpPathOps : PathOps
{
    public static readonly SftpPathOps Instance = new();

    private SftpPathOps() : base('/', StringComparison.Ordinal) { }

    public override string NameOf(string path)
    {
        int start = SftpUri.PathStart(path);
        if (start < 0) return base.NameOf(path);
        var remote = path.AsSpan(start).TrimEnd('/');
        return remote.IsEmpty ? "" : remote[(remote.LastIndexOf('/') + 1)..].ToString();
    }

    public override string? ParentOf(string path)
    {
        int start = SftpUri.PathStart(path);
        if (start < 0) return base.ParentOf(path);
        var remote = path.AsSpan(start).TrimEnd('/');
        if (remote.IsEmpty) return null;                       // the root
        int i = remote.LastIndexOf('/');
        return i <= 0 ? path[..(start + 1)] : path[..(start + i)];
    }
}
