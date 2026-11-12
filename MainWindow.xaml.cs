using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DiskTree;

public partial class MainWindow : Window
{
    // After a change notification the UI waits this long before redrawing, so bursts collapse into one update.
    private static readonly TimeSpan RefreshDelay = TimeSpan.FromMilliseconds(120);

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly Stopwatch _sw = new();
    private CancellationTokenSource? _cts;
    private Scanner? _scanner;
    private NodeVM? _root;
    private Node? _scanTarget;
    private Task _scanTask = Task.CompletedTask;

    public MainWindow()
    {
        InitializeComponent();

        if (_settings.Width >= MinWidth && _settings.Height >= MinHeight)
        {
            Width = Math.Min(_settings.Width, SystemParameters.WorkArea.Width);
            Height = Math.Min(_settings.Height, SystemParameters.WorkArea.Height);
        }
        if (_settings.Maximized) WindowState = WindowState.Maximized;

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

        if (AdminHelper.IsAdmin)
        {
            Title = "DiskTree (Administrator)";
            AdminButton.Visibility = Visibility.Collapsed;
        }

        InitSnapshots();
        InitSearch();
        // Duplicate finder tab (DuplicatesPanel.xaml.cs): searches the selected folder, uses the shell menu from here.
        Dup.Settings = _settings;
        Dup.RootProvider = DuplicateRoot;
        Dup.ShellMenuRequested += (path, size, pos) => RunShell(path, isFile: true, size, null,
            () => ShellContextMenu.Show(this, path, pos, Extras(path, isDir: false)));
        Dup.ShellDeleteRequested += (path, size) => RunShell(path, isFile: true, size, null,
            () => ShellContextMenu.InvokeVerb(this, path, "delete"));

        Theme.Changed += () => _root?.RefreshTheme();
        ShowThemeButton();
        ShowExcludeButton();
        InitAge();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var cli = App.Options;

        // Optional: without admin rights protected folders are skipped. Declining is fine, the scan still works.
        if (!AdminHelper.IsAdmin)
        {
            if (cli.Admin)
            {
                // --admin: restart right away, no question. If UAC is declined the program just carries on.
                if (AdminHelper.RestartElevated(cli.ForwardArgs()))
                {
                    Application.Current.Shutdown();
                    return;
                }
            }
            else if (!cli.NoAdminPrompt && !AdminHelper.PromptSuppressed)
            {
                if (AdminHelper.Ask(this, out var never))
                {
                    if (AdminHelper.RestartElevated(cli.ForwardArgs()))
                    {
                        Application.Current.Shutdown();
                        return;
                    }
                }
                else if (never)
                {
                    AdminHelper.SuppressPrompt();
                }
            }
        }

        // A path on the command line is scanned right away.
        if (cli.Path != null) await BeginScan();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var bounds = WindowState == WindowState.Maximized ? RestoreBounds : new Rect(0, 0, ActualWidth, ActualHeight);
        _settings.Maximized = WindowState == WindowState.Maximized;
        _settings.Width = bounds.Width;
        _settings.Height = bounds.Height;
        _settings.Threads = ThreadsBox.SelectedItem is int t ? t : 16;
        _settings.LastPath = PathBox.Text.Trim().Trim('"');
        _settings.Save();
    }

    // ---- exclusions: saved ones plus those from the command line (--exclude) ----
    private ExcludeRules CurrentExcludes() => new(_settings.Excludes.Concat(App.Options.Excludes));

    private void ShowExcludeButton()
    {
        int n = _settings.Excludes.Count + App.Options.Excludes.Count;
        ExcludeButton.Content = n > 0 ? $"Ausschlüsse ({n})…" : "Ausschlüsse…";
    }

    private void Exclude_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ExcludeDialog(_settings.Excludes) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _settings.Excludes = dlg.Result;
        _settings.Save();
        ShowExcludeButton();
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        // Windows -> Hell -> Dunkel -> Windows ...
        _settings.Theme = _settings.Theme switch
        {
            ThemeSetting.System => ThemeSetting.Light,
            ThemeSetting.Light => ThemeSetting.Dark,
            _ => ThemeSetting.System,
        };
        Theme.Setting = _settings.Theme;
        ShowThemeButton();
    }

    private void ShowThemeButton() =>
        ThemeButton.Content = _settings.Theme switch
        {
            ThemeSetting.Light => "☀ Hell",
            ThemeSetting.Dark => "🌙 Dunkel",
            _ => "🌗 Windows",
        };

    private void Admin_Click(object sender, RoutedEventArgs e)
    {
        // Hand the current path over so the elevated instance starts scanning it right away.
        var args = PathList.Parse(PathBox.Text).Where(Directory.Exists).ToArray();

        if (AdminHelper.RestartElevated(args))
            Application.Current.Shutdown();
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
        await ScanPathAsync(paths.Count == 1 ? paths[0] : PathList.Join(paths));
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
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

    private static string NormalizePath(string text)
    {
        var path = text.Trim().Trim('"');
        return path.Length == 2 && path[1] == ':' ? path + "\\" : path;
    }

    private Task BeginScan() => _scanTask = StartScan();

    /// <summary>Scans another folder; a running scan is cancelled first.</summary>
    private async Task ScanPathAsync(string path)
    {
        if (_cts != null)
        {
            _cts.Cancel();
            await _scanTask;
        }
        PathBox.Text = path;
        await BeginScan();
    }

    private async Task StartScan()
    {
        if (_cts != null) return;

        var paths = PathList.Parse(PathBox.Text);
        if (paths.Count == 0) paths.Add(""); // reports "Pfad nicht gefunden" like before
        var missing = paths.Where(p => !Directory.Exists(p)).ToList();
        if (missing.Count > 0)
        {
            MessageBox.Show(this, $"Pfad nicht gefunden:\n{string.Join("\n", missing)}", "DiskTree", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        paths = PathList.Reduce(paths);
        var path = paths[0];
        _settings.LastPath = PathList.Join(paths);

        await RunAsync(ct =>
        {
            var scanner = _scanner = new Scanner { Excludes = CurrentExcludes() };
            OnNewScanner(scanner);
            // Event instead of polling: the scanner reports changes from its worker threads, the UI reacts.
            scanner.Changed += () => Dispatcher.InvokeAsync(() => OnScannerChanged(scanner), DispatcherPriority.Background);
            _root = null;
            _scanTarget = null;
            Tree.ItemsSource = null;
            TopList.ItemsSource = null;

            // ScanAsync creates the tree (drive -> ... -> scanned folder) and starts the workers synchronously,
            // so the tree can be shown right away and fills up while the scan is running.
            var scan = paths.Count > 1
                ? scanner.ScanManyAsync(paths, (int)ThreadsBox.SelectedItem, ct)
                : scanner.ScanAsync(path, (int)ThreadsBox.SelectedItem, ct);
            _scanTarget = scanner.ScanTarget;
            _root = new NodeVM(scanner.Root!, Kind.Dir, null, 0) { IsExpanded = true };
            Treemap.Reset(_scanTarget ?? scanner.Root);
            Tree.ItemsSource = new[] { _root };
            ExpandToScanTarget();
            return scan;
        });
    }

    private Task RescanAsync(Node? node) =>
        node == null || _scanner == null ? Task.CompletedTask
        : _scanner.IsMulti && node == _scanner.Root ? BeginScan() // the artificial root: scan all paths again
        : _scanTask = StartRescan(node);

    /// <summary>
    /// Scans one folder of the existing tree again, in place. Its row and everything above it get new numbers,
    /// the rest of the tree is untouched.
    /// </summary>
    private async Task StartRescan(Node node)
    {
        var scanner = _scanner!;
        if (!CanRescan(node)) return;

        // A running scan has to stop first. (_scanTask still is the old scan here: this method runs
        // synchronously up to the first await, before RescanAsync assigns its own task.)
        if (_cts != null)
        {
            _cts.Cancel();
            await _scanTask;
        }

        await RunAsync(ct =>
        {
            // The previous scan's last change event may never have been acknowledged (the UI had already
            // finished); without this the scanner stays silent and the row would not update.
            scanner.Acknowledge();
            scanner.Excludes = CurrentExcludes(); // the same rules as the scan (incl. changes made since)
            var scan = scanner.RescanAsync(node, (int)ThreadsBox.SelectedItem, ct);
            _root?.FindDescendant(node)?.ResetForRescan();
            _scanTarget = scanner.ScanTarget;
            _root?.Refresh(); // spinner and "…" right away
            return scan;
        });
    }

    /// <summary>
    /// Shared by scan and rescan: <paramref name="begin"/> starts the scan, sets the UI up and returns the scan task;
    /// this method shows the progress and the result.
    /// </summary>
    private async Task RunAsync(Func<CancellationToken, Task> begin)
    {
        _cts = new CancellationTokenSource();
        ScanButton.Content = "Abbrechen";
        _sw.Restart();

        try
        {
            await begin(_cts.Token);
            _sw.Stop();
            _root?.Refresh();
            UpdateStatus(running: false, done: true);
            _ = UpdatePieAsync();
            RefreshTop();
        }
        catch (OperationCanceledException)
        {
            _sw.Stop();
            _root?.Refresh();
            UpdateStatus(running: false);
            StatusText.Text = "Abgebrochen – " + StatusText.Text;
            _ = UpdatePieAsync();
            RefreshTop();
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            ScanButton.Content = "Scannen";
            CurrentText.Text = "";
            ScanProgress.Visibility = Visibility.Collapsed;
            TaskbarInfo.ProgressState = TaskbarItemProgressState.None;
        }
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

    private async Task OnScannerChanged(Scanner scanner)
    {
        // Let more changes pile up, then acknowledge first so anything arriving during the redraw
        // raises a new event instead of being lost.
        await Task.Delay(RefreshDelay);
        if (scanner != _scanner) return;
        if (_root == null || _cts == null)
        {
            scanner.Acknowledge(); // nothing to show now, but do not swallow the next scan's events
            return;
        }

        scanner.Acknowledge();
        UpdateStatus(running: true);
        _root.Refresh();

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

    private void UpdateStatus(bool running, bool done = false)
    {
        if (_scanner == null) return;
        var s = _scanner;

        bool known = s.ExpectedBytes > 0;
        double fraction = known ? Math.Min(0.99, (double)s.Bytes / s.ExpectedBytes) : 0;
        string percent = running && known ? $" · {fraction * 100:0} %" : "";

        StatusText.Text =
            $"{(done ? "Fertig: " : "")}{s.Dirs:N0} Ordner · {s.Files:N0} Dateien · {NodeVM.FormatSize(s.Bytes)}" +
            $" · {s.Errors:N0} nicht lesbar" + (s.Excluded > 0 ? $" · {s.Excluded:N0} ausgeschlossen" : "") +
            $" · {_sw.Elapsed:mm\\:ss\\.f}{percent}";
        StatusText.ToolTip = s.Excluded > 0 ? ExcludedTooltip(s) : null;
        CurrentText.Text = running ? s.Current : "";

        if (running)
        {
            // A drive scan knows roughly how much there is to find (used space); a folder scan does not.
            ScanProgress.Visibility = Visibility.Visible;
            ScanProgress.IsIndeterminate = !known;
            if (known) ScanProgress.Value = fraction;
            TaskbarInfo.ProgressState = known ? TaskbarItemProgressState.Normal : TaskbarItemProgressState.Indeterminate;
            if (known) TaskbarInfo.ProgressValue = fraction;
        }
        else
        {
            ScanProgress.Visibility = Visibility.Collapsed;
            TaskbarInfo.ProgressState = TaskbarItemProgressState.None;
        }
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

    private static readonly Brush[] CategoryBrushes = FileTypes.All.Select(c =>
    {
        var b = new SolidColorBrush(Color.FromRgb((byte)(c.Color >> 16), (byte)(c.Color >> 8), (byte)c.Color));
        b.Freeze();
        return (Brush)b;
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
            $"{NodeVM.FormatSize(sizes[i])} · {counts[i]:N0} Dateien · {(total > 0 ? (double)sizes[i] / total : 0):P1}";

        Pie.SetData(
            order.Select(i => new PieSlice(FileTypes.All[i].Name, sizes[i], CategoryBrushes[i], Detail(i))).ToList(),
            NodeVM.FormatSize(total), $"{files:N0} Dateien");
        Legend.ItemsSource = order.Select(i => new TypeRow
        {
            Brush = CategoryBrushes[i],
            Name = FileTypes.All[i].Name,
            SizeText = NodeVM.FormatSize(sizes[i]),
            PercentText = (total > 0 ? (double)sizes[i] / total : 0).ToString("P1"),
            Detail = Detail(i),
        }).ToList();
        PieTitle.Text = "Dateitypen – " + title;
    }

    private static (long[] Sizes, long[] Counts) SumTypes(Node start, bool ownOnly)
    {
        var sizes = new long[FileTypes.Count];
        var counts = new long[FileTypes.Count];
        var stack = new Stack<Node>();
        stack.Push(start);

        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n.OwnTypes is { } types)
                foreach (var t in types) { sizes[t.Cat] += t.Size; counts[t.Cat] += t.Count; }
            if (!ownOnly)
                foreach (var c in n.Snapshot()) stack.Push(c);
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

        var selected = (TopList.SelectedItem as TopRow)?.Path;
        var rows = _scanner.TopFiles().Select(f => new TopRow
        {
            Path = f.Path,
            Name = Path.GetFileName(f.Path),
            Folder = Path.GetDirectoryName(f.Path) ?? "",
            Size = f.Size,
            SizeText = NodeVM.FormatSize(f.Size),
        }).ToList();

        TopList.ItemsSource = rows;
        if (selected != null)
            TopList.SelectedItem = rows.FirstOrDefault(r => r.Path == selected);
    }

    private void TopList_RightDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource) is { } item) item.IsSelected = true;
    }

    private void TopList_RightUp(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource)?.DataContext is not TopRow row) return;
        e.Handled = true;
        ShowFileMenu(row, TopList.PointToScreen(e.GetPosition(TopList)));
    }

    private void TopList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource)?.DataContext is TopRow row)
            OpenInExplorer(row.Path);
    }

    private void TopList_KeyDown(object sender, KeyEventArgs e)
    {
        if (TopList.SelectedItem is not TopRow row) return;

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
        if (FindAncestor<TreeViewItem>(e.OriginalSource) is { } item) item.IsSelected = true;
    }

    private void Tree_RightUp(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<TreeViewItem>(e.OriginalSource) is not { DataContext: NodeVM vm }) return;
        e.Handled = true;
        ShowTreeMenu(vm, Tree.PointToScreen(e.GetPosition(Tree)));
    }

    private void Tree_KeyDown(object sender, KeyEventArgs e)
    {
        if (Tree.SelectedItem is not NodeVM vm) return;

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
        return list;
    }

    /// <summary>Opens the folder that contains the item and selects the item there.</summary>
    private static void OpenInExplorer(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return; // e.g. a stored scan of another machine
        OpenInExplorerCore(path);
    }

    private static void OpenInExplorerCore(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });

    private void RunShell(string path, bool isFile, long size, NodeVM? vm, Action action)
    {
        if (IsSnapshotMode) { SnapshotHint(); return; }
        int ageBucket = isFile ? AgeBuckets.OfFile(path) : -1; // read before the file is gone
        try
        {
            action();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "DiskTree", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // The menu may have deleted the item (recycle bin or permanently): if it is gone, take it out of the tree.
        if (File.Exists(path) || Directory.Exists(path)) return;

        if (vm != null)
            vm.RemoveFromTree(ageBucket);
        else if (isFile && _scanner?.FindNode(Path.GetDirectoryName(path) ?? "") is { } dir)
            dir.RemoveFile(Path.GetFileName(path), size, ageBucket);

        _scanner?.RemoveTop(path);
        if (_scanner?.Root is { } drive) Scanner.ReadDrive(drive);

        _root?.Refresh();
        _ = UpdatePieAsync();
        RefreshTop();
    }

    private static T? FindAncestor<T>(object? source) where T : DependencyObject
    {
        var d = source as DependencyObject;
        while (d != null && d is not T)
            d = d is Visual or Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }
}

public sealed class TypeRow
{
    public required Brush Brush { get; init; }
    public required string Name { get; init; }
    public required string SizeText { get; init; }
    public required string PercentText { get; init; }
    public required string Detail { get; init; }
}

public sealed class TopRow
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required string Folder { get; init; }
    public required long Size { get; init; }
    public required string SizeText { get; init; }
}
