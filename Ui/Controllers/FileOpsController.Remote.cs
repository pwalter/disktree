using System.IO;
using System.Windows;
using System.Windows.Input;
using DiskTree.Core;
using DiskTree.Core.Storage;
using DiskTree.Features.FileOperations;
using DiskTree.Infrastructure;

namespace DiskTree.Ui.Controllers;

/// <summary>An entry of our own context menu: enabled with an action, or greyed out with the reason as tooltip.</summary>
public readonly record struct MenuEntry(string Text, Action? Run, string? WhyNot = null);

/// <summary>
/// Rename, move and delete for good on a source that changes files itself (<see cref="IStorageWriter"/>, SFTP, S3): no
/// recycle bin, no ZIP, no Windows shell. Every delete goes through <see cref="PermanentDeleteDialog"/> (default button
/// "Abbrechen"; S3 adds the versioning of the bucket and the requests); names are checked case-sensitively; the server
/// does the work off the UI thread and Esc cancels it between entries. S3 renames and moves folders by copying every
/// object: that is shown and confirmed first (<see cref="CopyPlan"/>), and every request of a step goes through one
/// <see cref="ListRun"/> with the cost pause of the settings. The tree, the charts and the lists are kept in step through
/// <see cref="TreeUpdate"/> (or a rescan of what could not be followed exactly), like the disk actions.
/// </summary>
public sealed partial class FileOpsController
{
    private CancellationTokenSource? _opCts;
    private string _lastRemoteTarget = "";

    /// <summary>The writer of the step that runs now, bound to its request counter (S3); null between steps.</summary>
    private IStorageWriter? _opWriter;

    /// <summary>Requests the current action has sent so far (all its steps), for the report of a paid source.</summary>
    private long _actionRequests;

    /// <summary>The source of the active tab's scan changes files itself (SFTP, S3), else null (the disk, nothing scanned).</summary>
    private IStorageProvider? RemoteWritable => Scanner?.Provider is { } p && StorageFeatures.IsRemoteWritable(p) ? p : null;

    /// <summary>
    /// Inside a step (<see cref="RunBusyRemote"/>): the writer bound to the step's run, so every request is counted, can
    /// pause at the cost limit and ends with Esc. Outside: the provider itself.
    /// </summary>
    private IStorageWriter Writer => _opWriter ?? (IStorageWriter)Scanner!.Provider;

    private bool CostsPerRequest => Scanner?.Provider is { } p && (p.Caps & StorageCaps.CostsPerRequest) != 0;

    private static string SourceName(IStorageProvider p) => p.Profile is { } pr ? $"{p.DisplayName} „{pr.Name}“" : p.DisplayName;

    /// <summary>Esc while a remote action runs: it stops after the current entry. True if there was one to stop.</summary>
    public bool CancelOperation()
    {
        if (_opCts is not { IsCancellationRequested: false } cts) return false;
        cts.Cancel();
        _status.ShowText("Wird abgebrochen …");
        return true;
    }

    // ---------------------------------------------------------------- entry points

    /// <summary>
    /// The action entries of our menu for an item of a remote source: rename, move, delete for good (for the
    /// multi-selection the item is part of). Greyed out with the reason in a stored scan or where the source cannot.
    /// </summary>
    public IReadOnlyList<MenuEntry> RemoteEntries(string path, bool isDir)
    {
        if (Scanner?.Provider is not { } p) return [];
        var sel = RemoteSelectionFor(path, isDir);
        string suffix = sel.Count > 1 ? $" ({sel.Count:N0} Elemente)" : "";
        string? snapshot = _snapshots.IsSnapshotMode
            ? "Gespeicherter Scan: Änderungen sind nur in einem aktuellen Scan der Quelle möglich (F5 scannt neu)."
            : null;
        MenuEntry Entry(string text, StorageFeature f, Func<List<OpItem>, Task> op)
        {
            var why = StorageFeatures.WhyNot(p, f) ?? snapshot;
            return why != null ? new MenuEntry(text, null, why) : new MenuEntry(text, () => RunOp(() => op(sel)));
        }
        return
        [
            Entry("Umbenennen (F2)" + suffix, StorageFeature.Rename, RemoteRenameAsync),
            Entry("Verschieben nach …" + suffix, StorageFeature.Move, RemoteMoveAsync),
            Entry("Endgültig löschen …" + suffix, StorageFeature.Delete, RemoteDeleteAsync),
        ];
    }

    /// <summary>Delete on one row (tree, largest files, search hit, duplicate) of a remote source: the confirmation, then delete.</summary>
    public void DeleteRemote(string path, bool isDir, long size)
    {
        if (_snapshots.IsSnapshotMode) { _snapshots.ShowHint(); return; }
        if (Scanner?.Provider is { } p && StorageFeatures.WhyNot(p, StorageFeature.Delete) is { } why) { Notice(why); return; }
        var sel = RemoteSelectionFor(path, isDir);
        if (sel.Count == 0) sel = [RemoteItem(path, isDir, size)];
        RunOp(() => RemoteDeleteAsync(sel));
    }

    /// <summary>F2 / Delete in the lists of a remote source (one or several items; Delete always asks first).</summary>
    private bool HandleRemoteKey(Key key)
    {
        var sel = RemoteActiveSelection(null, preferFocus: true);
        if (sel.Count == 0) return false;
        if (_snapshots.IsSnapshotMode) { _snapshots.ShowHint(); return true; }
        if (WhyNot(key == Key.F2 ? StorageFeature.Rename : StorageFeature.Delete) is { } why) { Notice(why); return true; }
        RunOp(() => key == Key.F2 ? RemoteRenameAsync(sel) : RemoteDeleteAsync(sel));
        return true;
    }

    // ---------------------------------------------------------------- selection

    private OpItem RemoteItem(string path, bool isDir, long size) => new(path, isDir, size, -1, Scanner?.Paths);

    /// <summary>Like <see cref="ActiveSelection"/>, with the source's path rules (case-sensitive).</summary>
    private List<OpItem> RemoteActiveSelection(string? mustContain, bool preferFocus = false)
    {
        var paths = Scanner?.Paths;
        if (paths == null) return new List<OpItem>();
        var order = preferFocus
            ? _sources.OrderBy(c => c.Element.IsKeyboardFocusWithin ? 0 : 1)
            : _sources.OrderBy(c => c.Element.IsMouseOver ? 0 : c.Element.IsKeyboardFocusWithin ? 1 : 2);
        foreach (var c in order)
        {
            var e = c.Element;
            if (!(preferFocus ? e.IsKeyboardFocusWithin : e.IsMouseOver || e.IsKeyboardFocusWithin)) continue;
            var items = c.Selected().Select(i => RemoteItem(i.Path, i.IsDir, i.Size)).ToList();
            if (items.Count == 0) continue;
            if (mustContain != null && !items.Any(i => paths.NameEquals(i.Path, mustContain))) continue;
            return items;
        }
        return new List<OpItem>();
    }

    private List<OpItem> RemoteSelectionFor(string path, bool isDir)
    {
        var sel = RemoteActiveSelection(path);
        if (sel.Count > 0) return sel;
        var paths = Scanner?.Paths;
        if (_tree.SelectedRow is { } vm && paths != null && paths.NameEquals(vm.Path, path))
            return new List<OpItem> { RemoteItem(path, isDir, vm.Size) };
        long size = isDir ? Scanner?.FindNode(path)?.Size ?? 0 : 0;
        return new List<OpItem> { RemoteItem(path, isDir, size) };
    }

    /// <summary>
    /// The checks before an action (no stored scan, no running scan or action, not the top of the scan), then every
    /// item is looked up on the server: what is gone is left out, files get their age bucket. Null if nothing is left.
    /// </summary>
    private async Task<List<OpItem>?> PrepareRemoteAsync(List<OpItem> sel, IDictionary<string, long>? modified = null)
    {
        if (_snapshots.IsSnapshotMode) { _snapshots.ShowHint(); return null; }
        if (_opBusy || Scanner is not { Root: { } top } scanner) return null;
        _actionRequests = 0;
        if (_tabs.Busy != null)
        {
            Notice("Ein Scan läuft noch. Die Aktion ist erst möglich, wenn er fertig oder abgebrochen ist.");
            return null;
        }

        var paths = scanner.Paths;
        var items = RemoteFileOperations.Normalize(sel, paths);
        // Neither the top of the server nor the folder the tree starts at (it has nothing above it to take the numbers).
        items.RemoveAll(i => RemoteFileOperations.IsRoot(i) || paths.NameEquals(paths.TrimEnd(i.Path), paths.TrimEnd(top.FullPath))
                             || (scanner.IsMulti && top.Snapshot().Any(n => paths.NameEquals(n.FullPath, i.Path))));
        if (items.Count == 0)
        {
            Notice("Es gibt nichts zu tun: die Auswahl ist der oberste Ordner des Scans oder des Servers.");
            return null;
        }

        var res = new OpResult(paths.Comparer);
        var fresh = await RunBusyRemote("Prüfe die Auswahl auf dem Server …",
            ct => RemoteFileOperations.Refresh(Writer, items, scanner.Ages, res, modified, ct));
        if (fresh == null) return null;
        if (fresh.Count == 0 || res.Failed.Count > 0)
        {
            var lines = res.Failed.Select(f => $"• {f.Path}\n   {f.Error}")
                .Concat(res.Skipped.Select(s => $"• {s.Item.Path}\n   {s.Reason}")).Take(12);
            string text = (fresh.Count == 0 ? "Es gibt nichts zu tun:" : "Nicht alles ist auf dem Server verfügbar:") + "\n\n" + string.Join("\n", lines);
            if (fresh.Count == 0) { Dialogs.Warn(_window, text); return null; }
            if (!Dialogs.Confirm(_window, text + "\n\nMit dem Rest fortfahren?", MessageBoxImage.Warning)) return null;
        }
        return fresh;
    }

    // ---------------------------------------------------------------- actions

    private async Task RemoteRenameAsync(List<OpItem> sel)
    {
        var scanner = Scanner;
        if (scanner == null) return;
        var paths = scanner.Paths;
        var modified = new Dictionary<string, long>(paths.Comparer);
        var items = await PrepareRemoteAsync(sel, modified);
        if (items == null) return;

        // The names next to the items, once (typing in the dialog asks the server nothing; the server refuses a name
        // that appears meanwhile anyway).
        var folders = items.Select(i => i.Parent ?? "").Distinct(paths.Comparer).ToList();
        var names = await RunBusyRemote("Lese die Ordner …", ct =>
            folders.ToDictionary(f => f, f => Writer.ListNames(f, ct), paths.Comparer));
        if (names == null) return;
        var rules = new RenameRules(paths, names, modified);

        var dlg = new RenameDialog(items, rules) { Owner = _window };
        if (dlg.ShowDialog() != true) return;
        var plans = dlg.Result;

        // S3: a folder is renamed by copying all its objects – only after the user saw what that means.
        var copyPlan = ConfirmCopy(scanner, plans.Where(p => p.Ok).Select(p => p.Item).ToList(), moving: false, out bool declined);
        if (declined) return;

        var res = await RunBusyRemote("Benenne um …", ct => RemoteFileOperations.Rename(Writer, plans, ct, copyPlan), "Kopiere");
        if (res == null) return;
        if (Scanner != scanner) return; // the tab was switched meanwhile (not possible while busy, but cheap to check)
        TreeUpdate.Renamed(scanner, res);
        await RefreshAfterRemoteChangeAsync(scanner, res.Done.Select(i => i.Path));
        RemoteReport("Umbenannt", items, res);
        // A folder renamed only partly: its parent holds the old and the new name.
        if (res.Partial.Count > 0) RescanAll(scanner, res.Partial.Select(i => i.Parent ?? i.Path));
    }

    /// <summary>
    /// Renaming / moving folders on a source that copies every object (S3): shows objects, data, requests and that it
    /// is not atomic; default button "Nein". Null without folders or on another source; <paramref name="declined"/> if
    /// the user said no.
    /// </summary>
    private CopyPlan? ConfirmCopy(Scanner scanner, IReadOnlyList<OpItem> items, bool moving, out bool declined)
    {
        declined = false;
        if (!CopyPlan.Needed(Writer, items) || Writer is not ICopyingWriter copying) return null;
        var plan = new CopyPlan(items, moving, SourceName(scanner.Provider), copying,
            i => scanner.FindNode(i.Path) is { } n ? (n.FileCount, n.DirCount, n.Size) : null);
        if (!Dialogs.ConfirmRisky(_window, plan.Describe() + "\n\nFortfahren?", plan.Title))
        {
            declined = true;
            return null;
        }
        plan.Confirm();
        return plan;
    }

    /// <summary>Scans folders again one after the other (the nearest folder of the tree for each path; nothing twice).</summary>
    private void RescanAll(Scanner scanner, IEnumerable<string> paths)
    {
        var nodes = new List<Node>();
        foreach (var p in paths)
        {
            Node? n = null;
            for (var q = p; q != null && n == null; q = scanner.Paths.ParentOf(q)) n = scanner.FindNode(q);
            if (n != null) nodes.Add(n);
        }
        bool Below(Node n, Node other) { for (var a = n.Parent; a != null; a = a.Parent) if (a == other) return true; return false; }
        var tops = nodes.Distinct().Where(n => !nodes.Any(o => o != n && Below(n, o))).ToList();
        async Task Run()
        {
            foreach (var n in tops)
                if (Scanner == scanner) await _scan.RescanAsync(n);
        }
        Run().Forget("RescanAsync");
    }

    private async Task RemoteMoveAsync(List<OpItem> sel)
    {
        var scanner = Scanner;
        if (scanner == null) return;
        var items = await PrepareRemoteAsync(sel);
        if (items == null) return;

        var dlg = new RemoteTargetDialog(items, scanner, Writer, _lastRemoteTarget) { Owner = _window };
        if (dlg.ShowDialog() != true || dlg.Result is not { } choice) return;
        _lastRemoteTarget = choice.Target;

        var copyPlan = ConfirmCopy(scanner, items, moving: true, out bool declined);
        if (declined) return;

        var res = await RunBusyRemote("Verschiebe auf dem Server …", ct => RemoteFileOperations.Move(Writer, scanner.Paths, items, choice.Target,
            choice.Policy, (item, target) => _window.Dispatcher.Invoke(() =>
            {
                var d = new ConflictDialog(item, target, permanent: true) { Owner = _window };
                d.ShowDialog();
                return (d.Choice, d.ForAll);
            }), ct, copyPlan), "Kopiere");
        if (res == null) return;

        TreeUpdate.Removed(scanner, res.Done, RemoveFolderNode, res.NewPaths);
        TreeUpdate.Vanished(scanner, res.Vanished, RemoveFolderNode);
        var again = TreeUpdate.Arrived(scanner, choice.Target, res);
        await RefreshAfterRemoteChangeAsync(scanner, res.Done.Select(i => i.Path).Concat(res.Vanished));
        RemoteReport("Verschoben", items, res);
        // Moved only partly (S3: some objects of a folder are at the new place): both places are scanned again.
        if (res.Partial.Count > 0) RescanAll(scanner, res.Partial.Select(i => i.Path).Append(choice.Target));
        else if (again != null) _scan.RescanAsync(again).Forget("RescanAsync");
    }

    private async Task RemoteDeleteAsync(List<OpItem> sel)
    {
        var scanner = Scanner;
        if (scanner == null) return;
        var items = await PrepareRemoteAsync(sel);
        if (items == null) return;

        var plan = new PermanentDeletePlan(items, SourceName(scanner.Provider),
            i => scanner.FindNode(i.Path) is { } n ? (n.FileCount, n.DirCount, n.Size) : null);
        var all = items.Select(i => i.Path).ToList();
        if (Writer is ICopyingWriter)
        {
            // S3: whether the bucket keeps old versions (then only delete markers are written), and what it costs.
            var note = await RunBusyRemote("Prüfe die Versionierung des Buckets …", ct => ((ICopyingWriter)Writer).DescribeDelete(all, ct));
            if (note == null) return;
            long single = items.Count(i => !i.IsDir);
            long requests = ((ICopyingWriter)Writer).EstimateDelete(single, items.Count(i => i.IsDir), Math.Max(0, plan.Files - single));
            plan.SourceNote = note with
            {
                Text = (note.Text ?? "") + $"\n\nGeschätzte Anfragen: ca. {requests:N0} (LIST und DeleteObjects mit je bis zu 1.000 Objekten).",
            };
        }
        string? warn = _sources.Any(s => s.CoversAllCopies(all))
            ? "Achtung: Von mindestens einer Dublettengruppe sind alle Kopien in der Auswahl. Danach gibt es die Datei nicht mehr."
            : null;
        var dlg = new PermanentDeleteDialog(plan, warn) { Owner = _window };
        if (dlg.ShowDialog() != true || !plan.IsConfirmed) return;

        long lastTick = 0;
        var res = await RunBusyRemote("Lösche endgültig …", ct => RemoteFileOperations.Delete(Writer, plan, (files, bytes) =>
        {
            long now = Environment.TickCount64;
            if (now - lastTick < 200) return;
            lastTick = now;
            _window.Dispatcher.BeginInvoke(() =>
            {
                if (_opBusy) _status.ShowText($"Lösche endgültig … {files:N0} Dateien, {SizeFormat.Format(bytes)} (Esc bricht ab)");
            });
        }, ct));
        if (res == null) return;

        TreeUpdate.Removed(scanner, res.Done, RemoveFolderNode);
        TreeUpdate.Vanished(scanner, res.Vanished, RemoveFolderNode);
        await RefreshAfterRemoteChangeAsync(scanner, res.Done.Select(i => i.Path).Concat(res.Partial.Select(i => i.Path)).Concat(res.Vanished));
        RemoteReport("Endgültig gelöscht", items, res);

        // A folder that is only partly gone: its numbers come from a rescan (of the folder above all such folders).
        if (res.Partial.Count > 0 && CommonFolder(scanner, res.Partial) is { } again) _scan.RescanAsync(again).Forget("RescanAsync");
    }

    /// <summary>The deepest folder of the tree that contains all <paramref name="items"/>.</summary>
    private static Node? CommonFolder(Scanner scanner, IReadOnlyList<OpItem> items)
    {
        Node? common = null;
        foreach (var it in items)
        {
            var n = scanner.FindNode(it.Path);
            if (n == null) continue;
            if (common == null) { common = n; continue; }
            var chain = new HashSet<Node>();
            for (var a = n; a != null; a = a.Parent) chain.Add(a);
            while (common != null && !chain.Contains(common)) common = common.Parent;
        }
        return common;
    }

    /// <summary>A folder leaves the tree: with its row if it has one.</summary>
    private void RemoveFolderNode(Node node)
    {
        if (Root?.FindDescendant(node) is { } vm) vm.RemoveFromTree();
        else TreeEditor.RemoveFolder(node);
    }

    // ---------------------------------------------------------------- running

    /// <summary>
    /// Runs a step on the server off the UI thread: scans and other actions are locked meanwhile (<see cref="IsBusy"/>),
    /// Esc cancels (<see cref="CancelOperation"/>). Errors of the connection end up in a message; null then.
    /// </summary>
    /// <param name="progressVerb">For a source that reports objects and bytes (S3 copies): "Kopiere" → "Kopiere … 120 Objekte, 1,2 GB".</param>
    private async Task<T?> RunBusyRemote<T>(string status, Func<CancellationToken, T> work, string? progressVerb = null) where T : class
    {
        using var cts = _opCts = new CancellationTokenSource();
        var provider = Scanner?.Provider;
        // A paid source (S3): the requests of the step go through one run with the cost pause of the settings.
        long threshold = CostsPerRequest ? Math.Max(0, _requestThreshold?.Invoke() ?? 0) : 0;
        var run = new ListRun(cts.Token, threshold);
        run.LimitReached += r => _window.Dispatcher.InvokeAsync(() => AskRequests(r, threshold, cts));
        long lastTick = 0;
        Action<long, long>? progress = progressVerb == null ? null : (objects, bytes) =>
        {
            long now = Environment.TickCount64;
            if (now - Interlocked.Read(ref lastTick) < 200) return;
            Interlocked.Exchange(ref lastTick, now);
            _window.Dispatcher.BeginInvoke(() =>
            {
                if (_opBusy) _status.ShowText($"{progressVerb} … {objects:N0} Objekte, {SizeFormat.Format(bytes)} (Esc bricht ab)");
            });
        };
        _opWriter = provider is IRunBoundWriter bound ? bound.Bind(run, progress) : null;
        _opBusy = true;
        _status.ShowText(status + " (Esc bricht ab)");
        Mouse.OverrideCursor = Cursors.AppStarting;
        try
        {
            return await Task.Run(() => work(cts.Token));
        }
        catch (OperationCanceledException)
        {
            _status.ShowText("Abgebrochen.");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            _status.ShowText("Fehler: " + ex.Message);
            Dialogs.Warn(_window, ex.Message);
            return null;
        }
        finally
        {
            _actionRequests += run.Requests;
            _opWriter = null;
            _opBusy = false;
            _opCts = null;
            Mouse.OverrideCursor = null;
        }
    }

    /// <summary>The cost limit of a step is reached (S3): go on, or cancel (what was copied is still finished cleanly).</summary>
    private void AskRequests(ListRun run, long threshold, CancellationTokenSource cts)
    {
        if (!_opBusy || run.Cancellation.IsCancellationRequested || Scanner?.Provider is not { } p) return;
        bool more = Dialogs.Confirm(_window,
            $"Die Aktion hat bereits {run.Requests:N0} Anfragen an {SourceName(p)} gestellt.\n\n" +
            "Bei Cloud-Speichern kostet jede Anfrage Geld. Die Aktion wartet auf diese Entscheidung.\n\n" +
            $"Weiter? (Die nächste Rückfrage kommt nach weiteren {Math.Max(1, threshold):N0} Anfragen.)\n\n" +
            "Ja = weiter · Nein = abbrechen (ein schon kopiertes Objekt wird noch sauber abgeschlossen)\n\n" +
            "Die Schwelle lässt sich unter „Quellen …“ ändern.",
            MessageBoxImage.Warning);
        if (more) run.Resume(run.Requests + Math.Max(1, threshold));
        else
        {
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { /* the step is over */ }
        }
    }

    /// <summary>
    /// After a remote action: free space of the server (read in the background), tree rows, charts, largest files; the
    /// lists forget what is gone.
    /// </summary>
    private async Task RefreshAfterRemoteChangeAsync(Scanner scanner, IEnumerable<string> gone)
    {
        var paths = scanner.Paths;
        var goneList = gone.ToList();
        if (scanner.Root is { } top && !scanner.IsMulti)
        {
            try { await Task.Run(() => scanner.ReadCapacity(top)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the old numbers stay */ }
        }
        if (Scanner != scanner) return;
        _tree.RefreshRows();
        _views.RefreshViews();
        if (goneList.Count > 0)
            foreach (var s in _sources) s.Forget(p => goneList.Any(g => paths.IsSameOrUnder(g, p)));
    }

    private void RemoteReport(string verb, List<OpItem> items, OpResult res)
    {
        string text = $"{verb}: {res.Done.Count:N0} von {items.Count:N0} ({SizeFormat.Format(res.DoneBytes)})";
        if (res.Skipped.Count > 0) text += $", {res.Skipped.Count:N0} übersprungen";
        if (res.Failed.Count > 0) text += $", {res.Failed.Count:N0} Fehler";
        if (res.Cancelled) text += ", abgebrochen";
        if (CostsPerRequest) text += $" – {_actionRequests:N0} Anfragen";
        _status.ShowText(text + ".");

        var failed = res.Failed.ToList();
        bool deleting = verb.Contains("gelöscht", StringComparison.Ordinal);
        string? hint = res.Partial.Count > 0
            ? deleting
                ? "Nicht alles ließ sich löschen; die betroffenen Ordner werden neu eingelesen."
                : "Nicht alles wurde übertragen: Jedes Objekt liegt vollständig entweder am alten oder am neuen Ort (siehe oben). " +
                  "Die betroffenen Ordner werden neu eingelesen."
            : res.Cancelled ? "Abgebrochen: Was bis dahin erledigt war, bleibt so." : null;
        if (failed.Count > 0 || hint != null) ShowFailures(text + ".", failed, hint);
    }
}
