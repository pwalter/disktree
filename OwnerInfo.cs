using System.Collections.Concurrent;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DiskTree;

/// <summary>Owner lookup (SID to account name, cached, with timeout) and per-owner size aggregation. Never used by the scanner.</summary>
public static class OwnerInfo
{
    private static readonly ConcurrentDictionary<string, string> NameCache = new();

    public static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(4);

    /// <summary>SID of the owner of a file or folder, or null if it cannot be read (access denied, offline, ...).</summary>
    public static string? GetOwnerSid(string path)
    {
        try
        {
            FileSystemSecurity sec = Directory.Exists(path)
                ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner)
                : new FileInfo(path).GetAccessControl(AccessControlSections.Owner);
            return (sec.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier)?.Value;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Resolves a SID to DOMAIN\name; the SID string if it cannot be resolved in time. Cached.</summary>
    public static async Task<string> ResolveNameAsync(string sid, TimeSpan? timeout = null)
    {
        if (NameCache.TryGetValue(sid, out var cached)) return cached;
        string name;
        try
        {
            name = await Task.Run(() => new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value)
                .WaitAsync(timeout ?? ResolveTimeout);
        }
        catch
        {
            name = sid; // unknown / orphaned SID or domain controller too slow
        }
        NameCache[sid] = name;
        return name;
    }

    /// <summary>Synchronous variant for worker threads (cached; no timeout of its own).</summary>
    public static string ResolveName(string sid)
    {
        if (NameCache.TryGetValue(sid, out var cached)) return cached;
        string name;
        try { name = new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value; }
        catch { name = sid; }
        NameCache[sid] = name;
        return name;
    }

    public static async Task<string?> GetOwnerNameAsync(string path)
    {
        var sid = await Task.Run(() => GetOwnerSid(path));
        return sid == null ? null : await ResolveNameAsync(sid);
    }

    /// <summary>The ACL of a folder as readable lines (identity, allow/deny, rights, inheritance).</summary>
    public static string[] GetAclLines(string path)
    {
        try
        {
            var rules = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
                .GetAccessRules(true, true, typeof(NTAccount));
            return rules.Cast<FileSystemAccessRule>()
                .Select(r => $"{(r.AccessControlType == AccessControlType.Allow ? "+" : "-")} {r.IdentityReference.Value}: {Describe(r.FileSystemRights)}" +
                             (r.IsInherited ? " (geerbt)" : ""))
                .ToArray();
        }
        catch
        {
            return new[] { "Rechte nicht lesbar (Zugriff verweigert oder nicht verfügbar)." };
        }
    }

    private static string Describe(FileSystemRights r)
    {
        if ((r & FileSystemRights.FullControl) == FileSystemRights.FullControl) return "Vollzugriff";
        if ((r & FileSystemRights.Modify) == FileSystemRights.Modify) return "Ändern";
        if ((r & (FileSystemRights.ReadAndExecute | FileSystemRights.Write)) == (FileSystemRights.ReadAndExecute | FileSystemRights.Write)) return "Lesen, Ausführen, Schreiben";
        if ((r & FileSystemRights.ReadAndExecute) == FileSystemRights.ReadAndExecute) return "Lesen und Ausführen";
        if ((r & FileSystemRights.Write) == FileSystemRights.Write) return "Schreiben";
        if ((r & FileSystemRights.Read) == FileSystemRights.Read) return "Lesen";
        return r.ToString();
    }
}

public sealed record OwnerRow(string Owner, long Bytes, long Files);

public sealed record OwnerResult(List<OwnerRow> Rows, long Seen, long Skipped, long Failed, bool Cancelled)
{
    public long TotalBytes => Rows.Sum(r => r.Bytes);
}

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

        progress?.Invoke(Interlocked.Read(ref seen), Interlocked.Read(ref failed));
        var rows = bytes.Select(kv => new OwnerRow(kv.Key, kv.Value, counts[kv.Key])).OrderByDescending(r => r.Bytes).ToList();
        return new OwnerResult(rows, seen, skipped, failed, cancelled);
    }

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
