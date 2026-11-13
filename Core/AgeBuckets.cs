
namespace DiskTree.Core;

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

    // Which timestamp and which "now" the buckets of a scan come from: see AgeClassifier (one per scan).

    /// <summary>
    /// First bucket that counts as "old" for an age threshold in months (one of <see cref="LimitMonths"/>;
    /// anything else means 24 months). The threshold is only a way to look at the data, it can change without a new scan.
    /// </summary>
    public static int OldFromMonths(int months)
    {
        int i = Array.IndexOf(LimitMonths, months);
        return (i < 0 ? 2 : i) + 1;
    }

    /// <summary>The threshold in months for a first "old" bucket (inverse of <see cref="OldFromMonths"/>).</summary>
    public static int MonthsOfOldFrom(int oldFrom) => LimitMonths[oldFrom - 1];

    public static string ThresholdText(int months) => months switch
    {
        1 => "1 Monat",
        12 => "1 Jahr",
        _ => months % 12 == 0 ? $"{months / 12} Jahre" : $"{months} Monate",
    };

    /// <summary>Bytes in a subtree that are older than the threshold.</summary>
    public static long OldSize(Node n, int oldFrom)
    {
        var a = n.AgeSub;
        if (a == null) return 0;
        long sum = 0;
        for (int b = oldFrom; b < Count; b++) sum += Math.Max(0, Volatile.Read(ref a[b]));
        return sum;
    }

    /// <summary>Bytes in the folder's own files that are older than the threshold.</summary>
    public static long OldOwnSize(Node n, int oldFrom)
    {
        var a = n.OwnAges;
        if (a == null) return 0;
        long sum = 0;
        for (int b = oldFrom; b < Count; b++) sum += a[b];
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
