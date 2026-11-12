namespace DiskTree;

/// <summary>One folder that differs between two scans.</summary>
public sealed record DiffRow(string Path, string Name, DiffKind Kind, long OldSize, long NewSize, long OldFiles, long NewFiles)
{
    public long Delta => NewSize - OldSize;
}
