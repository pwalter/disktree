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

    /// <summary>
    /// What the source has to add (S3: versioning of the bucket, requests): its text replaces the closing sentence about
    /// the missing recycle bin, its highlight is shown in a box of its own. Null: the default text.
    /// </summary>
    public DeleteNote? SourceNote { get; set; }

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
        string closing = SourceNote?.Text ?? "Diese Quelle hat keinen Papierkorb. Gelöschtes lässt sich nicht wiederherstellen.";
        return $"{what} mit zusammen {SizeFormat.Format(Bytes)} werden endgültig gelöscht.{inside}\n\n{names}\n\n" +
               $"Quelle: {Where}\nOrt: {folder}\n\n" + closing;
    }
}

/// <summary>
/// Renaming or moving folders on a source that copies every object and deletes the original afterwards (S3,
/// <see cref="ICopyingWriter"/>): how many objects, how much data, how many requests, and that it is not atomic. The user
/// has to confirm it (<see cref="Confirm"/>); without that, <see cref="RemoteFileOperations.Rename"/> and
/// <see cref="RemoteFileOperations.Move"/> refuse to touch a folder of such a source.
/// </summary>
public sealed class CopyPlan
{
    /// <param name="items">What is renamed / moved (normalized).</param>
    /// <param name="moving">Moving (true) or renaming (false), for the text.</param>
    /// <param name="where">The source as the user knows it.</param>
    /// <param name="writer">The writer that will do it (estimates and notes).</param>
    /// <param name="countOf">Files, folders and bytes inside a folder as the scan knows them; null if not in the tree.</param>
    public CopyPlan(IReadOnlyList<OpItem> items, bool moving, string where, ICopyingWriter writer,
        Func<OpItem, (long Files, long Folders, long Bytes)?>? countOf = null)
    {
        Items = items;
        Moving = moving;
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
                FolderObjects += c.Files;
                Bytes += c.Bytes;
            }
            else
            {
                Bytes += i.Size;
                CountsUnknown = true;
            }
        }
        Cost = writer.EstimateCopy(Files, Folders, FolderObjects);
        Notes = writer.CopyNotes;
    }

    /// <summary>Does this action need the confirmation? Folders on a source that copies instead of renaming.</summary>
    public static bool Needed(IStorageWriter writer, IEnumerable<OpItem> items) => writer is ICopyingWriter && items.Any(i => i.IsDir);

    public IReadOnlyList<OpItem> Items { get; }
    public bool Moving { get; }
    public string Where { get; }
    public long Files { get; }
    public long Folders { get; }

    /// <summary>Objects inside the folders as far as the scan knows them (without empty folder markers).</summary>
    public long FolderObjects { get; }
    public long Bytes { get; }
    public bool CountsUnknown { get; }
    public CopyCost Cost { get; }
    public string Notes { get; }

    public bool IsConfirmed { get; private set; }

    /// <summary>The user agreed in the dialog that showed <see cref="Describe"/>.</summary>
    public void Confirm() => IsConfirmed = true;

    public string Title => Moving ? "Ordner verschieben – Objekte werden kopiert und gelöscht" : "Ordner umbenennen – Objekte werden kopiert und gelöscht";

    public string Describe()
    {
        string verb = Moving ? "verschoben" : "umbenannt";
        string what = Folders == 1 && Files == 0 ? "1 Ordner"
            : Files == 0 ? $"{Folders:N0} Ordner"
            : $"{Items.Count:N0} Elemente ({Folders:N0} Ordner, {Files:N0} {(Files == 1 ? "Datei" : "Dateien")})";
        long objects = Files + FolderObjects;
        string names = string.Join("\n", Items.Take(6).Select(i => "  " + i.Name + (i.IsDir ? "/" : "")))
                       + (Items.Count > 6 ? $"\n  … und {Items.Count - 6:N0} weitere" : "");
        return $"{what} sollen {verb} werden. Diese Quelle kann nicht umbenennen oder verschieben: Jedes Objekt wird auf dem " +
               "Server kopiert, die Kopie geprüft und erst danach das Original gelöscht.\n\n" +
               $"{names}\n\n" +
               $"Objekte: ca. {objects:N0}" + (CountsUnknown ? " (Inhalt teils unbekannt)" : " (laut Scan, dazu ggf. leere Ordner-Marker)") + "\n" +
               $"Datenmenge: {SizeFormat.Format(Bytes)} (wird auf dem Server kopiert, nicht heruntergeladen)\n" +
               $"Geschätzte Anfragen: ca. {Cost.Total:N0} – {Cost.List:N0} LIST, {Cost.Head:N0} HEAD, {Cost.Copy:N0} COPY (zählt wie PUT), " +
               $"{Cost.Delete:N0} DELETE\n" +
               $"Quelle: {Where}\n\n" +
               "Nicht atomar: Bei Abbruch (Esc), Fehlern oder Verbindungsabbruch liegen Objekte teils am alten, teils am neuen Ort. " +
               "Jedes Objekt liegt dabei vollständig an einem der beiden Orte; der Bericht danach nennt, was wo liegt.\n\n" + Notes;
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
        IEnumerable<OpItem> each = plan.Items;
        // A source that deletes many files in one request (S3, up to 1000 keys): the single files first, in batches.
        if (writer is IBatchDeleter batch && plan.Items.Count(i => !i.IsDir) > 1)
        {
            var single = plan.Items.Where(i => !i.IsDir).ToList();
            each = plan.Items.Where(i => i.IsDir).ToList();
            var byPath = new Dictionary<string, OpItem>(plan.Items[0].Paths?.Comparer ?? StringComparer.Ordinal);
            foreach (var i in single) byPath.TryAdd(i.Path, i);
            try
            {
                var tried = batch.DeleteFiles(single.Select(i => i.Path).ToList(), ct);
                foreach (var (path, error) in tried)
                {
                    if (!byPath.TryGetValue(path, out var item)) continue;
                    if (error != null) { res.Failed.Add((path, error)); continue; }
                    res.Done.Add(item);
                    files++;
                    bytes += item.Size;
                }
                if (tried.Count < single.Count) res.Cancelled = true;
            }
            catch (OperationCanceledException) { res.Cancelled = true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                foreach (var i in single) res.Failed.Add((i.Path, ex.Message));
            }
            progress?.Invoke(files, bytes);
            if (res.Cancelled) each = [];
        }

        foreach (var item in each)
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
        FindVanished(writer, res, res.Done);
        return res;
    }

    /// <summary>
    /// A source whose folders are only key prefixes (S3): the folders the removed items lay in may be gone with them;
    /// they go to <see cref="OpResult.Vanished"/>. Errors leave it as it is (a rescan shows it).
    /// </summary>
    private static void FindVanished(IStorageWriter writer, OpResult res, IEnumerable<OpItem> left)
    {
        if (writer is not IPrefixFolders prefixes) return;
        var parents = left.Select(i => i.Parent).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        if (parents.Count == 0) return;
        try
        {
            res.Vanished.AddRange(prefixes.GoneFolders(parents));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            // The tree may show an empty folder that is gone; the next scan drops it.
        }
    }

    /// <summary>A folder of a source that copies instead of renaming (S3) is touched only with a confirmed <see cref="CopyPlan"/>.</summary>
    private static void RequireCopyConfirmation(IStorageWriter writer, IEnumerable<OpItem> items, CopyPlan? confirmed)
    {
        if (CopyPlan.Needed(writer, items) && confirmed?.IsConfirmed != true)
            throw new InvalidOperationException("Ordner dieser Quelle werden kopiert und danach gelöscht; das braucht eine Bestätigung.");
    }

    /// <summary>After a rename / move on a source that copies: the change time the new entry got (age buckets).</summary>
    private static void TakeStamp(IStorageWriter writer, OpResult res, string newPath)
    {
        if (writer is ICopyingWriter copying && copying.TryGetNewStamp(newPath, out var ticks)) res.NewTicks[newPath] = ticks;
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

    /// <summary>
    /// Carries out checked plans; the source refuses a name that appeared meanwhile (nothing is replaced). Folders of a
    /// source that copies instead of renaming (S3) need a confirmed <paramref name="confirmed"/>; a folder that was renamed
    /// only partly is in <see cref="OpResult.Partial"/>.
    /// </summary>
    public static OpResult Rename(IStorageWriter writer, IEnumerable<RenamePlan> plans, CancellationToken ct, CopyPlan? confirmed = null)
    {
        var list = plans.ToList();
        RequireCopyConfirmation(writer, list.Where(p => p.Ok).Select(p => p.Item), confirmed);
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
                TakeStamp(writer, res, newPath);
            }
            catch (OperationCanceledException) { res.Cancelled = true; break; }
            catch (PartialChangeException ex)
            {
                res.Failed.AddRange(ex.Failed.Count > 0 ? ex.Failed : [(p.Item.Path, ex.Message)]);
                res.Partial.Add(p.Item);
                if (ex.Cancelled) { res.Cancelled = true; break; }
            }
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
        Func<OpItem, string, (ConflictPolicy Choice, bool ForAll)>? ask, CancellationToken ct, CopyPlan? confirmed = null)
    {
        RequireCopyConfirmation(writer, items, confirmed);
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
                TakeStamp(writer, res, newPath);
            }
            catch (OperationCanceledException) { res.Cancelled = true; break; }
            catch (PartialChangeException ex)
            {
                // Part of it is at the new place: both folders need a rescan.
                res.Failed.AddRange(ex.Failed.Count > 0 ? ex.Failed : [(item.Path, ex.Message)]);
                res.Partial.Add(item);
                if (ex.Cancelled) { res.Cancelled = true; break; }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                res.Failed.Add((item.Path, ex.Message));
            }
        }
        FindVanished(writer, res, res.Done);
        return res;
    }

    /// <summary>The top of the source a path belongs to ("sftp://user@host/" for "sftp://user@host/a/b").</summary>
    public static string TopOf(PathOps paths, string path)
    {
        var top = path;
        for (var p = paths.ParentOf(path); p != null; p = paths.ParentOf(p)) top = p;
        return top;
    }

    /// <summary>
    /// A typed destination as a full path of the same source: a full path ("sftp://user@host/x", must be on the same
    /// server as <paramref name="top"/>) or an absolute one below the top ("/x/y"). Null with a reason otherwise.
    /// </summary>
    public static string? ResolveTarget(string text, IStorageProvider provider, string top, out string? error)
    {
        error = null;
        var t = text.Trim().Trim('"').Trim();
        var paths = provider.Paths;
        bool bucketScoped = provider is IPrefixFolders; // S3: the top is the bucket
        if (t.Length == 0) { error = "Bitte einen Zielordner wählen oder eingeben."; return null; }
        string full;
        try
        {
            if (t.StartsWith('/') && !t.StartsWith("//", StringComparison.Ordinal)) full = provider.Normalize(paths.Join(top, t.TrimStart('/')));
            else if (provider.TryParse(t, out var loc)) full = loc.Path;
            else
            {
                error = bucketScoped
                    ? $"Bitte einen vollständigen Pfad in diesem Bucket angeben ({top}/… oder /ordner)."
                    : "Bitte einen vollständigen Pfad dieses Servers angeben (sftp://… oder /ordner).";
                return null;
            }
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return null;
        }
        if (!paths.IsSameOrUnder(top, full))
        {
            // S3: only inside the same bucket – another bucket may lie in another region or account, with other
            // credentials, versioning and encryption defaults, and it is not part of the scanned tree.
            error = bucketScoped ? $"Verschieben geht nur innerhalb desselben Buckets: {top}" : $"Verschieben geht nur auf demselben Server: {top}";
            return null;
        }
        return full;
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
