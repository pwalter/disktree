using System.Globalization;
using System.IO;
using System.Text;

namespace DiskTree;

public enum ExportKind { Subtree, WholeScan, TopFiles, ReportHtml, ReportPdf }

/// <summary>
/// CSV export (semicolon separated, UTF-8 with BOM, so German Excel opens it directly).
/// Numbers come as plain bytes plus one readable column. Safe to call from a background thread,
/// also while a scan is still running (the numbers are then a snapshot).
/// </summary>
public static class Exporter
{
    private const char Sep = ';';

    public static string DefaultFileName(ExportKind kind, Node? node) =>
        kind == ExportKind.TopFiles
            ? $"DiskTree-groesste-Dateien-{DateTime.Now:yyyyMMdd-HHmm}.csv"
            : $"DiskTree-{Safe(node?.Name)}-{DateTime.Now:yyyyMMdd-HHmm}.csv";

    private static string Safe(string? name)
    {
        var s = new string((name ?? "").Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        return s.Length == 0 ? "Baum" : s;
    }

    /// <summary>Writes the folder tree below <paramref name="root"/>, depth first, biggest folders first. Returns the row count.</summary>
    public static int WriteTree(string file, Node root)
    {
        using var w = Open(file);
        Line(w, "Ebene", "Name", "Pfad", "Größe (Bytes)", "Größe", "Dateien", "Ordner", "Anteil an Startordner (%)", "Scan vollständig");

        long total = Math.Max(1, Interlocked.Read(ref root.Size));
        int rows = 0;
        var stack = new Stack<(Node Node, int Level)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            var (n, level) = stack.Pop();
            long size = Interlocked.Read(ref n.Size);
            Line(w,
                level.ToString(CultureInfo.InvariantCulture),
                n.Name.Length == 0 ? n.FullPath : n.Name,
                n.FullPath,
                size.ToString(CultureInfo.InvariantCulture),
                NodeVM.FormatSize(size),
                Interlocked.Read(ref n.FileCount).ToString(CultureInfo.InvariantCulture),
                Interlocked.Read(ref n.DirCount).ToString(CultureInfo.InvariantCulture),
                (size * 100.0 / total).ToString("0.##", CultureInfo.CurrentCulture),
                n.IsDone ? "ja" : "nein");
            rows++;

            // Push the smallest first so the biggest child is popped (written) first.
            foreach (var c in n.Snapshot().OrderBy(c => Interlocked.Read(ref c.Size)))
                stack.Push((c, level + 1));
        }
        return rows;
    }

    public static int WriteTopFiles(string file, IReadOnlyList<Scanner.BigFile> files)
    {
        using var w = Open(file);
        Line(w, "Rang", "Name", "Ordner", "Pfad", "Größe (Bytes)", "Größe");
        int rank = 0;
        foreach (var f in files)
        {
            rank++;
            Line(w,
                rank.ToString(CultureInfo.InvariantCulture),
                Path.GetFileName(f.Path),
                Path.GetDirectoryName(f.Path) ?? "",
                f.Path,
                f.Size.ToString(CultureInfo.InvariantCulture),
                NodeVM.FormatSize(f.Size));
        }
        return rank;
    }

    private static StreamWriter Open(string file) =>
        new(file, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 1 << 16) { NewLine = "\r\n" };

    private static void Line(StreamWriter w, params string[] cells)
    {
        for (int i = 0; i < cells.Length; i++)
        {
            if (i > 0) w.Write(Sep);
            w.Write(Escape(cells[i]));
        }
        w.WriteLine();
    }

    private static string Escape(string s)
    {
        // Names starting with = + - @ would be read as a formula by Excel.
        if (s.Length > 0 && s[0] is '=' or '+' or '-' or '@') s = "'" + s;
        return s.IndexOfAny(new[] { Sep, '"', '\r', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}
