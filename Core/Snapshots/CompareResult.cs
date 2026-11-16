using DiskTree.Core.Storage;

namespace DiskTree.Core.Snapshots;

public sealed class CompareResult
{
    public required List<DiffRow> Rows { get; init; }
    /// <summary>The folder both scans cover; only what is below it was compared.</summary>
    public required string CommonPath { get; init; }
    public long OldTotal { get; init; }
    public long NewTotal { get; init; }
    /// <summary>Path rules of the compared scans (for taking the row paths apart).</summary>
    public PathOps Paths { get; init; } = DiskPathOps.Instance;
}
