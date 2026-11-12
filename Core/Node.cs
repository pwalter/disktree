namespace DiskTree.Core;

public sealed class Node
{
    public string Name = "";
    public string FullPath = "";
    public Node? Parent;

    // Fields (not properties) so the scanner can use Interlocked on them. Changes after the scan go through TreeEditor.
    public long Size;       // incl. all descendants (grows while scanning)
    public long FileCount;  // incl. all descendants
    public long DirCount;   // incl. all descendants
    public long Excluded;   // subfolders skipped by an exclusion rule, incl. all descendants

    // All files directly inside this folder are aggregated into one entry.
    public long OwnSize;
    public long OwnCount;

    /// <summary>Own files per document category. Replaced as a whole (never mutated) so readers need no lock.</summary>
    public volatile TypeStat[]? OwnTypes;

    /// <summary>Own files per age bucket (see <see cref="AgeBuckets"/>); replaced as a whole.</summary>
    public volatile long[]? OwnAges;

    /// <summary>Bytes per age bucket of the whole subtree (updated with Interlocked like Size).</summary>
    public long[]? AgeSub;

    /// <summary>Number of folders in this subtree (itself included) that are not fully scanned yet.</summary>
    public int Open;

    /// <summary>True once this folder's own entries were all enumerated.</summary>
    public volatile bool Scanned;

    // Only set on the drive node at the top of the tree.
    public long DriveTotal;
    public long DriveFree;
    /// <summary>The scan started below the drive root, so the tree only contains a part of the drive.</summary>
    public bool PartialDrive;

    // Subfolders. Only the worker that scans this folder writes to it,
    // the UI thread reads it while the scan is still running, so access goes through lock + Snapshot().
    private readonly List<Node> _children = new();

    public bool IsDone => Volatile.Read(ref Open) == 0;

    public void AddChild(Node child)
    {
        lock (_children) _children.Add(child);
    }

    public void RemoveChild(Node child)
    {
        lock (_children) _children.Remove(child);
    }

    public void ClearChildren()
    {
        lock (_children) _children.Clear();
    }

    public Node[] Snapshot()
    {
        lock (_children) return _children.ToArray();
    }

    public bool HasChildren
    {
        get { lock (_children) return _children.Count > 0 || OwnCount > 0; }
    }
}
