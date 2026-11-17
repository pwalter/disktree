using System.IO;

namespace DiskTree.Core.Storage;

/// <summary>
/// Optional for an <see cref="IStorageWriter"/> of a source that charges per request (S3): one writer per action, bound
/// to the action's <see cref="ListRun"/> (request counter, cost pause, cancel down to the client) and reporting its
/// progress in objects and bytes. The provider itself stays usable as an unbound writer (each call counts on its own).
/// </summary>
public interface IRunBoundWriter
{
    /// <summary>A writer for one action: every request goes through <paramref name="run"/>; <paramref name="progress"/> gets (objects, bytes) done so far.</summary>
    IStorageWriter Bind(ListRun run, Action<long, long>? progress = null);
}

/// <summary>What renaming / moving costs on a source that copies every object (estimate, in requests).</summary>
/// <param name="List">Listings (one per 1000 objects of a folder, plus checks).</param>
/// <param name="Head">HEAD requests (reading attributes before, checking the copy after).</param>
/// <param name="Copy">COPY requests (priced like PUT).</param>
/// <param name="Delete">Delete requests (one per 1000 originals).</param>
public readonly record struct CopyCost(long List, long Head, long Copy, long Delete)
{
    public long Total => List + Head + Copy + Delete;
}

/// <summary>
/// Optional for an <see cref="IStorageWriter"/> that cannot rename or move in place (S3): every object is copied and
/// the original deleted afterwards. That is not atomic, costs requests and gives the copies a new change time; the UI
/// asks before folders are renamed or moved (<c>CopyPlan</c>) and the tree takes the new times (<see cref="TryGetNewStamp"/>).
/// </summary>
public interface ICopyingWriter
{
    /// <summary>Requests for renaming / moving <paramref name="files"/> single files and <paramref name="folderObjects"/> objects inside folders.</summary>
    CopyCost EstimateCopy(long files, long folders, long folderObjects);

    /// <summary>Requests for deleting <paramref name="files"/> single files and <paramref name="folderObjects"/> objects inside <paramref name="folders"/> folders.</summary>
    long EstimateDelete(long files, long folders, long folderObjects);

    /// <summary>Lines for the confirmation: what a copy keeps and what it does not keep.</summary>
    string CopyNotes { get; }

    /// <summary>
    /// What the confirmation before deleting has to say about this source (S3: whether the bucket keeps old versions);
    /// may send requests. <see cref="DeleteNote.Text"/> replaces the closing sentence about the missing recycle bin.
    /// </summary>
    DeleteNote DescribeDelete(IReadOnlyCollection<string> paths, CancellationToken ct);

    /// <summary>
    /// The change time (UTC ticks) of what a rename / move just created at <paramref name="newPath"/> (for a folder: of its
    /// newest object), so the age buckets follow; false if unknown.
    /// </summary>
    bool TryGetNewStamp(string newPath, out long ticks);
}

/// <summary>What the delete confirmation says about the source: the closing text and, if needed, a highlighted line.</summary>
/// <param name="Text">Replaces "Diese Quelle hat keinen Papierkorb …" (null: keep it).</param>
/// <param name="Highlight">Shown in a box of its own (e.g. "Versionierung aktiv: …"), or null.</param>
public sealed record DeleteNote(string? Text, string? Highlight = null);

/// <summary>Optional: deletes many single files in few requests (S3: DeleteObjects with up to 1000 keys).</summary>
public interface IBatchDeleter
{
    /// <summary>
    /// Deletes the files (never folders) and returns one entry per path it tried: error null = gone. Stops between batches
    /// on cancel (paths not tried are not in the result).
    /// </summary>
    IReadOnlyList<(string Path, string? Error)> DeleteFiles(IReadOnlyList<string> paths, CancellationToken ct);
}

/// <summary>
/// Optional for a source whose folders are only prefixes of keys (S3): a folder exists only while something lies in it
/// (an object, or an empty folder marker). After deleting or moving away the last thing, the folder is gone as well.
/// </summary>
public interface IPrefixFolders
{
    /// <summary>
    /// Of <paramref name="folders"/> (and the folders above them): those that are gone now. Asks the source, also after a
    /// cancel (one listing per folder), so the tree can follow.
    /// </summary>
    IReadOnlyList<string> GoneFolders(IEnumerable<string> folders);
}

/// <summary>
/// A change that was done only partly (S3: some objects of a folder were copied, others not; or an original could not
/// be deleted after its copy). The source is in a consistent state, but both places need a rescan; <see cref="Failed"/>
/// lists what went wrong per object.
/// </summary>
public sealed class PartialChangeException : IOException
{
    public PartialChangeException(string message, IReadOnlyList<(string Path, string Error)> failed, bool cancelled = false)
        : base(message)
    {
        Failed = failed;
        Cancelled = cancelled;
    }

    public IReadOnlyList<(string Path, string Error)> Failed { get; }

    /// <summary>It stopped because the action was cancelled (after a consistent step).</summary>
    public bool Cancelled { get; }
}
