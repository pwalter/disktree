using System.IO;

namespace DiskTree;

/// <summary>
/// Age ("last modified") buckets. Like the file types they are summed per folder while scanning, so no
/// per-file memory is needed: <see cref="Node.OwnAges"/> holds the folder's own files (sizes + counts),
/// <see cref="Node.AgeSub"/> the sizes of the whole subtree (kept up to date like Node.Size, for the tree column).
/// </summary>
public static class AgeBuckets
{
    public const int Count = 5;

    public static readonly string[] Names = { "Unter 1 Monat", "1 Monat – 1 Jahr", "1 – 2 Jahre", "2 – 5 Jahre", "Über 5 Jahre" };

    /// <summary>Lower age limit (in months) of buckets 1..4, i.e. the choices for the "old" threshold.</summary>
    public static readonly int[] LimitMonths = { 1, 12, 24, 60 };

    private static readonly long[] Cutoffs = new long[LimitMonths.Length];
    private static int _oldFrom = 3; // first bucket counted as "old"

    static AgeBuckets() => SetReference(DateTime.UtcNow);

    /// <summary>Fixes "now" for a scan. Deletions later on use the same reference so they hit the bucket the file was counted in.</summary>
    public static void SetReference(DateTime utcNow)
    {
        for (int i = 0; i < LimitMonths.Length; i++)
            Cutoffs[i] = utcNow.AddMonths(-LimitMonths[i]).Ticks;
    }

    public static int Of(long lastWriteUtcTicks)
    {
        if (lastWriteUtcTicks >= Cutoffs[0]) return 0;
        if (lastWriteUtcTicks >= Cutoffs[1]) return 1;
        if (lastWriteUtcTicks >= Cutoffs[2]) return 2;
        if (lastWriteUtcTicks >= Cutoffs[3]) return 3;
        return 4;
    }

    /// <summary>Bucket of an existing file, or -1 if it cannot be read.</summary>
    public static int OfFile(string path)
    {
        try { return Of(File.GetLastWriteTimeUtc(path).Ticks); }
        catch { return -1; }
    }

    /// <summary>Age threshold in months (one of <see cref="LimitMonths"/>); files at least this old count as "old".</summary>
    public static int ThresholdMonths
    {
        get => LimitMonths[_oldFrom - 1];
        set
        {
            int i = Array.IndexOf(LimitMonths, value);
            _oldFrom = (i < 0 ? 2 : i) + 1;
        }
    }

    public static int OldFrom => _oldFrom;

    public static string ThresholdText(int months) => months switch
    {
        1 => "1 Monat",
        12 => "1 Jahr",
        _ => months % 12 == 0 ? $"{months / 12} Jahre" : $"{months} Monate",
    };

    /// <summary>Bytes in a subtree that are older than the threshold.</summary>
    public static long OldSize(Node n)
    {
        var a = n.AgeSub;
        if (a == null) return 0;
        long sum = 0;
        for (int b = _oldFrom; b < Count; b++) sum += Math.Max(0, Volatile.Read(ref a[b]));
        return sum;
    }

    /// <summary>Bytes in the folder's own files that are older than the threshold.</summary>
    public static long OldOwnSize(Node n)
    {
        var a = n.OwnAges;
        if (a == null) return 0;
        long sum = 0;
        for (int b = _oldFrom; b < Count; b++) sum += a[b];
        return sum;
    }

    /// <summary>Adds (or, with negative values, takes away) bytes of one bucket to the node and all its parents.</summary>
    public static void AddSub(Node node, int bucket, long size)
    {
        for (var n = node; n != null; n = n.Parent)
        {
            var a = n.AgeSub ?? Interlocked.CompareExchange(ref n.AgeSub, new long[Count], null) ?? n.AgeSub!;
            Interlocked.Add(ref a[bucket], size);
        }
    }

    /// <summary>Hands the bytes collected since the last call up to the folder and its parents, then clears them.</summary>
    public static void FlushDelta(Node dir, Span<long> delta)
    {
        for (int b = 0; b < Count; b++)
        {
            if (delta[b] == 0) continue;
            AddSub(dir, b, delta[b]);
            delta[b] = 0;
        }
    }

    /// <summary>Compact snapshot of a folder's own files: sizes in [0..4], counts in [5..9]; null if there are none.</summary>
    public static long[]? Pack(ReadOnlySpan<long> sizes, ReadOnlySpan<long> counts)
    {
        bool any = false;
        var r = new long[Count * 2];
        for (int i = 0; i < Count; i++)
        {
            r[i] = sizes[i];
            r[Count + i] = counts[i];
            any |= counts[i] != 0;
        }
        return any ? r : null;
    }

    /// <summary>Copy of the own-files snapshot with one file taken out (after a deletion).</summary>
    public static long[]? Without(long[]? own, int bucket, long size)
    {
        if (own == null || bucket < 0) return own;
        var r = (long[])own.Clone();
        r[bucket] = Math.Max(0, r[bucket] - size);
        r[Count + bucket] = Math.Max(0, r[Count + bucket] - 1);
        return r;
    }

    /// <summary>Takes a whole subtree's age sums out of all its parents (folder deleted or about to be rescanned).</summary>
    public static void RemoveSubtree(Node node)
    {
        if (node.AgeSub is not { } a || node.Parent == null) return;
        for (int b = 0; b < Count; b++)
        {
            long v = Volatile.Read(ref a[b]);
            if (v != 0) AddSub(node.Parent, b, -v);
        }
    }
}
