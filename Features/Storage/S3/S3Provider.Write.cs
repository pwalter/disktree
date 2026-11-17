using DiskTree.Core.Storage;

namespace DiskTree.Features.Storage.S3;

// File actions: the provider is a writer of its own (each call counts in a run of its own) and hands out writers bound
// to the run of one action (S3Writer), which the UI uses so the requests of the action are counted and can pause.
public sealed partial class S3Provider : IStorageWriter, IRunBoundWriter, ICopyingWriter, IBatchDeleter, IPrefixFolders
{
    public IStorageWriter Bind(ListRun run, Action<long, long>? progress = null) => new S3Writer(this, run, progress);

    private S3Writer Once(CancellationToken ct) => new(this, new ListRun(ct), null);

    public StorageItemInfo? Stat(string path, CancellationToken ct) => Once(ct).Stat(path, ct);

    public DeleteOutcome Delete(string path, bool recursive, Action<DeleteOutcome>? progress, CancellationToken ct) =>
        Once(ct).Delete(path, recursive, progress, ct);

    public string Rename(string path, string newName, CancellationToken ct) => Once(ct).Rename(path, newName, ct);

    public string Move(string path, string targetFolder, string? newName, bool replace, CancellationToken ct) =>
        Once(ct).Move(path, targetFolder, newName, replace, ct);

    public void CreateDirectory(string path, CancellationToken ct) => Once(ct).CreateDirectory(path, ct);

    public IReadOnlyCollection<string> ListNames(string folder, CancellationToken ct) => Once(ct).ListNames(folder, ct);

    public IReadOnlyList<(string Path, string? Error)> DeleteFiles(IReadOnlyList<string> paths, CancellationToken ct) =>
        Once(ct).DeleteFiles(paths, ct);

    public IReadOnlyList<string> GoneFolders(IEnumerable<string> folders) => Once(default).GoneFolders(folders);

    public CopyCost EstimateCopy(long files, long folders, long folderObjects) => Once(default).EstimateCopy(files, folders, folderObjects);

    public long EstimateDelete(long files, long folders, long folderObjects) => Once(default).EstimateDelete(files, folders, folderObjects);

    public string CopyNotes => Once(default).CopyNotes;

    public DeleteNote DescribeDelete(IReadOnlyCollection<string> paths, CancellationToken ct) => Once(ct).DescribeDelete(paths, ct);

    /// <summary>An unbound call keeps nothing: the new times are known to the writer of the action (<see cref="Bind"/>).</summary>
    public bool TryGetNewStamp(string newPath, out long ticks)
    {
        ticks = 0;
        return false;
    }
}
