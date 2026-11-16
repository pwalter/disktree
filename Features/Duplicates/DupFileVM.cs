using System.Windows;
using DiskTree.Core.Storage;

namespace DiskTree.Features.Duplicates;

public sealed class DupFileVM
{
    public DupGroupVM Group { get; }
    public string Path { get; }
    public string Name { get; }
    public string Folder { get; }
    /// <summary>Check mark for choosing several files for move / ZIP / delete.</summary>
    public bool IsChecked { get; set; }
    /// <summary>The check box is only offered where files can be moved / zipped / deleted (the disk).</summary>
    public Visibility CheckVisibility { get; }

    /// <param name="paths">Path rules of the source the file belongs to (null: the disk).</param>
    public DupFileVM(DupGroupVM group, string path, PathOps? paths = null, bool canCheck = true)
    {
        Group = group;
        Path = path;
        paths ??= DiskPathOps.Instance;
        Name = paths.NameOf(path);
        Folder = paths.ParentOf(path) ?? "";
        CheckVisibility = canCheck ? Visibility.Visible : Visibility.Collapsed;
    }
}
