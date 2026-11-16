using System.IO;
using DiskTree.Core.Storage;

namespace DiskTree.Core;

/// <summary>Which of a file's three timestamps is used (age buckets, search filter).</summary>
public enum AgeStamp { Modified = 0, Created = 1, Accessed = 2 }

/// <summary>
/// Names and conversions of the timestamp the age buckets are built from (see <see cref="AgeClassifier"/>).
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

    // Which timestamp a tree was built from is a property of its scan (AgeClassifier.Stamp, Scanner.Ages); the
    // user's choice for the next scan is kept per tab by the UI.

    public static long TicksOf(in StorageEntry e, AgeStamp stamp) => stamp switch
    {
        AgeStamp.Created => e.CreatedTicks,
        AgeStamp.Accessed => e.AccessedTicks,
        _ => e.ModifiedTicks,
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
