using System.Collections.Concurrent;
using System.IO;
using System.IO.Enumeration;

namespace DiskTree;

public sealed class Node
{
    public string Name = "";
    public string FullPath = "";
    public Node? Parent;

    // Fields (not properties) so the scanner can use Interlocked on them.
    public long Size;       // incl. all descendants (grows while scanning)
    public long FileCount;  // incl. all descendants
    public long DirCount;   // incl. all descendants
    public long Excluded;   // subfolders skipped by an exclusion rule, incl. all descendants

    // All files directly inside this folder are aggregated into one entry.
    public long OwnSize;
    public long OwnCount;

    /// <summary>Own files per document category. Replaced as a whole (never mutated) so readers need no lock.</summary>
    public volatile TypeStat[]? OwnTypes;

    /// <summary>Number of folders in this subtree (itself included) that are not fully scanned yet.</summary>
    public int Open;

    /// <summary>True once this folder's own entries were all enumerated.</summary>
    public volatile bool Scanned;

    // Only set on the drive node at the top of the tree.
    public long DriveTotal;
    public long DriveFree;
    /// <summary>The scan started below the drive root, so the tree only contains a part of the drive.</summary>
    public bool PartialDrive;

    /// <summary>Takes a deleted file out of this folder's numbers and all parents'.</summary>
    public void RemoveFile(string name, long size)
    {
        OwnSize -= size;
        OwnCount--;
        OwnTypes = FileTypes.Without(OwnTypes, FileTypes.CategoryOf(name), size);
        for (var n = this; n != null; n = n.Parent)
        {
            Interlocked.Add(ref n.Size, -size);
            Interlocked.Add(ref n.FileCount, -1);
        }
    }

    // Subfolders. Only the worker that scans this folder writes to it,
    // the UI thread reads it while the scan is still running, so access goes through lock + Snapshot().
    private readonly List<Node> _children = new();

    public bool IsDone => Volatile.Read(ref Open) == 0;

    public void AddChild(Node child)
    {
        lock (_children) _children.Add(child);
    }

    public void RemoveChild(Node child)
    {
        lock (_children) _children.Remove(child);
    }

    public void ClearChildren()
    {
        lock (_children) _children.Clear();
    }

    public Node[] Snapshot()
    {
        lock (_children) return _children.ToArray();
    }

    public bool HasChildren
    {
        get { lock (_children) return _children.Count > 0 || OwnCount > 0; }
    }
}

public sealed class Scanner
{
    private readonly record struct Entry(string? Name, bool IsDir, long Length, bool Reparse, int Cat);

    private long _files, _dirs, _bytes, _errors, _pending;
    private int _signalled;
    private volatile string _current = "";

    public long Files => Interlocked.Read(ref _files);
    public long Dirs => Interlocked.Read(ref _dirs);
    public long Bytes => Interlocked.Read(ref _bytes);
    public long Errors => Interlocked.Read(ref _errors);
    public string Current => _current;

    /// <summary>Folders to skip. Set before ScanAsync / RescanAsync; null = skip nothing.</summary>
    public ExcludeRules? Excludes { get; set; }

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
        for (var n = dir; n != null; n = n.Parent)
            Interlocked.Increment(ref n.Excluded);
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
        var prefix = path.TrimEnd('\\') + "\\";
        lock (_topLock)
        {
            _top.RemoveAll(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)
                                || f.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            Volatile.Write(ref _topMin, _top.Count >= TopCount ? _top.Min(f => f.Size) : 1 << 20);
        }
    }

    /// <summary>Finds the node for a path inside the tree (only folders are nodes).</summary>
    public Node? FindNode(string path)
    {
        var cur = Root;
        if (cur == null || !path.StartsWith(cur.FullPath, StringComparison.OrdinalIgnoreCase)) return null;

        foreach (var part in path[cur.FullPath.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            cur = cur.Snapshot().FirstOrDefault(c => string.Equals(c.Name, part, StringComparison.OrdinalIgnoreCase));
            if (cur == null) return null;
        }
        return cur;
    }

    /// <summary>(Re-)reads capacity and free space of the drive node.</summary>
    public static void ReadDrive(Node drive)
    {
        try
        {
            var di = new DriveInfo(drive.FullPath);
            drive.DriveTotal = di.TotalSize;
            drive.DriveFree = di.AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // Network paths and the like have no capacity; the tree then just starts at the scanned folder.
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
        var full = Path.GetFullPath(path);
        var driveRoot = Path.GetPathRoot(full) ?? full;
        var top = new Node { Name = driveRoot, FullPath = driveRoot, Open = 1 };
        ReadDrive(top);

        var root = top;
        if (top.DriveTotal > 0 && !string.Equals(full.TrimEnd('\\'), driveRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            top.PartialDrive = true;
            top.Scanned = true;
            foreach (var part in full[driveRoot.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                var child = new Node { Name = part, FullPath = Path.Join(root.FullPath, part), Parent = root, Open = 1, Scanned = true };
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
        await RunAsync(root, threads, ct);
        return root;
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

        // Take the old subtree out of every parent. Open is corrected as well: a cancelled scan may have
        // left unfinished folders behind, which the parents still count.
        int oldOpen = Volatile.Read(ref node.Open);
        for (var n = node.Parent; n != null; n = n.Parent)
        {
            Interlocked.Add(ref n.Size, -node.Size);
            Interlocked.Add(ref n.FileCount, -node.FileCount);
            Interlocked.Add(ref n.DirCount, -node.DirCount);
            Interlocked.Add(ref n.Excluded, -node.Excluded);
            Interlocked.Add(ref n.Open, 1 - oldOpen);
        }

        node.Size = node.FileCount = node.DirCount = node.Excluded = 0;
        node.OwnSize = node.OwnCount = 0;
        node.OwnTypes = null;
        node.ClearChildren();
        Volatile.Write(ref node.Open, 1);
        node.Scanned = false;

        // The tree now only ends at 'node' if it is an ancestor of the former scan target (or the drive itself).
        for (var n = ScanTarget; n != null; n = n.Parent)
        {
            if (n == node) { ScanTarget = node; break; }
        }
        if (node == top)
        {
            top.PartialDrive = false;
            ReadDrive(top);
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

        await RunAsync(node, threads, ct);
    }

    private async Task RunAsync(Node start, int threads, CancellationToken ct)
    {
        using var queue = new BlockingCollection<Node>(new ConcurrentQueue<Node>());

        _pending = 1;
        queue.Add(start);
        Signal();

        var workers = Enumerable.Range(0, threads)
            .Select(_ => Task.Factory.StartNew(() => Worker(queue, ct), ct,
                TaskCreationOptions.LongRunning, TaskScheduler.Default))
            .ToArray();

        await Task.WhenAll(workers);
        ct.ThrowIfCancellationRequested();
        ReadDrive(Root!);
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
            catch
            {
                Interlocked.Increment(ref _errors);
            }
            finally
            {
                dir.Scanned = true;
                for (var n = dir; n != null; n = n.Parent)
                    Interlocked.Decrement(ref n.Open);
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

        var options = new EnumerationOptions
        {
            AttributesToSkip = 0, // include hidden + system
            IgnoreInaccessible = false,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
        };

        try
        {
            var entries = new FileSystemEnumerable<Entry>(dir.FullPath,
                (ref FileSystemEntry e) =>
                {
                    bool isDir = e.IsDirectory;
                    long len = isDir ? 0 : e.Length;
                    // Folders need a name; files only when they could make it into the largest-files list.
                    string? name = isDir || len >= Volatile.Read(ref _topMin) ? e.FileName.ToString() : null;
                    return new Entry(name, isDir, len, (e.Attributes & FileAttributes.ReparsePoint) != 0,
                        isDir ? 0 : FileTypes.CategoryOf(e.FileName));
                }, options);

            foreach (var e in entries)
            {
                if (e.IsDir)
                {
                    // Junctions / symlinks would double count or loop.
                    if (e.Reparse) continue;

                    if (Excludes is { IsEmpty: false } rules && rules.IsExcluded(e.Name!, dir.FullPath))
                    {
                        NoteExcluded(dir, Path.Join(dir.FullPath, e.Name));
                        continue;
                    }

                    var child = new Node
                    {
                        Name = e.Name!,
                        FullPath = Path.Join(dir.FullPath, e.Name),
                        Parent = dir,
                        Open = 1,
                    };

                    // The folder counts as open for all ancestors until its worker is finished.
                    for (var n = dir; n != null; n = n.Parent)
                        Interlocked.Increment(ref n.Open);

                    dir.AddChild(child);
                    subDirs++;
                    Interlocked.Increment(ref _pending);
                    queue.Add(child, ct);
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
                    if (e.Name != null)
                        OfferTop(Path.Join(dir.FullPath, e.Name), e.Length);
                }

                // Report progress upwards in batches so big folders show up live too.
                if (count + subDirs >= 500)
                {
                    Flush(dir, ref sum, ref count, ref subDirs);
                    dir.OwnTypes = FileTypes.Pack(typeSize, typeCount);
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
            Interlocked.Increment(ref _dirs);
        }
    }

    private void Flush(Node dir, ref long sum, ref long count, ref long subDirs)
    {
        for (var n = dir; n != null; n = n.Parent)
        {
            Interlocked.Add(ref n.Size, sum);
            Interlocked.Add(ref n.FileCount, count);
            Interlocked.Add(ref n.DirCount, subDirs);
        }

        Interlocked.Add(ref _files, count);
        Interlocked.Add(ref _bytes, sum);
        sum = count = subDirs = 0;
    }
}
