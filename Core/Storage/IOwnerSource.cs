namespace DiskTree.Core.Storage;

/// <summary>What "Speicher pro Besitzer" sums up by: the owning user, or (POSIX sources) the owning group.</summary>
public enum OwnerGrouping
{
    User,
    Group,
}

/// <summary>
/// Owner of one file or folder as the source shows it.
/// </summary>
/// <param name="Owner">The owner for the user ("DOM\anna", "pascal (uid 1000)", "uid 1003").</param>
/// <param name="Group">The owning group (POSIX sources), else null.</param>
/// <param name="Permissions">A one-line summary of the access rights (POSIX: "drwxr-x---  pascal:users  (0750)"), else null.</param>
public sealed record OwnerDetails(string Owner, string? Group = null, string? Permissions = null);

/// <summary>Bytes and files of one owner (user or group).</summary>
public sealed record OwnerRow(string Owner, long Bytes, long Files);

/// <summary>
/// "Speicher pro Besitzer" of a folder.
/// </summary>
/// <param name="Rows">Per owner, the biggest first (by user, or by what the aggregation was asked for).</param>
/// <param name="Seen">Files looked at.</param>
/// <param name="Skipped">Files left out by the sampling (smaller than the minimum size).</param>
/// <param name="Failed">Files whose owner could not be read (disk), or folders that could not be listed (<see cref="FailedAreFolders"/>).</param>
/// <param name="Cancelled">Stopped early: the rows are a partial result.</param>
/// <param name="Error">Why the analysis could not run (folder gone, connection lost …), else null.</param>
/// <param name="FailedAreFolders"><see cref="Failed"/> counts folders (sources that list folders with their owners).</param>
/// <param name="GroupRows">The same files per owning group (POSIX sources), else null.</param>
public sealed record OwnerResult(List<OwnerRow> Rows, long Seen, long Skipped, long Failed, bool Cancelled, string? Error = null,
    bool FailedAreFolders = false, List<OwnerRow>? GroupRows = null)
{
    public long TotalBytes => Rows.Sum(r => r.Bytes);

    /// <summary>The rows for one way of grouping (by group only where the source knows groups, else by user).</summary>
    public List<OwnerRow> RowsBy(OwnerGrouping by) => by == OwnerGrouping.Group && GroupRows != null ? GroupRows : Rows;
}

/// <summary>
/// Optional for a source that knows who owns its files (the "Besitzer" tab): the owner of one entry, its access rights
/// and "Speicher pro Besitzer" for a folder. The local disk answers with Windows owners and ACLs (Features/Owner), an
/// SFTP server with POSIX uid / gid and mode bits (<see cref="StorageCaps.PosixPermissions"/>).
/// <para>
/// Everything may block (network, name lookups): callers run it off the UI thread. Errors of single entries are
/// counted, not thrown; a folder that cannot be read at all ends up in <see cref="OwnerResult.Error"/>.
/// </para>
/// </summary>
public interface IOwnerSource
{
    /// <summary>Header of the rights section ("Rechte (ACL)", "Rechte (POSIX)").</summary>
    string RightsTitle { get; }

    /// <summary>The source knows owning groups too: "Speicher pro Besitzer" can be shown per group.</summary>
    bool HasGroups { get; }

    /// <summary>
    /// The aggregation reads the owner of each file one by one (disk ACLs): sampling (only big files) makes it faster.
    /// False: owners come with the folder listing, every file costs the same.
    /// </summary>
    bool SamplingHelps { get; }

    /// <summary>Owner of a file or folder; null if it cannot be read (no access, gone).</summary>
    Task<OwnerDetails?> GetOwnerAsync(string path, CancellationToken ct);

    /// <summary>The access rights of a folder as readable lines.</summary>
    string[] GetRights(string path, CancellationToken ct);

    /// <summary>
    /// Sums the bytes of all files below <paramref name="folder"/> per owner (and per group where known). Files smaller
    /// than <paramref name="minSize"/> are skipped when <see cref="SamplingHelps"/>. <paramref name="progress"/> gets
    /// (files seen, failed) now and then. Never throws for a missing / unreadable folder (see <see cref="OwnerResult.Error"/>).
    /// </summary>
    OwnerResult Aggregate(string folder, long minSize, Action<long, long>? progress, CancellationToken ct);
}
