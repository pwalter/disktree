using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DiskTree.Core;
using DiskTree.Core.Snapshots;
using DiskTree.Infrastructure;
using DiskTree.Ui.Controllers;
using Microsoft.Win32;

namespace DiskTree.Ui;

public partial class MainWindow : Window, IExportSource
{
    // After a change notification the UI waits this long before redrawing, so bursts collapse into one update.
    private static readonly TimeSpan RefreshDelay = TimeSpan.FromMilliseconds(120);

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly WindowSettings _winSettings;
    private readonly AdminStartup _admin;
    private UpdateController _updater = null!;
    // Scan state (_scanner, _root, _cts ...) belongs to the active scan tab: see MainWindow.Tabs.cs.

    public MainWindow()
    {
        InitializeComponent();

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

        InitTabs();
        InitSnapshots();
        InitSearch();
        InitOps();
        // Duplicate finder tab (DuplicatesPanel.xaml.cs): searches the selected folder, uses the shell menu from here.
        Dup.Settings = _settings;
        Dup.RootProvider = DuplicateRoot;
        Dup.ShellMenuRequested += (path, size, pos) => RunShell(path, isFile: true, size, null,
            () => ShellContextMenu.Show(this, path, pos, Extras(path, isDir: false)));
        Dup.ShellDeleteRequested += (path, size) => RunShell(path, isFile: true, size, null,
            () => ShellContextMenu.InvokeVerb(this, path, "delete"));

        Theme.Changed += () => { _root?.RefreshTheme(); _ = UpdatePieAsync(); };
        InitTabChips();
        _ = new ExportController(this, ExportButton, this);
        _updater = new UpdateController(this, _settings, UpdateButton, AutoUpdateItem, CheckUpdateItem, OpenReleasesItem,
            () => PathBox.Text, () => Busy != null);
        _ = new AppMenuController(this, _settings, VersionMenu, ExplorerItem, PortableItem);
        InitAge();
        InitAgeStamp();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_admin.ElevateIfWanted()) return; // replaced by an elevated instance

        // A path on the command line is scanned right away.
        if (App.Options.Path != null) await BeginScan();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        foreach (var tab in _tabs) tab.Cts?.Cancel(); // scans of all tabs stop with the window
        _winSettings.CaptureBounds();
        _settings.Threads = ThreadsBox.SelectedItem is int t ? t : 16;
        _settings.LastPath = PathBox.Text.Trim().Trim('"');
        _settings.Save();
    }

    // ---- IExportSource: what the export controller reads from the window
    Scanner? IExportSource.Scanner => _scanner;
    Node? IExportSource.SelectedFolder => Tree.SelectedItem is NodeVM { Kind: Kind.Dir, Node: { } sel } ? sel : null;
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
        if (files[0].EndsWith(SnapshotStore.Extension, StringComparison.OrdinalIgnoreCase) && File.Exists(files[0]))
        {
            await LoadSnapshotAsync(files[0]);
            return;
        }
        var paths = files.Select(f => File.Exists(f) ? Path.GetDirectoryName(f)! : f)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // With Ctrl or Shift held the folder opens in a new tab instead of replacing the scan of the current one.
        bool newTab = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
        await ScanPathAsync(paths.Count == 1 ? paths[0] : PathList.Join(paths), newTab);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (TabKey(e)) { e.Handled = true; return; }
        if (e.Key == Key.F5 && _cts == null)
        {
            e.Handled = true;
            // A selected folder is scanned again in place (like Explorer's F5 for the current view), otherwise everything.
            if (Tree.SelectedItem is NodeVM { Kind: Kind.Dir, Node: { } node })
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
        if (Busy is { } other && (newTab || other != _cur)) { NoticeBusy(other); return; }
        if (newTab && !_cur.IsEmpty) NewTab();

        var s = _cur;
        if (s.Cts != null)
        {
            s.Cts.Cancel();
            await s.ScanTask;
        }
        if (_cur != s) ActivateTab(s); // the user switched tabs while the old scan stopped
        PathBox.Text = path;
        await BeginScan();
    }

    private async Task StartScan(ScanSession s)
    {
        if (s.Cts != null) return;
        if (Busy is { } other) { NoticeBusy(other); return; }

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
            OnNewScanner(scanner);
            // Event instead of polling: the scanner reports changes from its worker threads, the UI reacts.
            scanner.Changed += () => Dispatcher.InvokeAsync(() => OnScannerChanged(s, scanner), DispatcherPriority.Background);
            s.Root = null;
            s.ScanTarget = null;
            s.SelectedVm = null;
            Tree.ItemsSource = null;
            TopList.ItemsSource = null;

            // ScanAsync creates the tree (drive -> ... -> scanned folder) and starts the workers synchronously,
            // so the tree can be shown right away and fills up while the scan is running.
            var scan = paths.Count > 1
                ? scanner.ScanManyAsync(paths, (int)ThreadsBox.SelectedItem, ct)
                : scanner.ScanAsync(path, (int)ThreadsBox.SelectedItem, ct);
            Dispatcher.InvokeAsync(UpdateAgeNotices); // the scan has adopted the tab's choice by now
            SetTabTitle(s, paths);
            s.ScanTarget = scanner.ScanTarget;
            s.Root = new NodeVM(scanner.Root!, Kind.Dir, null, 0) { IsExpanded = true };
            Treemap.Reset(s.ScanTarget ?? scanner.Root);
            Tree.ItemsSource = new[] { s.Root };
            ExpandToScanTarget();
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
        if (!CanRescan(node)) return;
        if (Busy is { } other && other != s) { NoticeBusy(other); return; }

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
        if (s == _cur) ScanButton.Content = "Abbrechen";
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
            if (s == _cur)
            {
                ScanButton.Content = "Scannen";
                CurrentText.Text = "";
                ScanProgress.Visibility = Visibility.Collapsed;
            }
            UpdateTaskbar();
        }
    }

    private void FinishScan(ScanSession s, bool canceled)
    {
        if (s.Scanner == null) return; // the tab was closed meanwhile
        bool active = s == _cur;
        if (active) s.Root?.Refresh(); // an inactive tab catches up when it is shown
        UpdateStatus(s, running: false, done: !canceled);
        if (canceled) SetStatus(s, "Abgebrochen – " + s.Status);
        if (!active) return;
        _ = UpdatePieAsync();
        RefreshTop();
    }

    // When a subfolder is scanned the tree starts at the drive: open the chain down to the scanned folder.
    private void ExpandToScanTarget()
    {
        var vm = _root;
        while (vm != null && vm.Node != _scanTarget)
        {
            vm.IsExpanded = true;
            vm = vm.Children.FirstOrDefault(c => c.Kind == Kind.Dir);
        }
        if (vm != null) vm.IsExpanded = true;
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
        if (!_pieBusy && now - _pieLastTick > 1000)
            _ = UpdatePieAsync();
        if (now - _topLastTick > 1000)
            RefreshTop();
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

        if (session == _cur) ApplyStatus(session);
        else UpdateTaskbar();
    }

    // ---------------------------------------------------------------- file-type chart

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        Treemap.SetTreeSelection(PieTarget().Node);
        _ = UpdatePieAsync();
        OwnerTab.SetFolder(PieTarget().Node?.FullPath);
    }

    // Treemap click / dive: select that folder in the tree (opening the path down to it).
    private void Treemap_NodeClicked(Node node)
    {
        if (_root == null) return;
        var chain = new List<Node>();
        for (var n = node; n != null; n = n.Parent) chain.Insert(0, n);
        if (chain.Count == 0 || chain[0] != _root.Node) return;

        var vm = _root;
        foreach (var n in chain.Skip(1))
        {
            vm.IsExpanded = true;
            vm = vm.FindDescendant(n);
            if (vm == null) return;
        }
        vm.IsSelected = true;
        Dispatcher.InvokeAsync(() => BringIntoView(vm), DispatcherPriority.Loaded);
    }

    private void BringIntoView(NodeVM vm)
    {
        var path = new List<NodeVM>();
        for (var v = vm; v != null; v = v.Parent) path.Insert(0, v);
        ItemsControl host = Tree;
        for (int i = 0; i < path.Count; i++)
        {
            if (host.ItemContainerGenerator.ContainerFromItem(path[i]) is not TreeViewItem item) return;
            if (i == path.Count - 1) { item.BringIntoView(); return; }
            host = item;
        }
    }

    private int _pieVersion;
    private bool _pieBusy;
    private long _pieLastTick;

    private static readonly Brush[] CategoryBrushesLight = MakeCategoryBrushes(false);
    private static readonly Brush[] CategoryBrushesDark = MakeCategoryBrushes(true);
    private static Brush[] CategoryBrushes => Theme.IsDark ? CategoryBrushesDark : CategoryBrushesLight;

    // On the dark panel the darkest categories (e.g. programs, #3F4A57) would vanish: lighten them a little.
    private static Brush[] MakeCategoryBrushes(bool dark) => FileTypes.All.Select(c =>
    {
        byte r = (byte)(c.Color >> 16), g = (byte)(c.Color >> 8), bl = (byte)c.Color;
        if (dark && 0.299 * r + 0.587 * g + 0.114 * bl < 110)
        {
            r = (byte)(r + (255 - r) * 0.45); g = (byte)(g + (255 - g) * 0.45); bl = (byte)(bl + (255 - bl) * 0.45);
        }
        return BrushFactory.Frozen(r, g, bl);
    }).ToArray();

    // What the chart describes: the selected folder with everything below it, or just the files of one folder.
    private (Node? Node, bool OwnOnly, string Title) PieTarget()
    {
        switch (Tree.SelectedItem as NodeVM)
        {
            case { Kind: Kind.Dir } d:
                return (d.Node, false, d.Node?.Name ?? "");
            case { Kind: Kind.Files } f:
                return (f.Parent?.Node, true, "Dateien in " + f.Parent?.Node?.Name);
            case { Kind: Kind.File } file:
                var dir = file.Parent?.Parent;
                return (dir?.Node, true, "Dateien in " + dir?.Node?.Name);
        }
        var target = _scanTarget ?? _root?.Node;
        return (target, false, target?.Name ?? "");
    }

    private async Task UpdatePieAsync()
    {
        _ = Treemap.RefreshAsync();
        var (node, ownOnly, title) = PieTarget();
        _ = UpdateAgeAsync(node, ownOnly, title);
        int version = ++_pieVersion;
        _pieLastTick = Environment.TickCount64;

        if (node == null)
        {
            Pie.SetData(Array.Empty<PieSlice>(), "", "");
            Legend.ItemsSource = null;
            PieTitle.Text = "Dateitypen";
            return;
        }

        _pieBusy = true;
        long[] sizes, counts;
        try
        {
            (sizes, counts) = await Task.Run(() => SumTypes(node, ownOnly));
        }
        finally
        {
            _pieBusy = false;
        }
        if (version != _pieVersion) return; // a newer request is on its way

        long total = sizes.Sum(), files = counts.Sum();
        var order = Enumerable.Range(0, FileTypes.Count)
            .Where(i => counts[i] > 0)
            .OrderByDescending(i => sizes[i])
            .ToList();

        string Detail(int i) =>
            $"{SizeFormat.Format(sizes[i])} · {counts[i]:N0} Dateien · {(total > 0 ? (double)sizes[i] / total : 0):P1}";

        Pie.SetData(
            order.Select(i => new PieSlice(FileTypes.All[i].Name, sizes[i], CategoryBrushes[i], Detail(i))).ToList(),
            SizeFormat.Format(total), $"{files:N0} Dateien");
        Legend.ItemsSource = order.Select(i => new LegendRow
        {
            Brush = CategoryBrushes[i],
            Name = FileTypes.All[i].Name,
            SizeText = SizeFormat.Format(sizes[i]),
            PercentText = (total > 0 ? (double)sizes[i] / total : 0).ToString("P1"),
            Detail = Detail(i),
        }).ToList();
        PieTitle.Text = "Dateitypen – " + title;
    }

    private static (long[] Sizes, long[] Counts) SumTypes(Node start, bool ownOnly)
    {
        var sizes = new long[FileTypes.Count];
        var counts = new long[FileTypes.Count];
        foreach (var n in ownOnly ? [start] : start.Walk())
        {
            if (n.OwnTypes is { } types)
                foreach (var t in types) { sizes[t.Cat] += t.Size; counts[t.Cat] += t.Count; }
        }
        return (sizes, counts);
    }

    // ---------------------------------------------------------------- largest files

    private long _topLastTick;

    private void RefreshTop()
    {
        _topLastTick = Environment.TickCount64;
        if (_scanner == null)
        {
            TopList.ItemsSource = null;
            return;
        }

        var selected = TopList.SelectedItems.Cast<TopRow>().Select(r => r.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = _scanner.TopFiles().Select(f => new TopRow
        {
            Path = f.Path,
            Name = Path.GetFileName(f.Path),
            Folder = Path.GetDirectoryName(f.Path) ?? "",
            Size = f.Size,
            SizeText = SizeFormat.Format(f.Size),
        }).ToList();

        TopList.ItemsSource = rows;
        foreach (var r in rows)
            if (selected.Contains(r.Path)) TopList.SelectedItems.Add(r);
    }

    private void TopList_RightDown(object sender, MouseButtonEventArgs e)
    {
        // Right click inside a multi-selection keeps it (the menu acts on all of it); elsewhere it selects just that row.
        if (VisualTreeHelpers.FindAncestor<ListBoxItem>(e.OriginalSource) is { IsSelected: false } item)
        {
            TopList.UnselectAll();
            item.IsSelected = true;
        }
    }

    private void TopList_RightUp(object sender, MouseButtonEventArgs e)
    {
        if (VisualTreeHelpers.FindAncestor<ListBoxItem>(e.OriginalSource)?.DataContext is not TopRow row) return;
        e.Handled = true;
        ShowFileMenu(row, TopList.PointToScreen(e.GetPosition(TopList)));
    }

    private void TopList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (VisualTreeHelpers.FindAncestor<ListBoxItem>(e.OriginalSource)?.DataContext is TopRow row)
            OpenInExplorer(row.Path);
    }

    private void TopList_KeyDown(object sender, KeyEventArgs e)
    {
        if (TopList.SelectedItem is not TopRow row) return;
        if (OpsKey(e.Key)) { e.Handled = true; return; } // F2, Delete on several rows

        if (e.Key == Key.Delete)
        {
            e.Handled = true;
            RunShell(row.Path, isFile: true, row.Size, null, () => ShellContextMenu.InvokeVerb(this, row.Path, "delete"));
        }
        else if (e.Key == Key.Apps || (e.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift))
        {
            e.Handled = true;
            ShowFileMenu(row, TopList.PointToScreen(Mouse.GetPosition(TopList)));
        }
    }

    // Folder for the duplicate search: the selected folder (or the folder of the selected file), else what was scanned.
    private string? DuplicateRoot()
    {
        var vm = Tree.SelectedItem as NodeVM;
        if (vm is { Kind: Kind.File or Kind.Files }) vm = vm.Parent is { Kind: Kind.Dir } p ? p : vm.Parent?.Parent;
        var path = vm is { Kind: Kind.Dir } ? vm.Path : (_scanTarget ?? _root?.Node)?.FullPath;
        return string.IsNullOrEmpty(path) ? null : path;
    }

    private void ShowFileMenu(TopRow row, Point screenPos)
    {
        if (IsSnapshotMode) { ShowSnapshotMenu(row.Path, isDir: false); return; }
        ShowFileMenuShell(row, screenPos);
    }

    private void ShowFileMenuShell(TopRow row, Point screenPos) =>
        RunShell(row.Path, isFile: true, row.Size, null,
            () => ShellContextMenu.Show(this, row.Path, screenPos, Extras(row.Path, isDir: false)));

    // ---------------------------------------------------------------- tree: context menu and keys

    // Right click selects the row under the cursor first, like in Explorer.
    private void Tree_RightDown(object sender, MouseButtonEventArgs e)
    {
        if (VisualTreeHelpers.FindAncestor<TreeViewItem>(e.OriginalSource) is { } item) item.IsSelected = true;
    }

    private void Tree_RightUp(object sender, MouseButtonEventArgs e)
    {
        if (VisualTreeHelpers.FindAncestor<TreeViewItem>(e.OriginalSource) is not { DataContext: NodeVM vm }) return;
        e.Handled = true;
        ShowTreeMenu(vm, Tree.PointToScreen(e.GetPosition(Tree)));
    }

    private void Tree_KeyDown(object sender, KeyEventArgs e)
    {
        if (Tree.SelectedItem is not NodeVM vm) return;
        if (OpsTreeKey(e.Key, vm)) { e.Handled = true; return; }

        if (e.Key == Key.Delete && CanUseShell(vm))
        {
            e.Handled = true;
            RunShell(vm.Path, vm.Kind == Kind.File, vm.Size, vm, () => ShellContextMenu.InvokeVerb(this, vm.Path, "delete"));
        }
        else if (e.Key == Key.Apps || (e.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift))
        {
            e.Handled = true;
            ShowTreeMenu(vm, Tree.PointToScreen(Mouse.GetPosition(Tree)));
        }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && vm.Path.Length > 0)
        {
            e.Handled = true;
            Clipboard.SetText(vm.Path);
        }
    }

    // The "Dateien" row stands for a whole folder's files, so it gets no menu: "Löschen" there would hit the folder.
    // The artificial "Mehrere Pfade" root has no path either.
    private static bool CanUseShell(NodeVM vm) => vm.Kind is Kind.Dir or Kind.File && vm.Path.Length > 0;

    private void ShowTreeMenu(NodeVM vm, Point screenPos)
    {
        if (!CanUseShell(vm)) return;
        if (IsSnapshotMode) { ShowSnapshotMenu(vm.Path, vm.Kind == Kind.Dir); return; }
        RunShell(vm.Path, vm.Kind == Kind.File, vm.Size, vm,
            () => ShellContextMenu.Show(this, vm.Path, screenPos, Extras(vm.Path, vm.Kind == Kind.Dir)));
    }

    // Our own entries on top of the Windows menu.
    private IReadOnlyList<(string Text, Action Run)> Extras(string path, bool isDir)
    {
        var list = new List<(string, Action)>();

        // Re-evaluate from here: for a folder the folder itself, for a file the folder it lies in.
        // Afterwards the folder (and everything above it) has the numbers of what is really on disk.
        if (isDir)
            list.Add(("Ab hier neu scannen", () => _ = RescanAsync(_scanner?.FindNode(path))));
        else
            list.Add(("Ordner dieser Datei neu scannen", () => _ = RescanAsync(_scanner?.FindNode(Path.GetDirectoryName(path) ?? ""))));

        list.Add(("Übergeordneten Ordner öffnen", () => OpenInExplorer(path)));
        list.Add(("Pfad kopieren", () => Clipboard.SetText(path)));
        list.AddRange(OpExtras(path, isDir)); // move / ZIP / rename / delete (MainWindow.Operations.cs)
        return list;
    }

    /// <summary>Opens the folder that contains the item and selects the item there.</summary>
    private static void OpenInExplorer(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return; // e.g. a stored scan of another machine
        ShellOpen.InExplorer(path);
    }

    private void RunShell(string path, bool isFile, long size, NodeVM? vm, Action action)
    {
        if (IsSnapshotMode) { SnapshotHint(); return; }
        if (Busy is { } other && other != _cur) { NoticeBusy(other); return; } // only one scan at a time, nothing else meanwhile
        int ageBucket = isFile ? _scanner?.Ages.OfFile(path) ?? -1 : -1; // read before the file is gone
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Dialogs.Warn(this, ex.Message);
            return;
        }

        // The menu may have deleted the item (recycle bin or permanently): if it is gone, take it out of the tree.
        if (File.Exists(path) || Directory.Exists(path)) return;

        if (vm != null)
            vm.RemoveFromTree(ageBucket);
        else if (isFile && _scanner?.FindNode(Path.GetDirectoryName(path) ?? "") is { } dir)
            TreeEditor.RemoveFile(dir, Path.GetFileName(path), size, ageBucket);

        _scanner?.RemoveTop(path);
        if (_scanner?.Root is { } drive) Scanner.ReadDrive(drive);

        _root?.Refresh();
        _ = UpdatePieAsync();
        RefreshTop();
    }
}
