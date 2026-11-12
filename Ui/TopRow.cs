namespace DiskTree.Ui;

public sealed class TopRow
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required string Folder { get; init; }
    public required long Size { get; init; }
    public required string SizeText { get; init; }
}
