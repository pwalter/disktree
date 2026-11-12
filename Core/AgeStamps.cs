using System.IO;
using System.IO.Enumeration;

namespace DiskTree.Core;

/// <summary>Which of a file's three timestamps is used (age buckets, search filter).</summary>
public enum AgeStamp { Modified = 0, Created = 1, Accessed = 2 }

/// <summary>
/// The timestamp the age buckets are built from.
///
/// Design decision: the scan sums only ONE set of buckets (the chosen timestamp), it does not keep all three.
/// The three timestamps come out of the same directory record, so reading another one is free, but three bucket
/// sets would triple OwnAges/AgeSub (about 340 bytes more per folder, i.e. ~25 MB more for the 70,000 folders of
/// C:\Windows, hundreds of MB for a big data drive) and the snapshot files. Measured scan time of C:\Windows is the
/// same for all three timestamps (see PR). The price: switching the timestamp needs a new scan, which the "Alter"
/// tab says clearly and offers to do in one click.
/// </summary>
public static class AgeStamps
{
    public static readonly string[] Names = { "Zuletzt geändert", "Erstellt", "Zuletzt geöffnet" };

    /// <summary>Adjective form for titles ("Alter – zuletzt geändert").</summary>
    public static readonly string[] Lower = { "zuletzt geändert", "erstellt", "zuletzt geöffnet" };

    /// <summary>The timestamp the numbers of the current tree were built from (set when a scan starts or a snapshot is loaded).</summary>
    public static AgeStamp Current { get; set; } = AgeStamp.Modified;

    /// <summary>The user's choice for the next scan.</summary>
    public static AgeStamp Preferred { get; set; } = AgeStamp.Modified;

    /// <summary>Called when a whole scan starts: the new tree uses the user's choice. (A rescan below keeps <see cref="Current"/>.)</summary>
    public static void BeginScan()
    {
        Current = Preferred;
        ScanStarted?.Invoke();
    }

    /// <summary>Raised by <see cref="BeginScan"/> (on the thread that starts the scan).</summary>
    public static event Action? ScanStarted;

    public static long TicksOf(ref FileSystemEntry e, AgeStamp stamp) => stamp switch
    {
        AgeStamp.Created => e.CreationTimeUtc.UtcTicks,
        AgeStamp.Accessed => e.LastAccessTimeUtc.UtcTicks,
        _ => e.LastWriteTimeUtc.UtcTicks,
    };

    public static long TicksOfFile(string path, AgeStamp stamp) => stamp switch
    {
        AgeStamp.Created => File.GetCreationTimeUtc(path).Ticks,
        AgeStamp.Accessed => File.GetLastAccessTimeUtc(path).Ticks,
        _ => File.GetLastWriteTimeUtc(path).Ticks,
    };

    public static string ToKey(AgeStamp s) => s switch { AgeStamp.Created => "created", AgeStamp.Accessed => "accessed", _ => "modified" };

    public static AgeStamp FromKey(string? key) => key switch
    {
        "created" => AgeStamp.Created,
        "accessed" => AgeStamp.Accessed,
        _ => AgeStamp.Modified,
    };
}
