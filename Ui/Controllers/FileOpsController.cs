using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using DiskTree.Core;
using DiskTree.Features.FileOperations;

namespace DiskTree.Ui.Controllers;

/// <summary>One selected item of a list: a file or a folder.</summary>
public readonly record struct SelectedEntry(string Path, bool IsDir, long Size);

/// <summary>
/// A list the clean-up actions can work on (largest files, search hits, duplicates): its multi-selection, and what it
/// has to do after items were removed behind its back.
/// </summary>
public interface ISelectionSource
{
    /// <summary>The control of the list: whether the mouse is over it or it has the keyboard focus tells which list the user works in.</summary>
    FrameworkElement Element { get; }
    /// <summary>The selected items.</summary>
    IReadOnlyList<SelectedEntry> Selected();
    /// <summary>Items that no longer exist leave the list.</summary>
    void PruneMissing() { }
    /// <summary>Whether, for at least one group of identical files the list knows, all copies are among <paramref name="paths"/>.</summary>
    bool CoversAllCopies(IEnumerable<string> paths) => false;
}

/// <summary>An <see cref="ISelectionSource"/> built from a few functions of the list it stands for.</summary>
public sealed class ListSelection : ISelectionSource
{
    private readonly Func<IEnumerable<SelectedEntry>> _selected;
    private readonly Action? _prune;
    private readonly Func<IEnumerable<string>, bool>? _coversAllCopies;

    public ListSelection(FrameworkElement element, Func<IEnumerable<SelectedEntry>> selected,
        Action? prune = null, Func<IEnumerable<string>, bool>? coversAllCopies = null)
    {
        Element = element;
        _selected = selected;
        _prune = prune;
        _coversAllCopies = coversAllCopies;
    }

    public FrameworkElement Element { get; }
    public IReadOnlyList<SelectedEntry> Selected() => _selected().ToList();
    public void PruneMissing() => _prune?.Invoke();
    public bool CoversAllCopies(IEnumerable<string> paths) => _coversAllCopies?.Invoke(paths) ?? false;
}

/// <summary>
/// Clean-up actions on the selection (move, ZIP, rename, delete to the recycle bin): menu entries, keys, confirmation,
/// and keeping tree / file types / age data / largest files in step afterwards (the tree numbers through
/// <see cref="TreeEditor"/>). The file work itself is in FileOperations.
/// </summary>
public sealed class FileOpsController
{
    private readonly Window _window;
    private readonly ScanTabsController _tabs;
    private readonly TreeController _tree;
    private readonly ScanStatusView _status;
    private readonly SnapshotController _snapshots;
    private readonly ScanController _scan;
    private readonly IScanViews _views;
    private readonly IReadOnlyList<ISelectionSource> _sources;

    private bool _opBusy;
    private string _lastMoveTarget = "";

    public FileOpsController(Window window, ScanTabsController tabs, TreeController tree, ScanStatusView status,
        SnapshotController snapshots, ScanController scan, IScanViews views, IReadOnlyList<ISelectionSource> sources)
    {
        _window = window;
        _tabs = tabs;
        _tree = tree;
        _status = status;
        _snapshots = snapshots;
        _scan = scan;
        _views = views;
        _sources = sources;
    }

    /// <summary>An action runs: the active tab must not change meanwhile.</summary>
    public bool IsBusy => _opBusy;

    private Scanner? Scanner => _tabs.Current.Scanner;
    private NodeVM? Root => _tabs.Current.Root;

    private void Notice(string text) => Dialogs.Info(_window, text);

    // ---------------------------------------------------------------- entry points

    /// <summary>Our entries for the context menu of <paramref name="path"/>; empty in a stored scan.</summary>
    public IReadOnlyList<(string Text, Action Run)> OperationExtras(string path, bool isDir)
    {
        if (_snapshots.IsSnapshotMode) return [];

        var sel = SelectionFor(path, isDir);
        string suffix = sel.Count > 1 ? $" ({sel.Count:N0} Elemente)" : "";
        var list = new List<(string, Action)>
        {
            ("Verschieben nach …" + suffix, () => RunOp(() => MoveAsync(sel))),
            ("In ZIP archivieren …" + suffix, () => RunOp(() => ZipAsync(sel))),
            ("Umbenennen (F2)" + suffix, () => RunOp(() => RenameAsync(sel))),
        };
        if (sel.Count > 1)
            list.Add(($"Auswahl löschen (Papierkorb) … ({sel.Count:N0} Elemente)", () => RunOp(() => DeleteAsync(sel))));
        return list;
    }

    /// <summary>F2 and Delete (on several items) in the lists. Returns true if the key was used.</summary>
    public bool HandleKey(Key key)
    {
        if (key != Key.F2 && key != Key.Delete) return false;

        var sel = ActiveSelection(null, preferFocus: true);
        if (sel.Count == 0 || (key == Key.Delete && sel.Count < 2)) return false; // one item: the shell's own Delete

        if (_snapshots.IsSnapshotMode) { _snapshots.ShowHint(); return true; }
        RunOp(() => key == Key.F2 ? RenameAsync(sel) : DeleteAsync(sel));
        return true;
    }

    /// <summary>F2 in the tree (single selection).</summary>
    public bool HandleTreeKey(Key key, NodeVM vm)
    {
        if (key != Key.F2 || !ShellMenuCoordinator.CanUseShell(vm)) return false;
        if (_snapshots.IsSnapshotMode) { _snapshots.ShowHint(); return true; }
        var sel = new List<OpItem> { ItemOf(vm.Path, vm.Kind == Kind.Dir, vm.Size) };
        RunOp(() => RenameAsync(sel));
        return true;
    }

    // Runs after the menu is closed, so the menu code never sees the files change underneath it.
    private void RunOp(Func<Task> op) =>
        _window.Dispatcher.BeginInvoke(async () =>
        {
            try { await op(); }
            catch (Exception ex)
            {
                _opBusy = false;
                Dialogs.Warn(_window, ex.Message);
            }
        });

    // ---------------------------------------------------------------- selection

    private OpItem ItemOf(string path, bool isDir, long size) =>
        new(path, isDir, size, isDir ? -1 : Scanner?.Ages.OfFile(path) ?? -1);

    /// <summary>Selection of the list the user is working in (mouse over it, else keyboard focus).</summary>
    private List<OpItem> ActiveSelection(string? mustContain, bool preferFocus = false)
    {
        var order = preferFocus
            ? _sources.OrderBy(c => c.Element.IsKeyboardFocusWithin ? 0 : 1)
            : _sources.OrderBy(c => c.Element.IsMouseOver ? 0 : c.Element.IsKeyboardFocusWithin ? 1 : 2);

        foreach (var c in order)
        {
            var e = c.Element;
            if (!(preferFocus ? e.IsKeyboardFocusWithin : e.IsMouseOver || e.IsKeyboardFocusWithin)) continue;
            var items = c.Selected().Select(i => ItemOf(i.Path, i.IsDir, i.Size)).ToList();
            if (items.Count == 0) continue;
            if (mustContain != null && !items.Any(i => string.Equals(i.Path, mustContain, StringComparison.OrdinalIgnoreCase))) continue;
            return items;
        }
        return new List<OpItem>();
    }

    /// <summary>The items an action from the context menu of <paramref name="path"/> applies to: the multi-selection it is part of, else just itself.</summary>
    private List<OpItem> SelectionFor(string path, bool isDir)
    {
        var sel = ActiveSelection(path);
        if (sel.Count > 0) return sel;

        if (_tree.SelectedRow is { } vm && string.Equals(vm.Path, path, StringComparison.OrdinalIgnoreCase))
            return new List<OpItem> { ItemOf(path, isDir, vm.Size) };

        long size = 0;
        try { size = isDir ? Scanner?.FindNode(path)?.Size ?? 0 : new FileInfo(path).Length; } catch { }
        return new List<OpItem> { ItemOf(path, isDir, size) };
    }

    /// <summary>Checks the preconditions and cleans the selection up. Null (after telling the user) if nothing can be done.</summary>
    private List<OpItem>? Prepare(List<OpItem> sel, bool allowRoot = false)
    {
        if (_snapshots.IsSnapshotMode) { _snapshots.ShowHint(); return null; }
        if (_opBusy) return null;
        if (_tabs.Busy != null)
        {
            Notice("Ein Scan läuft noch. Die Aktion ist erst möglich, wenn er fertig oder abgebrochen ist.");
            return null;
        }

        var items = FileOperations.Normalize(sel.Where(i => i.IsDir ? Directory.Exists(i.Path) : File.Exists(i.Path)));
        if (!allowRoot) items.RemoveAll(FileOperations.IsRoot);
        if (items.Count == 0)
        {
            Notice("Es gibt nichts zu tun: die Auswahl existiert nicht mehr oder ist ein Laufwerk.");
            return null;
        }
        return items;
    }

    // ---------------------------------------------------------------- actions

    private async Task MoveAsync(List<OpItem> sel)
    {
        var items = Prepare(sel);
        if (items == null) return;

        var dlg = new TargetDialog(false, items, _lastMoveTarget) { Owner = _window };
        if (dlg.ShowDialog() != true || dlg.Result is not { } choice) return;
        _lastMoveTarget = choice.Target;

        var hwnd = new WindowInteropHelper(_window).Handle;
        var res = await RunBusy("Verschiebe …", () => FileOperations.Move(items, choice.Target, choice.Policy,
            (item, target) => _window.Dispatcher.Invoke(() =>
            {
                var d = new ConflictDialog(item, target) { Owner = _window };
                d.ShowDialog();
                return (d.Choice, d.ForAll);
            }), hwnd));

        ApplyRemoved(res.Done);
        string? hint = UpdateDestination(choice.Target, res);
        Report("Verschoben", items, res, hint);
    }

    private async Task ZipAsync(List<OpItem> sel)
    {
        var items = Prepare(sel);
        if (items == null) return;

        var first = items[0];
        string name = items.Count == 1 ? Path.GetFileNameWithoutExtension(first.Name) is { Length: > 0 } n ? n : first.Name
            : Path.GetFileName((first.Parent ?? "").TrimEnd('\\')) is { Length: > 0 } p ? p : "Archiv";
        var dlg = new TargetDialog(true, items, Path.Combine(first.Parent ?? "", name + ".zip")) { Owner = _window };
        if (dlg.ShowDialog() != true || dlg.Result is not { } choice) return;

        // An existing archive is replaced by the new one (the old one goes to the recycle bin); its numbers leave the tree first.
        var zipItem = File.Exists(choice.Target) ? ItemOf(choice.Target, false, new FileInfo(choice.Target).Length) : null;

        var res = await RunBusy("Archiviere …", () => FileOperations.Zip(items, choice.Target));
        bool created = File.Exists(choice.Target) && !File.Exists(choice.Target + ".dt_tmp") && res.Failed.All(f => f.Path != choice.Target);

        if (created)
        {
            if (zipItem != null) ApplyRemoved(new[] { zipItem });
            long zipSize = new FileInfo(choice.Target).Length;
            if (Scanner?.FindNode(Path.GetDirectoryName(choice.Target) ?? "") is { } dir)
            {
                TreeEditor.AddFile(dir, Path.GetFileName(choice.Target), zipSize, Scanner.Ages.OfFile(choice.Target));
                RefreshAfterChange();
            }
        }

        string text = created
            ? $"ZIP erstellt: {Path.GetFileName(choice.Target)} ({SizeFormat.Format(new FileInfo(choice.Target).Length)}, {res.Done.Count:N0} von {items.Count:N0} Elementen vollständig)."
            : "ZIP wurde nicht erstellt.";

        OpResult? recycled = null;
        if (created && choice.RecycleOriginals && res.Done.Count > 0)
        {
            long bytes = res.Done.Sum(i => i.Size);
            if (Dialogs.Confirm(_window,
                    $"{res.Done.Count:N0} vollständig archivierte Originale ({SizeFormat.Format(bytes)}) jetzt in den Papierkorb legen?"))
            {
                var hwnd = new WindowInteropHelper(_window).Handle;
                var done = res.Done.ToList();
                recycled = await RunBusy("Originale in den Papierkorb …", () => FileOperations.RecycleAll(done, hwnd));
                ApplyRemoved(recycled.Done);
                text += $" {recycled.Done.Count:N0} Originale in den Papierkorb gelegt.";
            }
        }

        var failed = res.Failed.Concat(recycled?.Failed ?? []).ToList();
        _status.ShowText(text + (failed.Count > 0 ? $" {failed.Count:N0} Fehler." : ""));
        if (failed.Count > 0) ShowFailures(created ? "ZIP erstellt, aber nicht alles ließ sich archivieren." : "ZIP nicht erstellt.", failed);
    }

    private async Task RenameAsync(List<OpItem> sel)
    {
        var items = Prepare(sel);
        if (items == null) return;

        var dlg = new RenameDialog(items) { Owner = _window };
        if (dlg.ShowDialog() != true) return;
        var plans = dlg.Result;

        var res = await RunBusy("Benenne um …", () => FileOperations.Rename(plans));

        var scanner = Scanner;
        foreach (var item in res.Done)
        {
            var newPath = res.NewPaths[item.Path];
            var newName = Path.GetFileName(newPath);
            if (item.IsDir)
            {
                if (scanner?.FindNode(item.Path) is { } node) TreeEditor.RenameFolder(node, newName);
            }
            else if (scanner?.FindNode(item.Parent ?? "") is { } dir)
            {
                // The extension may have changed, and with it the file type: counted out and in again.
                TreeEditor.RenameFile(dir, item.Name, newName, item.Size, item.AgeBucket);
            }
            scanner?.RenameTop(item.Path, newPath);
        }
        RefreshAfterChange();
        Report("Umbenannt", items, res, null);
    }

    private async Task DeleteAsync(List<OpItem> sel)
    {
        var items = Prepare(sel);
        if (items == null) return;

        long size = items.Sum(i => i.Size);
        int dirs = items.Count(i => i.IsDir);
        string what = dirs == 0 ? $"{items.Count:N0} Dateien" : $"{items.Count:N0} Elemente (davon {dirs:N0} Ordner)";
        string names = string.Join("\n", items.Take(6).Select(i => "  " + i.Name)) + (items.Count > 6 ? $"\n  … und {items.Count - 6:N0} weitere" : "");
        var paths = items.Select(i => i.Path).ToList();
        string warn = _sources.Any(s => s.CoversAllCopies(paths))
            ? "\n\nAchtung: Von mindestens einer Dublettengruppe sind alle Kopien in der Auswahl. Danach gäbe es die Datei nur noch im Papierkorb."
            : "";
        if (!Dialogs.Confirm(_window,
                $"{what} mit zusammen {SizeFormat.Format(size)} in den Papierkorb verschieben?\n\n{names}{warn}", MessageBoxImage.Warning))
            return;

        var hwnd = new WindowInteropHelper(_window).Handle;
        var res = await RunBusy("Lösche in den Papierkorb …", () => FileOperations.RecycleAll(items, hwnd));
        ApplyRemoved(res.Done);
        Report("In den Papierkorb gelegt", items, res, null);
    }

    // PHASE-D: while the action runs a scan can still be started (the scan button and F5 do not look at _opBusy; only
    // ScanPathAsync and the tab changes do)
    private async Task<OpResult> RunBusy(string status, Func<OpResult> work)
    {
        _opBusy = true;
        _status.ShowText(status);
        Mouse.OverrideCursor = Cursors.Wait;
        try { return await Task.Run(work); }
        finally
        {
            _opBusy = false;
            Mouse.OverrideCursor = null;
        }
    }

    // ---------------------------------------------------------------- consistency afterwards

    /// <summary>Takes items that are gone from the tree: sizes, file types and age buckets of the folders above, largest files.</summary>
    private void ApplyRemoved(IEnumerable<OpItem> removed)
    {
        var scanner = Scanner;
        if (scanner == null) return;
        foreach (var it in removed)
        {
            if (it.IsDir)
            {
                if (scanner.FindNode(it.Path) is { Parent: not null } node)
                {
                    if (Root?.FindDescendant(node) is { } vm) vm.RemoveFromTree();
                    else TreeEditor.RemoveFolder(node); // its row was never created
                }
            }
            else if (scanner.FindNode(it.Parent ?? "") is { } dir)
            {
                TreeEditor.RemoveFile(dir, it.Name, it.Size, it.AgeBucket);
            }
            scanner.RemoveTop(it.Path);
        }
        RefreshAfterChange();
    }

    private void RefreshAfterChange()
    {
        if (Scanner?.Root is { } drive) Scanner.ReadDrive(drive);
        _tree.RefreshRows();
        _views.RefreshViews();
        foreach (var s in _sources) s.PruneMissing();
    }

    /// <summary>
    /// After a move: if the destination folder is part of the tree, the arrived files are counted in (plain files) or the
    /// folder is scanned again (folders, replaced files). Returns a hint for the user if the tree could not be updated.
    /// </summary>
    private string? UpdateDestination(string destDir, OpResult res)
    {
        var scanner = Scanner;
        if (scanner == null || res.Done.Count == 0) return null;

        var node = scanner.FindNode(destDir);
        if (node == null)
        {
            // Inside the scanned area but not in the tree (new folder)? Then the parent needs a rescan to show it.
            for (var p = Path.GetDirectoryName(destDir.TrimEnd('\\')); p != null; p = Path.GetDirectoryName(p))
                if (scanner.FindNode(p) != null)
                    return "Das Ziel liegt im gescannten Bereich, ist aber noch nicht im Baum. „Ab hier neu scannen“ im übergeordneten Ordner aktualisiert es.";
            return null;
        }

        bool plain = !res.Replaced && res.Done.All(i => !i.IsDir) && node.Scanned;
        if (plain)
        {
            foreach (var it in res.Done)
            {
                var newPath = res.NewPaths[it.Path];
                TreeEditor.AddFile(node, Path.GetFileName(newPath), it.Size, it.AgeBucket);
            }
            RefreshAfterChange();
            return null;
        }

        if (node.FileCount < 100_000)
        {
            _ = _scan.RescanAsync(node);
            return null;
        }
        return "Das Ziel liegt im Baum, ist aber groß: bitte „Ab hier neu scannen“ auf dem Zielordner ausführen.";
    }

    // ---------------------------------------------------------------- result

    private void Report(string verb, List<OpItem> items, OpResult res, string? hint)
    {
        string text = $"{verb}: {res.Done.Count:N0} von {items.Count:N0} ({SizeFormat.Format(res.DoneBytes)})";
        if (res.Skipped.Count > 0) text += $", {res.Skipped.Count:N0} übersprungen";
        if (res.Failed.Count > 0) text += $", {res.Failed.Count:N0} Fehler";
        _status.ShowText(text + ".");

        var failed = res.Failed.ToList();
        if (failed.Count > 0 || hint != null)
            ShowFailures(text + ".", failed, hint);
    }

    private void ShowFailures(string headline, IReadOnlyList<(string Path, string Error)> failed, string? hint = null)
    {
        var lines = failed.Take(12).Select(f => $"• {f.Path}\n   {f.Error}");
        string more = failed.Count > 12 ? $"\n… und {failed.Count - 12:N0} weitere Fehler." : "";
        string body = headline
            + (failed.Count > 0 ? "\n\nNicht bearbeitet:\n" + string.Join("\n", lines) + more : "")
            + (hint != null ? "\n\n" + hint : "");
        if (failed.Count > 0) Dialogs.Warn(_window, body); else Dialogs.Info(_window, body);
    }
}
