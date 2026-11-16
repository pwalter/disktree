using System.IO;
using System.Text.RegularExpressions;
using DiskTree.Core;
using DiskTree.Core.Storage;

namespace DiskTree.Features.FileOperations;

/// <summary>
/// Rules for checking new names of a source other than the disk: case-sensitive names, '/' as the only forbidden
/// character, and the names that already exist in each folder (listed beforehand, so typing in the dialog asks the
/// server nothing).
/// </summary>
public sealed class RenameRules
{
    private readonly Dictionary<string, HashSet<string>> _names;

    /// <param name="paths">Path rules of the source.</param>
    /// <param name="namesIn">The names in the folders the items lie in (folder path → names).</param>
    /// <param name="modifiedTicks">Change time (UTC ticks) per item path, for {date}; missing = no date.</param>
    public RenameRules(PathOps paths, IReadOnlyDictionary<string, IReadOnlyCollection<string>> namesIn,
        IReadOnlyDictionary<string, long>? modifiedTicks = null)
    {
        Paths = paths;
        _names = new Dictionary<string, HashSet<string>>(paths.Comparer);
        foreach (var (folder, names) in namesIn) _names[folder] = new HashSet<string>(names, paths.Comparer);
        ModifiedTicks = modifiedTicks ?? new Dictionary<string, long>();
    }

    public PathOps Paths { get; }

    /// <summary>Names differ by case ("a" and "A" are two entries).</summary>
    public bool CaseSensitive => Paths.Comparison == StringComparison.Ordinal;

    public IReadOnlyDictionary<string, long> ModifiedTicks { get; }

    /// <summary>Is there an entry of that name in the folder (as listed)?</summary>
    public bool Exists(string folder, string name) => _names.TryGetValue(folder, out var set) && set.Contains(name);

    /// <summary>Why a name cannot be used on this source, or null.</summary>
    public static string? Invalid(string name)
    {
        if (name.Length == 0 || name.Trim().Length == 0) return "Name ist leer";
        if (name is "." or "..") return "Ungültiger Name";
        if (name.IndexOf('/') >= 0 || name.IndexOf('\0') >= 0) return "Ungültige Zeichen (/)";
        return null;
    }
}

/// <summary>
/// Deleting on a source without recycle bin: the plan of what goes, which the user has to confirm explicitly
/// (<see cref="Confirm"/>, done only by the "Endgültig löschen" button of the dialog). Without that,
/// <see cref="RemoteFileOperations.Delete"/> refuses to start.
/// </summary>
public sealed class PermanentDeletePlan
{
    public const string Title = "Endgültig löschen – kann nicht rückgängig gemacht werden";

    /// <param name="items">What is deleted (normalized: nothing inside a selected folder twice).</param>
    /// <param name="where">The source as the user knows it ("SFTP tester@nas „Backup“").</param>
    /// <param name="countOf">Files, folders and bytes inside a folder as the scan knows them; null if not in the tree.</param>
    public PermanentDeletePlan(IReadOnlyList<OpItem> items, string where, Func<OpItem, (long Files, long Folders, long Bytes)?>? countOf = null)
    {
        Items = items;
        Where = where;
        foreach (var i in items)
        {
            if (!i.IsDir)
            {
                Files++;
                Bytes += i.Size;
                continue;
            }
            Folders++;
            if (countOf?.Invoke(i) is { } c)
            {
                Files += c.Files;
                Folders += c.Folders;
                Bytes += c.Bytes;
            }
            else
            {
                Bytes += i.Size;
                CountsUnknown = true;
            }
        }
    }

    public IReadOnlyList<OpItem> Items { get; }
    public string Where { get; }

    /// <summary>All files that go (also those inside the selected folders, as far as the scan knows them).</summary>
    public long Files { get; }

    /// <summary>All folders that go (the selected ones and those inside them).</summary>
    public long Folders { get; }
    public long Bytes { get; }

    /// <summary>A selected folder is not in the tree: what is inside it is not counted.</summary>
    public bool CountsUnknown { get; }

    public bool IsConfirmed { get; private set; }

    /// <summary>The user pressed "Endgültig löschen" in the dialog that showed this plan.</summary>
    public void Confirm() => IsConfirmed = true;

    /// <summary>The text of the confirmation: what, how much, examples, where.</summary>
    public string Describe()
    {
        int dirs = Items.Count(i => i.IsDir), files = Items.Count - dirs;
        string what = dirs == 0 ? $"{files:N0} {(files == 1 ? "Datei" : "Dateien")}"
            : files == 0 ? $"{dirs:N0} Ordner"
            : $"{Items.Count:N0} Elemente ({files:N0} {(files == 1 ? "Datei" : "Dateien")}, {dirs:N0} Ordner)";
        string inside = dirs == 0 ? ""
            : $"\nMit allem Inhalt: zusammen {Files:N0} Dateien in {Folders:N0} Ordnern" + (CountsUnknown ? " (Inhalt teils unbekannt)" : " (laut Scan)") + ".";
        string names = string.Join("\n", Items.Take(6).Select(i => "  " + i.Name + (i.IsDir ? "/" : "")))
                       + (Items.Count > 6 ? $"\n  … und {Items.Count - 6:N0} weitere" : "");
        string folder = Items.Select(i => i.Parent).Distinct().Count() == 1 ? Items[0].Parent ?? "" : "verschiedene Ordner";
        return $"{what} mit zusammen {SizeFormat.Format(Bytes)} werden endgültig gelöscht.{inside}\n\n{names}\n\n" +
               $"Quelle: {Where}\nOrt: {folder}\n\n" +
               "Diese Quelle hat keinen Papierkorb. Gelöschtes lässt sich nicht wiederherstellen.";
    }
}

/// <summary>
/// Delete for good, rename and move on a source with <see cref="IStorageWriter"/> (SFTP): the same steps as
/// <see cref="FileOperations"/> for the disk, but with the source's path rules (case-sensitive), no recycle bin and the
/// server doing the work. Errors are collected per item; the action goes on after a failure. No UI here.
/// </summary>
public static class RemoteFileOperations
{
    /// <summary>Removes duplicates and items inside another selected folder (they go with it), with the source's rules.</summary>
    public static List<OpItem> Normalize(IEnumerable<OpItem> items, PathOps paths)
    {
        var list = items.GroupBy(i => paths.TrimEnd(i.Path), paths.Comparer).Select(g => g.First()).ToList();
        var dirs = list.Where(i => i.IsDir).Select(i => i.Path).ToList();
        return list.Where(i => !dirs.Any(d => paths.IsUnder(d, i.Path)))
            .OrderBy(i => i.Parent, paths.Comparer)
            .ThenBy(i => i.Name, paths.Comparer)
            .ToList();
    }

    /// <summary>The top of the source cannot be changed.</summary>
    public static bool IsRoot(OpItem i) => i.Parent == null;

    /// <summary>
    /// Looks every item up on the source right before an action: what is gone is skipped, the rest gets its real kind and,
    /// for files, its age bucket (<paramref name="ages"/>). Returns the items that exist.
    /// </summary>
    public static List<OpItem> Refresh(IStorageWriter writer, IReadOnlyList<OpItem> items, AgeClassifier? ages,
        OpResult res, IDictionary<string, long>? modifiedTicks, CancellationToken ct)
    {
        var list = new List<OpItem>();
        foreach (var i in items)
        {
            ct.ThrowIfCancellationRequested();
            StorageItemInfo? info;
            try { info = writer.Stat(i.Path, ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                res.Failed.Add((i.Path, ex.Message));
                continue;
            }
            if (info is not { } s) { res.Skipped.Add((i, "nicht mehr vorhanden")); continue; }
            if (modifiedTicks != null && s.ModifiedTicks > 0) modifiedTicks[i.Path] = s.ModifiedTicks;
            if (s.IsDirectory) { list.Add(i with { IsDir = true, AgeBucket = -1 }); continue; }
            int bucket = ages == null ? -1 : ages.Of(ages.Stamp switch
            {
                AgeStamp.Accessed => s.AccessedTicks,
                AgeStamp.Created => s.CreatedTicks,
                _ => s.ModifiedTicks,
            });
            // The size stays the one of the list (what the scan counted, like the disk actions); only the age bucket is read now.
            list.Add(i with { IsDir = false, AgeBucket = bucket });
        }
        return list;
    }

    // ---------------------------------------------------------------- delete for good

    /// <summary>
    /// Deletes everything of a confirmed plan, folders with all their content. Without <see cref="PermanentDeletePlan.Confirm"/>
    /// it throws and touches nothing. A folder whose content could not be removed completely is in
    /// <see cref="OpResult.Partial"/> (it needs a rescan); a cancel ends after the current entry
    /// (<see cref="OpResult.Cancelled"/>, the interrupted folder is partial).
    /// </summary>
    public static OpResult Delete(IStorageWriter writer, PermanentDeletePlan plan, Action<long, long>? progress, CancellationToken ct)
    {
        if (!plan.IsConfirmed)
            throw new InvalidOperationException("Endgültiges Löschen ohne Bestätigung ist nicht erlaubt.");

        var res = new OpResult(plan.Items.FirstOrDefault()?.Paths?.Comparer);
        long files = 0, bytes = 0;
        foreach (var item in plan.Items)
        {
            if (ct.IsCancellationRequested) { res.Cancelled = true; break; }
            long baseFiles = files, baseBytes = bytes;
            try
            {
                var outcome = writer.Delete(item.Path, item.IsDir, o => progress?.Invoke(baseFiles + o.Files, baseBytes + o.Bytes), ct);
                files += outcome.Files;
                bytes += outcome.Bytes;
                if (outcome.Complete) res.Done.Add(item);
                else
                {
                    res.Failed.AddRange(outcome.Failed);
                    if (item.IsDir && outcome.Files + outcome.Folders > 0) res.Partial.Add(item);
                }
            }
            catch (OperationCanceledException)
            {
                res.Cancelled = true;
                if (item.IsDir) res.Partial.Add(item); // some of it may be gone already
                break;
            }
            catch (FileNotFoundException)
            {
                res.Skipped.Add((item, "nicht mehr vorhanden"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                res.Failed.Add((item.Path, ex.Message));
                if (item.IsDir) res.Partial.Add(item);
            }
            progress?.Invoke(files, bytes);
        }
        return res;
    }

    // ---------------------------------------------------------------- rename

    private static readonly Regex NumberToken = new(@"\{n(?::(\d+))?\}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Like <see cref="FileOperations.PlanPattern"/> with the rules of the source.</summary>
    public static List<RenamePlan> PlanPattern(IReadOnlyList<OpItem> items, string pattern, int start, RenameRules rules)
    {
        var plans = new List<RenamePlan>();
        int counter = start;
        foreach (var item in items)
        {
            var (stem, ext) = Split(item);
            var date = rules.ModifiedTicks.TryGetValue(item.Path, out var t) && t > 0
                ? new DateTime(t, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd") : "";
            var name = pattern
                .Replace("{name}", stem, StringComparison.OrdinalIgnoreCase)
                .Replace("{date}", date, StringComparison.OrdinalIgnoreCase);
            int n = counter++;
            name = NumberToken.Replace(name, m => m.Groups[1].Success ? n.ToString().PadLeft(int.Parse(m.Groups[1].Value), '0') : n.ToString());
            plans.Add(new RenamePlan(item, name + ext, null));
        }
        return Validate(plans, rules);
    }

    /// <summary>Like <see cref="FileOperations.PlanReplace"/> with the rules of the source.</summary>
    public static List<RenamePlan> PlanReplace(IReadOnlyList<OpItem> items, string find, string replace, bool ignoreCase, RenameRules rules)
    {
        var plans = new List<RenamePlan>();
        foreach (var item in items)
        {
            var (stem, ext) = Split(item);
            var name = find.Length == 0 ? stem : stem.Replace(find, replace, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            plans.Add(new RenamePlan(item, name + ext, null));
        }
        return Validate(plans, rules);
    }

    /// <summary>One item with a typed name (extension included).</summary>
    public static List<RenamePlan> PlanSingle(OpItem item, string newName, RenameRules rules) =>
        Validate(new List<RenamePlan> { new(item, newName, null) }, rules);

    /// <summary>
    /// Name and extension for patterns: the extension starts at the last '.' that is not the first character (".bashrc"
    /// has none, "a.tar.gz" keeps ".gz"). Folders have none.
    /// </summary>
    public static (string Stem, string Ext) Split(OpItem i)
    {
        var name = i.Name;
        if (i.IsDir) return (name, "");
        int dot = name.LastIndexOf('.');
        return dot > 0 ? (name[..dot], name[dot..]) : (name, "");
    }

    /// <summary>
    /// Marks plans that cannot be carried out: invalid name, name taken in the folder (case-sensitive where the source is),
    /// the same new name twice in one folder. A name that differs only by case is a new name on a case-sensitive source.
    /// </summary>
    public static List<RenamePlan> Validate(List<RenamePlan> plans, RenameRules rules)
    {
        var paths = rules.Paths;
        var seen = new Dictionary<string, int>(paths.Comparer);
        foreach (var p in plans)
            if (!p.Unchanged) seen[p.NewPath] = seen.GetValueOrDefault(p.NewPath) + 1;

        for (int i = 0; i < plans.Count; i++)
        {
            var p = plans[i];
            if (p.Unchanged) continue;

            string? problem = RenameRules.Invalid(p.NewName);
            if (problem == null && seen[p.NewPath] > 1) problem = "Name kommt mehrfach vor";
            else if (problem == null && !paths.NameEquals(p.NewPath, p.Item.Path) && rules.Exists(p.Item.Parent ?? "", p.NewName))
                problem = "Name existiert schon";
            if (problem != null) plans[i] = p with { Problem = problem };
        }
        return plans;
    }

    /// <summary>Carries out checked plans; the source refuses a name that appeared meanwhile (nothing is replaced).</summary>
    public static OpResult Rename(IStorageWriter writer, IEnumerable<RenamePlan> plans, CancellationToken ct)
    {
        var list = plans.ToList();
        var res = new OpResult(list.FirstOrDefault()?.Item.Paths?.Comparer);
        foreach (var p in list)
        {
            if (ct.IsCancellationRequested) { res.Cancelled = true; break; }
            if (p.Unchanged) { res.Skipped.Add((p.Item, "Name unverändert")); continue; }
            if (p.Problem != null) { res.Failed.Add((p.Item.Path, p.Problem)); continue; }
            try
            {
                var newPath = writer.Rename(p.Item.Path, p.NewName, ct);
                res.Done.Add(p.Item);
                res.NewPaths[p.Item.Path] = newPath;
            }
            catch (OperationCanceledException) { res.Cancelled = true; break; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                res.Failed.Add((p.Item.Path, ex.Message));
            }
        }
        return res;
    }

    // ---------------------------------------------------------------- move

    /// <summary>"name (2).ext": the first free variant in <paramref name="taken"/> (compared with the source's rules).</summary>
    public static string UniqueName(ISet<string> taken, string name, bool isDir)
    {
        var (stem, ext) = Split(new OpItem("/" + name, isDir, 0, -1, new PathOps('/', StringComparison.Ordinal)));
        for (int n = 2; n < 10000; n++)
        {
            var candidate = $"{stem} ({n}){ext}";
            if (!taken.Contains(candidate)) return candidate;
        }
        return $"{stem} ({Guid.NewGuid():N}){ext}";
    }

    /// <summary>
    /// Moves the items into <paramref name="destDir"/> on the same source (which must exist). A name that exists there is
    /// handled by <paramref name="policy"/> (<see cref="ConflictPolicy.Ask"/>: <paramref name="ask"/> decides, without it
    /// skip): skip, keep both under a new name, or replace — the old entry is deleted for good (a file is replaced
    /// atomically where the source can).
    /// </summary>
    public static OpResult Move(IStorageWriter writer, PathOps paths, IReadOnlyList<OpItem> items, string destDir, ConflictPolicy policy,
        Func<OpItem, string, (ConflictPolicy Choice, bool ForAll)>? ask, CancellationToken ct)
    {
        var res = new OpResult(paths.Comparer);
        HashSet<string> taken;
        try
        {
            if (writer.Stat(destDir, ct) is not { IsDirectory: true })
            {
                res.Failed.Add((destDir, "Zielordner nicht vorhanden."));
                return res;
            }
            taken = new HashSet<string>(writer.ListNames(destDir, ct), paths.Comparer);
        }
        catch (OperationCanceledException) { res.Cancelled = true; return res; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            res.Failed.Add((destDir, "Zielordner nicht verfügbar: " + ex.Message));
            return res;
        }

        foreach (var item in items)
        {
            if (ct.IsCancellationRequested) { res.Cancelled = true; break; }
            try
            {
                if (item.Parent is { } parent && paths.NameEquals(paths.TrimEnd(parent), paths.TrimEnd(destDir)))
                {
                    res.Skipped.Add((item, "liegt schon in diesem Ordner"));
                    continue;
                }
                if (item.IsDir && paths.IsSameOrUnder(item.Path, destDir))
                    throw new IOException("Ein Ordner kann nicht in sich selbst verschoben werden.");

                string name = item.Name;
                bool replace = false;
                if (taken.Contains(name))
                {
                    var target = paths.Join(destDir, name);
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
                    if (choice == ConflictPolicy.Rename) name = UniqueName(taken, name, item.IsDir);
                    else
                    {
                        // Replace: a file replaces a file in one step; anything with a folder involved deletes the old entry first.
                        var existing = writer.Stat(target, ct);
                        if (existing is { IsDirectory: false } && !item.IsDir) replace = true;
                        else if (existing != null)
                        {
                            var gone = writer.Delete(target, recursive: true, null, ct);
                            if (!gone.Complete)
                                throw new IOException("Vorhandenes Ziel konnte nicht vollständig gelöscht werden: " + gone.Failed[0].Error);
                        }
                        res.Replaced = true;
                    }
                }

                var newPath = writer.Move(item.Path, destDir, name, replace, ct);
                taken.Add(name);
                res.Done.Add(item);
                res.NewPaths[item.Path] = newPath;
            }
            catch (OperationCanceledException) { res.Cancelled = true; break; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                res.Failed.Add((item.Path, ex.Message));
            }
        }
        return res;
    }

    /// <summary>Creates a folder and the missing folders above it (like mkdir -p). Throws on errors.</summary>
    public static void CreateFolders(IStorageWriter writer, PathOps paths, string folder, CancellationToken ct)
    {
        var missing = new Stack<string>();
        for (var f = folder; f != null; f = paths.ParentOf(f))
        {
            var info = writer.Stat(f, ct);
            if (info is { IsDirectory: true }) break;
            if (info != null) throw new IOException($"Es gibt schon eine Datei dieses Namens: {f}");
            missing.Push(f);
        }
        while (missing.Count > 0) writer.CreateDirectory(missing.Pop(), ct);
    }
}
