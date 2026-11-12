using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace DiskTree;

/// <summary>Tab "Duplikate": starts a <see cref="DuplicateFinder"/> run for a folder and lists the groups of identical files.</summary>
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

    /// <summary>Folder to search: the folder selected in the tree (set by the main window).</summary>
    public Func<string?>? RootProvider { get; set; }

    /// <summary>For remembering the minimum size.</summary>
    public AppSettings? Settings { get; set; }

    /// <summary>The user asked for the shell context menu of a file (path, size, screen position). The host shows it.</summary>
    public event Action<string, long, Point>? ShellMenuRequested;

    /// <summary>The user pressed Delete on a file (path, size). The host runs the shell's delete.</summary>
    public event Action<string, long>? ShellDeleteRequested;

    /// <summary>Keys the host handles for the selection (F2, Delete on several files); returns true if it used the key.</summary>
    public Func<Key, bool>? ExtraKeyDown { get; set; }

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
        var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        return _groups.Any(g => g.Files.Count > 0 && g.Files.All(f => set.Contains(f.Path)));
    }

    /// <summary>Drops files that are gone from disk (after move / delete / rename), and groups that are no duplicates any more.</summary>
    public void PruneMissing()
    {
        foreach (var f in _groups.SelectMany(g => g.Files).ToList()) Prune(f);
    }

    /// <summary>The host switched to another scan: stop a running search and drop the groups of the old folder.</summary>
    public void Reset()
    {
        _cts?.Cancel();
        _groups.Clear();
        InfoText.Text = "";
        if (IsVisible) ShowTarget();
    }

    public DuplicatesPanel()
    {
        InitializeComponent();
        foreach (var o in MinOptions) MinBox.Items.Add(o);
        MinBox.SelectedItem = MinOptions[2];
        List.ItemsSource = _groups;
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

    private void ShowTarget()
    {
        var root = RootProvider?.Invoke();
        TargetText.Text = string.IsNullOrEmpty(root) ? "Duplikate" : "Duplikate – " + (Path.GetFileName(root.TrimEnd('\\')) is { Length: > 0 } n ? n : root);
        TargetText.ToolTip = root;
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null) { _cts.Cancel(); return; }

        var root = RootProvider?.Invoke();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            InfoText.Text = "Kein Ordner zum Durchsuchen. Bitte zuerst einen Ordner scannen und markieren.";
            return;
        }
        ShowTarget();

        bool network = DuplicateFinder.IsNetworkPath(root);
        if (network && MessageBox.Show(Window.GetWindow(this),
                "Der Ordner liegt auf einem Netzlaufwerk. Die Suche liest dort viele Dateien und kann lange dauern " +
                "und das Netzwerk belasten. Sie läuft deshalb nur mit wenigen parallelen Zugriffen.\n\nTrotzdem suchen?",
                "DiskTree", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        long min = (MinBox.SelectedItem as MinOption)?.Bytes ?? 1L << 20;
        int parallelism = network ? 2 : Math.Clamp(Environment.ProcessorCount / 2, 2, 6);

        _groups.Clear();
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
                : $"{found.Count:N0} Gruppen · {NodeVM.FormatSize(wasted)} verschwendet ({sw.Elapsed:mm\\:ss}" +
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
            _cts.Dispose();
            _cts = null;
            RunButton.Content = "Duplikate suchen";
            MinBox.IsEnabled = true;
            Bar.Visibility = Visibility.Collapsed;
        }
    }

    private void OnProgress(DupProgress p)
    {
        if (_cts == null) return; // a late report after the run ended must not overwrite the result
        switch (p.Phase)
        {
            case DupPhase.Enumerating:
                Bar.IsIndeterminate = true;
                InfoText.Text = $"Dateien werden aufgelistet … {p.FilesDone:N0}";
                break;
            case DupPhase.Sizes:
                InfoText.Text = $"{p.FilesDone:N0} Dateien nach Größe sortiert …";
                break;
            case DupPhase.PartialHash:
                Bar.IsIndeterminate = false;
                Bar.Value = p.FilesTotal > 0 ? (double)p.FilesDone / p.FilesTotal * 0.3 : 0;
                InfoText.Text = $"Vergleiche Dateianfänge: {p.FilesDone:N0} / {p.FilesTotal:N0}";
                break;
            case DupPhase.FullHash:
                Bar.IsIndeterminate = false;
                Bar.Value = 0.3 + (p.BytesTotal > 0 ? (double)p.BytesDone / p.BytesTotal * 0.7 : 0);
                InfoText.Text = $"Prüfe Inhalt: {NodeVM.FormatSize(p.BytesDone)} / {NodeVM.FormatSize(p.BytesTotal)}";
                break;
        }
    }

    // ---------------------------------------------------------------- context menu / keys

    private void List_RightDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<TreeViewItem>(e.OriginalSource) is { } item) item.IsSelected = true;
    }

    private void List_RightUp(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<TreeViewItem>(e.OriginalSource)?.DataContext is not DupFileVM file) return;
        e.Handled = true;
        ShowMenu(file, List.PointToScreen(e.GetPosition(List)));
    }

    private void List_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<TreeViewItem>(e.OriginalSource)?.DataContext is DupFileVM file)
        {
            e.Handled = true;
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file.Path}\"") { UseShellExecute = true });
        }
    }

    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        if (List.SelectedItem is not DupFileVM file) return;
        if (ExtraKeyDown?.Invoke(e.Key) == true) { e.Handled = true; return; }

        if (e.Key == Key.Delete)
        {
            e.Handled = true;
            ShellDeleteRequested?.Invoke(file.Path, file.Group.Size);
            Prune(file);
        }
        else if (e.Key == Key.Apps || (e.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift))
        {
            e.Handled = true;
            ShowMenu(file, List.PointToScreen(Mouse.GetPosition(List)));
        }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            Clipboard.SetText(file.Path);
        }
    }

    private void ShowMenu(DupFileVM file, Point screenPos)
    {
        ShellMenuRequested?.Invoke(file.Path, file.Group.Size, screenPos);
        Prune(file);
    }

    // The menu may have deleted or moved the file: if it is gone, it leaves the group (a group of one is no duplicate any more).
    private void Prune(DupFileVM file)
    {
        if (File.Exists(file.Path)) return;
        var group = file.Group;
        group.Files.Remove(file);
        if (group.Files.Count < 2) _groups.Remove(group);
        else group.Refresh();

        long wasted = _groups.Sum(g => g.Wasted);
        InfoText.Text = _groups.Count == 0 ? "Keine Duplikate mehr." : $"{_groups.Count:N0} Gruppen · {NodeVM.FormatSize(wasted)} verschwendet";
    }

    private static T? FindAncestor<T>(object? source) where T : DependencyObject
    {
        var d = source as DependencyObject;
        while (d != null && d is not T)
            d = d is Visual or Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }
}

public sealed class DupGroupVM : INotifyPropertyChanged
{
    public long Size { get; }
    public ObservableCollection<DupFileVM> Files { get; } = new();
    public bool IsExpanded { get; set; }

    public DupGroupVM(DuplicateGroup g)
    {
        Size = g.Size;
        foreach (var p in g.Paths) Files.Add(new DupFileVM(this, p));
    }

    public long Wasted => Size * (Files.Count - 1);
    public string Title => $"{Files.Count} × {NodeVM.FormatSize(Size)}";
    public string Subtitle => Files.Count > 0 ? Files[0].Name : "";
    public string WastedText => NodeVM.FormatSize(Wasted);
    public string Detail => $"{Files.Count} identische Dateien à {NodeVM.FormatSize(Size)}: {NodeVM.FormatSize(Wasted)} verschwendet";

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Refresh()
    {
        foreach (var n in new[] { nameof(Title), nameof(Subtitle), nameof(WastedText), nameof(Detail) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}

public sealed class DupFileVM
{
    public DupGroupVM Group { get; }
    public string Path { get; }
    public string Name { get; }
    public string Folder { get; }
    /// <summary>Check mark for choosing several files for move / ZIP / delete.</summary>
    public bool IsChecked { get; set; }

    public DupFileVM(DupGroupVM group, string path)
    {
        Group = group;
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        Folder = System.IO.Path.GetDirectoryName(path) ?? "";
    }
}
