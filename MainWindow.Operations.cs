using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace DiskTree;

// Clean-up actions on the selection (move, ZIP, rename, delete to the recycle bin): menu entries, keys, confirmation,
// and keeping tree / file types / age data / largest files in step afterwards. The file work itself is in FileOperations.
public partial class MainWindow
{
    private bool _opBusy;
    private string _lastMoveTarget = "";

    private void InitOps()
    {
        SearchPane.ExtraKeyDown = OpsKey;
        Dup.ExtraKeyDown = OpsKey;
    }

    // ---------------------------------------------------------------- entry points

    /// <summary>Our entries for the context menu of <paramref name="path"/>; empty in a stored scan.</summary>
    private IReadOnlyList<(string Text, Action Run)> OpExtras(string path, bool isDir)
    {
        if (IsSnapshotMode) return [];

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
    private bool OpsKey(Key key)
    {
        if (key != Key.F2 && key != Key.Delete) return false;

        var sel = ActiveSelection(null, preferFocus: true);
        if (sel.Count == 0 || (key == Key.Delete && sel.Count < 2)) return false; // one item: the shell's own Delete

        if (IsSnapshotMode) { SnapshotHint(); return true; }
        RunOp(() => key == Key.F2 ? RenameAsync(sel) : DeleteAsync(sel));
        return true;
    }

    /// <summary>F2 in the tree (single selection).</summary>
    private bool OpsTreeKey(Key key, NodeVM vm)
    {
        if (key != Key.F2 || !CanUseShell(vm)) return false;
        if (IsSnapshotMode) { SnapshotHint(); return true; }
        var sel = new List<OpItem> { ItemOf(vm.Path, vm.Kind == Kind.Dir, vm.Size) };
        RunOp(() => RenameAsync(sel));
        return true;
    }

    // Runs after the menu is closed, so the menu code never sees the files change underneath it.
    private void RunOp(Func<Task> op) =>
        Dispatcher.BeginInvoke(async () =>
        {
            try { await op(); }
            catch (Exception ex)
            {
                _opBusy = false;
                MessageBox.Show(this, ex.Message, "DiskTree", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        });

    // ---------------------------------------------------------------- selection

    private static OpItem ItemOf(string path, bool isDir, long size) =>
        new(path, isDir, size, isDir ? -1 : AgeBuckets.OfFile(path));

    /// <summary>Selection of the list the user is working in (mouse over it, else keyboard focus).</summary>
    private List<OpItem> ActiveSelection(string? mustContain, bool preferFocus = false)
    {
        var sources = new FrameworkElement[] { TopList, SearchPane.Results, Dup.List };
        var order = preferFocus
            ? sources.OrderBy(c => c.IsKeyboardFocusWithin ? 0 : 1)
            : sources.OrderBy(c => c.IsMouseOver ? 0 : c.IsKeyboardFocusWithin ? 1 : 2);

        foreach (var c in order)
        {
            if (!(preferFocus ? c.IsKeyboardFocusWithin : c.IsMouseOver || c.IsKeyboardFocusWithin)) continue;
            var items = SelectionOf(c);
            if (items.Count == 0) continue;
            if (mustContain != null && !items.Any(i => string.Equals(i.Path, mustContain, StringComparison.OrdinalIgnoreCase))) continue;
            return items;
        }
        return new List<OpItem>();
    }

    private List<OpItem> SelectionOf(FrameworkElement c)
    {
        if (c == TopList)
            return TopList.SelectedItems.Cast<TopRow>().Select(r => ItemOf(r.Path, false, r.Size)).ToList();
        if (c == SearchPane.Results)
            return SearchPane.SelectedHits.Select(h => ItemOf(h.Path, h.IsDir, h.Size)).ToList();
        if (c == Dup.List)
            return Dup.SelectedFiles().Select(f => ItemOf(f.Path, false, f.Size)).ToList();
        return new List<OpItem>();
    }

    /// <summary>The items an action from the context menu of <paramref name="path"/> applies to: the multi-selection it is part of, else just itself.</summary>
    private List<OpItem> SelectionFor(string path, bool isDir)
    {
        var sel = ActiveSelection(path);
        if (sel.Count > 0) return sel;

        if (Tree.SelectedItem is NodeVM vm && string.Equals(vm.Path, path, StringComparison.OrdinalIgnoreCase))
            return new List<OpItem> { ItemOf(path, isDir, vm.Size) };

        long size = 0;
        try { size = isDir ? _scanner?.FindNode(path)?.Size ?? 0 : new FileInfo(path).Length; } catch { }
        return new List<OpItem> { ItemOf(path, isDir, size) };
    }

    /// <summary>Checks the preconditions and cleans the selection up. Null (after telling the user) if nothing can be done.</summary>
    private List<OpItem>? Prepare(List<OpItem> sel, bool allowRoot = false)
    {
        if (IsSnapshotMode) { SnapshotHint(); return null; }
        if (_opBusy) return null;
        if (_cts != null)
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

        var dlg = new TargetDialog(false, items, _lastMoveTarget) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result is not { } choice) return;
        _lastMoveTarget = choice.Target;

        var hwnd = new WindowInteropHelper(this).Handle;
        var res = await RunBusy("Verschiebe …", () => FileOperations.Move(items, choice.Target, choice.Policy,
            (item, target) => Dispatcher.Invoke(() =>
            {
                var d = new ConflictDialog(item, target) { Owner = this };
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
        var dlg = new TargetDialog(true, items, Path.Combine(first.Parent ?? "", name + ".zip")) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result is not { } choice) return;

        // An existing archive is replaced by the new one (the old one goes to the recycle bin); its numbers leave the tree first.
        var zipItem = File.Exists(choice.Target) ? ItemOf(choice.Target, false, new FileInfo(choice.Target).Length) : null;

        var res = await RunBusy("Archiviere …", () => FileOperations.Zip(items, choice.Target));
        bool created = File.Exists(choice.Target) && !File.Exists(choice.Target + ".dt_tmp") && res.Failed.All(f => f.Path != choice.Target);

        if (created)
        {
            if (zipItem != null) ApplyRemoved(new[] { zipItem });
            long zipSize = new FileInfo(choice.Target).Length;
            if (_scanner?.FindNode(Path.GetDirectoryName(choice.Target) ?? "") is { } dir)
            {
                FileOperations.AddFile(dir, Path.GetFileName(choice.Target), zipSize, AgeBuckets.OfFile(choice.Target));
                RefreshAfterChange();
            }
        }

        string text = created
            ? $"ZIP erstellt: {Path.GetFileName(choice.Target)} ({NodeVM.FormatSize(new FileInfo(choice.Target).Length)}, {res.Done.Count:N0} von {items.Count:N0} Elementen vollständig)."
            : "ZIP wurde nicht erstellt.";

        OpResult? recycled = null;
        if (created && choice.RecycleOriginals && res.Done.Count > 0)
        {
            long bytes = res.Done.Sum(i => i.Size);
            if (MessageBox.Show(this,
                    $"{res.Done.Count:N0} vollständig archivierte Originale ({NodeVM.FormatSize(bytes)}) jetzt in den Papierkorb legen?",
                    "DiskTree", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                var done = res.Done.ToList();
                recycled = await RunBusy("Originale in den Papierkorb …", () => FileOperations.RecycleAll(done, hwnd));
                ApplyRemoved(recycled.Done);
                text += $" {recycled.Done.Count:N0} Originale in den Papierkorb gelegt.";
            }
        }

        var failed = res.Failed.Concat(recycled?.Failed ?? []).ToList();
        StatusText.Text = text + (failed.Count > 0 ? $" {failed.Count:N0} Fehler." : "");
        if (failed.Count > 0) ShowFailures(created ? "ZIP erstellt, aber nicht alles ließ sich archivieren." : "ZIP nicht erstellt.", failed);
    }

    private async Task RenameAsync(List<OpItem> sel)
    {
        var items = Prepare(sel);
        if (items == null) return;

        var dlg = new RenameDialog(items) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var plans = dlg.Result;

        var res = await RunBusy("Benenne um …", () => FileOperations.Rename(plans));

        foreach (var item in res.Done)
        {
            var newPath = res.NewPaths[item.Path];
            var newName = Path.GetFileName(newPath);
            if (item.IsDir)
            {
                if (_scanner?.FindNode(item.Path) is { } node) FileOperations.RenameNode(node, newName);
            }
            else if (_scanner?.FindNode(item.Parent ?? "") is { } dir)
            {
                // The extension may have changed, and with it the file type: count the file out and in again.
                dir.RemoveFile(item.Name, item.Size, item.AgeBucket);
                FileOperations.AddFile(dir, newName, item.Size, item.AgeBucket);
            }
            _scanner?.RenameTop(item.Path, newPath);
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
        string warn = Dup.AllCopiesChecked(items.Select(i => i.Path))
            ? "\n\nAchtung: Von mindestens einer Dublettengruppe sind alle Kopien in der Auswahl. Danach gäbe es die Datei nur noch im Papierkorb."
            : "";
        if (MessageBox.Show(this,
                $"{what} mit zusammen {NodeVM.FormatSize(size)} in den Papierkorb verschieben?\n\n{names}{warn}",
                "DiskTree", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        var hwnd = new WindowInteropHelper(this).Handle;
        var res = await RunBusy("Lösche in den Papierkorb …", () => FileOperations.RecycleAll(items, hwnd));
        ApplyRemoved(res.Done);
        Report("In den Papierkorb gelegt", items, res, null);
    }

    private async Task<OpResult> RunBusy(string status, Func<OpResult> work)
    {
        _opBusy = true;
        StatusText.Text = status;
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
        if (_scanner == null) return;
        foreach (var it in removed)
        {
            if (it.IsDir)
            {
                if (_scanner.FindNode(it.Path) is { Parent: not null } node)
                {
                    if (_root?.FindDescendant(node) is { } vm) vm.RemoveFromTree();
                    else DetachNode(node);
                }
            }
            else if (_scanner.FindNode(it.Parent ?? "") is { } dir)
            {
                dir.RemoveFile(it.Name, it.Size, it.AgeBucket);
            }
            _scanner.RemoveTop(it.Path);
        }
        RefreshAfterChange();
    }

    // Same as NodeVM.RemoveFromTree for a folder whose row was never created.
    private static void DetachNode(Node n)
    {
        var parent = n.Parent!;
        AgeBuckets.RemoveSubtree(n);
        for (var p = parent; p != null; p = p.Parent)
        {
            Interlocked.Add(ref p.Size, -n.Size);
            Interlocked.Add(ref p.FileCount, -n.FileCount);
            Interlocked.Add(ref p.DirCount, -(n.DirCount + 1));
        }
        parent.RemoveChild(n);
    }

    private void RefreshAfterChange()
    {
        if (_scanner?.Root is { } drive) Scanner.ReadDrive(drive);
        _root?.Refresh();
        _ = UpdatePieAsync();
        RefreshTop();
        SearchPane.PruneMissing();
        Dup.PruneMissing();
    }

    /// <summary>
    /// After a move: if the destination folder is part of the tree, the arrived files are counted in (plain files) or the
    /// folder is scanned again (folders, replaced files). Returns a hint for the user if the tree could not be updated.
    /// </summary>
    private string? UpdateDestination(string destDir, OpResult res)
    {
        if (_scanner == null || res.Done.Count == 0) return null;

        var node = _scanner.FindNode(destDir);
        if (node == null)
        {
            // Inside the scanned area but not in the tree (new folder)? Then the parent needs a rescan to show it.
            for (var p = Path.GetDirectoryName(destDir.TrimEnd('\\')); p != null; p = Path.GetDirectoryName(p))
                if (_scanner.FindNode(p) != null)
                    return "Das Ziel liegt im gescannten Bereich, ist aber noch nicht im Baum. „Ab hier neu scannen“ im übergeordneten Ordner aktualisiert es.";
            return null;
        }

        bool plain = !res.Replaced && res.Done.All(i => !i.IsDir) && node.Scanned;
        if (plain)
        {
            foreach (var it in res.Done)
            {
                var newPath = res.NewPaths[it.Path];
                FileOperations.AddFile(node, Path.GetFileName(newPath), it.Size, it.AgeBucket);
            }
            RefreshAfterChange();
            return null;
        }

        if (node.FileCount < 100_000)
        {
            _ = RescanAsync(node);
            return null;
        }
        return "Das Ziel liegt im Baum, ist aber groß: bitte „Ab hier neu scannen“ auf dem Zielordner ausführen.";
    }

    // ---------------------------------------------------------------- result

    private void Report(string verb, List<OpItem> items, OpResult res, string? hint)
    {
        string text = $"{verb}: {res.Done.Count:N0} von {items.Count:N0} ({NodeVM.FormatSize(res.DoneBytes)})";
        if (res.Skipped.Count > 0) text += $", {res.Skipped.Count:N0} übersprungen";
        if (res.Failed.Count > 0) text += $", {res.Failed.Count:N0} Fehler";
        StatusText.Text = text + ".";

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
        MessageBox.Show(this, body, "DiskTree", MessageBoxButton.OK, failed.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }
}
