namespace DiskTree.Core.Storage;

/// <summary>
/// How the paths of one storage source are built and compared: separator, case rule, joining, taking apart.
/// Everything in the tree (nodes, snapshot, largest files, search hits) carries the provider's own full paths;
/// code that has to split or compare them goes through here instead of <see cref="System.IO.Path"/>.
/// <para>
/// The base class is the generic rule (one separator, a configurable comparison); <see cref="DiskPathOps"/> keeps the
/// exact behaviour of <see cref="System.IO.Path"/> for Windows paths.
/// </para>
/// </summary>
public class PathOps
{
    public PathOps(char separator, StringComparison comparison)
    {
        Separator = separator;
        Comparison = comparison;
        Comparer = StringComparer.FromComparison(comparison);
    }

    public char Separator { get; }

    /// <summary>How names and paths are compared (Windows: OrdinalIgnoreCase, S3/SFTP: Ordinal).</summary>
    public StringComparison Comparison { get; }

    /// <summary>The same rule for dictionaries and sets.</summary>
    public StringComparer Comparer { get; }

    public bool NameEquals(string a, string b) => string.Equals(a, b, Comparison);

    /// <summary>The path without separators at the end ("C:\a\" gives "C:\a", "C:\" gives "C:").</summary>
    public string TrimEnd(string path) => path.TrimEnd(Separator);

    /// <summary>The path with exactly one separator at the end ("C:\a", "C:\a\" and "C:\a\\" all give "C:\a\").</summary>
    public string EnsureTrailingSeparator(string path) => path.TrimEnd(Separator) + Separator;

    /// <summary>
    /// True if <paramref name="child"/> lies strictly inside the folder <paramref name="parent"/>. The check stops at
    /// a separator: "C:\ab" is not inside "C:\a". The same folder is not "inside" itself.
    /// </summary>
    public bool IsUnder(string parent, string child)
    {
        var p = EnsureTrailingSeparator(parent);
        var c = EnsureTrailingSeparator(child);
        return c.Length > p.Length && c.StartsWith(p, Comparison);
    }

    /// <summary>Like <see cref="IsUnder"/>, but the same folder counts as well.</summary>
    public bool IsSameOrUnder(string parent, string child) =>
        EnsureTrailingSeparator(child).StartsWith(EnsureTrailingSeparator(parent), Comparison);

    /// <summary>The parts of a relative path ("a\b\" gives a, b); empty parts are dropped.</summary>
    public string[] Split(string relative) => relative.Split(Separator, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// What is left of <paramref name="path"/> below <paramref name="ancestor"/> (which must contain it, see
    /// <see cref="IsSameOrUnder"/>); "" for the same folder. May start with a separator.
    /// </summary>
    public string RelativeTo(string ancestor, string path) => path[Math.Min(path.Length, TrimEnd(ancestor).Length)..];

    /// <summary>A name below a folder: exactly one separator between them (none is added after an empty parent).</summary>
    public virtual string Join(string parent, string name)
    {
        if (parent.Length == 0) return name;
        if (name.Length == 0) return parent;
        return parent[^1] == Separator || name[0] == Separator
            ? string.Concat(parent, name)
            : string.Concat(parent, Separator.ToString(), name);
    }

    /// <summary>The last part of a path, separators at the end ignored ("C:\a\b.txt" gives "b.txt", "C:\a\" gives "a"); "" for a root.</summary>
    public virtual string NameOf(string path)
    {
        var t = TrimEnd(path);
        return t[(t.LastIndexOf(Separator) + 1)..];
    }

    /// <summary>
    /// The folder a path lies in, separators at the end ignored ("C:\a\b" gives "C:\a", "C:\a" gives "C:\");
    /// null for a root or a bare name.
    /// </summary>
    public virtual string? ParentOf(string path)
    {
        var t = TrimEnd(path);
        int i = t.LastIndexOf(Separator);
        if (i < 0) return null;
        return i == 0 ? t[..1] : t[..i];
    }
}

/// <summary>
/// Windows paths: backslash, case-insensitive. Joining and taking apart is left to <see cref="System.IO.Path"/>
/// (which also knows '/', drive letters and \\server\share), so the results are exactly what the app always had.
/// </summary>
public sealed class DiskPathOps : PathOps
{
    public static readonly DiskPathOps Instance = new();

    private DiskPathOps() : base('\\', StringComparison.OrdinalIgnoreCase) { }

    public override string Join(string parent, string name) => System.IO.Path.Join(parent, name);

    public override string NameOf(string path) => System.IO.Path.GetFileName(path.TrimEnd('\\'));

    public override string? ParentOf(string path) => System.IO.Path.GetDirectoryName(path.TrimEnd('\\'));
}
