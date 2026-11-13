using DiskTree.Core.Storage;

namespace DiskTree.Core;

/// <summary>
/// Folders the scanner skips. Compiled once per scan, so checking a folder costs a hash lookup (plus a few
/// wildcard comparisons if there are any) and no regex.
/// <para>
/// Pattern forms (case-insensitive):
/// <list type="bullet">
/// <item><c>node_modules</c> - folder name, wherever it occurs</item>
/// <item><c>*.tmp</c>, <c>cache?</c> - folder name with wildcards (<c>*</c>, <c>?</c>)</item>
/// <item><c>C:\Windows\Temp</c> - full path; the folder and everything below it</item>
/// <item><c>C:\Users\*\AppData</c> - full path with wildcards</item>
/// </list>
/// </para>
/// </summary>
public sealed class ExcludeRules
{
    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly string[] _nameWildcards;
    private readonly string[] _pathWildcards;

    public bool IsEmpty { get; }

    public ExcludeRules(IEnumerable<string> patterns)
    {
        var nameWild = new List<string>();
        var pathWild = new List<string>();

        foreach (var raw in patterns)
        {
            var p = Normalize(raw);
            if (p.Length == 0) continue;

            bool wild = p.AsSpan().IndexOfAny('*', '?') >= 0;
            if (IsPathPattern(p))
            {
                if (wild) pathWild.Add(p); else _paths.Add(p);
            }
            else
            {
                if (wild) nameWild.Add(p); else _names.Add(p);
            }
        }

        _nameWildcards = nameWild.ToArray();
        _pathWildcards = pathWild.ToArray();
        IsEmpty = _names.Count == 0 && _paths.Count == 0 && _nameWildcards.Length == 0 && _pathWildcards.Length == 0;
    }

    /// <summary>Trims, strips quotes, unifies the separator and drops a trailing one. Empty = not a pattern.</summary>
    public static string Normalize(string? pattern)
    {
        var p = (pattern ?? "").Trim().Trim('"').Trim().Replace('/', '\\');
        // "C:\" keeps its backslash (it is the drive root), everything else loses a trailing one.
        if (!(p.Length == 3 && p[1] == ':' && p[2] == '\\')) p = p.TrimEnd('\\');
        return p;
    }

    private static bool IsPathPattern(string p) => p.Contains('\\') || p.Contains(':');

    /// <summary>
    /// Wildcard match: '*' = any run of characters (also across '\'), '?' = exactly one, case-insensitive.
    /// Own implementation because FileSystemName.MatchesSimpleExpression treats '\' as an escape character.
    /// Linear backtracking over the last '*', no allocation.
    /// </summary>
    public static bool WildcardMatch(ReadOnlySpan<char> pattern, ReadOnlySpan<char> text)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && pattern[p] == '*') { star = p++; mark = t; }
            else if (p < pattern.Length &&
                     (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(text[t])))
            { p++; t++; }
            else if (star >= 0) { p = star + 1; t = ++mark; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    /// <summary>Is the subfolder <paramref name="name"/> of <paramref name="parentPath"/> (a Windows path) excluded?</summary>
    public bool IsExcluded(string name, string parentPath) => IsExcluded(name, parentPath, DiskPathOps.Instance);

    /// <summary>
    /// Like <see cref="IsExcluded(string, string)"/>, the full path joined by <paramref name="paths"/>. The patterns
    /// themselves stay Windows-style and case-insensitive (see the class comment).
    /// </summary>
    public bool IsExcluded(string name, string parentPath, PathOps paths)
    {
        if (IsEmpty) return false;

        if (_names.Contains(name)) return true;
        foreach (var w in _nameWildcards)
            if (WildcardMatch(w, name)) return true;

        if (_paths.Count > 0 || _pathWildcards.Length > 0)
        {
            var full = paths.Join(parentPath, name);
            if (_paths.Contains(full)) return true;
            foreach (var w in _pathWildcards)
                if (WildcardMatch(w, full)) return true;
        }
        return false;
    }
}
