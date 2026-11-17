namespace DiskTree.Core.Storage;

/// <summary>What <see cref="IStorageWriter.Stat"/> knows about an existing entry (links are not followed).</summary>
/// <param name="IsDirectory">A folder.</param>
/// <param name="IsLink">A symbolic link (removed / moved as the link itself, never its target).</param>
/// <param name="Length">Size in bytes (files).</param>
/// <param name="ModifiedTicks">UTC ticks, 0 if unknown.</param>
/// <param name="AccessedTicks">UTC ticks, 0 if unknown.</param>
/// <param name="CreatedTicks">UTC ticks, 0 if unknown.</param>
public readonly record struct StorageItemInfo(bool IsDirectory, bool IsLink, long Length, long ModifiedTicks = 0,
    long AccessedTicks = 0, long CreatedTicks = 0);

/// <summary>What a (recursive) delete removed and what it could not remove. It goes on after errors.</summary>
public sealed class DeleteOutcome
{
    public long Files;
    public long Folders;
    public long Bytes;

    /// <summary>Entries that could not be removed, with the reason. A folder whose content was not removed completely stays.</summary>
    public List<(string Path, string Error)> Failed { get; } = new();

    public bool Complete => Failed.Count == 0;
}

/// <summary>
/// Optional for a source whose files can be changed (SFTP, S3): delete for good, rename, move on the same source.
/// The local disk does not use it (it has the recycle bin and the Windows shell, Features/FileOperations). S3 renames
/// and moves by copying (<see cref="ICopyingWriter"/>, with the optional interfaces next to it). The UI asks
/// <see cref="StorageFeatures"/>, which wants both the caps (<see cref="StorageCaps.Delete"/>,
/// <see cref="StorageCaps.Move"/>, <see cref="StorageCaps.Rename"/>) and this interface.
/// <para>
/// Paths are the provider's full paths. Every call may block on the network: callers run it off the UI thread.
/// Errors come as <see cref="System.IO.IOException"/> / <see cref="UnauthorizedAccessException"/> with a message for the
/// user (never a secret), a cancel as <see cref="OperationCanceledException"/>. Nothing here ever replaces or deletes
/// something that was not asked for: <see cref="Rename"/> and <see cref="Move"/> refuse an existing target unless
/// <c>replace</c> says otherwise.
/// </para>
/// </summary>
public interface IStorageWriter
{
    /// <summary>The entry at <paramref name="path"/>, or null if there is none (links are not followed).</summary>
    StorageItemInfo? Stat(string path, CancellationToken ct);

    /// <summary>Is there an entry at <paramref name="path"/>?</summary>
    bool Exists(string path, CancellationToken ct) => Stat(path, ct) != null;

    /// <summary>
    /// Removes a file or link, or a folder: with <paramref name="recursive"/> everything in it first, else only if it
    /// is empty. There is no recycle bin: what is removed is gone. Goes on after errors (collected in the result);
    /// <paramref name="progress"/> is called now and then with the numbers so far. Cancel: stops between entries.
    /// </summary>
    DeleteOutcome Delete(string path, bool recursive, Action<DeleteOutcome>? progress, CancellationToken ct);

    /// <summary>Gives an entry a new name in the same folder (never replaces an existing entry). Returns the new path.</summary>
    string Rename(string path, string newName, CancellationToken ct);

    /// <summary>
    /// Moves an entry into <paramref name="targetFolder"/> of the same source, as <paramref name="newName"/> (null: its
    /// name). An existing target is refused, unless <paramref name="replace"/> is set and both are files: then the old
    /// file is replaced (atomically where the source can). Returns the new path.
    /// </summary>
    string Move(string path, string targetFolder, string? newName, bool replace, CancellationToken ct);

    /// <summary>Creates a folder (its parent must exist).</summary>
    void CreateDirectory(string path, CancellationToken ct);

    /// <summary>The names directly inside a folder (for conflict checks), compared with the source's rules.</summary>
    IReadOnlyCollection<string> ListNames(string folder, CancellationToken ct);
}
