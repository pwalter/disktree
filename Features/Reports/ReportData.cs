using System.Globalization;
using DiskTree.Core;

namespace DiskTree.Features.Reports;

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
