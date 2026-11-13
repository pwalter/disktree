using DiskTree.Core.Storage;

namespace DiskTree.Core;

/// <summary>
/// Sorts file timestamps into the age buckets (<see cref="AgeBuckets"/>) of ONE scan: it fixes "now" and the
/// timestamp kind when the scan starts and never changes afterwards, so worker threads can read it without a lock
/// and a later deletion hits the bucket the file was counted in. Every <see cref="Scanner"/> owns its own
/// (see <see cref="Scanner.Ages"/>), so scans in different tabs do not influence each other.
/// </summary>
public sealed class AgeClassifier
{
    private readonly long[] _cutoffs = new long[AgeBuckets.LimitMonths.Length];

    /// <summary>The timestamp the buckets are built from.</summary>
    public AgeStamp Stamp { get; }

    /// <summary>"Now" of the scan (UTC).</summary>
    public DateTime ReferenceUtc { get; }

    public AgeClassifier(AgeStamp stamp, DateTime referenceUtc)
    {
        Stamp = stamp;
        ReferenceUtc = referenceUtc;
        for (int i = 0; i < AgeBuckets.LimitMonths.Length; i++)
            _cutoffs[i] = referenceUtc.AddMonths(-AgeBuckets.LimitMonths[i]).Ticks;
    }

    /// <summary>Bucket (0 = newest .. <see cref="AgeBuckets.Count"/> - 1) for a timestamp in UTC ticks.</summary>
    public int Of(long utcTicks)
    {
        if (utcTicks >= _cutoffs[0]) return 0;
        if (utcTicks >= _cutoffs[1]) return 1;
        if (utcTicks >= _cutoffs[2]) return 2;
        if (utcTicks >= _cutoffs[3]) return 3;
        return 4;
    }

    /// <summary>The timestamp of a directory entry that this scan sums up.</summary>
    public long TicksOf(in StorageEntry e) => AgeStamps.TicksOf(in e, Stamp);

    /// <summary>How the scan lists folders: errors are reported, only <see cref="Stamp"/> is read.</summary>
    public ListOptions ListOptions => _listOptions ??= ListOptions.Strict with { Stamps = ListOptions.For(Stamp) };

    private ListOptions? _listOptions;

    /// <summary>Bucket of an existing file, or -1 if it cannot be read.</summary>
    public int OfFile(string path)
    {
        try { return Of(AgeStamps.TicksOfFile(path, Stamp)); }
        catch { return -1; }
    }
}
