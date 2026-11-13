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

public partial class MainWindow : Window, IExportSource, IScanViews
{
    private readonly AppSettings _settings = AppSettings.Load();
    private WindowSettings _winSettings = null!;
    private AdminStartup _admin = null!;
    private UpdateController _updater = null!;
    private ShellMenuCoordinator _shell = null!;
    private ScanTabsController _tabs = null!;
    private ScanStatusView _status = null!;
    private TreeController _tree = null!;
    private ScanController _scan = null!;
    private FileOpsController _fileOps = null!;

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
        _tabs = new ScanTabsController(this, ScanTabBar, () => _fileOps.IsBusy || _updater.IsUpdating, () => PathBox.Focus());
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
        _scan = new ScanController(this, _settings, _winSettings, _tabs, _status, _tree, _snapshots, this, PathBox, ThreadsBox, ScanButton);
        _shell = new ShellMenuCoordinator(this, this, _snapshots);
        InitSearch();
        InitFileOps();
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

    // Move / ZIP / rename / delete work on the selection of the list the user is in: largest files, search hits, duplicates.
    private void InitFileOps()
    {
        _fileOps = new FileOpsController(this, _tabs, _tree, _status, _snapshots, _scan, this,
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
        Dup.ExtraKeyDown = _fileOps.HandleKey;
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
        if (App.Options.Path != null) await _scan.BeginScan();
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

    // ---- IExportSource: what the export controller reads from the window
    Scanner? IExportSource.Scanner => _scanner;
    Node? IExportSource.SelectedFolder => _tree.SelectedFolder;
    bool IExportSource.ScanRunning => _scan.IsRunning;

    // ---- IScanViews: what a running scan asks of the views
    void IScanViews.ClearTopFiles() => TopPane.Clear();
    void IScanViews.AgeNoticesChanged() => UpdateAgeNotices();

    void IScanViews.RefreshViews()
    {
        _ = RefreshChartsAsync();
        TopPane.Refresh(_scanner);
    }

    void IScanViews.RefreshLive()
    {
        long now = Environment.TickCount64;
        if (!TypePane.IsBusy && now - TypePane.LastUpdateTick > 1000)
            _ = RefreshChartsAsync();
        if (now - TopPane.LastRefreshTick > 1000)
            TopPane.Refresh(_scanner);
    }

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
            ShowSearch();
        }
        else if (e.Key == Key.Escape && _scan.IsRunning)
        {
            e.Handled = true;
            _scan.Cancel();
        }
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
}
