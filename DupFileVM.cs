namespace DiskTree;

public sealed class DupFileVM
{
    public DupGroupVM Group { get; }
    public string Path { get; }
    public string Name { get; }
    public string Folder { get; }
    /// <summary>Check mark for choosing several files for move / ZIP / delete.</summary>
    public bool IsChecked { get; set; }

    public DupFileVM(DupGroupVM group, string path)
    {
        Group = group;
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        Folder = System.IO.Path.GetDirectoryName(path) ?? "";
    }
}
