using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DiskTree.Core;
using DiskTree.Core.Storage;
using DiskTree.Features.Scan;
using DiskTree.Infrastructure;

namespace DiskTree.Ui.Controllers;

/// <summary>What a running scan asks of the views that show its results (file types, age, treemap, largest files).</summary>
public interface IScanViews
{
    /// <summary>A new scan starts: the list of the largest files of the old one goes.</summary>
    void ClearTopFiles();
    /// <summary>The timestamp the active tab's numbers are built from (or its choice) may have changed.</summary>
    void AgeNoticesChanged();
    /// <summary>Charts and the largest files for the scan of the active tab.</summary>
    void RefreshViews();
    /// <summary>While the scan runs: like <see cref="RefreshViews"/>, but only if the last update is a while ago.</summary>
    void RefreshLive();
}

/// <summary>
/// Runs the scans: a scan of a folder or drive, a rescan of one folder of the tree, the status the scanner reports
/// while it works, and the end (finished or cancelled). It always works on one <see cref="ScanSession"/> and only
/// touches window controls while that session is the active tab: the user may switch tabs while the scan runs.
/// Only one scan runs at a time across all tabs (see <see cref="ScanTabsController"/>).
/// </summary>
public sealed class ScanController
{
    // After a change notification the UI waits this long before redrawing, so bursts collapse into one update.
    private static readonly TimeSpan RefreshDelay = TimeSpan.FromMilliseconds(120);

    private readonly Window _window;
    private readonly AppSettings _settings;
    private readonly WindowSettings _winSettings;
    private readonly ScanTabsController _tabs;
    private readonly ScanStatusView _status;
    private readonly TreeController _tree;
    private readonly SnapshotController _snapshots;
    private readonly IScanViews _views;
    private readonly ComboBox _pathBox;
    private readonly ComboBox _threadsBox;

    public ScanController(Window window, AppSettings settings, WindowSettings winSettings, ScanTabsController tabs,
        ScanStatusView status, TreeController tree, SnapshotController snapshots, IScanViews views,
        ComboBox pathBox, ComboBox threadsBox, Button scanButton)
    {
        _window = window;
        _settings = settings;
        _winSettings = winSettings;
        _tabs = tabs;
        _status = status;
        _tree = tree;
        _snapshots = snapshots;
        _views = views;
        _pathBox = pathBox;
        _threadsBox = threadsBox;
        scanButton.Click += async (_, _) => await ScanOrCancelAsync();
    }

    private ScanSession Cur => _tabs.Current;

    /// <summary>
    /// A file operation (or the updater) works on the tree: a scan would change the same nodes at the same time.
    /// Tells the user and returns true.
    /// </summary>
    private bool BlockedByOperation()
    {
        if (!_tabs.TabsLocked) return false;
        Dialogs.Info(_window, "Eine Dateiaktion (oder ein Update) läuft gerade. Ein Scan ist erst danach möglich.");
        return true;
    }

    /// <summary>A scan or rescan of the active tab runs.</summary>
    public bool IsRunning => Cur.IsRunningOrPending;

    /// <summary>Stops the scan of the active tab (it finishes as "Abgebrochen").</summary>
    public void Cancel() => Cur.Cts?.Cancel();

    // The scan button: "Scannen" starts, "Abbrechen" cancels.
    private async Task ScanOrCancelAsync()
    {
        if (IsRunning) { Cancel(); return; }
        await BeginScan();
    }

    /// <summary>F5: a selected folder is scanned again in place (like Explorer's F5 for the current view), otherwise everything.</summary>
    public void RescanOrScan(Node? selectedFolder)
    {
        if (selectedFolder != null)
            RescanAsync(selectedFolder).Forget("RescanAsync");
        else
            BeginScan().Forget("BeginScan");
    }

    /// <summary>Scans what the path box says, in the active tab.</summary>
    public Task BeginScan()
    {
        var s = Cur;
        return s.ScanTask = StartScan(s);
    }

    /// <summary>Scans another folder (in a new tab if asked); a running scan of the tab is cancelled first.</summary>
    public async Task ScanPathAsync(string path, bool newTab = false)
    {
        if (BlockedByOperation() || _snapshots.BlockedByLoad()) return;
        if (_tabs.Busy is { } other && (newTab || other != Cur)) { _tabs.NoticeBusy(other); return; }
        if (newTab && !Cur.IsEmpty) _tabs.NewTab();

        var s = Cur;
        if (s.Pending) return;
        if (s.Cts != null)
        {
            s.Pending = true; // nothing may start in this tab while the old scan stops
            try
            {
                s.Cts.Cancel();
                await s.ScanTask;
            }
            finally { s.Pending = false; }
        }
        if (Cur != s) _tabs.Activate(s); // the user switched tabs while the old scan stopped
        _pathBox.Text = path;
        await BeginScan();
    }

    private async Task StartScan(ScanSession s)
    {
        if (s.IsRunningOrPending) return;
        if (BlockedByOperation() || _snapshots.BlockedByLoad()) return;
        if (_tabs.Busy is { } other) { _tabs.NoticeBusy(other); return; }

        // The path box (and PathList) only knows the local file system so far; other sources get their own way in.
        var provider = StorageProviders.Disk;
        var paths = PathList.Parse(_pathBox.Text);
        if (paths.Count == 0) paths.Add(""); // reports "Pfad nicht gefunden" like before
        var missing = paths.Where(p => !provider.FolderExists(p)).ToList();
        if (missing.Count > 0)
        {
            Dialogs.Warn(_window, $"Pfad nicht gefunden:\n{string.Join("\n", missing)}");
            return;
        }
        paths = PathList.Reduce(paths);
        var path = paths[0];
        _settings.LastPath = PathList.Join(paths);

        await RunAsync(s, ct =>
        {
            var scanner = s.Scanner = new Scanner { Provider = provider, Excludes = _winSettings.CurrentExcludes(), AgeStamp = s.AgePreferred };
            _snapshots.OnNewScanner(scanner);
            // Event instead of polling: the scanner reports changes from its worker threads, the UI reacts.
            scanner.Changed += () => _window.Dispatcher.InvokeAsync(() => OnScannerChanged(s, scanner), DispatcherPriority.Background);
            s.Root = null;
            s.ScanTarget = null;
            s.SelectedVm = null;
            _tree.Clear();
            _views.ClearTopFiles();

            // ScanAsync creates the tree (drive -> ... -> scanned folder) and starts the workers synchronously,
            // so the tree can be shown right away and fills up while the scan is running.
            int threads = (int)_threadsBox.SelectedItem;
            var scan = paths.Count > 1
                ? scanner.ScanManyAsync(paths, threads, ct)
                : scanner.ScanAsync(path, threads, ct);
            _window.Dispatcher.InvokeAsync(_views.AgeNoticesChanged); // the scan has adopted the tab's choice by now
            SetTabTitle(s, paths);
            _tree.ShowScan(s);
            return scan;
        });
    }

    /// <summary>Scans one folder of the existing tree again, in place (nothing happens without a scan).</summary>
    public Task RescanAsync(Node? node)
    {
        var scanner = Cur.Scanner;
        if (node == null || scanner == null) return Task.CompletedTask;
        if (scanner.IsMulti && node == scanner.Root) return BeginScan(); // the artificial root: scan all paths again
        var s = Cur;
        return s.ScanTask = StartRescan(s, node);
    }

    /// <summary>
    /// Scans one folder of the existing tree again, in place. Its row and everything above it get new numbers,
    /// the rest of the tree is untouched.
    /// </summary>
    private async Task StartRescan(ScanSession s, Node node)
    {
        var scanner = s.Scanner!;
        if (BlockedByOperation() || _snapshots.BlockedByLoad() || !_snapshots.CanRescan(node)) return;
        if (_tabs.Busy is { } other && other != s) { _tabs.NoticeBusy(other); return; }

        // A running scan has to stop first. (s.ScanTask still is the old scan here: this method runs
        // synchronously up to the first await, before RescanAsync assigns its own task.)
        if (s.Pending) return; // another scan/rescan already waits for the old one to stop
        if (s.Cts != null)
        {
            s.Pending = true; // F5, the scan button and drops see the tab as busy while the old scan stops
            try
            {
                s.Cts.Cancel();
                await s.ScanTask;
            }
            finally { s.Pending = false; }
        }

        await RunAsync(s, ct =>
        {
            // The previous scan's last change event may never have been acknowledged (the UI had already
            // finished); without this the scanner stays silent and the row would not update.
            scanner.Acknowledge();
            scanner.Excludes = _winSettings.CurrentExcludes(); // the same rules as the scan (incl. changes made since)
            var scan = scanner.RescanAsync(node, (int)_threadsBox.SelectedItem, ct);
            s.Root?.FindDescendant(node)?.ResetForRescan();
            s.ScanTarget = scanner.ScanTarget;
            if (s == Cur) s.Root?.Refresh(); // spinner and "…" right away
            return scan;
        });
    }

    /// <summary>
    /// Shared by scan and rescan: <paramref name="begin"/> starts the scan, sets the UI up and returns the scan task;
    /// this method shows the progress and the result. It only touches window controls while <paramref name="s"/> is
    /// the active tab: the user may switch tabs while the scan runs.
    /// </summary>
    private async Task RunAsync(ScanSession s, Func<CancellationToken, Task> begin)
    {
        var cts = s.Cts = new CancellationTokenSource();
        if (s == Cur) _status.ShowScanButton(true);
        s.Sw.Restart();

        try
        {
            await begin(cts.Token);
            s.Sw.Stop();
            FinishScan(s, canceled: false);
        }
        catch (OperationCanceledException)
        {
            s.Sw.Stop();
            FinishScan(s, canceled: true);
        }
        finally
        {
            cts.Dispose();
            s.Cts = null;
            s.CurrentLine = "";
            s.ProgressVisible = false;
            if (s == Cur) _status.ShowIdle();
            _status.UpdateTaskbar();
        }
    }

    private void FinishScan(ScanSession s, bool canceled)
    {
        if (s.Scanner == null) return; // the tab was closed meanwhile
        bool active = s == Cur;
        if (active) s.Root?.Refresh(); // an inactive tab catches up when it is shown
        UpdateStatus(s, running: false, done: !canceled);
        if (canceled) _status.SetStatus(s, "Abgebrochen – " + s.Status);
        if (!active) return;
        _views.RefreshViews();
    }

    private async Task OnScannerChanged(ScanSession s, Scanner scanner)
    {
        // Let more changes pile up, then acknowledge first so anything arriving during the redraw
        // raises a new event instead of being lost.
        await Task.Delay(RefreshDelay);
        if (scanner != s.Scanner) return;
        if (s.Root == null || s.Cts == null)
        {
            scanner.Acknowledge(); // nothing to show now, but do not swallow the next scan's events
            return;
        }

        scanner.Acknowledge();
        UpdateStatus(s, running: true);
        if (s != Cur) return; // a background tab only keeps its numbers; the tree is brought up to date when shown

        s.Root.Refresh();

        // Chart and top list have to walk / copy a lot, so they are refreshed less often than the tree.
        _views.RefreshLive();
    }

    private static string ExcludedTooltip(Scanner s)
    {
        var paths = s.ExcludedPaths();
        var text = "Ausgeschlossene Ordner (nicht gescannt):\n" + string.Join("\n", paths.Take(15));
        return s.Excluded > 15 ? text + $"\n… und {s.Excluded - 15:N0} weitere" : text;
    }

    /// <summary>Computes the status line of a session from its scanner and stores it there; the window shows it if the tab is active.</summary>
    private void UpdateStatus(ScanSession session, bool running, bool done = false)
    {
        if (session.Scanner == null) return;
        var s = session.Scanner;

        bool known = s.ExpectedBytes > 0;
        double fraction = known ? Math.Min(0.99, (double)s.Bytes / s.ExpectedBytes) : 0;
        string percent = running && known ? $" · {fraction * 100:0} %" : "";

        session.Status =
            $"{(done ? "Fertig: " : "")}{s.Dirs:N0} Ordner · {s.Files:N0} Dateien · {SizeFormat.Format(s.Bytes)}" +
            $" · {s.Errors:N0} nicht lesbar" + (s.Excluded > 0 ? $" · {s.Excluded:N0} ausgeschlossen" : "") +
            $" · {session.Sw.Elapsed:mm\\:ss\\.f}{percent}";
        session.StatusTip = s.Excluded > 0 ? ExcludedTooltip(s) : null;
        session.CurrentLine = running ? s.Current : "";

        // A drive scan knows roughly how much there is to find (used space); a folder scan does not.
        session.ProgressVisible = running;
        session.ProgressKnown = known;
        session.ProgressFraction = fraction;

        if (session == Cur) _status.Apply(session);
        else _status.UpdateTaskbar();
    }

    /// <summary>Tab title and tooltip from what was scanned.</summary>
    private static void SetTabTitle(ScanSession s, IReadOnlyList<string> paths)
    {
        string tip = PathList.Join(paths);
        s.SetTitle(paths.Count > 1 ? $"{paths.Count} Pfade" : paths[0], tip);
    }
}
