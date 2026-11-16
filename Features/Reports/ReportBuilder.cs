using DiskTree.Core;

namespace DiskTree.Features.Reports;

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
        foreach (var n in start.Walk())
        {
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
            AgeStamp = scanner.Ages.Stamp,
            TopFiles = top,
            Paths = scanner.Paths,
        };
    }

    public static string DefaultFileName(string ext) => $"DiskTree-Bericht-{DateTime.Now:yyyyMMdd-HHmm}.{ext}";
}
