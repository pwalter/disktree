using System.Windows;
using DiskTree.Core;

namespace DiskTree.Features.Treemap;

/// <summary>One rectangle of the treemap: a folder, the files of a folder, or the "Weitere" remainder.</summary>
public sealed class TreemapItem
{
    public string Name = "";
    public long Size;
    public long Files;
    public Node? Node;          // null for the "Weitere" remainder
    public bool IsFiles;        // the files lying directly in Node
    public List<TreemapItem> Kids = new();
    public Rect Rect;
    public bool Header;         // the rectangle reserves a title strip
    public int Depth;
}
