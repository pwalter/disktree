using System.IO;

namespace DiskTree.Core;

/// <summary>The path box may hold several paths, separated by semicolons.</summary>
public static class PathList
{
    public const char Separator = ';';

    /// <summary>Splits the text into normalized, distinct paths. A text that is a single existing folder stays one path.</summary>
    public static List<string> Parse(string? text)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return list;

        var whole = Normalize(text);
        if (Directory.Exists(whole)) { list.Add(whole); return list; }

        foreach (var part in text.Split(Separator, StringSplitOptions.RemoveEmptyEntries))
        {
            var p = Normalize(part);
            if (p.Length > 0 && !list.Contains(p, StringComparer.OrdinalIgnoreCase)) list.Add(p);
        }
        return list;
    }

    public static string Join(IEnumerable<string> paths) => string.Join(Separator + " ", paths);

    /// <summary>"C:" becomes "C:\", surrounding quotes and blanks go away.</summary>
    public static string Normalize(string text)
    {
        var path = text.Trim().Trim('"').Trim();
        return path.Length == 2 && path[1] == ':' ? path + "\\" : path;
    }

    /// <summary>Full paths without duplicates and without paths that lie inside another one (they would be counted twice).</summary>
    public static List<string> Reduce(IEnumerable<string> paths)
    {
        var full = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        static string Dir(string p) => p.TrimEnd('\\') + "\\";
        return full.Where(p => !full.Any(o => !ReferenceEquals(o, p)
                                              && Dir(p).Length > Dir(o).Length
                                              && Dir(p).StartsWith(Dir(o), StringComparison.OrdinalIgnoreCase))).ToList();
    }
}
