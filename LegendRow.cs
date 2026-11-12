using System.Windows.Media;

namespace DiskTree;

public sealed class LegendRow
{
    public required Brush Brush { get; init; }
    public required string Name { get; init; }
    public required string SizeText { get; init; }
    public required string PercentText { get; init; }
    public required string Detail { get; init; }
}
