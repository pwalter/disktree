using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using DiskTree.Core;

namespace DiskTree.Features.FileOperations;

/// <summary>A file or folder the clean-up actions work on. <see cref="AgeBucket"/> is read before the action, while the file still exists.</summary>
public sealed record OpItem(string Path, bool IsDir, long Size, int AgeBucket = -1)
{
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\'));
    public string? Parent => System.IO.Path.GetDirectoryName(Path.TrimEnd('\\'));
}

public enum ConflictPolicy { Ask, Skip, Replace, Rename }

/// <summary>Outcome of one action. Errors are collected per file, the action goes on after a failure.</summary>
public sealed class OpResult
{
    /// <summary>Items that are gone from their old place (moved, deleted, renamed) or fully handled (archived).</summary>
    public List<OpItem> Done { get; } = new();
    public List<(string Path, string Error)> Failed { get; } = new();
    public List<(OpItem Item, string Reason)> Skipped { get; } = new();
    /// <summary>Old path to new path (move, rename).</summary>
    public Dictionary<string, string> NewPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>An existing target was replaced: sizes in the destination folder changed in a way that needs a rescan.</summary>
    public bool Replaced { get; set; }

    public long DoneBytes => Done.Sum(i => i.Size);
}

public sealed record RenamePlan(OpItem Item, string NewName, string? Problem)
{
    public bool Unchanged => string.Equals(Item.Name, NewName, StringComparison.Ordinal);
    public bool Ok => Problem == null && !Unchanged;
    public string NewPath => System.IO.Path.Combine(Item.Parent ?? "", NewName);
}

public static class FileOperations
{
    // ---------------------------------------------------------------- selection helpers

    /// <summary>Removes duplicates and items that lie inside another selected folder (they move / go with it).</summary>
    public static List<OpItem> Normalize(IEnumerable<OpItem> items)
    {
        var list = items.GroupBy(i => i.Path.TrimEnd('\\'), StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        var dirs = list.Where(i => i.IsDir).Select(i => i.Path.TrimEnd('\\') + "\\").ToList();
        return list.Where(i => !dirs.Any(d => i.Path.StartsWith(d, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(i => i.Parent, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>A drive root cannot be moved, renamed or archived.</summary>
    public static bool IsRoot(OpItem i) => i.Parent == null;

    public static string UniqueName(string folder, string name)
    {
        string stem = System.IO.Path.GetFileNameWithoutExtension(name), ext = System.IO.Path.GetExtension(name);
        for (int n = 2; n < 10000; n++)
        {
            var candidate = $"{stem} ({n}){ext}";
            var p = System.IO.Path.Combine(folder, candidate);
            if (!File.Exists(p) && !Directory.Exists(p)) return candidate;
        }
        return $"{stem} ({Guid.NewGuid():N}){ext}";
    }

    // ---------------------------------------------------------------- recycle bin

    /// <summary>Sends one file or folder to the recycle bin. Returns null on success, otherwise the reason.</summary>
    public static string? Recycle(string path, IntPtr hwnd = default)
    {
        // FOF_ALLOWUNDO: recycle bin. FOF_WANTNUKEWARNING: if the shell cannot recycle (too big, network share) it asks
        // before deleting for good instead of doing it silently, although the other confirmations are off.
        var op = new SHFILEOPSTRUCT
        {
            hwnd = hwnd,
            wFunc = FO_DELETE,
            pFrom = path + "\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING | FOF_NOERRORUI | FOF_SILENT,
        };
        int r;
        try { r = SHFileOperation(ref op); }
        catch (Exception ex) { return ex.Message; }
        if (r != 0) return Describe(r);
        if (op.fAnyOperationsAborted) return "Abgebrochen (nicht in den Papierkorb verschiebbar).";
        if (File.Exists(path) || Directory.Exists(path)) return "Konnte nicht in den Papierkorb verschoben werden.";
        return null;
    }

    public static OpResult RecycleAll(IReadOnlyList<OpItem> items, IntPtr hwnd = default)
    {
        var res = new OpResult();
        foreach (var item in items)
        {
            var err = Recycle(item.Path, hwnd);
            if (err == null) res.Done.Add(item);
            else res.Failed.Add((item.Path, err));
        }
        return res;
    }

    // ---------------------------------------------------------------- move

    public static OpResult Move(IReadOnlyList<OpItem> items, string destDir, ConflictPolicy policy,
        Func<OpItem, string, (ConflictPolicy Choice, bool ForAll)>? ask, IntPtr hwnd = default)
    {
        var res = new OpResult();
        try { Directory.CreateDirectory(destDir); }
        catch (Exception ex)
        {
            res.Failed.Add((destDir, "Zielordner nicht verfügbar: " + ex.Message));
            return res;
        }

        var dest = destDir.TrimEnd('\\') + "\\";
        foreach (var item in items)
        {
            try
            {
                if (string.Equals((item.Parent ?? "").TrimEnd('\\') + "\\", dest, StringComparison.OrdinalIgnoreCase))
                {
                    res.Skipped.Add((item, "liegt schon in diesem Ordner"));
                    continue;
                }
                if (item.IsDir && dest.StartsWith(item.Path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Ein Ordner kann nicht in sich selbst verschoben werden.");

                var target = dest + item.Name;
                if (File.Exists(target) || Directory.Exists(target))
                {
                    var choice = policy;
                    if (choice == ConflictPolicy.Ask)
                    {
                        if (ask == null) choice = ConflictPolicy.Skip;
                        else
                        {
                            var (c, forAll) = ask(item, target);
                            choice = c;
                            if (forAll) policy = c;
                        }
                    }
                    if (choice == ConflictPolicy.Skip) { res.Skipped.Add((item, "Name im Ziel schon vorhanden")); continue; }
                    if (choice == ConflictPolicy.Rename) target = dest + UniqueName(destDir, item.Name);
                    else
                    {
                        // Replace: the old target goes to the recycle bin, so it can be restored.
                        var err = Recycle(target, hwnd);
                        if (err != null) throw new IOException("Vorhandene Datei im Ziel konnte nicht ersetzt werden: " + err);
                        res.Replaced = true;
                    }
                }

                MoveOne(item, target);
                res.Done.Add(item);
                res.NewPaths[item.Path] = target;
            }
            catch (Exception ex)
            {
                res.Failed.Add((item.Path, ex.Message));
            }
        }
        return res;
    }

    private static void MoveOne(OpItem item, string target)
    {
        if (!item.IsDir) { File.Move(item.Path, target); return; }

        bool sameVolume = string.Equals(System.IO.Path.GetPathRoot(item.Path), System.IO.Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase);
        if (sameVolume) { Directory.Move(item.Path, target); return; }

        // Directory.Move cannot cross volumes; the shell copies and then removes the source.
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_MOVE,
            pFrom = item.Path.TrimEnd('\\') + "\0",
            pTo = target + "\0",
            fFlags = FOF_NOCONFIRMATION | FOF_NOCONFIRMMKDIR | FOF_NOERRORUI | FOF_SILENT,
        };
        int r = SHFileOperation(ref op);
        if (r != 0) throw new IOException(Describe(r));
        if (op.fAnyOperationsAborted || Directory.Exists(item.Path)) throw new IOException("Ordner konnte nicht vollständig verschoben werden.");
    }

    // ---------------------------------------------------------------- zip

    /// <summary>
    /// Writes the items into one ZIP file. Files that cannot be read are listed in <see cref="OpResult.Failed"/> and left out;
    /// an item only counts as <see cref="OpResult.Done"/> when everything in it was archived.
    /// </summary>
    public static OpResult Zip(IReadOnlyList<OpItem> items, string zipPath)
    {
        var res = new OpResult();
        var tmp = zipPath + ".dt_tmp";
        var self = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { System.IO.Path.GetFullPath(zipPath), System.IO.Path.GetFullPath(tmp) };
        var rootNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int entries = 0;

        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                foreach (var item in items)
                {
                    int failedBefore = res.Failed.Count;
                    var name = item.Name;
                    if (!rootNames.Add(name))
                    {
                        string stem = System.IO.Path.GetFileNameWithoutExtension(item.Name), ext = System.IO.Path.GetExtension(item.Name);
                        int k = 2;
                        while (!rootNames.Add(name = $"{stem} ({k}){ext}")) k++;
                    }

                    try
                    {
                        if (item.IsDir) AddDir(zip, item.Path, name, self, res, ref entries);
                        else AddFile(zip, item.Path, name, self, res, ref entries);
                    }
                    catch (Exception ex)
                    {
                        res.Failed.Add((item.Path, ex.Message));
                    }
                    if (res.Failed.Count == failedBefore) res.Done.Add(item);
                }
            }

            // Verify the archive before anything may be deleted on the strength of it.
            using (var check = ZipFile.OpenRead(tmp))
                if (check.Entries.Count != entries) throw new IOException("Das Archiv ist unvollständig.");

            if (File.Exists(zipPath))
            {
                // An existing archive of that name is replaced, but not destroyed: it goes to the recycle bin.
                var old = Recycle(zipPath);
                if (old != null) throw new IOException("Vorhandene ZIP-Datei konnte nicht ersetzt werden: " + old);
            }
            File.Move(tmp, zipPath);
        }
        catch (Exception ex)
        {
            try { File.Delete(tmp); } catch { }
            res.Done.Clear(); // nothing was archived, so nothing may be removed afterwards
            res.Failed.Add((zipPath, "ZIP-Datei konnte nicht erstellt werden: " + ex.Message));
        }
        return res;
    }

    private static void AddFile(ZipArchive zip, string path, string entryName, HashSet<string> self, OpResult res, ref int entries)
    {
        if (self.Contains(System.IO.Path.GetFullPath(path))) return; // the archive itself
        try
        {
            // Opened first: a file another program holds exclusively fails here, before an empty entry exists.
            using var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var entry = zip.CreateEntry(entryName.Replace('\\', '/'), CompressionLevel.Optimal);
            var time = File.GetLastWriteTime(path);
            entry.LastWriteTime = time.Year < 1980 ? new DateTime(1980, 1, 1) : time;
            using var dst = entry.Open();
            src.CopyTo(dst);
            entries++;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            res.Failed.Add((path, ex.Message));
        }
    }

    private static void AddDir(ZipArchive zip, string dir, string entryName, HashSet<string> self, OpResult res, ref int entries)
    {
        zip.CreateEntry(entryName.Replace('\\', '/') + "/");
        entries++;

        string[] subDirs, files;
        try
        {
            subDirs = Directory.GetDirectories(dir);
            files = Directory.GetFiles(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            res.Failed.Add((dir, ex.Message));
            return;
        }

        foreach (var f in files) AddFile(zip, f, entryName + "/" + System.IO.Path.GetFileName(f), self, res, ref entries);
        foreach (var d in subDirs)
        {
            try
            {
                // Junctions / symbolic links are not followed (loops, other drives).
                if (new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            }
            catch { continue; }
            AddDir(zip, d, entryName + "/" + System.IO.Path.GetFileName(d), self, res, ref entries);
        }
    }

    // ---------------------------------------------------------------- rename

    private static readonly Regex NumberToken = new(@"\{n(?::(\d+))?\}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Pattern with {name} (name without extension), {n} / {n:3} (counter, optionally zero-padded) and {date} (change date).
    /// A file keeps its extension.
    /// </summary>
    public static List<RenamePlan> PlanPattern(IReadOnlyList<OpItem> items, string pattern, int start)
    {
        var plans = new List<RenamePlan>();
        int counter = start;
        foreach (var item in items)
        {
            var (stem, ext) = Split(item);
            var date = SafeDate(item);
            var name = pattern
                .Replace("{name}", stem, StringComparison.OrdinalIgnoreCase)
                .Replace("{date}", date, StringComparison.OrdinalIgnoreCase);
            int n = counter++;
            name = NumberToken.Replace(name, m => m.Groups[1].Success ? n.ToString().PadLeft(int.Parse(m.Groups[1].Value), '0') : n.ToString());
            plans.Add(new RenamePlan(item, name + ext, null));
        }
        return Validate(plans);
    }

    public static List<RenamePlan> PlanReplace(IReadOnlyList<OpItem> items, string find, string replace, bool ignoreCase)
    {
        var plans = new List<RenamePlan>();
        foreach (var item in items)
        {
            var (stem, ext) = Split(item);
            var name = find.Length == 0 ? stem : stem.Replace(find, replace, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            plans.Add(new RenamePlan(item, name + ext, null));
        }
        return Validate(plans);
    }

    /// <summary>One item with a name typed by the user (extension included, as in Explorer).</summary>
    public static List<RenamePlan> PlanSingle(OpItem item, string newName) =>
        Validate(new List<RenamePlan> { new(item, newName.Trim(), null) });

    private static (string Stem, string Ext) Split(OpItem i) =>
        i.IsDir ? (i.Name, "") : (System.IO.Path.GetFileNameWithoutExtension(i.Name), System.IO.Path.GetExtension(i.Name));

    private static string SafeDate(OpItem i)
    {
        try { return (i.IsDir ? Directory.GetLastWriteTime(i.Path) : File.GetLastWriteTime(i.Path)).ToString("yyyy-MM-dd"); }
        catch { return ""; }
    }

    /// <summary>Marks plans that cannot be carried out: invalid name, name taken on disk, same new name twice in one folder.</summary>
    public static List<RenamePlan> Validate(List<RenamePlan> plans)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in plans)
            if (!p.Unchanged) seen[p.NewPath] = seen.GetValueOrDefault(p.NewPath) + 1;

        for (int i = 0; i < plans.Count; i++)
        {
            var p = plans[i];
            if (p.Unchanged) continue;

            string? problem = null;
            if (p.NewName.Length == 0 || p.NewName.Trim().Length == 0) problem = "Name ist leer";
            else if (p.NewName.IndexOfAny(invalid) >= 0) problem = "Ungültige Zeichen";
            else if (p.NewName.EndsWith('.') || p.NewName.EndsWith(' ')) problem = "Endet mit Punkt oder Leerzeichen";
            else if (seen[p.NewPath] > 1) problem = "Name kommt mehrfach vor";
            else if (!string.Equals(p.NewPath, p.Item.Path, StringComparison.OrdinalIgnoreCase) && (File.Exists(p.NewPath) || Directory.Exists(p.NewPath)))
                problem = "Name existiert schon";

            if (problem != null) plans[i] = p with { Problem = problem };
        }
        return plans;
    }

    public static OpResult Rename(IEnumerable<RenamePlan> plans)
    {
        var res = new OpResult();
        foreach (var p in plans)
        {
            if (p.Unchanged) { res.Skipped.Add((p.Item, "Name unverändert")); continue; }
            if (p.Problem != null) { res.Failed.Add((p.Item.Path, p.Problem)); continue; }
            try
            {
                if (p.Item.IsDir) Directory.Move(p.Item.Path, p.NewPath);
                else File.Move(p.Item.Path, p.NewPath);
                res.Done.Add(p.Item);
                res.NewPaths[p.Item.Path] = p.NewPath;
            }
            catch (Exception ex)
            {
                res.Failed.Add((p.Item.Path, ex.Message));
            }
        }
        return res;
    }

    // ---------------------------------------------------------------- interop

    private static string Describe(int code) => code switch
    {
        0x71 => "Quelle und Ziel sind identisch.",
        0x74 => "Ein Laufwerksstamm kann nicht bearbeitet werden.",
        0x75 => "Vom Benutzer abgebrochen.",
        0x76 => "Der Pfad ist zu lang.",
        0x78 => "Zugriff verweigert.",
        0x79 => "Der Pfad ist zu lang.",
        0x7C => "Ungültiger Pfad.",
        0x7E or 0x7F or 0x80 => "Zielordner nicht verfügbar.",
        0x10000 or 0x402 => "Unbekannter Fehler.",
        _ => new Win32Exception(code).Message,
    };

    private const uint FO_MOVE = 0x1, FO_DELETE = 0x3;
    private const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40,
        FOF_NOCONFIRMMKDIR = 0x200, FOF_NOERRORUI = 0x400, FOF_WANTNUKEWARNING = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);
}
