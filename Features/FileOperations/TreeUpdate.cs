using DiskTree.Core;

namespace DiskTree.Features.FileOperations;

/// <summary>
/// Keeps a scanned tree in step after file actions on a source with its own path rules (SFTP): sizes, counts, file
/// types, age buckets and the largest files, all through <see cref="TreeEditor"/>, so the numbers equal a fresh scan.
/// The UI adds the rows on top (FileOpsController).
/// </summary>
public static class TreeUpdate
{
    /// <summary>
    /// Items that are gone from their place (deleted, moved away). <paramref name="removeFolder"/>: how a folder node leaves
    /// the tree (the UI also drops its row); default <see cref="TreeEditor.RemoveFolder"/>. <paramref name="movedTo"/>: new
    /// paths of moved items; one that lands inside the tree keeps its place in the largest files under the new path.
    /// </summary>
    public static void Removed(Scanner scanner, IEnumerable<OpItem> removed, Action<Node>? removeFolder = null,
        IReadOnlyDictionary<string, string>? movedTo = null)
    {
        foreach (var it in removed)
        {
            if (it.IsDir)
            {
                if (scanner.FindNode(it.Path) is { Parent: not null } node) (removeFolder ?? TreeEditor.RemoveFolder)(node);
            }
            else if (it.Parent is { } parent && scanner.FindNode(parent) is { } dir)
            {
                TreeEditor.RemoveFile(dir, it.Name, it.Size, it.AgeBucket);
            }
            if (movedTo != null && movedTo.TryGetValue(it.Path, out var to) && scanner.Paths.ParentOf(to) is { } toDir
                && scanner.FindNode(toDir) != null)
                scanner.RenameTop(it.Path, to);
            else
                scanner.RemoveTop(it.Path);
        }
    }

    /// <summary>Renamed items: folders (and everything below) get the new path, files are counted out and in again (the type may change).</summary>
    public static void Renamed(Scanner scanner, OpResult res)
    {
        var paths = scanner.Paths;
        foreach (var item in res.Done)
        {
            var newPath = res.NewPaths[item.Path];
            var newName = paths.NameOf(newPath);
            if (item.IsDir)
            {
                if (scanner.FindNode(item.Path) is { } node) TreeEditor.RenameFolder(node, newName, paths);
            }
            else if (item.Parent is { } parent && scanner.FindNode(parent) is { } dir)
            {
                TreeEditor.RenameFile(dir, item.Name, newName, item.Size, item.AgeBucket);
            }
            scanner.RenameTop(item.Path, newPath);
        }
    }

    /// <summary>
    /// After a move into <paramref name="destDir"/>: plain files are counted into its node right away. If folders arrived,
    /// something was replaced or the folder is not fully scanned, the folder is returned for a rescan (in place). A new
    /// folder inside the scanned area: the folder above it that is in the tree is returned. Null: nothing more to do.
    /// </summary>
    public static Node? Arrived(Scanner scanner, string destDir, OpResult res)
    {
        if (res.Done.Count == 0) return null;
        var paths = scanner.Paths;
        var node = scanner.FindNode(destDir);
        if (node == null)
        {
            for (var p = paths.ParentOf(destDir); p != null; p = paths.ParentOf(p))
                if (scanner.FindNode(p) is { } above)
                    return above; // a new folder inside the tree: the folder above it is scanned again to show it
            return null;
        }

        bool plain = !res.Replaced && res.Done.All(i => !i.IsDir) && node.Scanned;
        if (!plain) return node;
        foreach (var it in res.Done)
        {
            var newPath = res.NewPaths[it.Path];
            TreeEditor.AddFile(node, paths.NameOf(newPath), it.Size, it.AgeBucket);
        }
        return null;
    }
}
