using DiskTree.Core;

namespace DiskTree.Ui;

/// <summary>
/// The "Älter als" threshold the user picked (a way to look at the age data, saved in the settings): it applies to
/// all tabs and can change at any time without a new scan. UI thread only.
/// </summary>
public static class AgeThreshold
{
    /// <summary>First age bucket that counts as "old".</summary>
    public static int OldFrom { get; private set; } = AgeBuckets.OldFromMonths(24);

    /// <summary>The threshold in months (one of <see cref="AgeBuckets.LimitMonths"/>).</summary>
    public static int Months
    {
        get => AgeBuckets.MonthsOfOldFrom(OldFrom);
        set => OldFrom = AgeBuckets.OldFromMonths(value);
    }

    /// <summary>Bytes in a subtree that are older than the threshold.</summary>
    public static long OldSize(Node n) => AgeBuckets.OldSize(n, OldFrom);

    /// <summary>Bytes in the folder's own files that are older than the threshold.</summary>
    public static long OldOwnSize(Node n) => AgeBuckets.OldOwnSize(n, OldFrom);
}
