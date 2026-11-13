namespace DiskTree.Core;

// The list of the largest files lives in the scanner; a rename has to change the paths in it.
public sealed partial class Scanner
{
    /// <summary>A file or folder was renamed: paths in the largest-files list follow.</summary>
    public void RenameTop(string oldPath, string newPath)
    {
        var paths = Paths;
        var oldPrefix = paths.EnsureTrailingSeparator(oldPath);
        lock (_topLock)
        {
            for (int i = 0; i < _top.Count; i++)
            {
                var p = _top[i].Path;
                if (paths.NameEquals(p, oldPath))
                    _top[i] = _top[i] with { Path = newPath };
                else if (p.StartsWith(oldPrefix, paths.Comparison))
                    _top[i] = _top[i] with { Path = paths.EnsureTrailingSeparator(newPath) + p[oldPrefix.Length..] };
            }
        }
    }
}
