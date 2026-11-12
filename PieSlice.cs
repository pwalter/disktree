using System.Windows.Media;

namespace DiskTree;

public sealed record PieSlice(string Label, double Value, Brush Brush, string Detail);
