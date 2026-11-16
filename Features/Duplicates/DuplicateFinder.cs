using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using DiskTree.Core.Storage;

namespace DiskTree.Features.Duplicates;

/// <summary>What a duplicate group is based on.</summary>
public enum DupBasis
{
    /// <summary>The whole content was compared (SHA-256, or files up to 128 KB read completely): certain.</summary>
    FullHash,
    /// <summary>Same size and the same confirmed strong content tag (S3: single-part ETag = MD5), nothing downloaded: certain.</summary>
    ContentTag,
    /// <summary>Same size and the same first and last 64 KB only (or only a weak tag): probable, not certain.</summary>
    PartialHash,
}

/// <summary>A set of byte-identical files (or, with <see cref="DupBasis.PartialHash"/>, probably identical ones).</summary>
public sealed record DuplicateGroup(long Size, string[] Paths)
{
    /// <summary>Space that would be freed by keeping only one copy.</summary>
    public long Wasted => Size * (Paths.Length - 1);

    /// <summary>How the files were compared (the disk always compares the whole content).</summary>
    public DupBasis Basis { get; init; } = DupBasis.FullHash;

    /// <summary>"sicher": the content is known to be equal; otherwise "wahrscheinlich".</summary>
    public bool IsCertain => Basis != DupBasis.PartialHash;
}

public enum DupPhase { Enumerating, Sizes, PartialHash, FullHash, Done, Tags }

/// <param name="Requests">Requests sent to a remote source so far (listing, tags, downloads); 0 for the disk.</param>
public readonly record struct DupProgress(DupPhase Phase, long FilesDone, long FilesTotal, long BytesDone, long BytesTotal, string Current,
    long Requests = 0);

/// <summary>How thoroughly a remote source is compared (the disk always compares completely).</summary>
public enum DupMode
{
    /// <summary>Downloads what is needed for certainty: partial hash, then the full SHA-256 of what is still equal.</summary>
    Thorough,
    /// <summary>
    /// No full downloads: strong content tags (S3 single-part ETag) decide without download, the rest is compared by
    /// its first and last 64 KB and marked "wahrscheinlich" (files up to 128 KB are read completely, so they are certain).
    /// </summary>
    Fast,
}

/// <summary>One file that has the same size as at least one other.</summary>
public readonly record struct DupFile(string Path, long Size, string? Tag);

/// <summary>The files of a folder grouped by size (only sizes that occur at least twice): the input of the hashing.</summary>
public sealed class DupCandidates
{
    public required IStorageProvider Provider { get; init; }
    public required string Root { get; init; }
    /// <summary>Groups of files of the same size, in the order the sizes were first seen.</summary>
    public required IReadOnlyList<List<DupFile>> SameSize { get; init; }
    /// <summary>Files listed (at least the minimum size).</summary>
    public long FilesListed { get; init; }
    /// <summary>Folders / listings that could not be read.</summary>
    public long Errors { get; init; }
    /// <summary>Requests the listing sent.</summary>
    public long ListRequests { get; init; }

    public long Files => SameSize.Sum(g => (long)g.Count);
}

/// <summary>
/// What a run on a remote source would cost, before anything is read. Downloads of the thorough mode are the worst
/// case (every file whose first and last 64 KB match is downloaded completely).
/// </summary>
public sealed record DupEstimate(
    int Groups, long Files,
    long PartialBytes, long FullBytesWorst,
    long ThoroughRequestsWorst,
    long TagChecks, long FastBytes, long FastRequests,
    long FastBytesWorst, long FastRequestsWorst)
{
    public long ThoroughBytesWorst => PartialBytes + FullBytesWorst;
}

/// <summary>
/// A limit for the bytes one run downloads. When a read would go over it, <c>askMore</c> is asked once (serialized):
/// yes raises the limit by the original amount (or as far as that read needs), no stops all further downloads of the
/// run (files not read are counted as skipped; what was found so far stays).
/// </summary>
public sealed class DownloadBudget
{
    private readonly long _step;
    private readonly Func<long, long, Task<bool>>? _askMore;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _reserved;
    private long _limit;
    private volatile bool _declined;

    /// <param name="limit">Bytes per run (0 = no limit).</param>
    /// <param name="askMore">Gets (reserved so far, limit); true = go on. Null = stop at the limit.</param>
    public DownloadBudget(long limit, Func<long, long, Task<bool>>? askMore)
    {
        _limit = _step = Math.Max(0, limit);
        _askMore = askMore;
    }

    public long Limit => Interlocked.Read(ref _limit);
    public long Reserved => Interlocked.Read(ref _reserved);
    public bool Declined => _declined;

    /// <summary>Takes <paramref name="bytes"/> from the budget; false if the user does not want to go over the limit.</summary>
    public async ValueTask<bool> ReserveAsync(long bytes, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (_declined) return false;
            long used = Interlocked.Read(ref _reserved);
            long limit = Interlocked.Read(ref _limit);
            if (limit <= 0 || used + bytes <= limit)
            {
                if (Interlocked.CompareExchange(ref _reserved, used + bytes, used) == used) return true;
                continue;
            }

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_declined) return false;
                if (Interlocked.Read(ref _reserved) + bytes <= Interlocked.Read(ref _limit)) continue; // raised meanwhile
                bool more = _askMore != null && await _askMore(Interlocked.Read(ref _reserved), Interlocked.Read(ref _limit)).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (!more)
                {
                    _declined = true;
                    return false;
                }
                long need = Interlocked.Read(ref _reserved) + bytes;
                Interlocked.Exchange(ref _limit, Math.Max(Interlocked.Read(ref _limit) + _step, need));
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}

/// <summary>
/// Finds duplicate files below a folder of any storage source: group by size, then compare a partial hash (first + last
/// block), then the full SHA-256. Every step only looks at files that are still candidates, so most files are read
/// little or never. Reparse points (junctions, symlinks) are skipped like in the <see cref="Scanner"/>.
/// <para>
/// The files are listed through <see cref="IStorageProvider.Enumerate{T}"/> (folder by folder) or, if the source
/// offers it, <see cref="IDeepListing"/> (S3: one flat listing). The disk reads contents with System.IO as before;
/// remote sources through <see cref="IContentHashSource"/> (byte ranges, content tags), counted by a
/// <see cref="ListRun"/> and limited by a <see cref="DownloadBudget"/>. Downloaded bytes are only hashed in memory.
/// </para>
/// </summary>
public sealed class DuplicateFinder
{
    private const int Block = 64 * 1024;

    /// <summary>Files up to this size are read completely by the partial hash (so its result is certain).</summary>
    public const long SmallFile = 2L * Block;

    public long Errors => Interlocked.Read(ref _errors);
    private long _errors;

    /// <summary>Files not (completely) compared because the download limit was not raised.</summary>
    public long Skipped => Interlocked.Read(ref _skipped);
    private long _skipped;

    /// <summary>Bytes read from a remote source by this run.</summary>
    public long BytesRead => Interlocked.Read(ref _bytesRead);
    private long _bytesRead;

    public static bool IsNetworkPath(string path)
    {
        try
        {
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return true;
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch
        {
            return false;
        }
    }

    // ================================================================== the disk (unchanged behaviour)

    /// <summary>Duplicates below a folder of the local file system (or a share): lists and compares completely.</summary>
    /// <param name="minSize">Files smaller than this are ignored (empty files always are).</param>
    /// <param name="parallelism">Number of files hashed at the same time.</param>
    public async Task<List<DuplicateGroup>> FindAsync(string root, long minSize, int parallelism,
        IProgress<DupProgress>? progress, CancellationToken ct)
    {
        _errors = 0;
        var candidates = await CollectAsync(DiskProvider.Instance, root, minSize, new ListRun(ct), progress);
        _errors = candidates.Errors;
        return await HashAsync(candidates.SameSize, Math.Max(1, parallelism), progress, ct,
            (f, partialOnly, token, bytesRead) => new ValueTask<string?>(TryHash(f.Path, f.Size, partialOnly, token, bytesRead)), () => 0);
    }

    // ================================================================== listing

    /// <summary>
    /// Lists all files below <paramref name="root"/> (links skipped) and groups them by size. For the disk exactly the
    /// files (and their order) of a recursive <see cref="System.IO.Enumeration.FileSystemEnumerable{TResult}"/>.
    /// Remote sources: a root that cannot be listed is thrown (sign-in, network); unreadable subfolders count as errors.
    /// </summary>
    public static Task<DupCandidates> CollectAsync(IStorageProvider provider, string root, long minSize, ListRun run,
        IProgress<DupProgress>? progress) =>
        Task.Run(() => Collect(provider, root, Math.Max(1, minSize), run, progress), run.Cancellation);

    private static DupCandidates Collect(IStorageProvider provider, string root, long minSize, ListRun run, IProgress<DupProgress>? progress)
    {
        var ct = run.Cancellation;
        var map = new Dictionary<long, List<DupFile>>();
        long seen = 0, errors = 0;
        bool disk = provider.Id == DiskProvider.ProviderId;

        void Add(string path, long len, string? tag)
        {
            ct.ThrowIfCancellationRequested();
            if (len < minSize) return;
            if (!map.TryGetValue(len, out var list)) map[len] = list = new List<DupFile>(2);
            list.Add(new DupFile(path, len, tag));
            if ((++seen & 0x3FF) == 0)
                progress?.Report(new DupProgress(DupPhase.Enumerating, seen, 0, 0, 0, path, run.Requests));
        }

        if (disk)
        {
            errors = ListDisk(root, ct, Add);
        }
        else if (provider is IDeepListing deep)
        {
            var options = new ListOptions { Stamps = StampSet.None, Run = run };
            foreach (var f in deep.ListFiles(provider.Normalize(root), options)) Add(f.Path, f.Length, f.ContentTag);
        }
        else
        {
            var files = ListRemote(provider, provider.Normalize(root), run, progress, ref errors);
            foreach (var f in files) Add(f.Path, f.Length, f.ContentTag);
        }

        progress?.Report(new DupProgress(DupPhase.Sizes, seen, 0, 0, 0, "", run.Requests));
        return new DupCandidates
        {
            Provider = provider,
            Root = root,
            SameSize = map.Values.Where(l => l.Count > 1).ToList(),
            FilesListed = seen,
            Errors = errors,
            ListRequests = run.Requests,
        };
    }

    /// <summary>
    /// The disk, folder by folder through the provider, in the order of a recursive FileSystemEnumerable (one folder
    /// completely, its subfolders queued); unreadable folders are skipped like with IgnoreInaccessible. A root that
    /// cannot be listed counts as one error (as before).
    /// </summary>
    private static long ListDisk(string root, CancellationToken ct, Action<string, long, string?> add)
    {
        var disk = DiskProvider.Instance;
        var options = new ListOptions { IgnoreInaccessible = true, Stamps = StampSet.None };
        long errors = 0;
        string top;
        try { top = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return 1; }

        var queue = new Queue<string>();
        queue.Enqueue(top);
        bool first = true;
        var subdirs = new List<string>();
        var files = new List<(string Path, long Len)>();
        while (queue.Count > 0)
        {
            var dir = queue.Dequeue();
            subdirs.Clear();
            files.Clear();
            try
            {
                foreach (var (name, isDir, len) in disk.Enumerate(dir,
                             (in StorageEntry e) => e.IsReparsePoint ? (null, false, 0L) : (e.Name.ToString(), e.IsDirectory, e.Length), options))
                {
                    ct.ThrowIfCancellationRequested();
                    if (name == null) continue; // junction / symlink (and cloud placeholders), like the scanner
                    var path = Path.Join(dir, name);
                    if (isDir) subdirs.Add(path);
                    else files.Add((path, len));
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (DirectoryNotFoundException) when (!first) { /* vanished meanwhile: skipped, as before */ }
            catch (Exception)
            {
                errors++;
                if (first) return errors;
            }
            first = false;
            // Files in listing order, then the subfolders queued (FileSystemEnumerable interleaves them in listing order,
            // but only files are reported, so the order of the files is the same).
            foreach (var (path, len) in files) add(path, len, null);
            foreach (var d in subdirs) queue.Enqueue(d);
        }
        return errors;
    }

    /// <summary>A remote source folder by folder, a level at a time with the source's number of parallel listings.</summary>
    private static List<DeepFile> ListRemote(IStorageProvider provider, string root, ListRun run, IProgress<DupProgress>? progress,
        ref long errors)
    {
        var ct = run.Cancellation;
        var paths = provider.Paths;
        var options = new ListOptions { IgnoreInaccessible = false, Stamps = StampSet.None, Run = run };
        var files = new ConcurrentBag<DeepFile>();
        long errorCount = 0;
        int threads = Math.Clamp(provider.ScanThreads ?? 4, 1, 16);

        // The root first, on its own: if it cannot be listed (sign-in, network, gone) the search fails with that message.
        var level = ListOne(provider, root, options, files, paths, ct);
        long folders = 1;
        while (level.Count > 0)
        {
            var next = new ConcurrentBag<string>();
            try
            {
                Parallel.ForEach(level, new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = ct }, dir =>
                {
                    try
                    {
                        foreach (var sub in ListOne(provider, dir, options, files, paths, ct)) next.Add(sub);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && !ct.IsCancellationRequested)
                    {
                        Interlocked.Increment(ref errorCount);
                    }
                });
            }
            catch (AggregateException) when (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }
            folders += level.Count;
            progress?.Report(new DupProgress(DupPhase.Enumerating, files.Count, 0, 0, 0, $"{folders:N0} Ordner", run.Requests));
            level = next.ToList();
        }
        errors += errorCount;

        // Parallel listing gives no stable order: sort by path so a run gives the same result every time.
        var list = files.ToList();
        list.Sort((a, b) => paths.Comparer.Compare(a.Path, b.Path));
        return list;
    }

    private static List<string> ListOne(IStorageProvider provider, string dir, ListOptions options, ConcurrentBag<DeepFile> files,
        PathOps paths, CancellationToken ct)
    {
        var subdirs = new List<string>();
        foreach (var (name, isDir, len) in provider.Enumerate(dir,
                     (in StorageEntry e) => e.IsReparsePoint ? (null, false, 0L) : (e.Name.ToString(), e.IsDirectory, e.Length), options))
        {
            ct.ThrowIfCancellationRequested();
            if (name == null) continue; // links are not followed
            var path = paths.Join(dir, name);
            if (isDir) subdirs.Add(path);
            else files.Add(new DeepFile(path, len, null));
        }
        return subdirs;
    }

    // ================================================================== estimate

    /// <summary>What comparing the candidates would read and ask from the source, per mode (see <see cref="DupEstimate"/>).</summary>
    public static DupEstimate Estimate(DupCandidates c)
    {
        var src = c.Provider as IContentHashSource;
        long Req(long len) => src?.RequestsFor(len) ?? 0;
        long PartialReq(long size) => size <= SmallFile ? Req(size) : 2 * Req(Block);
        static long PartialLen(long size) => Math.Min(size, SmallFile);

        long partialBytes = 0, fullBytes = 0, thoroughReq = 0;
        long tagChecks = 0, fastBytes = 0, fastReq = 0, fastBytesWorst = 0, fastReqWorst = 0;
        foreach (var g in c.SameSize)
        {
            long size = g[0].Size;
            partialBytes += g.Count * PartialLen(size);
            thoroughReq += g.Count * PartialReq(size);
            if (size > SmallFile)
            {
                fullBytes += g.Count * size;
                thoroughReq += g.Count * Req(size);
            }

            // Fast: strong tags are checked (HEAD), the rest is compared by its partial hash; a strong tag class needs one
            // partial hash as well if unsure files of the same size are around.
            var strong = src == null ? new List<DupFile>() : g.Where(f => src.ClassifyTag(f.Tag) == ContentTagKind.Strong).ToList();
            int unsure = g.Count - strong.Count;
            int classes = strong.Select(f => NormalizeTag(f.Tag)).Distinct(StringComparer.Ordinal).Count();
            int partials = unsure + (unsure > 0 ? classes : 0);
            tagChecks += strong.Count * (src?.TagRequests ?? 0);
            fastBytes += partials * PartialLen(size);
            fastReq += strong.Count * (src?.TagRequests ?? 0) + partials * PartialReq(size);
            // Worst case: no tag is confirmed (e.g. SSE-KMS), every file needs its partial hash.
            fastBytesWorst += g.Count * PartialLen(size);
            fastReqWorst += strong.Count * (src?.TagRequests ?? 0) + g.Count * PartialReq(size);
        }
        return new DupEstimate(c.SameSize.Count, c.Files, partialBytes, fullBytes, thoroughReq, tagChecks, fastBytes, fastReq,
            fastBytesWorst, fastReqWorst);
    }

    private static string NormalizeTag(string? tag) => (tag ?? "").Trim().Trim('"').ToLowerInvariant();

    // ================================================================== remote sources

    /// <summary>
    /// Compares the candidates of a remote source (see <see cref="DupMode"/>). The run counts every request (and pauses
    /// at its cost limit); its token ends downloads in flight. <paramref name="budget"/> limits the downloaded bytes.
    /// </summary>
    public async Task<List<DuplicateGroup>> FindAsync(DupCandidates candidates, DupMode mode, int parallelism, ListRun run,
        DownloadBudget? budget, IProgress<DupProgress>? progress)
    {
        var src = candidates.Provider as IContentHashSource
                  ?? throw new NotSupportedException($"{candidates.Provider.DisplayName}: Dateiinhalte können nicht gelesen werden.");
        _errors = candidates.Errors;
        _skipped = 0;
        _bytesRead = 0;
        var ct = run.Cancellation;
        parallelism = Math.Max(1, parallelism);
        budget ??= new DownloadBudget(0, null);

        ValueTask<string?> Hash(DupFile f, bool partialOnly, CancellationToken token, Action<long>? bytesRead) =>
            RemoteHashAsync(src, f, partialOnly, run, budget, token, bytesRead);

        if (mode == DupMode.Thorough)
            return await HashAsync(candidates.SameSize, parallelism, progress, ct, Hash, () => run.Requests);

        return await FastAsync(src, candidates.SameSize, parallelism, run, progress, Hash);
    }

    /// <summary>The fast mode: content tags where they are strong, partial hashes for the rest, no full downloads.</summary>
    private async Task<List<DuplicateGroup>> FastAsync(IContentHashSource src, IReadOnlyList<List<DupFile>> candidates, int parallelism,
        ListRun run, IProgress<DupProgress>? progress, Func<DupFile, bool, CancellationToken, Action<long>?, ValueTask<string?>> hash)
    {
        var ct = run.Cancellation;
        var po = new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct };

        // 1. Confirm the strong tags (S3: one HEAD each); what is not confirmed is compared by content.
        var toCheck = candidates.SelectMany(g => g).Where(f => src.ClassifyTag(f.Tag) == ContentTagKind.Strong).ToList();
        var confirmed = new ConcurrentDictionary<string, string>(StringComparer.Ordinal); // path -> tag
        long checkedFiles = 0;
        await Parallel.ForEachAsync(toCheck, po, (f, token) =>
        {
            try
            {
                if (src.TryGetContentTag(f.Path, f.Tag, run, out var tag, out bool strong) && strong)
                    confirmed[f.Path] = NormalizeTag(tag);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
            catch (Exception) { Interlocked.Increment(ref _errors); }
            progress?.Report(new DupProgress(DupPhase.Tags, Interlocked.Increment(ref checkedFiles), toCheck.Count, 0, 0, f.Path, run.Requests));
            return ValueTask.CompletedTask;
        });

        // 2. Per size: classes of equal confirmed tags; the unsure files (and one file of each class, to compare them
        //    with the unsure ones) get a partial hash.
        var work = new List<(List<DupFile> Group, List<List<DupFile>> Classes, List<DupFile> Unsure)>();
        foreach (var g in candidates)
        {
            var classes = g.Where(f => confirmed.ContainsKey(f.Path)).GroupBy(f => confirmed[f.Path], StringComparer.Ordinal)
                .Select(c => c.ToList()).ToList();
            var unsure = g.Where(f => !confirmed.ContainsKey(f.Path)).ToList();
            work.Add((g, classes, unsure));
        }

        var toHash = work.SelectMany(w => w.Unsure.Count == 0 ? Enumerable.Empty<DupFile>() : w.Unsure.Concat(w.Classes.Select(c => c[0])))
            .ToList();
        long total = toHash.Count, totalBytes = toHash.Sum(f => Math.Min(f.Size, SmallFile));
        long done = 0, bytes = 0;
        var partial = new ConcurrentDictionary<string, string?>(StringComparer.Ordinal);
        await Parallel.ForEachAsync(toHash, po, async (f, token) =>
        {
            partial[f.Path] = await hash(f, true, token, null);
            long d = Interlocked.Increment(ref done);
            long b = Interlocked.Add(ref bytes, Math.Min(f.Size, SmallFile));
            progress?.Report(new DupProgress(DupPhase.PartialHash, d, total, b, totalBytes, f.Path, run.Requests));
        });

        // 3. Groups.
        var result = new List<DuplicateGroup>();
        foreach (var (g, classes, unsure) in work)
        {
            long size = g[0].Size;
            if (unsure.Count == 0)
            {
                foreach (var c in classes.Where(c => c.Count > 1))
                    result.Add(new DuplicateGroup(size, c.Select(f => f.Path).ToArray()) { Basis = DupBasis.ContentTag });
                continue;
            }

            var used = new HashSet<List<DupFile>>();
            var buckets = unsure.Select(f => (Files: new List<DupFile> { f }, Class: (List<DupFile>?)null))
                .Concat(classes.Select(c => (Files: c, Class: (List<DupFile>?)c)))
                .Where(x => partial.TryGetValue(x.Files[0].Path, out var h) && h != null)
                .GroupBy(x => partial[x.Files[0].Path]!, StringComparer.Ordinal);
            foreach (var bucket in buckets)
            {
                var reps = bucket.Where(x => x.Class != null).ToList();
                var loose = bucket.Where(x => x.Class == null).SelectMany(x => x.Files).ToList();
                if (reps.Count >= 2)
                {
                    // Different confirmed tags: different contents. Unsure files with the same partial hash cannot be
                    // assigned to one of them without a full download; they only form a group among themselves.
                    if (loose.Count > 1)
                        result.Add(Group(size, loose, DupBasis.PartialHash));
                    continue;
                }
                var members = loose.Concat(reps.SelectMany(r => r.Files)).ToList();
                if (reps.Count == 1) used.Add(reps[0].Class!);
                if (members.Count < 2) continue;
                var basis = size <= SmallFile ? DupBasis.FullHash
                    : loose.Count == 0 ? DupBasis.ContentTag
                    : DupBasis.PartialHash;
                result.Add(Group(size, members, basis));
            }
            // Classes whose sample could not be read (or that matched nothing) are duplicates among themselves anyway.
            foreach (var c in classes.Where(c => c.Count > 1 && !used.Contains(c)))
                result.Add(Group(size, c, DupBasis.ContentTag));
        }

        result.Sort((a, b) => b.Wasted.CompareTo(a.Wasted));
        progress?.Report(new DupProgress(DupPhase.Done, total, total, totalBytes, totalBytes, "", run.Requests));
        return result;
    }

    private static DuplicateGroup Group(long size, List<DupFile> files, DupBasis basis) =>
        new(size, files.Select(f => f.Path).ToArray()) { Basis = basis };

    /// <summary>Like <see cref="TryHash"/> over a remote source: partial = first and last block (2 range reads), else everything.</summary>
    private async ValueTask<string?> RemoteHashAsync(IContentHashSource src, DupFile f, bool partialOnly, ListRun run, DownloadBudget budget,
        CancellationToken ct, Action<long>? bytesRead)
    {
        try
        {
            using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            bool ok;
            if (!partialOnly || f.Size <= SmallFile)
                ok = await FeedRangeAsync(src, f, 0, f.Size, h, run, budget, ct, partialOnly ? null : bytesRead);
            else
                ok = await FeedRangeAsync(src, f, 0, Block, h, run, budget, ct, null)
                     && await FeedRangeAsync(src, f, f.Size - Block, Block, h, run, budget, ct, null);
            if (!ok)
            {
                Interlocked.Increment(ref _skipped);
                return null;
            }
            return Convert.ToHexString(h.GetHashAndReset());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        catch
        {
            Interlocked.Increment(ref _errors);
            return null;
        }
    }

    /// <summary>Reads a byte range into the hash (in memory only); false if the download limit stopped it.</summary>
    private async ValueTask<bool> FeedRangeAsync(IContentHashSource src, DupFile f, long offset, long count, IncrementalHash h, ListRun run,
        DownloadBudget budget, CancellationToken ct, Action<long>? bytesRead)
    {
        if (!await budget.ReserveAsync(count, ct)) return false;
        var buf = ArrayPool<byte>.Shared.Rent((int)Math.Min(1 << 20, Math.Max(count, 1)));
        bool dangling = false;
        Stream? s = null;
        try
        {
            // Opening may block (SFTP open, first range request): off the caller's thread. A cancel does not wait for
            // the source to give up (an SFTP read may sit in its operation timeout): the run ends at once, the request
            // is left to fail in the background.
            var open = Task.Run(() => src.OpenRead(f.Path, offset, count, run), ct);
            try
            {
                s = await open.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _ = open.ContinueWith(t => DisposeQuietly(t.Result), TaskContinuationOptions.OnlyOnRanToCompletion);
                throw;
            }
            while (count > 0)
            {
                ct.ThrowIfCancellationRequested();
                // The token is not handed to the stream: some clients run blocking work in their cancel callback (SSH.NET
                // on a dead connection), which would hold up whoever cancels (the UI). The source ends its requests through
                // the run's token / CancelWith; here the wait ends at once.
                var read = s.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, count)), CancellationToken.None).AsTask();
                int n;
                try
                {
                    n = await read.WaitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!read.IsCompleted)
                {
                    dangling = true; // the read may still write into buf: it must not go back to the pool
                    _ = read.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                    throw;
                }
                if (n <= 0) throw new EndOfStreamException(); // shorter than listed: changed meanwhile
                h.AppendData(buf, 0, n);
                count -= n;
                Interlocked.Add(ref _bytesRead, n);
                bytesRead?.Invoke(n);
            }
            return true;
        }
        finally
        {
            if (s != null)
            {
                // Closing a remote handle is a request as well: after a cancel it is done in the background.
                if (ct.IsCancellationRequested) _ = Task.Run(() => DisposeQuietly(s));
                else s.Dispose();
            }
            if (!dangling) ArrayPool<byte>.Shared.Return(buf);
        }
    }

    private static void DisposeQuietly(Stream s)
    {
        try { s.Dispose(); }
        catch (Exception) { /* a handle on an aborted connection */ }
    }

    // ================================================================== partial + full hash

    /// <summary>
    /// The comparison of the disk and of the thorough mode: partial hash of every candidate, then the full hash of the
    /// files that are still equal and bigger than two blocks (smaller ones were read completely already).
    /// </summary>
    private async Task<List<DuplicateGroup>> HashAsync(IReadOnlyList<List<DupFile>> candidates, int parallelism,
        IProgress<DupProgress>? progress, CancellationToken ct,
        Func<DupFile, bool, CancellationToken, Action<long>?, ValueTask<string?>> hash, Func<long> requests)
    {
        long total = candidates.Sum(l => (long)l.Count);
        long totalBytes = candidates.Sum(l => l.Sum(f => f.Size));

        // 2. Partial hash. Files up to two blocks are read completely here, so their partial hash already is the full one.
        long done = 0, bytes = 0;
        var partial = new List<List<DupFile>>();
        var po = new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct };
        foreach (var sameSize in candidates)
        {
            var hashed = new (DupFile File, string? Hash)[sameSize.Count];
            await Parallel.ForEachAsync(Enumerable.Range(0, sameSize.Count), po, async (i, token) =>
            {
                var f = sameSize[i];
                hashed[i] = (f, await hash(f, true, token, null));
                long d = Interlocked.Increment(ref done);
                long b = Interlocked.Add(ref bytes, Math.Min(f.Size, 2L * Block));
                progress?.Report(new DupProgress(DupPhase.PartialHash, d, total, b, totalBytes, f.Path, requests()));
            });
            foreach (var g in hashed.Where(h => h.Hash != null).GroupBy(h => h.Hash!))
            {
                var list = g.Select(h => h.File).ToList();
                if (list.Count > 1) partial.Add(list);
            }
        }

        // 3. Full hash, only for what is still equal. Small files (read completely above) need no second pass.
        long fullTotal = partial.Sum(l => l.Where(f => f.Size > 2L * Block).Sum(f => f.Size));
        long fullFiles = partial.Sum(l => (long)l.Count(f => f.Size > 2L * Block));
        long fullDone = 0, fullBytes = 0;
        var result = new List<DuplicateGroup>();
        foreach (var group in partial)
        {
            ct.ThrowIfCancellationRequested();
            if (group[0].Size <= 2L * Block)
            {
                // identical partial hash == identical content
                result.Add(new DuplicateGroup(group[0].Size, group.Select(f => f.Path).ToArray()));
                continue;
            }

            var hashed = new (DupFile File, string? Hash)[group.Count];
            await Parallel.ForEachAsync(Enumerable.Range(0, group.Count), po, async (i, token) =>
            {
                var f = group[i];
                hashed[i] = (f, await hash(f, false, token,
                    n => progress?.Report(new DupProgress(DupPhase.FullHash, Interlocked.Read(ref fullDone), fullFiles,
                        Interlocked.Add(ref fullBytes, n), fullTotal, f.Path, requests()))));
                progress?.Report(new DupProgress(DupPhase.FullHash, Interlocked.Increment(ref fullDone), fullFiles,
                    Interlocked.Read(ref fullBytes), fullTotal, f.Path, requests()));
            });
            foreach (var g in hashed.Where(h => h.Hash != null).GroupBy(h => h.Hash!))
            {
                var paths = g.Select(h => h.File.Path).ToArray();
                if (paths.Length > 1) result.Add(new DuplicateGroup(group[0].Size, paths));
            }
        }

        result.Sort((a, b) => b.Wasted.CompareTo(a.Wasted));
        progress?.Report(new DupProgress(DupPhase.Done, total, total, totalBytes, totalBytes, "", requests()));
        return result;
    }

    /// <summary>Hash of a local file (partial: size + first and last block); null when the file cannot be read.</summary>
    private string? TryHash(string path, long size, bool partialOnly, CancellationToken ct, Action<long>? bytesRead)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                1, FileOptions.SequentialScan);
            if (fs.Length != size) return null; // changed since it was listed

            using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buf = new byte[partialOnly ? Block : 1 << 20];

            if (partialOnly)
            {
                if (size <= 2L * Block)
                {
                    Feed(fs, h, buf, size, ct, null);
                }
                else
                {
                    Feed(fs, h, buf, Block, ct, null);
                    fs.Seek(-Block, SeekOrigin.End);
                    Feed(fs, h, buf, Block, ct, null);
                }
            }
            else
            {
                Feed(fs, h, buf, size, ct, bytesRead);
            }
            return Convert.ToHexString(h.GetHashAndReset());
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            Interlocked.Increment(ref _errors);
            return null;
        }
    }

    private static void Feed(FileStream fs, IncrementalHash h, byte[] buf, long count, CancellationToken ct, Action<long>? bytesRead)
    {
        while (count > 0)
        {
            ct.ThrowIfCancellationRequested();
            int n = fs.Read(buf, 0, (int)Math.Min(buf.Length, count));
            if (n <= 0) throw new EndOfStreamException();
            h.AppendData(buf, 0, n);
            count -= n;
            bytesRead?.Invoke(n);
        }
    }
}
