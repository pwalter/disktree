namespace DiskTree.Core;

/// <summary>
/// The one place that keeps the numbers of a scanned tree consistent when something changes after the scan:
/// a file or folder is deleted, a file appears, something is renamed, a folder is about to be scanned again.
/// Every change walks the chain of parents (sizes, counts, age buckets) here and nowhere else; the scanner uses
/// <see cref="AddToChain"/> too while it reads the disk. The numbers must always come out as those of a fresh scan
/// (see the tests).
/// </summary>
public static class TreeEditor
{
    /// <summary>Adds (or, with negative values, takes away) size and counts to <paramref name="from"/> and all its parents.</summary>
    public static void AddToChain(Node from, long size, long files, long dirs = 0)
    {
        for (var n = from; n != null; n = n.Parent)
        {
            Interlocked.Add(ref n.Size, size);
            Interlocked.Add(ref n.FileCount, files);
            Interlocked.Add(ref n.DirCount, dirs);
        }
    }

    /// <summary>A scanned folder was found below <paramref name="dir"/> that a rule skipped: counted into it and all parents.</summary>
    public static void AddExcluded(Node dir, long count = 1)
    {
        for (var n = dir; n != null; n = n.Parent)
            Interlocked.Add(ref n.Excluded, count);
    }

    /// <summary>A folder starts being scanned: all its parents count it as open until its worker is done.</summary>
    public static void OpenChain(Node dir)
    {
        for (var n = dir; n != null; n = n.Parent)
            Interlocked.Increment(ref n.Open);
    }

    /// <summary>The worker of <paramref name="dir"/> is done: it no longer counts as open for itself and all parents.</summary>
    public static void CloseChain(Node dir)
    {
        for (var n = dir; n != null; n = n.Parent)
            Interlocked.Decrement(ref n.Open);
    }

    // ---------------------------------------------------------------- files

    /// <summary>A new file appeared in a scanned folder: counts it into the folder, its parents, types and age buckets.</summary>
    public static void AddFile(Node dir, string name, long size, int ageBucket)
    {
        dir.OwnSize += size;
        dir.OwnCount++;

        int cat = FileTypes.CategoryOf(name);
        var stats = (dir.OwnTypes ?? Array.Empty<TypeStat>()).ToList();
        int at = stats.FindIndex(s => s.Cat == cat);
        if (at >= 0) stats[at] = stats[at] with { Size = stats[at].Size + size, Count = stats[at].Count + 1 };
        else stats.Add(new TypeStat(cat, size, 1));
        dir.OwnTypes = stats.ToArray();

        if (ageBucket >= 0)
        {
            var own = dir.OwnAges != null ? (long[])dir.OwnAges.Clone() : new long[AgeBuckets.Count * 2];
            own[ageBucket] += size;
            own[AgeBuckets.Count + ageBucket]++;
            dir.OwnAges = own;
            AgeBuckets.AddSub(dir, ageBucket, size);
        }
        AddToChain(dir, size, 1);
    }

    /// <summary>Takes a deleted file out of the folder's numbers and all parents'. <paramref name="ageBucket"/> -1: age buckets stay as they are.</summary>
    public static void RemoveFile(Node dir, string name, long size, int ageBucket = -1)
    {
        dir.OwnSize -= size;
        dir.OwnCount--;
        dir.OwnTypes = FileTypes.Without(dir.OwnTypes, FileTypes.CategoryOf(name), size);
        if (ageBucket >= 0)
        {
            dir.OwnAges = AgeBuckets.Without(dir.OwnAges, ageBucket, size);
            AgeBuckets.AddSub(dir, ageBucket, -size);
        }
        AddToChain(dir, -size, -1);
    }

    /// <summary>A file was renamed (the extension may have changed, and with it the type): counted out and in again.</summary>
    public static void RenameFile(Node dir, string oldName, string newName, long size, int ageBucket)
    {
        RemoveFile(dir, oldName, size, ageBucket);
        AddFile(dir, newName, size, ageBucket);
    }

    // ---------------------------------------------------------------- folders

    /// <summary>
    /// A folder was deleted: its numbers leave all parents and it is cut out of the tree. A node without parent
    /// (the root) cannot be removed. (The parents' <see cref="Node.Excluded"/> is left alone, as it always was.)
    /// </summary>
    public static void RemoveFolder(Node node)
    {
        var parent = node.Parent;
        if (parent == null) return;

        AgeBuckets.RemoveSubtree(node);
        AddToChain(parent, -node.Size, -node.FileCount, -(node.DirCount + 1));
        parent.RemoveChild(node);
    }

    /// <summary>A folder was renamed on disk: the node and everything below get the new path.</summary>
    public static void RenameFolder(Node node, string newName)
    {
        node.Name = newName;
        node.FullPath = System.IO.Path.Combine(node.Parent?.FullPath ?? System.IO.Path.GetDirectoryName(node.FullPath.TrimEnd('\\')) ?? "", newName);
        Repath(node);
    }

    private static void Repath(Node node)
    {
        foreach (var c in node.Snapshot())
        {
            c.FullPath = System.IO.Path.Combine(node.FullPath, c.Name);
            Repath(c);
        }
    }

    /// <summary>
    /// A folder is going to be scanned again in place: its old numbers are taken out of every parent (Open is
    /// corrected as well: a cancelled scan may have left unfinished folders behind, which the parents still count),
    /// and the folder itself is emptied, open and not scanned yet.
    /// </summary>
    public static void ClearForRescan(Node node)
    {
        int oldOpen = Volatile.Read(ref node.Open);
        for (var n = node.Parent; n != null; n = n.Parent)
        {
            Interlocked.Add(ref n.Size, -node.Size);
            Interlocked.Add(ref n.FileCount, -node.FileCount);
            Interlocked.Add(ref n.DirCount, -node.DirCount);
            Interlocked.Add(ref n.Excluded, -node.Excluded);
            Interlocked.Add(ref n.Open, 1 - oldOpen);
        }

        AgeBuckets.RemoveSubtree(node);
        if (node.AgeSub is { } oldAges) Array.Clear(oldAges);
        node.OwnAges = null;
        node.Size = node.FileCount = node.DirCount = node.Excluded = 0;
        node.OwnSize = node.OwnCount = 0;
        node.OwnTypes = null;
        node.ClearChildren();
        Volatile.Write(ref node.Open, 1);
        node.Scanned = false;
    }
}
