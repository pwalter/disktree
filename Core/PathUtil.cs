using DiskTree.Core.Storage;

namespace DiskTree.Core;

/// <summary>
/// Small helpers for Windows folder paths (backslash separated, case-insensitive): the disk's <see cref="PathOps"/>.
/// Code that works on a scan's tree uses the scan's own <c>Scanner.Paths</c> instead.
/// </summary>
public static class PathUtil
{
    private static PathOps Disk => DiskPathOps.Instance;

    /// <summary>The path with exactly one backslash at the end ("C:\a", "C:\a\" and "C:\a\\" all give "C:\a\").</summary>
    public static string EnsureTrailingBackslash(string path) => Disk.EnsureTrailingSeparator(path);

    /// <summary>
    /// True if <paramref name="child"/> lies strictly inside the folder <paramref name="parent"/>. The check stops at
    /// a separator: "C:\ab" is not inside "C:\a". The same folder is not "inside" itself.
    /// </summary>
    public static bool IsUnder(string parent, string child) => Disk.IsUnder(parent, child);

    /// <summary>Like <see cref="IsUnder"/>, but the same folder counts as well.</summary>
    public static bool IsSameOrUnder(string parent, string child) => Disk.IsSameOrUnder(parent, child);
}
