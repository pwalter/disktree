using DiskTree.Core.Storage;

namespace DiskTree.Core.Snapshots;

/// <summary>Compares two scans by folder path: which folders grew, shrank, are new or were removed.</summary>
public static class SnapshotCompare
{
    /// <summary>
    /// Only the area both scans cover is compared (the scanned folder of the smaller scan), otherwise a drive
    /// scan against a folder scan would report everything outside the folder as "removed".
    /// A new or removed folder is one row for its top folder; its subfolders are not listed separately.
    /// Rows are sorted by the size of the change, biggest first.
    /// Both scans must come from the same storage source; <paramref name="paths"/> are its path rules (null = Windows).
    /// </summary>
    public static CompareResult Compare(Node oldTarget, Node newTarget, PathOps? paths = null)
    {
        paths ??= DiskPathOps.Instance;
        var a = paths.TrimEnd(oldTarget.FullPath);
        var b = paths.TrimEnd(newTarget.FullPath);

        Node? na, nb;
        string common;
        if (IsSameOrBelow(b, a, paths))
        {
            // The newer scan covers the smaller area (or the same).
            nb = newTarget;
            na = oldTarget.FindRelative(b[a.Length..], paths);
            common = newTarget.FullPath;
        }
        else if (IsSameOrBelow(a, b, paths))
        {
            na = oldTarget;
            nb = newTarget.FindRelative(a[b.Length..], paths);
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
                    ? new DiffRow(x.FullPath, x.Name, DiffKind.New, 0, x.Size)
                    : new DiffRow(x.FullPath, x.Name, DiffKind.Removed, x.Size, 0));
                continue;
            }

            if (o.Size != n.Size)
                rows.Add(new DiffRow(n.FullPath, n.Name, n.Size > o.Size ? DiffKind.Grown : DiffKind.Shrunk,
                    o.Size, n.Size));

            var oldKids = o.Snapshot().ToDictionary(c => c.Name, paths.Comparer);
            foreach (var c in n.Snapshot())
            {
                oldKids.Remove(c.Name, out var match);
                work.Push((match, c));
            }
            foreach (var c in oldKids.Values) work.Push((c, null));
        }

        rows.Sort((x, y) => Math.Abs(y.Delta).CompareTo(Math.Abs(x.Delta)));
        return new CompareResult
        {
            Rows = rows, CommonPath = common, OldTotal = na?.Size ?? 0, NewTotal = nb?.Size ?? 0, Paths = paths,
        };
    }

    private static bool IsSameOrBelow(string path, string ancestor, PathOps paths) =>
        path.Equals(ancestor, paths.Comparison)
        || (path.StartsWith(ancestor, paths.Comparison)
            && (ancestor.EndsWith(paths.Separator) || path[ancestor.Length] == paths.Separator));
}
