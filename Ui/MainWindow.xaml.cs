using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using DiskTree.Core;
using DiskTree.Features.Scan;
using DiskTree.Infrastructure;
using DiskTree.Ui.Controllers;
using Microsoft.Win32;

namespace DiskTree.Ui;

/// <summary>
/// The main window: layout (MainWindow.xaml) and wiring. What happens is in the controllers of Ui/Controllers; the
/// window creates them, hands them the controls and each other, and answers what they ask of the window (the
/// <c>I...Host</c> / <c>I...Source</c> interfaces below).
/// </summary>
public partial class MainWindow : Window, IExportSource, ISnapshotHost, IShellMenuHost
{
    private readonly AppSettings _settings = AppSettings.Load();
    private WindowSettings _winSettings = null!;
    private AdminStartup _admin = null!;
    private UpdateController _updater = null!;
    private ScanTabsController _tabs = null!;
    private ScanStatusView _status = null!;
    private TreeController _tree = null!;
    private SnapshotController _snapshots = null!;
    private ChartsController _charts = null!;
    private ScanController _scan = null!;
    private ShellMenuCoordinator _shell = null!;
    private SideTabsController _sideTabs = null!;
    private SearchController _search = null!;
    private FileOpsController _fileOps = null!;

    // The order is the dependency order: every step only uses what the steps above it created.
    //   toolbar (path, threads) -> scanning (tabs, tree, snapshots, charts, scan) -> panels that use the shell menus
    //   (shell menus need the snapshots) -> app buttons.
    public MainWindow()
    {
        InitializeComponent();

        InitToolbar();
        InitScanning();
        InitSidePanel();
        InitAppButtons();

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    // ---------------------------------------------------------------- construction

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

        _admin = new AdminStartup(this, AdminButton, () => PathBox.Text, SaveSettings);
    }

    /// <summary>Scan tabs, the tree, stored scans, the charts (the age panel needs the settings and the active tab) and the scan itself.</summary>
    private void InitScanning()
    {
        _tabs = new ScanTabsController(this, ScanTabBar, () => _fileOps.IsBusy || _updater.IsUpdating, () => PathBox.Focus());
        _tabs.Leaving += OnLeavingSession;
        _tabs.Shown += ShowSession;
        _status = new ScanStatusView(_tabs, ScanButton, StatusText, CurrentText, ScanProgress, TaskbarInfo);

        _tree = new TreeController(Tree, Treemap, _tabs);
        _tree.SelectionChanged += () => _charts.OnSelectionChanged();

        _snapshots = new SnapshotController(this, this, SnapshotSaveButton, OpenSnapshotButton);
        Compare.CurrentScanner = () => Cur.Cts == null ? Cur.Scanner : null;
        Compare.JumpRequested += path =>
        {
            if (!_tree.TryJumpToPath(path)) _status.ShowText($"Der Ordner ist im aktuellen Baum nicht vorhanden: {path}");
        };

        _charts = new ChartsController(_settings, _tabs, _tree, _snapshots, TypePane, AgePane, AgeTab, SideTabs, OldHeader,
            Treemap, TopPane, OwnerTab);
        _scan = new ScanController(this, _settings, _winSettings, _tabs, _status, _tree, _snapshots, _charts, PathBox, ThreadsBox, ScanButton);
        AgePane.RescanRequested += () =>
        {
            if (!string.IsNullOrWhiteSpace(PathBox.Text)) _scan.ScanPathAsync(PathBox.Text).Forget("ScanPathAsync");
        };

        // Static event: only while the window is open (otherwise the closed window would be kept alive and refreshed).
        Loaded += (_, _) => { Theme.Changed -= OnThemeChanged; Theme.Changed += OnThemeChanged; };
        Closed += (_, _) => Theme.Changed -= OnThemeChanged;
    }

    private void OnThemeChanged()
    {
        _tree.RefreshTheme();
        _charts.RefreshAsync().Forget("RefreshAsync");
    }

    /// <summary>The panels of the side panel that use the shell menus: search, largest files, duplicates; clean-up actions; list keys.</summary>
    private void InitSidePanel()
    {
        _shell = new ShellMenuCoordinator(this, this, _snapshots);
        _sideTabs = new SideTabsController(SideTabs, TabChips);
        _search = new SearchController(this, SearchPane, _sideTabs, SearchTab, _tree, _shell);

        // Move / ZIP / rename / delete work on the selection of the list the user is in: largest files, search hits, duplicates.
        _fileOps = new FileOpsController(this, _tabs, _tree, _status, _snapshots, _scan, _charts,
        [
            new ListSelection(TopPane.List,
                () => TopPane.SelectedRows.Select(r => new SelectedEntry(r.Path, false, r.Size))),
            new ListSelection(SearchPane.Results,
                () => SearchPane.SelectedHits.Select(h => new SelectedEntry(h.Path, h.IsDir, h.Size)),
                prune: SearchPane.PruneMissing),
            new ListSelection(Dup.List,
                () => Dup.SelectedFiles().Select(f => new SelectedEntry(f.Path, false, f.Size)),
                prune: Dup.PruneMissing, coversAllCopies: Dup.AllCopiesChecked),
        ]);
        SearchPane.ExtraKeyDown = _fileOps.HandleKey;

        // Duplicate finder tab (DuplicatesPanel.xaml.cs): searches the selected folder, uses the shell menu from here.
        Dup.Settings = _settings;
        Dup.RootProvider = _tree.DuplicateRoot;
        Dup.ShellMenuRequested += _shell.ShowFileShellMenu;
        Dup.ShellDeleteRequested += _shell.DeleteFile;
        Dup.ExtraKeyDown = _fileOps.HandleKey;

        // Context menu, Delete, F2 and the copy key for the tree and the largest files: one behaviour for all lists (ListInteraction).
        ListInteraction.Attach(Tree, new ListBehavior<NodeVM>
        {
            Selected = () => _tree.SelectedRow,
            ExtraKey = _fileOps.HandleTreeKey, // F2
            CanDelete = ShellMenuCoordinator.CanUseShell,
            Delete = _shell.DeleteNode,
            ShowMenu = _shell.ShowTreeMenu,
            CopyText = vm => vm.Path.Length > 0 ? vm.Path : null,
        });
        TopPane.ShowMenu = (row, pos) => _shell.ShowFileMenu(row.Path, row.Size, pos);
        TopPane.DeleteRow = row => _shell.DeleteFile(row.Path, row.Size);
        TopPane.ExtraKeyDown = _fileOps.HandleKey; // F2, Delete on several rows
    }

    /// <summary>Export, update check and the version menu.</summary>
    private void InitAppButtons()
    {
        _ = new ExportController(this, ExportButton, this);
        _updater = new UpdateController(this, _settings, UpdateButton, AutoUpdateItem, CheckUpdateItem, OpenReleasesItem,
            () => PathBox.Text, () => _tabs.Busy != null, SaveSettings);
        _ = new AppMenuController(this, _settings, VersionMenu, ExplorerItem, PortableItem);
    }

    // ---------------------------------------------------------------- window events

    private ScanSession Cur => _tabs.Current;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_admin.ElevateIfWanted()) return; // replaced by an elevated instance

        // A path on the command line is scanned right away.
        if (App.Options.Path != null) await _scan.BeginScan();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        foreach (var tab in _tabs.Tabs) tab.Cts?.Cancel(); // scans of all tabs stop with the window
        SaveSettings();
    }

    /// <summary>Takes over what the window holds (size, threads, path) and writes settings.json. Also before a restart of the program.</summary>
    private void SaveSettings()
    {
        _winSettings.CaptureBounds();
        _settings.Threads = ThreadsBox.SelectedItem is int t ? t : 16;
        _settings.LastPath = PathBox.Text.Trim().Trim('"');
        _settings.Save();
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
        await _scan.ScanPathAsync(paths.Count == 1 ? paths[0] : PathList.Join(paths), newTab);
    }

    // Shortcuts of the window: Ctrl+T / W / Tab ... (tabs), F5 (scan again), Ctrl+F (search), Esc (cancel the scan).
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_tabs.HandleKey(e)) { e.Handled = true; return; }
        if (e.Key == Key.F5 && !_scan.IsRunning)
        {
            e.Handled = true;
            _scan.RescanOrScan(_tree.SelectedFolder);
        }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            _search.Show();
        }
        else if (e.Key == Key.Escape && _scan.IsRunning)
        {
            e.Handled = true;
            _scan.Cancel();
        }
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
        _charts.ShowAgeStamp();

        _tree.Clear();
        _charts.ClearTopFiles();
        _tree.ShowExisting(s);

        _status.ShowScanButton(s.Cts != null);
        _status.Apply(s);
        _snapshots.UpdateTitle();
        _charts.AgeNoticesChanged();
        _charts.ShowAgeHeader();

        // File types, age, treemap, owner for the folder that will be selected (the scan target until then).
        _charts.RefreshViews();
        _charts.ShowOwnerFolder();
        Dup.Reset();

        // Bring back the selection; the rows do not exist before the tree was laid out.
        if (s.SelectedVm?.Node is { } n)
            Dispatcher.InvokeAsync(() => { if (Cur == s) _tree.Reveal(n); }, DispatcherPriority.Loaded);

        _tabs.EnsureVisible(s);
    }

    // ---------------------------------------------------------------- what the controllers ask of the window

    // ---- IExportSource
    Scanner? IExportSource.Scanner => Cur.Scanner;
    Node? IExportSource.SelectedFolder => _tree.SelectedFolder;
    bool IExportSource.ScanRunning => _scan.IsRunning;

    // ---- ISnapshotHost
    ScanSession ISnapshotHost.Current => Cur;
    bool ISnapshotHost.TabsLocked => _tabs.TabsLocked;
    bool ISnapshotHost.ScanRunning => _tabs.Busy != null;
    ScanSession ISnapshotHost.NewTab() => _tabs.NewTab();
    void ISnapshotHost.SetStatus(ScanSession session, string text) => _status.SetStatus(session, text);

    void ISnapshotHost.ShowSnapshot(ScanSession session, Scanner scanner, AgeStamp stamp)
    {
        _charts.OnSnapshotAgeStamp(stamp); // the tab's choice follows the file (the scanner already carries it in Ages)
        session.SelectedVm = null;
        session.Scanner = scanner;
        _tree.ShowScan(session);
        PathBox.Text = scanner.ScanTarget?.FullPath ?? "";
        _status.ClearCurrentLine();
    }

    void ISnapshotHost.ShowSnapshotStatus(ScanSession session, string text) => _status.SetSnapshotStatus(session, text);
    void ISnapshotHost.RefreshViews() => _charts.RefreshViews();

    // ---- IShellMenuHost
    Scanner? IShellMenuHost.Scanner => Cur.Scanner;
    bool IShellMenuHost.BlockedByOtherScan() => _tabs.BlockedByOtherScan();

    void IShellMenuHost.RefreshAfterDelete()
    {
        _tree.RefreshRows();
        _charts.RefreshViews();
    }

    void IShellMenuHost.Rescan(Node? node) => _scan.RescanAsync(node).Forget("RescanAsync");

    IReadOnlyList<(string Text, Action Run)> IShellMenuHost.OperationExtras(string path, bool isDir) =>
        _fileOps.OperationExtras(path, isDir);
}
