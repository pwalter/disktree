namespace DiskTree.Core;

/// <summary>Small helpers for Windows folder paths (backslash separated, case-insensitive).</summary>
public static class PathUtil
{
    /// <summary>The path with exactly one backslash at the end ("C:\a", "C:\a\" and "C:\a\\" all give "C:\a\").</summary>
    public static string EnsureTrailingBackslash(string path) => path.TrimEnd('\\') + "\\";

    /// <summary>
    /// True if <paramref name="child"/> lies strictly inside the folder <paramref name="parent"/>. The check stops at
    /// a separator: "C:\ab" is not inside "C:\a". The same folder is not "inside" itself.
    /// </summary>
    public static bool IsUnder(string parent, string child)
    {
        var p = EnsureTrailingBackslash(parent);
        var c = EnsureTrailingBackslash(child);
        return c.Length > p.Length && c.StartsWith(p, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Like <see cref="IsUnder"/>, but the same folder counts as well.</summary>
    public static bool IsSameOrUnder(string parent, string child) =>
        EnsureTrailingBackslash(child).StartsWith(EnsureTrailingBackslash(parent), StringComparison.OrdinalIgnoreCase);
}
