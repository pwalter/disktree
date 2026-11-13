
namespace DiskTree.Core.Snapshots;

/// <summary>One folder that differs between two scans.</summary>
public sealed record DiffRow(string Path, string Name, DiffKind Kind, long OldSize, long NewSize)
{
    public long Delta => NewSize - OldSize;
}
