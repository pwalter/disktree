namespace DiskTree.Core.Snapshots;

/// <summary>Compares two scans by folder path: which folders grew, shrank, are new or were removed.</summary>
public static class SnapshotCompare
{
    /// <summary>
    /// Only the area both scans cover is compared (the scanned folder of the smaller scan), otherwise a drive
    /// scan against a folder scan would report everything outside the folder as "removed".
    /// A new or removed folder is one row for its top folder; its subfolders are not listed separately.
    /// Rows are sorted by the size of the change, biggest first.
    /// </summary>
    public static CompareResult Compare(Node oldTarget, Node newTarget)
    {
        var a = oldTarget.FullPath.TrimEnd('\\');
        var b = newTarget.FullPath.TrimEnd('\\');

        Node? na, nb;
        string common;
        if (IsSameOrBelow(b, a))
        {
            // The newer scan covers the smaller area (or the same).
            nb = newTarget;
            na = Descend(oldTarget, b[a.Length..]);
            common = newTarget.FullPath;
        }
        else if (IsSameOrBelow(a, b))
        {
            na = oldTarget;
            nb = Descend(newTarget, a[b.Length..]);
            common = oldTarget.FullPath;
        }
        else
        {
            throw new InvalidOperationException(
                $"Die beiden Scans überschneiden sich nicht:\n{oldTarget.FullPath}\n{newTarget.FullPath}");
        }

        var rows = new List<DiffRow>();
        var work = new Stack<(Node? Old, Node? New)>();
        work.Push((na, nb));

        while (work.Count > 0)
        {
            var (o, n) = work.Pop();
            if (o == null && n == null) continue;

            if (o == null || n == null)
            {
                // Only the top of a new/removed subtree is reported.
                var x = (o ?? n)!;
                rows.Add(o == null
                    ? new DiffRow(x.FullPath, x.Name, DiffKind.New, 0, x.Size, 0, x.FileCount)
                    : new DiffRow(x.FullPath, x.Name, DiffKind.Removed, x.Size, 0, x.FileCount, 0));
                continue;
            }

            if (o.Size != n.Size)
                rows.Add(new DiffRow(n.FullPath, n.Name, n.Size > o.Size ? DiffKind.Grown : DiffKind.Shrunk,
                    o.Size, n.Size, o.FileCount, n.FileCount));

            var oldKids = o.Snapshot().ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var c in n.Snapshot())
            {
                oldKids.Remove(c.Name, out var match);
                work.Push((match, c));
            }
            foreach (var c in oldKids.Values) work.Push((c, null));
        }

        rows.Sort((x, y) => Math.Abs(y.Delta).CompareTo(Math.Abs(x.Delta)));
        return new CompareResult { Rows = rows, CommonPath = common, OldTotal = na?.Size ?? 0, NewTotal = nb?.Size ?? 0 };
    }

    private static bool IsSameOrBelow(string path, string ancestor) =>
        path.Equals(ancestor, StringComparison.OrdinalIgnoreCase)
        || (path.StartsWith(ancestor, StringComparison.OrdinalIgnoreCase)
            && (ancestor.EndsWith('\\') || path[ancestor.Length] == '\\'));

    private static Node? Descend(Node from, string relative)
    {
        var cur = from;
        foreach (var part in relative.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            cur = cur.Snapshot().FirstOrDefault(c => string.Equals(c.Name, part, StringComparison.OrdinalIgnoreCase));
            if (cur == null) return null;
        }
        return cur;
    }
}
