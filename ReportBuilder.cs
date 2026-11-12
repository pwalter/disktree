using System.Globalization;

namespace DiskTree;

/// <summary>Everything a space report shows, collected once so the HTML and PDF writers print the same numbers.</summary>
public sealed class ReportData
{
    public sealed record FolderRow(string Path, long Size, long Own, long Files);
    public sealed record TypeRow(string Name, uint Color, long Size, long Count);
    public sealed record AgeRow(string Name, long Size, long Count);

    public string Title { get; init; } = "";
    public DateTime Created { get; init; }
    public bool Interim { get; init; }
    public long TotalSize { get; init; }
    public long Files { get; init; }
    public long Dirs { get; init; }
    public long Excluded { get; init; }
    public long DriveTotal { get; init; }
    public long DriveFree { get; init; }
    public bool PartialDrive { get; init; }
    public string DriveName { get; init; } = "";
    public IReadOnlyList<FolderRow> Folders { get; init; } = Array.Empty<FolderRow>();
    public IReadOnlyList<TypeRow> Types { get; init; } = Array.Empty<TypeRow>();
    public IReadOnlyList<AgeRow> Ages { get; init; } = Array.Empty<AgeRow>();
    public IReadOnlyList<Scanner.BigFile> TopFiles { get; init; } = Array.Empty<Scanner.BigFile>();

    public const int FolderCount = 20;
    public const int FileCount = 50;

    public static string Percent(long part, long whole) =>
        (whole <= 0 ? 0 : part * 100.0 / whole).ToString("0.#", CultureInfo.CurrentCulture) + " %";
}

public static class ReportBuilder
{
    /// <summary>Reads the finished (or still running) scan into a <see cref="ReportData"/>. Safe on a background thread.</summary>
    public static ReportData Build(Scanner scanner, bool interim)
    {
        var start = scanner.ScanTarget ?? scanner.Root ?? throw new InvalidOperationException("Kein Scan vorhanden.");
        var drive = scanner.Root;

        var sizes = new long[FileTypes.Count];
        var counts = new long[FileTypes.Count];
        var ageSizes = new long[AgeBuckets.Count];
        var ageCounts = new long[AgeBuckets.Count];

        // The smallest of the kept folders sits at the top of the queue and is dropped first.
        var best = new PriorityQueue<Node, long>();
        var stack = new Stack<Node>();
        stack.Push(start);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n.OwnTypes is { } types)
                foreach (var t in types) { sizes[t.Cat] += t.Size; counts[t.Cat] += t.Count; }
            if (n.OwnAges is { } ages)
                for (int b = 0; b < AgeBuckets.Count; b++) { ageSizes[b] += ages[b]; ageCounts[b] += ages[AgeBuckets.Count + b]; }

            if (n != start)
            {
                long s = Interlocked.Read(ref n.Size);
                if (s > 0)
                {
                    best.Enqueue(n, s);
                    if (best.Count > ReportData.FolderCount) best.Dequeue();
                }
            }
            foreach (var c in n.Snapshot()) stack.Push(c);
        }

        var folders = new List<ReportData.FolderRow>();
        while (best.Count > 0)
        {
            var n = best.Dequeue();
            folders.Add(new(n.FullPath, Interlocked.Read(ref n.Size), n.OwnSize, Interlocked.Read(ref n.FileCount)));
        }
        folders.Reverse();

        var typeRows = new List<ReportData.TypeRow>();
        for (int i = 0; i < FileTypes.Count; i++)
            if (counts[i] > 0)
                typeRows.Add(new(FileTypes.All[i].Name, FileTypes.All[i].Color, sizes[i], counts[i]));
        typeRows.Sort((a, b) => b.Size.CompareTo(a.Size));

        var ageRows = new List<ReportData.AgeRow>();
        for (int b = 0; b < AgeBuckets.Count; b++)
            ageRows.Add(new(AgeBuckets.Names[b], ageSizes[b], ageCounts[b]));

        var top = scanner.TopFiles().Take(ReportData.FileCount).ToList();

        bool driveKnown = drive != null && drive.DriveTotal > 0;
        return new ReportData
        {
            Title = scanner.IsMulti ? string.Join("; ", start.Snapshot().Select(c => c.FullPath)) : start.FullPath,
            Created = DateTime.Now,
            Interim = interim,
            TotalSize = Interlocked.Read(ref start.Size),
            Files = Interlocked.Read(ref start.FileCount),
            Dirs = Interlocked.Read(ref start.DirCount),
            Excluded = Interlocked.Read(ref start.Excluded),
            DriveTotal = driveKnown ? drive!.DriveTotal : 0,
            DriveFree = driveKnown ? drive!.DriveFree : 0,
            PartialDrive = driveKnown && drive!.PartialDrive,
            DriveName = driveKnown ? drive!.Name : "",
            Folders = folders,
            Types = typeRows,
            Ages = ageRows,
            TopFiles = top,
        };
    }

    public static string DefaultFileName(string ext) => $"DiskTree-Bericht-{DateTime.Now:yyyyMMdd-HHmm}.{ext}";
}
