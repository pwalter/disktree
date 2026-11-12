using System.IO;
using System.Security.Cryptography;

namespace DiskTree.Features.Duplicates;

/// <summary>A set of byte-identical files.</summary>
public sealed record DuplicateGroup(long Size, string[] Paths)
{
    /// <summary>Space that would be freed by keeping only one copy.</summary>
    public long Wasted => Size * (Paths.Length - 1);
}

public enum DupPhase { Enumerating, Sizes, PartialHash, FullHash, Done }

public readonly record struct DupProgress(DupPhase Phase, long FilesDone, long FilesTotal, long BytesDone, long BytesTotal, string Current);

/// <summary>
/// Finds duplicate files below a folder: group by size, then compare a partial hash (first + last block),
/// then the full SHA-256. Every step only looks at files that are still candidates, so most files are read little or never.
/// Reparse points (junctions, symlinks) are skipped like in the <see cref="Scanner"/>.
/// </summary>
public sealed class DuplicateFinder
{
    private const int Block = 64 * 1024;

    public long Errors => Interlocked.Read(ref _errors);
    private long _errors;

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

    /// <param name="minSize">Files smaller than this are ignored (empty files always are).</param>
    /// <param name="parallelism">Number of files hashed at the same time.</param>
    public async Task<List<DuplicateGroup>> FindAsync(string root, long minSize, int parallelism,
        IProgress<DupProgress>? progress, CancellationToken ct)
    {
        minSize = Math.Max(1, minSize);
        parallelism = Math.Max(1, parallelism);
        _errors = 0;

        // 1. Enumerate and group by size (cheap: sizes come with the directory listing). One thread, to stay gentle on the disk.
        var bySize = await Task.Run(() => GroupBySize(root, minSize, progress, ct), ct);

        var candidates = bySize.Values.Where(l => l.Count > 1).ToList();
        long total = candidates.Sum(l => (long)l.Count);
        long totalBytes = candidates.Sum(l => l.Sum(f => f.Size));

        // 2. Partial hash. Files up to two blocks are read completely here, so their partial hash already is the full one.
        long done = 0, bytes = 0;
        var partial = new List<List<Cand>>();
        var po = new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct };
        foreach (var sameSize in candidates)
        {
            var hashed = new (Cand File, string? Hash)[sameSize.Count];
            await Parallel.ForEachAsync(Enumerable.Range(0, sameSize.Count), po, (i, token) =>
            {
                var f = sameSize[i];
                hashed[i] = (f, TryHash(f.Path, f.Size, partialOnly: true, token, null));
                long d = Interlocked.Increment(ref done);
                long b = Interlocked.Add(ref bytes, Math.Min(f.Size, 2L * Block));
                progress?.Report(new DupProgress(DupPhase.PartialHash, d, total, b, totalBytes, f.Path));
                return ValueTask.CompletedTask;
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

            var hashed = new (Cand File, string? Hash)[group.Count];
            await Parallel.ForEachAsync(Enumerable.Range(0, group.Count), po, (i, token) =>
            {
                var f = group[i];
                hashed[i] = (f, TryHash(f.Path, f.Size, partialOnly: false, token,
                    n => progress?.Report(new DupProgress(DupPhase.FullHash, Interlocked.Read(ref fullDone), fullFiles,
                        Interlocked.Add(ref fullBytes, n), fullTotal, f.Path))));
                progress?.Report(new DupProgress(DupPhase.FullHash, Interlocked.Increment(ref fullDone), fullFiles,
                    Interlocked.Read(ref fullBytes), fullTotal, f.Path));
                return ValueTask.CompletedTask;
            });
            foreach (var g in hashed.Where(h => h.Hash != null).GroupBy(h => h.Hash!))
            {
                var paths = g.Select(h => h.File.Path).ToArray();
                if (paths.Length > 1) result.Add(new DuplicateGroup(group[0].Size, paths));
            }
        }

        result.Sort((a, b) => b.Wasted.CompareTo(a.Wasted));
        progress?.Report(new DupProgress(DupPhase.Done, total, total, totalBytes, totalBytes, ""));
        return result;
    }

    private readonly record struct Cand(string Path, long Size);

    private Dictionary<long, List<Cand>> GroupBySize(string root, long minSize, IProgress<DupProgress>? progress, CancellationToken ct)
    {
        var map = new Dictionary<long, List<Cand>>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint, // skips junctions / symlinks (and cloud placeholders), like the scanner
            ReturnSpecialDirectories = false,
        };

        long seen = 0;
        var en = new System.IO.Enumeration.FileSystemEnumerable<(string Path, long Len)>(root,
            (ref System.IO.Enumeration.FileSystemEntry e) => (e.ToFullPath(), e.Length), options)
        {
            ShouldIncludePredicate = (ref System.IO.Enumeration.FileSystemEntry e) => !e.IsDirectory,
        };

        try
        {
            foreach (var (path, len) in en)
            {
                ct.ThrowIfCancellationRequested();
                if (len < minSize) continue;
                if (!map.TryGetValue(len, out var list)) map[len] = list = new List<Cand>(2);
                list.Add(new Cand(path, len));
                if ((++seen & 0x3FF) == 0)
                    progress?.Report(new DupProgress(DupPhase.Enumerating, seen, 0, 0, 0, path));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { Interlocked.Increment(ref _errors); }

        progress?.Report(new DupProgress(DupPhase.Sizes, seen, 0, 0, 0, ""));
        return map;
    }

    /// <summary>Hash of the file (partial: size + first and last block); null when the file cannot be read.</summary>
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
