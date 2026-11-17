using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DiskTree.Core;
using DiskTree.Core.Storage;
using DiskTree.Infrastructure;
using DiskTree.Ui;

namespace DiskTree.Features.Duplicates;

/// <summary>
/// Tab "Duplikate": starts a <see cref="DuplicateFinder"/> run for a folder and lists the groups of identical files.
/// The disk is searched and compared right away, as always. A remote source (S3, SFTP) is listed first; then a dialog
/// shows what comparing would download and cost and lets the user choose the mode (<see cref="DupEstimateDialog"/>).
/// Results of a remote source are marked "sicher" / "wahrscheinlich". A read-only source (S3) offers no file actions,
/// only "Im Baum zeigen" and "Pfad kopieren"; a source that changes files itself (SFTP) gets check marks, Delete (for good,
/// with confirmation) and the host's menu with rename / move / delete.
/// </summary>
public partial class DuplicatesPanel : UserControl
{
    private sealed record MinOption(string Text, long Bytes);

    private static readonly MinOption[] MinOptions =
    {
        new("Alle Größen", 1),
        new("100 KB", 100L << 10),
        new("1 MB", 1L << 20),
        new("10 MB", 10L << 20),
        new("100 MB", 100L << 20),
    };

    private CancellationTokenSource? _cts;
    private readonly ObservableCollection<DupGroupVM> _groups = new();

    /// <summary>The source the shown groups come from (null: none / the disk).</summary>
    private IStorageProvider? _shownSource;

    /// <summary>Folder to search: the folder selected in the tree (set by the main window).</summary>
    public Func<string?>? RootProvider { get; set; }

    /// <summary>The storage source of the active scan (null: the disk).</summary>
    public Func<IStorageProvider?>? SourceProvider { get; set; }

    /// <summary>For remembering the minimum size and the download limit.</summary>
    public AppSettings? Settings { get; set; }

    /// <summary>The user asked for the shell context menu of a file (path, size, screen position). The host shows it.</summary>
    public event Action<string, long, Point>? ShellMenuRequested;

    /// <summary>The user pressed Delete on a file (path, size). The host runs the shell's delete.</summary>
    public event Action<string, long>? ShellDeleteRequested;

    /// <summary>Menu of a file of a remote source whose files can be changed (SFTP): the host shows its own menu (path, size, position).</summary>
    public event Action<string, long, Point>? RemoteMenuRequested;

    /// <summary>"Im Baum zeigen" for a file of a remote source: the host selects its folder; false if the tree does not have it.</summary>
    public Func<string, bool>? RevealRequested { get; set; }

    /// <summary>Keys the host handles for the selection (F2, Delete on several files); returns true if it used the key.</summary>
    public Func<Key, bool>? ExtraKeyDown { get; set; }

    /// <summary>A search (listing, comparing, or the connection before it) is running.</summary>
    public bool IsRunning => _cts != null;

    /// <summary>Stops a running search (Esc): ends running downloads as well.</summary>
    public void Cancel() => CancelRun();

    /// <summary>
    /// Cancels without waiting for the callbacks (closing a connection that hangs must not freeze the window); the
    /// search itself ends at once.
    /// </summary>
    private void CancelRun()
    {
        if (_cts is { IsCancellationRequested: false } cts) _ = cts.CancelAsync();
    }

    /// <summary>The shown results come from a remote source: no file actions.</summary>
    private bool ShowsRemote => _shownSource != null && _shownSource.Id != DiskProvider.ProviderId;

    /// <summary>The shown results come from a remote source whose files can be deleted (SFTP): check marks, Delete, actions.</summary>
    private bool ShowsWritable => ShowsRemote && CanChange(_shownSource!);

    private static bool CanChange(IStorageProvider p) => StorageFeatures.IsRemoteWritable(p) && StorageFeatures.Has(p, StorageFeature.Delete);

    /// <summary>The files with a check mark; if none is checked, the selected file.</summary>
    public IReadOnlyList<(string Path, long Size)> SelectedFiles()
    {
        var checkedFiles = _groups.SelectMany(g => g.Files).Where(f => f.IsChecked).Select(f => (f.Path, f.Group.Size)).ToList();
        if (checkedFiles.Count > 0) return checkedFiles;
        return List.SelectedItem is DupFileVM sel ? new[] { (sel.Path, sel.Group.Size) } : [];
    }

    /// <summary>True if the given paths contain every copy of at least one duplicate group.</summary>
    public bool AllCopiesChecked(IEnumerable<string> paths)
    {
        var set = new HashSet<string>(paths, ShowsRemote ? _shownSource!.Paths.Comparer : StringComparer.OrdinalIgnoreCase);
        return _groups.Any(g => g.Files.Count > 0 && g.Files.All(f => set.Contains(f.Path)));
    }

    /// <summary>Drops files that are gone from disk (after move / delete / rename), and groups that are no duplicates any more.</summary>
    public void PruneMissing()
    {
        if (ShowsRemote) return; // nothing is changed on a remote source (and File.Exists does not know its paths)
        foreach (var f in _groups.SelectMany(g => g.Files).ToList()) Prune(f);
    }

    /// <summary>Files of a remote source that an action deleted, moved or renamed leave their groups (a group of one goes).</summary>
    public void ForgetPaths(Func<string, bool> gone)
    {
        if (!ShowsRemote) return;
        foreach (var f in _groups.SelectMany(g => g.Files).Where(f => gone(f.Path)).ToList()) RemoveFile(f);
    }

    /// <summary>The host switched to another scan: stop a running search and drop the groups of the old folder.</summary>
    public void Reset()
    {
        CancelRun();
        _groups.Clear();
        _shownSource = null;
        InfoText.Text = "";
        if (IsVisible) ShowTarget();
    }

    public DuplicatesPanel()
    {
        InitializeComponent();
        foreach (var o in MinOptions) MinBox.Items.Add(o);
        MinBox.SelectedItem = MinOptions[2];
        List.ItemsSource = _groups;
        ListInteraction.Attach(List, new ListBehavior<DupFileVM>
        {
            Selected = () => List.SelectedItem as DupFileVM,
            ExtraKey = (key, _) => ExtraKeyDown?.Invoke(key) == true,
            // Read-only remote sources (S3): Delete does nothing there. SFTP: Delete asks and deletes for good (host).
            CanDelete = _ => !ShowsRemote || ShowsWritable,
            Delete = file =>
            {
                ShellDeleteRequested?.Invoke(file.Path, file.Group.Size);
                Prune(file);
            },
            ShowMenu = ShowMenu,
            CopyText = file => file.Path,
        });
        Loaded += (_, _) =>
        {
            // Settings arrive after construction.
            if (Settings is { } s && MinOptions.FirstOrDefault(o => o.Bytes == s.DuplicateMinBytes) is { } m)
                MinBox.SelectedItem = m;
            MinBox.SelectionChanged -= MinBox_Changed;
            MinBox.SelectionChanged += MinBox_Changed;
        };
        IsVisibleChanged += (_, _) => { if (IsVisible && _cts == null) ShowTarget(); };
    }

    private void MinBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Settings != null && MinBox.SelectedItem is MinOption m) Settings.DuplicateMinBytes = m.Bytes;
    }

    private IStorageProvider Source => SourceProvider?.Invoke() ?? DiskProvider.Instance;

    private void ShowTarget()
    {
        var root = RootProvider?.Invoke();
        TargetText.Text = string.IsNullOrEmpty(root) ? "Duplikate" : "Duplikate – " + (Source.Paths.NameOf(root) is { Length: > 0 } n ? n : root);
        TargetText.ToolTip = root;
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null) { CancelRun(); return; }

        var source = Source;
        if (source.Id != DiskProvider.ProviderId)
        {
            await RunRemoteAsync(source);
            return;
        }

        var root = RootProvider?.Invoke();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            InfoText.Text = "Kein Ordner zum Durchsuchen. Bitte zuerst einen Ordner scannen und markieren.";
            return;
        }
        ShowTarget();

        bool network = DuplicateFinder.IsNetworkPath(root);
        if (network && !Dialogs.Confirm(Window.GetWindow(this),
                "Der Ordner liegt auf einem Netzlaufwerk. Die Suche liest dort viele Dateien und kann lange dauern " +
                "und das Netzwerk belasten. Sie läuft deshalb nur mit wenigen parallelen Zugriffen.\n\nTrotzdem suchen?"))
            return;

        long min = (MinBox.SelectedItem as MinOption)?.Bytes ?? 1L << 20;
        int parallelism = network ? 2 : Math.Clamp(Environment.ProcessorCount / 2, 2, 6);

        _groups.Clear();
        _shownSource = source;
        _cts = new CancellationTokenSource();
        RunButton.Content = "Abbrechen";
        MinBox.IsEnabled = false;
        Bar.Visibility = Visibility.Visible;
        Bar.IsIndeterminate = true;
        InfoText.Text = "Dateien werden aufgelistet …";

        var finder = new DuplicateFinder();
        var sw = Stopwatch.StartNew();
        var progress = new Progress<DupProgress>(OnProgress);
        try
        {
            var found = await finder.FindAsync(root, min, parallelism, progress, _cts.Token);
            foreach (var (g, i) in found.Select((g, i) => (g, i)))
                _groups.Add(new DupGroupVM(g) { IsExpanded = i < 15 });

            long wasted = found.Sum(g => g.Wasted);
            InfoText.Text = found.Count == 0
                ? $"Keine Duplikate gefunden ({sw.Elapsed:mm\\:ss}, {finder.Errors:N0} nicht lesbar)."
                : $"{found.Count:N0} Gruppen · {SizeFormat.Format(wasted)} verschwendet ({sw.Elapsed:mm\\:ss}" +
                  (finder.Errors > 0 ? $", {finder.Errors:N0} nicht lesbar" : "") + ")";
        }
        catch (OperationCanceledException)
        {
            InfoText.Text = "Abgebrochen.";
        }
        catch (Exception ex)
        {
            InfoText.Text = "Fehler: " + ex.Message;
        }
        finally
        {
            EndRun();
        }
    }

    private void EndRun()
    {
        _cts?.Dispose();
        _cts = null;
        RunButton.Content = "Duplikate suchen";
        MinBox.IsEnabled = true;
        Bar.Visibility = Visibility.Collapsed;
    }

    // ---------------------------------------------------------------- remote sources

    /// <summary>"S3 „Backup“" / "SFTP user@host": the source as the user knows it.</summary>
    private static string SourceName(IStorageProvider p) => p.Profile is { } pr ? $"{p.DisplayName} „{pr.Name}“" : p.DisplayName;

    private bool _costs;

    /// <summary>
    /// Connect (SFTP), list, show the estimate and let the user choose the mode, then compare. Esc / "Abbrechen" ends
    /// every step, also downloads in flight (the token goes into the client; SFTP: the connection's requests are aborted).
    /// </summary>
    private async Task RunRemoteAsync(IStorageProvider source)
    {
        var root = RootProvider?.Invoke();
        if (string.IsNullOrEmpty(root))
        {
            InfoText.Text = "Kein Ordner zum Durchsuchen. Bitte zuerst einen Ordner scannen und markieren.";
            return;
        }
        if (source is not IContentHashSource)
        {
            InfoText.Text = StorageFeatures.WhyNot(source, StorageFeature.Duplicates) ?? "Nicht verfügbar.";
            return;
        }
        ShowTarget();

        long min = (MinBox.SelectedItem as MinOption)?.Bytes ?? 1L << 20;
        _costs = (source.Caps & StorageCaps.CostsPerRequest) != 0;
        var window = Window.GetWindow(this);

        _groups.Clear();
        _shownSource = source;
        var cts = _cts = new CancellationTokenSource();
        RunButton.Content = "Abbrechen";
        MinBox.IsEnabled = false;
        Bar.Visibility = Visibility.Visible;
        Bar.IsIndeterminate = true;

        long threshold = Math.Max(0, Settings?.RequestWarnThreshold ?? 0);
        var run = new ListRun(cts.Token, _costs ? threshold : 0);
        run.LimitReached += r => Dispatcher.InvokeAsync(() => AskRequests(r, threshold, source));

        var finder = new DuplicateFinder();
        var sw = Stopwatch.StartNew();
        var progress = new Progress<DupProgress>(OnProgress);
        // Esc / "Abbrechen" also ends the requests on the source's connection right now (SFTP).
        using var abort = (source as IConnectedStorage)?.CancelWith(cts.Token);
        try
        {
            if (source is IConnectedStorage connected)
            {
                InfoText.Text = $"Verbinde mit {SourceName(source)} …";
                await Task.Run(() => connected.Connect(cts.Token), cts.Token);
            }

            InfoText.Text = $"Dateien werden von {SourceName(source)} aufgelistet …";
            var candidates = await DuplicateFinder.CollectAsync(source, root, min, run, progress);
            if (candidates.SameSize.Count == 0)
            {
                InfoText.Text = $"Keine Duplikate: keine zwei Dateien gleicher Größe ({candidates.FilesListed:N0} Dateien" +
                                (candidates.Errors > 0 ? $", {candidates.Errors:N0} Ordner nicht lesbar" : "") +
                                RequestsText(run) + ").";
                return;
            }

            var estimate = DuplicateFinder.Estimate(candidates);
            Bar.IsIndeterminate = false;
            Bar.Value = 0;
            InfoText.Text = $"{candidates.Files:N0} Dateien gleicher Größe gefunden – bitte Modus wählen …";
            var dlg = new DupEstimateDialog(SourceName(source), root, candidates, estimate,
                Settings?.DuplicateDownloadLimit ?? 2L << 30) { Owner = window };
            if (dlg.ShowDialog() != true || cts.IsCancellationRequested)
            {
                InfoText.Text = "Abgebrochen (vor dem Vergleich)" + RequestsText(run).Replace(", ", " – ") + ".";
                return;
            }
            if (Settings != null) Settings.DuplicateDownloadLimit = dlg.Limit;
            var mode = dlg.Mode;

            var budget = new DownloadBudget(dlg.Limit, (used, limit) => Dispatcher.InvokeAsync(() => AskDownloadLimit(used, limit, dlg.Limit)).Task);
            sw.Restart();
            var found = await finder.FindAsync(candidates, mode, source.ScanThreads is > 8 ? 8 : 4, run, budget, progress);

            foreach (var (g, i) in found.Select((g, i) => (g, i)))
                _groups.Add(new DupGroupVM(g, source.Paths, remote: true, canCheck: CanChange(source)) { IsExpanded = i < 15 });
            InfoText.Text = RemoteSummary(found, finder, run, sw.Elapsed, mode);
        }
        catch (OperationCanceledException)
        {
            InfoText.Text = "Abgebrochen" + (finder.BytesRead > 0 ? $" ({SizeFormat.Format(finder.BytesRead)} heruntergeladen" + RequestsText(run) + ")" : "") + ".";
        }
        catch (Exception ex)
        {
            InfoText.Text = "Fehler: " + ex.Message;
        }
        finally
        {
            EndRun();
        }
    }

    private string RequestsText(ListRun run) => _costs ? $", {run.Requests:N0} Anfragen" : "";

    private string RemoteSummary(List<DuplicateGroup> found, DuplicateFinder finder, ListRun run, TimeSpan elapsed, DupMode mode)
    {
        int probable = found.Count(g => !g.IsCertain);
        string tail = $"{SizeFormat.Format(finder.BytesRead)} heruntergeladen" + RequestsText(run) +
                      (finder.Errors > 0 ? $", {finder.Errors:N0} nicht lesbar" : "") +
                      (finder.Skipped > 0 ? $", {finder.Skipped:N0} wegen Download-Limit nicht verglichen" : "") +
                      $", {elapsed:mm\\:ss}";
        if (found.Count == 0) return $"Keine Duplikate gefunden ({tail}).";
        long wasted = found.Sum(g => g.Wasted);
        return $"{found.Count:N0} Gruppen · {SizeFormat.Format(wasted)} verschwendet" +
               (probable > 0 ? $" · {probable:N0} davon nur wahrscheinlich" : "") +
               $" ({(mode == DupMode.Fast ? "Schnell" : "Gründlich")}, {tail})";
    }

    /// <summary>The cost limit of the source (S3) was reached: go on (next question after as many again) or stop.</summary>
    private void AskRequests(ListRun run, long threshold, IStorageProvider source)
    {
        if (run.Cancellation.IsCancellationRequested || _cts == null) return;
        bool more = Dialogs.Confirm(Window.GetWindow(this),
            $"Die Duplikatsuche hat bereits {run.Requests:N0} Anfragen an {SourceName(source)} gestellt.\n\n" +
            "Bei Cloud-Speichern kostet jede Anfrage Geld. Die Suche wartet auf diese Entscheidung.\n\n" +
            $"Weiter suchen? (Die nächste Rückfrage kommt nach weiteren {Math.Max(1, threshold):N0} Anfragen.)\n\n" +
            "Ja = weiter · Nein = Suche abbrechen\n\nDie Schwelle lässt sich unter „Quellen …“ ändern.",
            MessageBoxImage.Warning);
        if (more) run.Resume(run.Requests + Math.Max(1, threshold));
        else CancelRun();
    }

    /// <summary>The download limit of the run is reached: download more (the limit grows by its amount again) or stop downloading.</summary>
    private bool AskDownloadLimit(long used, long limit, long step)
    {
        if (_cts == null || _cts.IsCancellationRequested) return false;
        return Dialogs.Confirm(Window.GetWindow(this),
            $"Das Download-Limit dieses Laufs ist erreicht: {SizeFormat.Format(used)} von {SizeFormat.Format(limit)} sind heruntergeladen " +
            "oder vorgemerkt, der nächste Vergleich bräuchte mehr.\n\n" +
            $"Weiter herunterladen? (Das Limit steigt um {SizeFormat.Format(step)}.)\n\n" +
            "Ja = weiter · Nein = nichts mehr herunterladen; was bis jetzt verglichen ist, wird angezeigt.",
            MessageBoxImage.Warning);
    }

    private void OnProgress(DupProgress p)
    {
        if (_cts == null) return; // a late report after the run ended must not overwrite the result
        string req = _costs && p.Requests > 0 ? $" · {p.Requests:N0} Anfragen" : "";
        switch (p.Phase)
        {
            case DupPhase.Enumerating:
                Bar.IsIndeterminate = true;
                InfoText.Text = ShowsRemote
                    ? $"Dateien werden aufgelistet … {p.FilesDone:N0} ({p.Current}){req}"
                    : $"Dateien werden aufgelistet … {p.FilesDone:N0}";
                break;
            case DupPhase.Sizes:
                InfoText.Text = $"{p.FilesDone:N0} Dateien nach Größe sortiert …";
                break;
            case DupPhase.Tags:
                Bar.IsIndeterminate = false;
                Bar.Value = p.FilesTotal > 0 ? (double)p.FilesDone / p.FilesTotal * 0.2 : 0;
                InfoText.Text = $"Prüfe ETags: {p.FilesDone:N0} / {p.FilesTotal:N0} Dateien{req}";
                break;
            case DupPhase.PartialHash:
                Bar.IsIndeterminate = false;
                Bar.Value = p.FilesTotal > 0 ? (double)p.FilesDone / p.FilesTotal * 0.3 : 0;
                InfoText.Text = ShowsRemote
                    ? $"Vergleiche Anfang und Ende: {p.FilesDone:N0} / {p.FilesTotal:N0} Dateien · {SizeFormat.Format(p.BytesDone)} / {SizeFormat.Format(p.BytesTotal)}{req}"
                    : $"Vergleiche Dateianfänge: {p.FilesDone:N0} / {p.FilesTotal:N0}";
                break;
            case DupPhase.FullHash:
                Bar.IsIndeterminate = false;
                Bar.Value = 0.3 + (p.BytesTotal > 0 ? (double)p.BytesDone / p.BytesTotal * 0.7 : 0);
                InfoText.Text = ShowsRemote
                    ? $"Lade und prüfe Inhalt: {SizeFormat.Format(p.BytesDone)} / {SizeFormat.Format(p.BytesTotal)} · {p.FilesDone:N0} / {p.FilesTotal:N0} Dateien{req}"
                    : $"Prüfe Inhalt: {SizeFormat.Format(p.BytesDone)} / {SizeFormat.Format(p.BytesTotal)}";
                break;
        }
    }

    // ---------------------------------------------------------------- context menu / keys

    private void List_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (VisualTreeHelpers.FindAncestor<TreeViewItem>(e.OriginalSource)?.DataContext is DupFileVM file)
        {
            e.Handled = true;
            if (ShowsRemote) Reveal(file);
            else ShellOpen.InExplorer(file.Path);
        }
    }

    private void ShowMenu(DupFileVM file, Point screenPos)
    {
        if (ShowsWritable && RemoteMenuRequested != null)
        {
            RemoteMenuRequested(file.Path, file.Group.Size, screenPos);
            return;
        }
        if (ShowsRemote)
        {
            ShowRemoteMenu(file, _shownSource!);
            return;
        }
        ShellMenuRequested?.Invoke(file.Path, file.Group.Size, screenPos);
        Prune(file);
    }

    private void Reveal(DupFileVM file)
    {
        if (RevealRequested?.Invoke(file.Path) != true)
            InfoText.Text = "Der Ordner dieser Datei ist im Baum nicht (mehr) vorhanden.";
    }

    /// <summary>
    /// Menu of a file of a remote source: show its folder in the tree, copy the path; the Windows actions are listed
    /// greyed out with the reason (the source is read-only).
    /// </summary>
    private void ShowRemoteMenu(DupFileVM file, IStorageProvider source)
    {
        var menu = new ContextMenu { PlacementTarget = List };
        void Add(string text, Action? run, string? whyNot = null)
        {
            var item = new MenuItem { Header = text, IsEnabled = run != null && whyNot == null, ToolTip = whyNot };
            ToolTipService.SetShowOnDisabled(item, true);
            if (run != null) item.Click += (_, _) => run();
            menu.Items.Add(item);
        }

        Add("Im Baum zeigen", () => Reveal(file));
        Add("Pfad kopieren", () => Clipboard.SetText(file.Path));
        menu.Items.Add(new Separator());
        Add("Übergeordneten Ordner öffnen", null, StorageFeatures.WhyNot(source, StorageFeature.Explorer));
        Add("Löschen (Papierkorb)", null, StorageFeatures.WhyNot(source, StorageFeature.Delete));
        Add("Windows-Kontextmenü", null, StorageFeatures.WhyNot(source, StorageFeature.ShellMenu));
        menu.IsOpen = true;
    }

    // The menu may have deleted or moved the file: if it is gone, it leaves the group (a group of one is no duplicate any more).
    private void Prune(DupFileVM file)
    {
        if (ShowsRemote || File.Exists(file.Path)) return;
        RemoveFile(file);
    }

    private void RemoveFile(DupFileVM file)
    {
        var group = file.Group;
        group.Files.Remove(file);
        if (group.Files.Count < 2) _groups.Remove(group);
        else group.Refresh();

        long wasted = _groups.Sum(g => g.Wasted);
        InfoText.Text = _groups.Count == 0 ? "Keine Duplikate mehr." : $"{_groups.Count:N0} Gruppen · {SizeFormat.Format(wasted)} verschwendet";
    }
}
