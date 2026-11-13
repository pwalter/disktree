using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DiskTree.Core;
using DiskTree.Core.Snapshots;
using DiskTree.Features.Scan;
using DiskTree.Infrastructure;
using DiskTree.Ui.Controllers;
using Microsoft.Win32;

namespace DiskTree.Ui;

public partial class MainWindow : Window, IExportSource
{
    // After a change notification the UI waits this long before redrawing, so bursts collapse into one update.
    private static readonly TimeSpan RefreshDelay = TimeSpan.FromMilliseconds(120);

    private readonly AppSettings _settings = AppSettings.Load();
    private WindowSettings _winSettings = null!;
    private AdminStartup _admin = null!;
    private UpdateController _updater = null!;
    private ShellMenuCoordinator _shell = null!;
    private ScanTabsController _tabs = null!;
    private ScanStatusView _status = null!;
    private TreeController _tree = null!;

    // transitional: what the other partial files still use, all pointing at the active scan tab
    private ScanSession _cur => _tabs.Current;
    private Scanner? _scanner { get => _cur.Scanner; set => _cur.Scanner = value; }
    private NodeVM? _root { get => _cur.Root; set => _cur.Root = value; }
    private Node? _scanTarget { get => _cur.ScanTarget; set => _cur.ScanTarget = value; }
    private CancellationTokenSource? _cts { get => _cur.Cts; set => _cur.Cts = value; }
    private ScanSession? Busy => _tabs.Busy;
    private bool TabsLocked => _tabs.TabsLocked;
    private void Notice(string text) => Dialogs.Info(this, text);

    // The order is the dependency order: every step only uses what the steps above it created.
    //   toolbar (path, threads) -> scan tabs -> snapshots -> shell menus (need snapshots) -> panels that use the
    //   shell menus -> side panel (chips, charts; the age panel needs the settings and the active tab) -> app buttons.
    public MainWindow()
    {
        InitializeComponent();

        InitToolbar();
        _tabs = new ScanTabsController(this, ScanTabBar, () => _opBusy || _updater.IsUpdating, () => PathBox.Focus());
        _tabs.Leaving += OnLeavingSession;
        _tabs.Shown += ShowSession;
        _status = new ScanStatusView(_tabs, ScanButton, StatusText, CurrentText, ScanProgress, TaskbarInfo);
        _tree = new TreeController(Tree, Treemap, _tabs);
        _tree.SelectionChanged += () =>
        {
            _ = RefreshChartsAsync();
            OwnerTab.SetFolder(_tree.CurrentChartTarget().Node?.FullPath);
        };
        InitSnapshots();
        _shell = new ShellMenuCoordinator(this, this, _snapshots);
        InitSearch();
        InitOps();
        InitDuplicates();
        InitListInteraction();

        Theme.Changed += () => { _tree.RefreshTheme(); _ = RefreshChartsAsync(); };
        InitTabChips();
        InitAge();

        _ = new ExportController(this, ExportButton, this);
        _updater = new UpdateController(this, _settings, UpdateButton, AutoUpdateItem, CheckUpdateItem, OpenReleasesItem,
            () => PathBox.Text, () => Busy != null);
        _ = new AppMenuController(this, _settings, VersionMenu, ExplorerItem, PortableItem);

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    /// <summary>Window settings and admin button, then the path box (drives, last path) and the thread count.</summary>
    private void InitToolbar()
    {
        _winSettings = new WindowSettings(this, _settings, ThemeButton, ExcludeButton);
        _winSettings.ApplyBounds();

        foreach (var d in DriveInfo.GetDrives())
        {
            try { if (d.IsReady) PathBox.Items.Add(d.Name); } catch { }
        }

        foreach (var t in new[] { 1, 2, 4, 8, 16, 32, 64 })
            ThreadsBox.Items.Add(t);
        var cli = App.Options;
        int threads = cli.Threads ?? (ThreadsBox.Items.Contains(_settings.Threads) ? _settings.Threads : 16);
        if (!ThreadsBox.Items.Contains(threads)) // a value from the command line that is not in the list
        {
            int at = 0;
            while (at < ThreadsBox.Items.Count && (int)ThreadsBox.Items[at]! < threads) at++;
            ThreadsBox.Items.Insert(at, threads);
        }
        ThreadsBox.SelectedItem = threads;

        PathBox.Text = cli.Path
                       ?? _settings.LastPath
                       ?? (PathBox.Items.Count > 0 ? (string)PathBox.Items[0]! : "");

        _admin = new AdminStartup(this, AdminButton, () => PathBox.Text);
    }

    // Duplicate finder tab (DuplicatesPanel.xaml.cs): searches the selected folder, uses the shell menu from here.
    private void InitDuplicates()
    {
        Dup.Settings = _settings;
        Dup.RootProvider = _tree.DuplicateRoot;
        Dup.ShellMenuRequested += _shell.ShowFileShellMenu;
        Dup.ShellDeleteRequested += _shell.DeleteFile;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_admin.ElevateIfWanted()) return; // replaced by an elevated instance

        // A path on the command line is scanned right away.
        if (App.Options.Path != null) await BeginScan();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        foreach (var tab in _tabs.Tabs) tab.Cts?.Cancel(); // scans of all tabs stop with the window
        _winSettings.CaptureBounds();
        _settings.Threads = ThreadsBox.SelectedItem is int t ? t : 16;
        _settings.LastPath = PathBox.Text.Trim().Trim('"');
        _settings.Save();
    }

    // ---------------------------------------------------------------- the active tab changes

    /// <summary>The window is about to leave a tab: what it shows is part of the tab it belongs to.</summary>
    private void OnLeavingSession(ScanSession old)
    {
        old.PathText = PathBox.Text;
        if (_tree.SelectedRow is { } sel) old.SelectedVm = sel;
    }

    /// <summary>Puts a session's state into the window: path box, tree, status line, and the side panel for its scan.</summary>
    private void ShowSession(ScanSession s)
    {
        // Results of the other tab (search hits, duplicate groups) would point into its tree.
        SearchPane.Reset();

        PathBox.Text = s.PathText;
        AgePane.ShowStamp(s.AgePreferred);

        _tree.Clear();
        TopPane.Clear();
        _tree.ShowExisting(s);

        _status.ShowScanButton(s.Cts != null);
        _status.Apply(s);
        _snapshots.UpdateTitle();
        UpdateAgeNotices();
        ShowAgeHeader();

        // File types, age, treemap, owner for the folder that will be selected (the scan target until then).
        _ = RefreshChartsAsync();
        TopPane.Refresh(_scanner);
        OwnerTab.SetFolder(_tree.CurrentChartTarget().Node?.FullPath);
        Dup.Reset();

        // Bring back the selection; the rows do not exist before the tree was laid out.
        if (s.SelectedVm?.Node is { } n)
            Dispatcher.InvokeAsync(() => { if (_cur == s) _tree.Reveal(n); }, DispatcherPriority.Loaded);

        _tabs.EnsureVisible(s);
    }

    /// <summary>Tab title and tooltip from what was scanned.</summary>
    private static void SetTabTitle(ScanSession s, IReadOnlyList<string> paths)
    {
        string tip = PathList.Join(paths);
        s.SetTitle(paths.Count > 1 ? $"{paths.Count} Pfade" : paths[0], tip);
    }

    // ---- IExportSource: what the export controller reads from the window
    Scanner? IExportSource.Scanner => _scanner;
    Node? IExportSource.SelectedFolder => _tree.SelectedFolder;
    bool IExportSource.ScanRunning => _cts != null;

    // ---------------------------------------------------------------- side panel tab chips

    private bool _chipSync;

    private void InitTabChips()
    {
        foreach (TabItem t in SideTabs.Items) TabChips.Items.Add(t.Header);
        SideTabs.SelectedIndex = 0;
        TabChips.SelectedIndex = 0;
        // Programmatic selection (Ctrl+F, treemap ...) and clicks on the chips stay in step.
        SideTabs.SelectionChanged += (s, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, SideTabs) || _chipSync) return;
            _chipSync = true;
            TabChips.SelectedIndex = SideTabs.SelectedIndex;
            _chipSync = false;
        };
    }

    private void TabChips_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_chipSync || TabChips.SelectedIndex < 0) return;
        _chipSync = true;
        SideTabs.SelectedIndex = TabChips.SelectedIndex;
        _chipSync = false;
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null) { _cts.Cancel(); return; }
        await BeginScan();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Ordner zum Analysieren wählen" };
        if (dlg.ShowDialog(this) == true) PathBox.Text = dlg.FolderName;
    }

    // Dropping folders (or files: their folders) on the window scans them; several at once are scanned together.
    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return;
        if (await _snapshots.TryHandleDropAsync(files[0])) return;
        var paths = files.Select(f => File.Exists(f) ? Path.GetDirectoryName(f)! : f)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // With Ctrl or Shift held the folder opens in a new tab instead of replacing the scan of the current one.
        bool newTab = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
        await ScanPathAsync(paths.Count == 1 ? paths[0] : PathList.Join(paths), newTab);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_tabs.HandleKey(e)) { e.Handled = true; return; }
        if (e.Key == Key.F5 && _cts == null)
        {
            e.Handled = true;
            // A selected folder is scanned again in place (like Explorer's F5 for the current view), otherwise everything.
            if (_tree.SelectedFolder is { } node)
                _ = RescanAsync(node);
            else
                _ = BeginScan();
        }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            ShowSearch();
        }
        else if (e.Key == Key.Escape && _cts != null)
        {
            e.Handled = true;
            _cts.Cancel();
        }
    }

    private Task BeginScan()
    {
        var s = _cur;
        return s.ScanTask = StartScan(s);
    }

    /// <summary>Scans another folder (in a new tab if asked); a running scan of the tab is cancelled first.</summary>
    private async Task ScanPathAsync(string path, bool newTab = false)
    {
        if (TabsLocked) return;
        if (Busy is { } other && (newTab || other != _cur)) { _tabs.NoticeBusy(other); return; }
        if (newTab && !_cur.IsEmpty) _tabs.NewTab();

        var s = _cur;
        if (s.Cts != null)
        {
            s.Cts.Cancel();
            await s.ScanTask;
        }
        if (_cur != s) _tabs.Activate(s); // the user switched tabs while the old scan stopped
        PathBox.Text = path;
        await BeginScan();
    }

    private async Task StartScan(ScanSession s)
    {
        if (s.Cts != null) return;
        if (Busy is { } other) { _tabs.NoticeBusy(other); return; }

        var paths = PathList.Parse(PathBox.Text);
        if (paths.Count == 0) paths.Add(""); // reports "Pfad nicht gefunden" like before
        var missing = paths.Where(p => !Directory.Exists(p)).ToList();
        if (missing.Count > 0)
        {
            Dialogs.Warn(this, $"Pfad nicht gefunden:\n{string.Join("\n", missing)}");
            return;
        }
        paths = PathList.Reduce(paths);
        var path = paths[0];
        _settings.LastPath = PathList.Join(paths);

        await RunAsync(s, ct =>
        {
            var scanner = s.Scanner = new Scanner { Excludes = _winSettings.CurrentExcludes(), AgeStamp = s.AgePreferred };
            _snapshots.OnNewScanner(scanner);
            // Event instead of polling: the scanner reports changes from its worker threads, the UI reacts.
            scanner.Changed += () => Dispatcher.InvokeAsync(() => OnScannerChanged(s, scanner), DispatcherPriority.Background);
            s.Root = null;
            s.ScanTarget = null;
            s.SelectedVm = null;
            _tree.Clear();
            TopPane.Clear();

            // ScanAsync creates the tree (drive -> ... -> scanned folder) and starts the workers synchronously,
            // so the tree can be shown right away and fills up while the scan is running.
            var scan = paths.Count > 1
                ? scanner.ScanManyAsync(paths, (int)ThreadsBox.SelectedItem, ct)
                : scanner.ScanAsync(path, (int)ThreadsBox.SelectedItem, ct);
            Dispatcher.InvokeAsync(UpdateAgeNotices); // the scan has adopted the tab's choice by now
            SetTabTitle(s, paths);
            _tree.ShowScan(s);
            return scan;
        });
    }

    private Task RescanAsync(Node? node)
    {
        if (node == null || _scanner == null) return Task.CompletedTask;
        if (_scanner.IsMulti && node == _scanner.Root) return BeginScan(); // the artificial root: scan all paths again
        var s = _cur;
        return s.ScanTask = StartRescan(s, node);
    }

    /// <summary>
    /// Scans one folder of the existing tree again, in place. Its row and everything above it get new numbers,
    /// the rest of the tree is untouched.
    /// </summary>
    private async Task StartRescan(ScanSession s, Node node)
    {
        var scanner = s.Scanner!;
        if (!_snapshots.CanRescan(node)) return;
        if (Busy is { } other && other != s) { _tabs.NoticeBusy(other); return; }

        // A running scan has to stop first. (s.ScanTask still is the old scan here: this method runs
        // synchronously up to the first await, before RescanAsync assigns its own task.)
        if (s.Cts != null)
        {
            s.Cts.Cancel();
            await s.ScanTask;
        }

        await RunAsync(s, ct =>
        {
            // The previous scan's last change event may never have been acknowledged (the UI had already
            // finished); without this the scanner stays silent and the row would not update.
            scanner.Acknowledge();
            scanner.Excludes = _winSettings.CurrentExcludes(); // the same rules as the scan (incl. changes made since)
            var scan = scanner.RescanAsync(node, (int)ThreadsBox.SelectedItem, ct);
            s.Root?.FindDescendant(node)?.ResetForRescan();
            s.ScanTarget = scanner.ScanTarget;
            if (s == _cur) s.Root?.Refresh(); // spinner and "…" right away
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
        if (s == _cur) _status.ShowScanButton(true);
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
            if (s == _cur) _status.ShowIdle();
            _status.UpdateTaskbar();
        }
    }

    private void FinishScan(ScanSession s, bool canceled)
    {
        if (s.Scanner == null) return; // the tab was closed meanwhile
        bool active = s == _cur;
        if (active) s.Root?.Refresh(); // an inactive tab catches up when it is shown
        UpdateStatus(s, running: false, done: !canceled);
        if (canceled) _status.SetStatus(s, "Abgebrochen – " + s.Status);
        if (!active) return;
        _ = RefreshChartsAsync();
        TopPane.Refresh(_scanner);
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
        if (s != _cur) return; // a background tab only keeps its numbers; the tree is brought up to date when shown

        s.Root.Refresh();

        // Chart and top list have to walk / copy a lot, so they are refreshed less often than the tree.
        long now = Environment.TickCount64;
        if (!TypePane.IsBusy && now - TypePane.LastUpdateTick > 1000)
            _ = RefreshChartsAsync();
        if (now - TopPane.LastRefreshTick > 1000)
            TopPane.Refresh(_scanner);
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

        if (session == _cur) _status.Apply(session);
        else _status.UpdateTaskbar();
    }

    /// <summary>Brings treemap, age chart and file type chart up to date for the selected folder.</summary>
    private async Task RefreshChartsAsync()
    {
        _ = Treemap.RefreshAsync();
        var target = _tree.CurrentChartTarget();
        _ = UpdateAgeAsync(target);
        await TypePane.UpdateAsync(target);
    }

    // ---------------------------------------------------------------- tree and list: context menu and keys

    // Context menu, Delete, F2 and the copy key for the tree and the largest files: one behaviour for all lists (ListInteraction).
    private void InitListInteraction()
    {
        ListInteraction.Attach(Tree, new ListBehavior<NodeVM>
        {
            Selected = () => _tree.SelectedRow,
            ExtraKey = OpsTreeKey, // F2
            CanDelete = ShellMenuCoordinator.CanUseShell,
            Delete = _shell.DeleteNode,
            ShowMenu = _shell.ShowTreeMenu,
            CopyText = vm => vm.Path.Length > 0 ? vm.Path : null,
        });
        TopPane.ShowMenu = (row, pos) => _shell.ShowFileMenu(row.Path, row.Size, pos);
        TopPane.DeleteRow = row => _shell.DeleteFile(row.Path, row.Size);
        TopPane.ExtraKeyDown = OpsKey; // F2, Delete on several rows
    }
}
