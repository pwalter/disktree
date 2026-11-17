using System.Collections.Concurrent;
using System.IO;
using DiskTree.Core.Storage;

namespace DiskTree.Features.Storage.S3;

/// <summary>The object is encrypted with a key of the customer (SSE-C): without that key it can be neither read nor copied.</summary>
public sealed class S3CustomerKeyException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>
/// Delete, rename and move on S3 for one action, bound to its <see cref="ListRun"/>: every request is counted, waits
/// while the run is paused at its cost limit and ends when the run is cancelled.
/// <para>
/// S3 has no rename: an object is copied (CopyObject on the server, nothing is downloaded), the copy is checked with a
/// HEAD (same size; same ETag where both are plain MD5s; same storage class and encryption) and only then the original
/// is deleted. A failed copy leaves the original alone; an original that cannot be deleted gets its copy removed again.
/// A folder (key prefix) is moved object by object, the originals are deleted in batches of up to 1000 keys. A cancel
/// stops before the next object, but the originals of the objects already copied are still deleted: every object is
/// always complete at exactly one place.
/// </para>
/// <para>
/// Kept: storage class (set explicitly, S3 would write STANDARD otherwise), Content-Type and metadata, tags, SSE-S3 and
/// SSE-KMS / DSSE-KMS with the same key. Not kept: object ACLs (the bucket default applies), Object Lock retention and
/// legal holds, the change time. Not copied (an error per object, the original stays): objects over 5 GB (they would
/// need a multipart copy, see <see cref="MaxCopySize"/>), archived objects (GLACIER / DEEP_ARCHIVE, Intelligent-Tiering
/// archive tiers) without a restore, SSE-C objects.
/// </para>
/// </summary>
public sealed class S3Writer : IStorageWriter, ICopyingWriter, IBatchDeleter, IPrefixFolders
{
    /// <summary>
    /// The limit of CopyObject. Bigger objects would need a multipart copy (UploadPartCopy), and with that metadata,
    /// Content-Type, tags, storage class and encryption have to be carried over by hand (a multipart upload does not copy
    /// them) and the ETag can no longer be compared. DiskTree does not do that: such objects are refused with a reason
    /// (moving them is a job for the AWS CLI, which handles this case).
    /// </summary>
    public const long MaxCopySize = 5L << 30;

    /// <summary>Keys per DeleteObjects (the S3 maximum).</summary>
    public const int DeleteBatch = 1000;

    private static readonly HashSet<string> ArchiveClasses = new(StringComparer.OrdinalIgnoreCase) { "GLACIER", "DEEP_ARCHIVE" };

    private readonly S3Provider _provider;
    private readonly ListRun _run;
    private readonly Action<long, long>? _progress;
    private readonly ConcurrentDictionary<string, long> _stamps = new(StringComparer.Ordinal);
    private long _objects, _bytes;

    internal S3Writer(S3Provider provider, ListRun run, Action<long, long>? progress)
    {
        _provider = provider;
        _run = run;
        _progress = progress;
    }

    /// <summary>The run every request of this writer goes through.</summary>
    public ListRun Run => _run;

    // ------------------------------------------------------------------ requests

    private IS3Client ClientFor(string path) => _provider.Resolve(path).Client;

    /// <summary>A request that may be cancelled: counted, waits while the run is paused.</summary>
    private T Send<T>(Func<CancellationToken, Task<T>> call, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _run.BeforeRequest();
        using var both = CancellationTokenSource.CreateLinkedTokenSource(ct, _run.Cancellation);
        return call(both.Token).GetAwaiter().GetResult();
    }

    private void Send(Func<CancellationToken, Task> call, CancellationToken ct) =>
        Send(async t => { await call(t).ConfigureAwait(false); return true; }, ct);

    /// <summary>
    /// A request that finishes a step that must not stop half way (checking a copy, deleting originals that were already
    /// copied, undoing a copy): counted, but neither paused nor cancelled.
    /// </summary>
    private T Finish<T>(Func<CancellationToken, Task<T>> call)
    {
        _run.CountRequest();
        return call(CancellationToken.None).GetAwaiter().GetResult();
    }

    private void Count(long objects, long bytes)
    {
        long o = Interlocked.Add(ref _objects, objects), b = Interlocked.Add(ref _bytes, bytes);
        _progress?.Invoke(o, b);
    }

    // ------------------------------------------------------------------ paths

    private static (string Bucket, string Prefix) Split(string path)
    {
        var (bucket, prefix) = S3Path.Split(S3Path.Normalize(path));
        if (bucket.Length == 0) throw new IOException("Bitte einen Bucket angeben, z. B. s3://bucket/ordner.");
        return (bucket, prefix);
    }

    /// <summary>The tree path of a key ("a/b.txt" in "bk" gives "s3://bk/a/b.txt").</summary>
    public static string PathOf(string bucket, string key) =>
        S3Path.Scheme + bucket + "/" + string.Join('/', key.Split('/').Select(S3Path.Encode));

    private static bool IsMarker(string rel, long size) => size == 0 && (rel.Length == 0 || rel.EndsWith('/'));

    private static string Text(S3DeleteError e) =>
        e.Code.Equals("AccessDenied", StringComparison.OrdinalIgnoreCase) ? "Zugriff verweigert (AccessDenied)"
        : e.Code.Length == 0 ? e.Message
        : $"{e.Code}: {e.Message}";

    // ------------------------------------------------------------------ IStorageWriter

    public StorageItemInfo? Stat(string path, CancellationToken ct)
    {
        var (bucket, prefix) = Split(path);
        var client = ClientFor(path);
        if (prefix.Length == 0)
        {
            try
            {
                Send(t => client.ListAsync(bucket, "", "/", null, 1, t), ct);
                return new StorageItemInfo(true, false, 0);
            }
            catch (DirectoryNotFoundException) { return null; }
        }

        // A file wins over a folder of the same name ("a" and "a/…" can both exist): deleting it then never takes the folder.
        try
        {
            var head = Send(t => client.HeadAsync(bucket, prefix[..^1], t), ct);
            return new StorageItemInfo(false, false, head.Size, head.ModifiedTicks);
        }
        catch (FileNotFoundException) { }
        catch (S3CustomerKeyException) { return new StorageItemInfo(false, false, 0); } // exists, attributes unreadable without its key

        var page = Send(t => client.ListAsync(bucket, prefix, "/", null, 1, t), ct);
        return page.Objects.Count > 0 || page.Prefixes.Count > 0 ? new StorageItemInfo(true, false, 0) : null;
    }

    public DeleteOutcome Delete(string path, bool recursive, Action<DeleteOutcome>? progress, CancellationToken ct)
    {
        var (bucket, prefix) = Split(path);
        if (prefix.Length == 0) throw new IOException("Ein ganzer Bucket wird nicht gelöscht: " + path);
        var client = ClientFor(path);
        var outcome = new DeleteOutcome();

        if (recursive)
        {
            var first = Send(t => client.ListAsync(bucket, prefix, null, null, DeleteBatch, t), ct);
            if (first.Objects.Any(o => o.Key.StartsWith(prefix, StringComparison.Ordinal)))
            {
                DeleteListed(client, bucket, prefix, first, outcome, progress, ct);
                return outcome;
            }
        }

        // A single object (or, not recursive, an empty folder: its marker).
        var key = prefix[..^1];
        long size;
        try { size = Send(t => client.HeadAsync(bucket, key, t), ct).Size; }
        catch (S3CustomerKeyException) { size = 0; }
        catch (FileNotFoundException)
        {
            if (recursive) throw new FileNotFoundException("Nicht gefunden: " + path);
            var page = Send(t => client.ListAsync(bucket, prefix, null, null, 2, t), ct);
            var objs = page.Objects.Where(o => o.Key.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            if (objs.Count == 0) throw new FileNotFoundException("Nicht gefunden: " + path);
            if (objs.Count > 1 || objs[0].Key != prefix || objs[0].Size != 0) throw new IOException("Der Ordner ist nicht leer: " + path);
            key = prefix;
            size = -1; // folder
        }

        var errors = Send(t => client.DeleteManyAsync(bucket, [key], t), ct);
        if (errors.Count > 0) outcome.Failed.Add((path, Text(errors[0])));
        else if (size < 0) outcome.Folders++;
        else
        {
            outcome.Files++;
            outcome.Bytes += size;
            Count(1, size);
        }
        progress?.Invoke(outcome);
        return outcome;
    }

    /// <summary>Deletes everything below a prefix, one DeleteObjects per listed page (up to 1000 keys), folder markers included.</summary>
    private void DeleteListed(IS3Client client, string bucket, string prefix, S3ListPage first, DeleteOutcome outcome,
        Action<DeleteOutcome>? progress, CancellationToken ct)
    {
        var page = first;
        var dirs = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var objs = page.Objects.Where(o => o.Key.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            if (objs.Count > 0)
            {
                IReadOnlyList<S3DeleteError> errors;
                try
                {
                    errors = Send(t => client.DeleteManyAsync(bucket, objs.Select(o => o.Key).ToList(), t), ct);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    errors = objs.Select(o => new S3DeleteError(o.Key, "", ex.Message)).ToList();
                }
                var failed = errors.GroupBy(e => e.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
                long files = 0, bytes = 0;
                foreach (var o in objs)
                {
                    if (failed.TryGetValue(o.Key, out var e))
                    {
                        outcome.Failed.Add((PathOf(bucket, o.Key), Text(e)));
                        continue;
                    }
                    var rel = o.Key[prefix.Length..];
                    for (int i = rel.IndexOf('/'); i >= 0; i = rel.IndexOf('/', i + 1)) dirs.Add(rel[..i]);
                    if (IsMarker(rel, o.Size)) continue;
                    files++;
                    bytes += o.Size;
                }
                outcome.Files += files;
                outcome.Bytes += bytes;
                Count(files, bytes);
                progress?.Invoke(outcome);
            }
            if (page.NextToken == null) break;
            var token = page.NextToken;
            page = Send(t => client.ListAsync(bucket, prefix, null, token, DeleteBatch, t), ct);
        }
        dirs.Remove("");
        outcome.Folders += dirs.Count + (outcome.Failed.Count == 0 ? 1 : 0);
    }

    public IReadOnlyList<(string Path, string? Error)> DeleteFiles(IReadOnlyList<string> paths, CancellationToken ct)
    {
        var result = new List<(string, string?)>();
        foreach (var group in paths.GroupBy(p => Split(p).Bucket, StringComparer.Ordinal))
        {
            var client = ClientFor(group.First());
            foreach (var chunk in group.Chunk(DeleteBatch))
            {
                if (ct.IsCancellationRequested || _run.Cancellation.IsCancellationRequested) return result;
                var keys = chunk.Select(p => Split(p).Prefix[..^1]).ToList();
                IReadOnlyList<S3DeleteError> errors;
                try
                {
                    errors = Send(t => client.DeleteManyAsync(group.Key, keys, t), ct);
                }
                catch (OperationCanceledException) { return result; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    errors = keys.Select(k => new S3DeleteError(k, "", ex.Message)).ToList();
                }
                var failed = errors.GroupBy(e => e.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
                for (int i = 0; i < chunk.Length; i++)
                {
                    bool bad = failed.TryGetValue(keys[i], out var e);
                    result.Add((chunk[i], bad ? Text(e) : null));
                    if (!bad) Count(1, 0);
                }
            }
        }
        return result;
    }

    public string Rename(string path, string newName, CancellationToken ct)
    {
        var parent = S3PathOps.Instance.ParentOf(S3Path.Normalize(path)) ?? throw new IOException("Ein Bucket lässt sich nicht umbenennen.");
        return Move(path, parent, newName, replace: false, ct);
    }

    public string Move(string path, string targetFolder, string? newName, bool replace, CancellationToken ct)
    {
        var paths = S3PathOps.Instance;
        var (bucket, prefix) = Split(path);
        if (prefix.Length == 0) throw new IOException("Ein Bucket lässt sich nicht verschieben.");
        var (targetBucket, _) = Split(targetFolder);
        if (!string.Equals(bucket, targetBucket, StringComparison.Ordinal))
            throw new IOException($"Verschieben geht nur innerhalb desselben Buckets („{bucket}“).");
        var name = newName ?? paths.NameOf(S3Path.Normalize(path));
        if (name.Length == 0 || name.Contains('/') || name is "." or "..") throw new IOException("Ungültiger Name: " + name);

        var target = S3Path.Normalize(paths.Join(S3Path.Normalize(targetFolder), name));
        var (_, targetPrefix) = Split(target);
        if (targetPrefix == prefix) throw new IOException("Quelle und Ziel sind gleich: " + target);
        if (targetPrefix.StartsWith(prefix, StringComparison.Ordinal))
            throw new IOException("Ein Ordner kann nicht in sich selbst verschoben werden.");

        var client = ClientFor(path);
        S3ObjectHead? head = null;
        try { head = Send(t => client.HeadAsync(bucket, prefix[..^1], t), ct); }
        catch (FileNotFoundException) { }
        catch (S3CustomerKeyException ex) { throw new IOException(ex.Message + " (" + path + ")", ex); }

        if (head != null) MoveObject(client, bucket, path, prefix[..^1], head, target, targetPrefix[..^1], replace, ct);
        else MoveFolder(client, bucket, path, prefix, target, targetPrefix, ct);
        return target;
    }

    /// <summary>Why an object cannot be copied, or null.</summary>
    public static string? NotCopyable(S3ObjectHead head)
    {
        if (head.CustomerKey) return S3Errors.CustomerKeyText;
        if (head.Size > MaxCopySize)
            return $"Größer als 5 GB ({Core.SizeFormat.Format(head.Size)}): S3 kopiert so große Objekte nur in Teilen (Multipart-Copy). " +
                   "Das macht DiskTree nicht, weil dabei Metadaten, Tags, Storage-Klasse und Verschlüsselung einzeln übertragen werden " +
                   "müssten. Bitte z. B. mit der AWS CLI verschieben (aws s3 mv).";
        if (head.StorageClass is { } c && ArchiveClasses.Contains(c) && !head.Restored)
            return $"Archiviert ({c}): das Objekt muss erst wiederhergestellt werden (Restore), bevor es kopiert werden kann.";
        if (head.ArchiveStatus is { } a)
            return $"In der Archiv-Stufe von Intelligent-Tiering ({a}): das Objekt muss erst wiederhergestellt werden.";
        return null;
    }

    private static bool Plain(S3ObjectHead h) => h.Encryption is null || h.Encryption.Equals("AES256", StringComparison.OrdinalIgnoreCase);

    private static string ClassOf(string? storageClass) => string.IsNullOrEmpty(storageClass) ? "STANDARD" : storageClass.ToUpperInvariant();

    /// <summary>
    /// Copies one object with its storage class and encryption and checks the copy. The copy itself is not cancelled
    /// half way (the server would finish it anyway). A copy that does not match is removed again if it did not replace
    /// anything (<paramref name="removeIfWrong"/>). Returns the HEAD of the copy.
    /// </summary>
    private S3ObjectHead CopyChecked(IS3Client client, string bucket, string key, S3ObjectHead head, string targetKey, string targetPath,
        bool removeIfWrong, CancellationToken ct)
    {
        if (NotCopyable(head) is { } why) throw new IOException(why);
        ct.ThrowIfCancellationRequested();
        _run.BeforeRequest(); // waits during a cost pause; throws once the action is cancelled – before the copy, never during
        var request = new S3CopyRequest(bucket, key, targetKey,
            StorageClass: ClassOf(head.StorageClass) == "STANDARD" ? null : head.StorageClass,
            Encryption: head.Encryption, KmsKeyId: head.KmsKeyId, BucketKey: head.BucketKey);
        client.CopyAsync(request, CancellationToken.None).GetAwaiter().GetResult();

        S3ObjectHead copy;
        try { copy = Finish(t => client.HeadAsync(bucket, targetKey, t)); }
        catch (FileNotFoundException) { throw new IOException($"Die Kopie ist am Ziel nicht zu finden ({targetPath}); das Original bleibt."); }

        string? wrong = null;
        if (copy.Size != head.Size) wrong = $"Größe {copy.Size:N0} statt {head.Size:N0} Bytes";
        else if (Plain(head) && Plain(copy) && _provider.ClassifyTag(head.ETag) == ContentTagKind.Strong
                 && _provider.ClassifyTag(copy.ETag) == ContentTagKind.Strong
                 && S3Provider.NormalizeETag(head.ETag) != S3Provider.NormalizeETag(copy.ETag))
            wrong = "andere Prüfsumme (ETag)";
        else if (ClassOf(copy.StorageClass) != ClassOf(head.StorageClass))
            wrong = $"Storage-Klasse {ClassOf(copy.StorageClass)} statt {ClassOf(head.StorageClass)}";
        else if (head.Encryption != null && !string.Equals(copy.Encryption, head.Encryption, StringComparison.OrdinalIgnoreCase))
            wrong = $"Verschlüsselung {copy.Encryption ?? "keine"} statt {head.Encryption}";
        else if (head.KmsKeyId != null && copy.KmsKeyId != null && !string.Equals(copy.KmsKeyId, head.KmsKeyId, StringComparison.Ordinal))
            wrong = "anderer KMS-Schlüssel";
        if (wrong != null)
        {
            string removed = "";
            if (removeIfWrong)
            {
                try { removed = Finish(t => client.DeleteManyAsync(bucket, [targetKey], t)).Count == 0 ? " Die Kopie wurde wieder entfernt." : ""; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* stays; reported below */ }
                if (removed.Length == 0) removed = $" Die fehlerhafte Kopie liegt noch unter {targetPath}.";
            }
            throw new IOException($"Die Kopie stimmt nicht mit dem Original überein ({wrong}); das Original bleibt.{removed}");
        }
        Count(1, head.Size);
        return copy;
    }

    /// <summary>Deletes originals whose copies were checked; best effort, also after a cancel. Returns the keys that failed with the reason.</summary>
    private Dictionary<string, string> DeleteOriginals(IS3Client client, string bucket, IReadOnlyList<string> keys)
    {
        var failed = new Dictionary<string, string>(StringComparer.Ordinal);
        if (keys.Count == 0) return failed;
        try
        {
            foreach (var e in Finish(t => client.DeleteManyAsync(bucket, keys, t))) failed.TryAdd(e.Key, Text(e));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            foreach (var k in keys) failed.TryAdd(k, ex.Message);
        }
        return failed;
    }

    /// <summary>Removes copies whose originals could not be deleted (so nothing lies twice). Returns the keys that stayed.</summary>
    private HashSet<string> Undo(IS3Client client, string bucket, IReadOnlyList<string> copies)
    {
        var stayed = new HashSet<string>(StringComparer.Ordinal);
        if (copies.Count == 0) return stayed;
        try
        {
            foreach (var e in Finish(t => client.DeleteManyAsync(bucket, copies, t))) stayed.Add(e.Key);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stayed.UnionWith(copies);
        }
        return stayed;
    }

    private void MoveObject(IS3Client client, string bucket, string path, string key, S3ObjectHead head, string target, string targetKey,
        bool replace, CancellationToken ct)
    {
        bool existed = false;
        try
        {
            Send(t => client.HeadAsync(bucket, targetKey, t), ct);
            existed = true;
        }
        catch (FileNotFoundException) { }
        catch (S3CustomerKeyException) { existed = true; }
        if (existed && !replace) throw new IOException("Das Ziel existiert schon: " + target);

        var copy = CopyChecked(client, bucket, key, head, targetKey, target, removeIfWrong: !existed, ct);
        if (DeleteOriginals(client, bucket, [key]).TryGetValue(key, out var why))
        {
            if (!existed && Undo(client, bucket, [targetKey]).Count == 0)
                throw new IOException($"Das Original ließ sich nicht löschen ({why}); die Kopie wurde wieder entfernt, es bleibt alles wie vorher.");
            throw new PartialChangeException($"Kopiert nach {target}, aber das Original ließ sich nicht löschen ({why}).",
                [(path, $"Kopiert nach {target}, aber das Original ließ sich nicht löschen ({why}): die Datei liegt jetzt an beiden Orten.")]);
        }
        _stamps[target] = copy.ModifiedTicks;
    }

    private void MoveFolder(IS3Client client, string bucket, string path, string prefix, string target, string targetPrefix, CancellationToken ct)
    {
        // Nothing may be under the new name (replacing a folder is done by the caller: delete first, after asking).
        var probe = Send(t => client.ListAsync(bucket, targetPrefix, null, null, 1, t), ct);
        if (probe.Objects.Count > 0 || probe.Prefixes.Count > 0) throw new IOException("Das Ziel existiert schon: " + target);

        var all = new List<S3ObjectInfo>();
        string? token = null;
        do
        {
            var tk = token;
            var page = Send(t => client.ListAsync(bucket, prefix, null, tk, DeleteBatch, t), ct);
            all.AddRange(page.Objects.Where(o => o.Key.StartsWith(prefix, StringComparison.Ordinal)));
            token = page.NextToken;
        } while (token != null);
        if (all.Count == 0) throw new FileNotFoundException("Nicht gefunden: " + path);

        // Before anything is copied: objects that cannot be copied at all keep the whole folder where it is.
        var problems = new List<(string Path, string Error)>();
        foreach (var o in all)
        {
            if (o.Size > MaxCopySize) problems.Add((PathOf(bucket, o.Key), NotCopyable(new S3ObjectHead(o.Size, o.ETag, null))!));
            else if (o.StorageClass is { } c && ArchiveClasses.Contains(c))
            {
                var key = o.Key;
                S3ObjectHead h;
                try { h = Send(t => client.HeadAsync(bucket, key, t), ct); }
                catch (FileNotFoundException) { continue; }
                if (NotCopyable(h) is { } why) problems.Add((PathOf(bucket, o.Key), why));
            }
        }
        if (problems.Count > 0)
            throw new IOException($"Der Ordner wurde nicht verschoben, nichts wurde verändert: {problems.Count:N0} " +
                                  $"{(problems.Count == 1 ? "Objekt lässt" : "Objekte lassen")} sich nicht kopieren.\n" +
                                  string.Join("\n", problems.Take(5).Select(p => $"• {p.Path}: {p.Error}")) +
                                  (problems.Count > 5 ? $"\n… und {problems.Count - 5:N0} weitere" : ""));

        var failed = new List<(string Path, string Error)>();
        var pending = new List<(string Key, string TargetKey)>();
        long moved = 0, newest = 0;
        bool cancelled = false;

        void Flush()
        {
            if (pending.Count == 0) return;
            var bad = DeleteOriginals(client, bucket, pending.Select(p => p.Key).ToList());
            var undo = pending.Where(p => bad.ContainsKey(p.Key)).ToList();
            var stayed = Undo(client, bucket, undo.Select(p => p.TargetKey).ToList());
            foreach (var p in undo)
                failed.Add((PathOf(bucket, p.Key), stayed.Contains(p.TargetKey)
                    ? $"Original ließ sich nicht löschen ({bad[p.Key]}); das Objekt liegt jetzt an beiden Orten."
                    : $"Original ließ sich nicht löschen ({bad[p.Key]}); die Kopie wurde wieder entfernt."));
            moved += pending.Count - undo.Count;
            pending.Clear();
        }

        foreach (var o in all)
        {
            if (ct.IsCancellationRequested || _run.Cancellation.IsCancellationRequested) { cancelled = true; break; }
            var key = o.Key;
            var targetKey = targetPrefix + key[prefix.Length..];
            try
            {
                var head = Send(t => client.HeadAsync(bucket, key, t), ct);
                var copy = CopyChecked(client, bucket, key, head, targetKey, PathOf(bucket, targetKey), removeIfWrong: true, ct);
                pending.Add((key, targetKey));
                newest = Math.Max(newest, copy.ModifiedTicks);
            }
            catch (OperationCanceledException) { cancelled = true; break; }
            catch (FileNotFoundException) { /* gone meanwhile: nothing to move */ }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add((PathOf(bucket, key), ex.Message));
            }
            if (pending.Count >= DeleteBatch) Flush();
        }
        Flush(); // also after a cancel: the originals of what was copied go, nothing stays twice

        if (moved > 0 && newest > 0) _stamps[target] = newest;
        if (failed.Count == 0 && !cancelled) return;
        if (cancelled && moved == 0 && failed.Count == 0) throw new OperationCanceledException(ct);
        long left = all.Count - moved;
        string message = cancelled
            ? $"Abgebrochen: {moved:N0} von {all.Count:N0} Objekten liegen jetzt unter {target}, {left:N0} noch unter {path}."
            : $"{moved:N0} von {all.Count:N0} Objekten nach {target} verschoben; {left:N0} liegen noch unter {path} ({failed.Count:N0} Fehler).";
        if (failed.Count > 0 && cancelled) failed.Insert(0, (path, message));
        throw new PartialChangeException(message, failed.Count > 0 ? failed : [(path, message)], cancelled);
    }

    public void CreateDirectory(string path, CancellationToken ct)
    {
        var (bucket, prefix) = Split(path);
        if (prefix.Length == 0) throw new IOException("Buckets legt DiskTree nicht an: " + path);
        var client = ClientFor(path);
        Send(t => client.PutEmptyAsync(bucket, prefix, t), ct); // the folder marker "prefix/", as the S3 console does it
    }

    public IReadOnlyCollection<string> ListNames(string folder, CancellationToken ct)
    {
        var (bucket, prefix) = Split(folder);
        var client = ClientFor(folder);
        var names = new HashSet<string>(StringComparer.Ordinal);
        string? token = null;
        do
        {
            var tk = token;
            var page = Send(t => client.ListAsync(bucket, prefix, "/", tk, S3Provider.PageSize, t), ct);
            foreach (var p in page.Prefixes)
            {
                if (p.Length <= prefix.Length || !p.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var rel = p[prefix.Length..].TrimEnd('/');
                if (!rel.Contains('/')) names.Add(S3Path.Encode(rel));
            }
            foreach (var o in page.Objects)
            {
                if (!o.Key.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var name = o.Key[prefix.Length..];
                if (name.Contains('/')) continue;
                if (name.Length == 0)
                {
                    if (o.Size == 0) continue; // the folder's marker
                    name = S3Path.EmptySegment;
                }
                names.Add(name);
            }
            token = page.NextToken;
        } while (token != null);
        return names;
    }

    // ------------------------------------------------------------------ IPrefixFolders

    public IReadOnlyList<string> GoneFolders(IEnumerable<string> folders)
    {
        var gone = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in folders.Select(S3Path.Normalize).OrderByDescending(f => f.Length))
        {
            for (string? cur = f; cur != null; cur = S3PathOps.Instance.ParentOf(cur))
            {
                var (bucket, prefix) = Split(cur);
                if (prefix.Length == 0 || !seen.Add(cur)) break;
                var client = ClientFor(cur);
                var page = Finish(t => client.ListAsync(bucket, prefix, "/", null, 1, t));
                if (page.Objects.Count > 0 || page.Prefixes.Count > 0) break;
                gone.Add(cur);
            }
        }
        return gone;
    }

    // ------------------------------------------------------------------ ICopyingWriter

    private static long Pages(long objects) => (objects + DeleteBatch - 1) / DeleteBatch;

    public CopyCost EstimateCopy(long files, long folders, long folderObjects) => new(
        List: folders * 2 + Pages(folderObjects) + (files + folders > 0 ? 1 : 0),
        Head: files * 3 + folders + folderObjects * 2,
        Copy: files + folderObjects,
        Delete: files + Math.Max(folders, Pages(folderObjects)));

    public long EstimateDelete(long files, long folders, long folderObjects) =>
        (files == 1 && folders == 0 ? 2 : Pages(files)) + folders * 2 + Pages(folderObjects) * 2 + 1;

    public string CopyNotes =>
        "Beim Kopieren bleiben erhalten: Storage-Klasse, Content-Type und Metadaten, Tags, serverseitige Verschlüsselung " +
        "(SSE-S3 und SSE-KMS mit demselben Schlüssel).\n" +
        "Nicht übernommen: objektbezogene ACLs (es gilt der Standard des Buckets), Object-Lock-Aufbewahrung und Legal Hold; " +
        "das Änderungsdatum wird neu gesetzt.\n" +
        "Nicht kopierbar (Fehler je Objekt, das Original bleibt): Objekte über 5 GB, archivierte Objekte (GLACIER, DEEP_ARCHIVE) " +
        "ohne Wiederherstellung, Objekte mit eigenem Schlüssel (SSE-C).\n" +
        "Verschoben wird nur innerhalb desselben Buckets. DELETE-Anfragen berechnet AWS nicht; andere Anbieter ggf. schon.";

    public DeleteNote DescribeDelete(IReadOnlyCollection<string> paths, CancellationToken ct)
    {
        var texts = new List<string>();
        var highlights = new List<string>();
        foreach (var bucket in paths.Select(p => Split(p).Bucket).Distinct(StringComparer.Ordinal))
        {
            S3Versioning? state;
            string? error = null;
            try
            {
                var client = ClientFor(S3Path.Scheme + bucket);
                state = Send(t => client.GetVersioningAsync(bucket, t), ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                state = null;
                error = ex.Message;
            }
            switch (state)
            {
                case S3Versioning.Off:
                    texts.Add($"Der Bucket „{bucket}“ ist nicht versioniert: S3 hat keinen Papierkorb, gelöschte Objekte sind endgültig weg.");
                    break;
                case S3Versioning.Enabled:
                    highlights.Add($"Versionierung ist im Bucket „{bucket}“ aktiv: S3 setzt beim Löschen nur Löschmarkierungen. " +
                                   "Die älteren Versionen bleiben erhalten – und belegen weiter Speicher, der weiter kostet.");
                    texts.Add($"Bucket „{bucket}“ (Versionierung aktiv): Die Objekte verschwinden aus der Liste und aus dem Scan, " +
                              "es entstehen nur Löschmarkierungen. Ältere Versionen und ihr Speicher bleiben erhalten; DiskTree löscht " +
                              "keine Versionen (das geht in der S3-Konsole oder per Lifecycle-Regel). In DiskTree lässt sich das Löschen nicht rückgängig machen.");
                    break;
                case S3Versioning.Suspended:
                    highlights.Add($"Versionierung ist im Bucket „{bucket}“ ausgesetzt: Es entstehen Löschmarkierungen; ältere Versionen aus der Zeit " +
                                   "mit aktiver Versionierung bleiben erhalten und kosten weiter Speicher.");
                    texts.Add($"Bucket „{bucket}“ (Versionierung ausgesetzt): Die aktuelle Version jedes Objekts wird durch eine Löschmarkierung " +
                              "ersetzt; ältere Versionen bleiben erhalten. DiskTree löscht keine Versionen. In DiskTree lässt sich das Löschen nicht rückgängig machen.");
                    break;
                default:
                    texts.Add($"Ob der Bucket „{bucket}“ versioniert ist, ließ sich nicht prüfen ({error}). Ist die Versionierung aktiv, " +
                              "entstehen nur Löschmarkierungen und ältere Versionen bleiben (mit ihren Kosten) erhalten; sonst sind gelöschte Objekte endgültig weg.");
                    break;
            }
        }
        return new DeleteNote(texts.Count > 0 ? string.Join("\n", texts) : null, highlights.Count > 0 ? string.Join("\n", highlights) : null);
    }

    public bool TryGetNewStamp(string newPath, out long ticks) => _stamps.TryGetValue(S3Path.Normalize(newPath), out ticks);
}
