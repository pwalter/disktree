using System.Collections.Concurrent;
using System.IO;
using DiskTree.Core.Storage;

namespace DiskTree.Core;

public sealed partial class Scanner
{
    private readonly record struct Entry(string? Name, bool IsDir, long Length, bool Reparse, int Cat, int Age);

    private long _files, _dirs, _bytes, _errors, _pending;
    private int _signalled;
    private volatile string _current = "";

    public long Files => Interlocked.Read(ref _files);
    public long Dirs => Interlocked.Read(ref _dirs);
    public long Bytes => Interlocked.Read(ref _bytes);
    public long Errors => Interlocked.Read(ref _errors);
    public string Current => _current;

    /// <summary>
    /// Where the scan reads from (default: the local file system). Set before ScanAsync / ScanManyAsync; a rescan and
    /// everything that takes the tree's paths apart (<see cref="FindNode"/>, the largest-files list) use it too.
    /// </summary>
    public IStorageProvider Provider { get; set; } = DiskProvider.Instance;

    /// <summary>Path rules of <see cref="Provider"/>.</summary>
    public PathOps Paths => Provider.Paths;

    /// <summary>Folders to skip. Set before ScanAsync / RescanAsync; null = skip nothing.</summary>
    public ExcludeRules? Excludes { get; set; }

    /// <summary>The timestamp the next <see cref="ScanAsync"/> / <see cref="ScanManyAsync"/> builds the age buckets from.</summary>
    public AgeStamp AgeStamp { get; set; } = AgeStamp.Modified;

    private volatile AgeClassifier? _ages;

    /// <summary>
    /// Sorts timestamps into age buckets for this scan's tree: fixed when the scan starts (a rescan below keeps it),
    /// or the one stored in a snapshot. Before the first scan it reflects <see cref="AgeStamp"/>.
    /// </summary>
    public AgeClassifier Ages => _ages ??= new AgeClassifier(AgeStamp, DateTime.UtcNow);

    /// <summary>Folders skipped by an exclusion rule in the current (re)scan.</summary>
    public long Excluded => Interlocked.Read(ref _excluded);

    private long _excluded;
    private const int ExcludedListMax = 200;
    private readonly List<string> _excludedPaths = new();

    /// <summary>The first few skipped folders (for a tooltip).</summary>
    public string[] ExcludedPaths() { lock (_excludedPaths) return _excludedPaths.ToArray(); }

    private void NoteExcluded(Node dir, string path)
    {
        Interlocked.Increment(ref _excluded);
        TreeEditor.AddExcluded(dir);
        lock (_excludedPaths)
            if (_excludedPaths.Count < ExcludedListMax) _excludedPaths.Add(path);
    }

    /// <summary>
    /// Top of the tree: always the drive (e.g. "C:\"). Available right after ScanAsync was called;
    /// fills up while the scan runs.
    /// </summary>
    public Node? Root { get; private set; }

    /// <summary>The folder that was actually scanned (the drive itself, or a folder somewhere below it).</summary>
    public Node? ScanTarget { get; private set; }

    /// <summary>The tree was built by <see cref="ScanManyAsync"/>: Root is an artificial node above several scanned paths.</summary>
    public bool IsMulti { get; private set; }

    /// <summary>Bytes the scan is expected to find (used space of the drive), or 0 when unknown.</summary>
    public long ExpectedBytes { get; private set; }

    // ---- largest files ----
    public sealed record BigFile(string Path, long Size);

    private const int TopCount = 200;
    private readonly object _topLock = new();
    private readonly List<BigFile> _top = new();
    private long _topMin = 1 << 20; // files below 1 MB are never interesting

    private void OfferTop(string path, long size)
    {
        lock (_topLock)
        {
            if (_top.Count < TopCount)
            {
                _top.Add(new BigFile(path, size));
                if (_top.Count < TopCount) return;
            }
            else
            {
                int smallest = 0;
                for (int i = 1; i < _top.Count; i++)
                    if (_top[i].Size < _top[smallest].Size) smallest = i;
                if (size <= _top[smallest].Size) return;
                _top[smallest] = new BigFile(path, size);
            }

            // The list is full: from now on only bigger files get a name allocated at all.
            Volatile.Write(ref _topMin, _top.Min(f => f.Size));
        }
    }

    public BigFile[] TopFiles()
    {
        lock (_topLock) return _top.OrderByDescending(f => f.Size).ToArray();
    }

    /// <summary>Forgets a deleted file or everything below a deleted folder.</summary>
    public void RemoveTop(string path)
    {
        lock (_topLock)
        {
            var paths = Paths;
            _top.RemoveAll(f => paths.NameEquals(f.Path, path) || paths.IsUnder(path, f.Path));
            Volatile.Write(ref _topMin, _top.Count >= TopCount ? _top.Min(f => f.Size) : 1 << 20);
        }
    }

    /// <summary>Finds the node for a path inside the tree (only folders are nodes).</summary>
    public Node? FindNode(string path)
    {
        var cur = Root;
        if (cur == null) return null;
        var paths = Paths;

        if (IsMulti)
        {
            // The artificial root has no path: pick the scanned path that contains the requested one.
            if (path.Length == 0) return cur;
            var top = cur.Snapshot().FirstOrDefault(c => paths.IsSameOrUnder(c.FullPath, path));
            if (top == null) return null;
            return top.FindRelative(paths.RelativeTo(top.FullPath, path), paths);
        }

        // The prefix must end at a separator: below the root "\\srv\share\a" the sibling "\\srv\share\ab" is not inside.
        if (!paths.IsSameOrUnder(cur.FullPath, path)) return null;
        return cur.FindRelative(paths.RelativeTo(cur.FullPath, path), paths);
    }

    /// <summary>(Re-)reads capacity and free space of the drive node (local file system).</summary>
    public static void ReadDrive(Node drive) => ReadCapacity(DiskProvider.Instance, drive);

    /// <summary>(Re-)reads capacity and free space of the top node from this scan's <see cref="Provider"/>.</summary>
    public void ReadCapacity(Node top) => ReadCapacity(Provider, top);

    private static void ReadCapacity(IStorageProvider provider, Node top)
    {
        // Unknown (network path etc.): the node keeps what it had, the tree then just starts at the scanned folder.
        if (provider.TryGetCapacity(top.FullPath, out long total, out long free))
        {
            top.DriveTotal = total;
            top.DriveFree = free;
        }
    }

    /// <summary>
    /// Raised (from a worker thread) when something changed. It is coalesced: it fires once and then not
    /// again until the consumer calls <see cref="Acknowledge"/>, so a flood of changes costs one event.
    /// </summary>
    public event Action? Changed;

    public void Acknowledge() => Volatile.Write(ref _signalled, 0);

    private void Signal()
    {
        if (Interlocked.Exchange(ref _signalled, 1) == 0)
            Changed?.Invoke();
    }

    public async Task<Node> ScanAsync(string path, int threads, CancellationToken ct)
    {
        // The tree always starts at the drive; a scan of a subfolder hangs below a chain of
        // placeholder nodes (one per path component), so percentages can refer to the whole drive.
        _ages = new AgeClassifier(AgeStamp, DateTime.UtcNow);
        var paths = Paths;
        var where = Provider.ResolveRoot(path);
        var full = where.Target;
        var driveRoot = where.Top;
        var top = new Node { Name = driveRoot, FullPath = driveRoot, Open = 1, DriveTotal = where.Total, DriveFree = where.Free };

        var root = top;
        if (top.DriveTotal > 0 && !paths.NameEquals(paths.TrimEnd(full), paths.TrimEnd(driveRoot)))
        {
            top.PartialDrive = true;
            top.Scanned = true;
            foreach (var part in paths.Split(full[driveRoot.Length..]))
            {
                var child = new Node { Name = part, FullPath = paths.Join(root.FullPath, part), Parent = root, Open = 1, Scanned = true };
                root.AddChild(child);
                root = child;
            }
            root.Scanned = false;
        }
        else if (top.DriveTotal == 0)
        {
            // No capacity known (network share etc.): start directly at the scanned folder.
            top.Name = full;
            top.FullPath = full;
        }

        Root = top;
        ScanTarget = root;
        if (top.DriveTotal > 0 && !top.PartialDrive)
            ExpectedBytes = Math.Max(0, top.DriveTotal - top.DriveFree);
        await RunAsync(new[] { root }, threads, ct);
        return root;
    }

    /// <summary>
    /// Scans several paths into one tree below an artificial root node (<see cref="IsMulti"/>); its numbers are the
    /// total. Each path is a direct child of the root (named by its full path, no drive capacity, works for UNC paths).
    /// Callers pass at least two paths that do not contain one another (see <see cref="PathList.Reduce"/>).
    /// </summary>
    public async Task<Node> ScanManyAsync(IReadOnlyList<string> paths, int threads, CancellationToken ct)
    {
        _ages = new AgeClassifier(AgeStamp, DateTime.UtcNow);
        var top = new Node { Name = "Mehrere Pfade", FullPath = "", Open = paths.Count, Scanned = true };
        var starts = new List<Node>();
        foreach (var p in paths)
        {
            var full = Provider.Normalize(p);
            var child = new Node { Name = full, FullPath = full, Parent = top, Open = 1 };
            top.AddChild(child);
            starts.Add(child);
        }

        IsMulti = true;
        Root = top;
        ScanTarget = top;
        ExpectedBytes = 0;
        await RunAsync(starts, threads, ct);
        return top;
    }

    /// <summary>
    /// Scans a folder that is already part of the tree again, in place: its old numbers are taken out of all
    /// parents, the subtree is emptied and filled anew. The rest of the tree stays as it is, so e.g. after
    /// deleting something below a folder, the folder and everything above it are re-evaluated.
    /// Must not run while another scan of this tree is still active.
    /// </summary>
    public async Task RescanAsync(Node node, int threads, CancellationToken ct)
    {
        var top = Root ?? throw new InvalidOperationException("Es wurde noch nichts gescannt.");

        // Take the old subtree out of every parent and empty it.
        TreeEditor.ClearForRescan(node);

        // The tree now only ends at 'node' if it is an ancestor of the former scan target (or the drive itself).
        for (var n = ScanTarget; n != null; n = n.Parent)
        {
            if (n == node) { ScanTarget = node; break; }
        }
        if (node == top)
        {
            top.PartialDrive = false;
            ReadCapacity(top);
        }
        ExpectedBytes = node == top && top.DriveTotal > 0 ? Math.Max(0, top.DriveTotal - top.DriveFree) : 0;

        // Fresh numbers for the status line; the largest-files list loses what was below the folder.
        Interlocked.Exchange(ref _files, 0);
        Interlocked.Exchange(ref _dirs, 0);
        Interlocked.Exchange(ref _bytes, 0);
        Interlocked.Exchange(ref _errors, 0);
        Interlocked.Exchange(ref _excluded, 0);
        lock (_excludedPaths) _excludedPaths.Clear();
        RemoveTop(node.FullPath);

        await RunAsync(new[] { node }, threads, ct);
    }

    /// <summary>
    /// Pause the (re)scan after this many requests to the source (0 = never; only sources that send requests count
    /// them, see <see cref="ListRun"/>). Set before ScanAsync / RescanAsync.
    /// </summary>
    public long RequestLimit { get; set; }

    /// <summary>The limit was reached and the scan waits (raised on a worker thread): <see cref="ListRun.Resume"/> or cancel.</summary>
    public event Action<ListRun>? RequestLimitReached;

    private ListRun? _run;
    private ListOptions? _listOptions;

    /// <summary>The current (or last) run: requests sent to the source, pause state. Null before the first scan.</summary>
    public ListRun? Run => _run;

    /// <summary>Requests the current (or last) run sent to the source (0 for the local disk).</summary>
    public long Requests => _run?.Requests ?? 0;

    private async Task RunAsync(IReadOnlyList<Node> starts, int threads, CancellationToken ct)
    {
        var run = new ListRun(ct, RequestLimit);
        run.LimitReached += r => RequestLimitReached?.Invoke(r);
        _listOptions = Ages.ListOptions with { Run = run };
        _run = run;

        using var queue = new BlockingCollection<Node>(new ConcurrentQueue<Node>());

        _pending = starts.Count;
        foreach (var start in starts) queue.Add(start);
        Signal();

        var workers = Enumerable.Range(0, threads)
            .Select(_ => Task.Factory.StartNew(() => Worker(queue, ct), ct,
                TaskCreationOptions.LongRunning, TaskScheduler.Default))
            .ToArray();

        try
        {
            await Task.WhenAll(workers);
        }
        catch (OperationCanceledException)
        {
            // All workers are done now. The folders still waiting in the queue were never taken: without this they
            // would keep Open > 0 for good (spinner in the tree).
            AbandonQueued(queue);
            throw;
        }
        ct.ThrowIfCancellationRequested();
        if (!IsMulti) ReadCapacity(Root!);
    }

    /// <summary>
    /// After a cancelled scan: folders that were queued but not read are closed like the one that was interrupted
    /// (no longer "open", no spinner). Their numbers stay as they are (nothing was counted for them).
    /// </summary>
    private static void AbandonQueued(BlockingCollection<Node> queue)
    {
        while (queue.TryTake(out var dir))
        {
            dir.Scanned = true;
            TreeEditor.CloseChain(dir);
        }
    }

    private void Worker(BlockingCollection<Node> queue, CancellationToken ct)
    {
        foreach (var dir in queue.GetConsumingEnumerable(ct))
        {
            try
            {
                ProcessDirectory(dir, queue, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // Expected access / path problems count as "nicht lesbar"; anything else (OutOfMemory, bugs) is not
                // swallowed: it ends the scan with that exception, where it is logged and reported.
                Interlocked.Increment(ref _errors);
            }
            finally
            {
                dir.Scanned = true;
                TreeEditor.CloseChain(dir);
                Signal();

                if (Interlocked.Decrement(ref _pending) == 0)
                    queue.CompleteAdding();
            }
        }
    }

    private void ProcessDirectory(Node dir, BlockingCollection<Node> queue, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _current = dir.FullPath;

        long sum = 0, count = 0, subDirs = 0;
        Span<long> typeSize = stackalloc long[FileTypes.Count];
        Span<long> typeCount = stackalloc long[FileTypes.Count];
        Span<long> ageSize = stackalloc long[AgeBuckets.Count];
        Span<long> ageCount = stackalloc long[AgeBuckets.Count];
        Span<long> ageDelta = stackalloc long[AgeBuckets.Count];

        var classifier = Ages;
        var paths = Paths;

        try
        {
            // Errors are not ignored (they count as "nicht lesbar"); only the timestamp of the age buckets is read.
            var entries = Provider.Enumerate(dir.FullPath,
                (in StorageEntry e) =>
                {
                    bool isDir = e.IsDirectory;
                    long len = isDir ? 0 : e.Length;
                    // Folders need a name; files only when they could make it into the largest-files list.
                    string? name = isDir || len >= Volatile.Read(ref _topMin) ? e.Name.ToString() : null;
                    return new Entry(name, isDir, len, e.IsReparsePoint,
                        isDir ? 0 : FileTypes.CategoryOf(e.Name),
                        isDir ? 0 : classifier.Of(classifier.TicksOf(in e)));
                }, _listOptions ?? classifier.ListOptions);

            foreach (var e in entries)
            {
                if (e.IsDir)
                {
                    // Junctions / symlinks would double count or loop.
                    if (e.Reparse) continue;

                    if (Excludes is { IsEmpty: false } rules && rules.IsExcluded(e.Name!, dir.FullPath, paths))
                    {
                        NoteExcluded(dir, paths.Join(dir.FullPath, e.Name!));
                        continue;
                    }

                    var child = new Node
                    {
                        Name = e.Name!,
                        FullPath = paths.Join(dir.FullPath, e.Name!),
                        Parent = dir,
                        Open = 1,
                    };

                    // The folder counts as open for all ancestors until its worker is finished.
                    TreeEditor.OpenChain(dir);

                    dir.AddChild(child);
                    subDirs++;
                    Interlocked.Increment(ref _pending);
                    queue.Add(child); // no token: a folder that is registered above must end up in the queue (see AbandonQueued)
                    Signal();
                }
                else
                {
                    sum += e.Length;
                    count++;
                    dir.OwnSize += e.Length;   // only this folder's worker writes these
                    dir.OwnCount++;
                    typeSize[e.Cat] += e.Length;
                    typeCount[e.Cat]++;
                    ageSize[e.Age] += e.Length;
                    ageCount[e.Age]++;
                    ageDelta[e.Age] += e.Length;
                    if (e.Name != null)
                        OfferTop(paths.Join(dir.FullPath, e.Name), e.Length);
                }

                // Report progress upwards in batches so big folders show up live too.
                if (count + subDirs >= 500)
                {
                    Flush(dir, ref sum, ref count, ref subDirs);
                    dir.OwnTypes = FileTypes.Pack(typeSize, typeCount);
                    dir.OwnAges = AgeBuckets.Pack(ageSize, ageCount);
                    AgeBuckets.FlushDelta(dir, ageDelta);
                    Signal();
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Interlocked.Increment(ref _errors);
        }
        finally
        {
            Flush(dir, ref sum, ref count, ref subDirs);
            dir.OwnTypes = FileTypes.Pack(typeSize, typeCount);
            dir.OwnAges = AgeBuckets.Pack(ageSize, ageCount);
            AgeBuckets.FlushDelta(dir, ageDelta);
            Interlocked.Increment(ref _dirs);
        }
    }

    private void Flush(Node dir, ref long sum, ref long count, ref long subDirs)
    {
        TreeEditor.AddToChain(dir, sum, count, subDirs);

        Interlocked.Add(ref _files, count);
        Interlocked.Add(ref _bytes, sum);
        sum = count = subDirs = 0;
    }
}
