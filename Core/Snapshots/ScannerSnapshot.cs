namespace DiskTree;

// Scanner built from a stored scan (see SnapshotStore). Kept in its own file so Scanner.cs stays untouched
// apart from being partial.
public sealed partial class Scanner
{
    /// <summary>True when the tree comes from a .disktree file and not from the disk (it may be out of date).</summary>
    public bool IsSnapshot { get; private set; }

    /// <summary>The file the tree was loaded from, when it is a snapshot.</summary>
    public string? SnapshotFile { get; set; }

    /// <summary>When the data was read from the disk (start of the scan, or the time stored in the file).</summary>
    public DateTime? ScannedAt { get; set; }

    public static Scanner FromSnapshot(LoadedSnapshot s, string? file)
    {
        var scanner = new Scanner
        {
            Root = s.Root,
            ScanTarget = s.Target,
            ExpectedBytes = s.ExpectedBytes,
            IsSnapshot = true,
            SnapshotFile = file,
            ScannedAt = s.Info.ScannedAt == default ? null : s.Info.ScannedAt,
        };
        lock (scanner._topLock)
        {
            scanner._top.AddRange(s.Top.OrderByDescending(f => f.Size).Take(TopCount));
            scanner._topMin = scanner._top.Count >= TopCount ? scanner._top.Min(f => f.Size) : 1 << 20;
        }
        return scanner;
    }
}
