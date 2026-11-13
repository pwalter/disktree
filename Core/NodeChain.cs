namespace DiskTree.Core;

/// <summary>The chain of folders from the top of a tree down to one folder (what the tree view has to open to show it).</summary>
public static class NodeChain
{
    /// <summary>
    /// <paramref name="root"/>, its subfolder, ..., <paramref name="target"/> (both ends included); null if
    /// <paramref name="target"/> is neither <paramref name="root"/> nor below it.
    /// </summary>
    public static List<Node>? Between(Node root, Node target)
    {
        var chain = new List<Node>();
        for (var n = target; n != null; n = n.Parent)
        {
            chain.Add(n);
            if (n == root) break;
        }
        if (chain[^1] != root) return null;
        chain.Reverse();
        return chain;
    }
}
