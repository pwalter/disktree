using System.Collections.Concurrent;
using System.IO;

namespace DiskTree.Features.Owner;

public static class OwnerAggregator
{
    /// <summary>
    /// Sums bytes per owner. <paramref name="ownerOf"/> returns the owner name or null (counted as failed, not reported).
    /// Files smaller than <paramref name="minSize"/> are skipped (sampling).
    /// </summary>
    public static OwnerResult Aggregate(IEnumerable<(string Path, long Size)> files, Func<string, string?> ownerOf,
        long minSize, int parallelism, Action<long, long>? progress, CancellationToken ct)
    {
        var bytes = new ConcurrentDictionary<string, long>();
        var counts = new ConcurrentDictionary<string, long>();
        long skipped = 0, failed = 0, seen = 0;
        bool cancelled = false;
        string? error = null;

        try
        {
            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism), CancellationToken = ct }, f =>
            {
                long n = Interlocked.Increment(ref seen);
                if (f.Size < minSize) { Interlocked.Increment(ref skipped); return; }
                var owner = ownerOf(f.Path);
                if (owner == null) { Interlocked.Increment(ref failed); return; }
                bytes.AddOrUpdate(owner, f.Size, (_, v) => v + f.Size);
                counts.AddOrUpdate(owner, 1, (_, v) => v + 1);
                if (n % 50 == 0) progress?.Invoke(n, Interlocked.Read(ref failed));
            });
        }
        catch (OperationCanceledException) { cancelled = true; }
        catch (Exception ex) when (IsExpected(ex))
        {
            // e.g. the folder was deleted or became unreadable: report it instead of crashing the caller
            error = (ex is AggregateException ag ? ag.Flatten().InnerExceptions.FirstOrDefault() ?? ag : ex).Message;
        }

        progress?.Invoke(Interlocked.Read(ref seen), Interlocked.Read(ref failed));
        var rows = bytes.Select(kv => new OwnerRow(kv.Key, kv.Value, counts[kv.Key])).OrderByDescending(r => r.Bytes).ToList();
        return new OwnerResult(rows, seen, skipped, failed, cancelled, error);
    }

    private static bool IsExpected(Exception ex) => ex switch
    {
        AggregateException ag => ag.Flatten().InnerExceptions.All(IsExpected),
        OperationCanceledException => false,
        IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException => true,
        _ => false,
    };

    /// <summary>Lazily enumerates all files below a folder (inaccessible folders are skipped).</summary>
    public static IEnumerable<(string Path, long Size)> EnumerateFiles(string root)
    {
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var fi in new DirectoryInfo(root).EnumerateFiles("*", opts))
        {
            long len;
            try { len = fi.Length; } catch { continue; }
            yield return (fi.FullName, len);
        }
    }
}
