using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DiskTree;

// Stored scans (.disktree) and the glue to the "Vergleich" tab. Kept in a separate file of the partial class.
public partial class MainWindow
{
    private void InitSnapshots()
    {
        Compare.CurrentScanner = () => _cts == null ? _scanner : null;
        Compare.JumpRequested += JumpToPath;
    }

    /// <summary>A fresh scan starts: remember when, and drop the "gespeicherter Scan" marker from the title.</summary>
    private void OnNewScanner(Scanner scanner)
    {
        scanner.ScannedAt = DateTime.Now;
        UpdateTitle();
    }

    private bool IsSnapshotMode => _scanner is { IsSnapshot: true };

    private void UpdateTitle()
    {
        var title = AdminHelper.IsAdmin ? "DiskTree (Administrator)" : "DiskTree";
        if (_scanner is { IsSnapshot: true } s)
            title += " – gespeicherter Scan" + (s.ScannedAt is { } t ? " vom " + t.ToString("g") : "");
        Title = title;
    }

    private static string StampText(DateTime? t) => t is { } v ? v.ToString("g") : "unbekannter Zeit";

    // ---------------------------------------------------------------- save

    private async void SaveSnapshot_Click(object sender, RoutedEventArgs e)
    {
        var scanner = _scanner;
        if (scanner?.Root == null)
        {
            Notice("Es gibt noch keinen Scan, den man speichern könnte.");
            return;
        }
        if (_cts != null)
        {
            Notice("Der Scan läuft noch. Bitte warten oder abbrechen, dann speichern.");
            return;
        }

        var target = scanner.ScanTarget ?? scanner.Root;
        var name = new string((string.IsNullOrEmpty(target.Name) ? "Scan" : target.Name)
            .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim('_', ' ');
        var dlg = new SaveFileDialog
        {
            Title = "Scan speichern",
            Filter = "DiskTree-Scan (*.disktree)|*.disktree",
            DefaultExt = SnapshotStore.Extension,
            FileName = $"{(name.Length > 0 ? name : "Scan")}_{(scanner.ScannedAt ?? DateTime.Now):yyyy-MM-dd}",
        };
        if (dlg.ShowDialog(this) != true) return;

        var file = dlg.FileName;
        var at = scanner.ScannedAt ?? DateTime.Now;
        SnapshotSaveButton.IsEnabled = false;
        var before = StatusText.Text;
        StatusText.Text = "Scan wird gespeichert …";
        try
        {
            await Task.Run(() => SnapshotStore.Save(file, scanner, at));
            StatusText.Text = $"Scan gespeichert: {file} ({NodeVM.FormatSize(new FileInfo(file).Length)})";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = before;
            Notice("Speichern fehlgeschlagen:\n" + ex.Message);
        }
        finally
        {
            SnapshotSaveButton.IsEnabled = true;
        }
    }

    // ---------------------------------------------------------------- open

    private async void OpenSnapshot_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Gespeicherten Scan öffnen",
            Filter = "DiskTree-Scan (*.disktree)|*.disktree|Alle Dateien|*.*",
        };
        if (dlg.ShowDialog(this) == true) await LoadSnapshotAsync(dlg.FileName);
    }

    private async Task LoadSnapshotAsync(string file)
    {
        if (_cts != null)
        {
            _cts.Cancel();
            await _scanTask;
        }

        StatusText.Text = "Scan wird geladen …";
        LoadedSnapshot snap;
        try
        {
            snap = await Task.Run(() => SnapshotStore.Load(file));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "";
            Notice($"Die Datei konnte nicht geladen werden:\n{file}\n\n{ex.Message}");
            return;
        }

        var scanner = Scanner.FromSnapshot(snap, file);
        OnSnapshotAgeStamp(snap.AgeStamp);
        _scanner = scanner;
        _scanTarget = scanner.ScanTarget;
        _root = new NodeVM(scanner.Root!, Kind.Dir, null, 0) { IsExpanded = true };
        Tree.ItemsSource = new[] { _root };
        ExpandToScanTarget();
        PathBox.Text = scanner.ScanTarget?.FullPath ?? "";
        CurrentText.Text = "";

        var t = scanner.ScanTarget!;
        StatusText.Text =
            $"Gespeicherter Scan vom {StampText(scanner.ScannedAt)}: {t.DirCount + 1:N0} Ordner · {t.FileCount:N0} Dateien · " +
            $"{NodeVM.FormatSize(t.Size)} · {t.FullPath}";
        UpdateTitle();
        _ = UpdatePieAsync();
        RefreshTop();
    }

    // ---------------------------------------------------------------- actions that need the disk

    private static bool Exists(string path) => Directory.Exists(path) || File.Exists(path);

    private void Notice(string text) =>
        MessageBox.Show(this, text, "DiskTree", MessageBoxButton.OK, MessageBoxImage.Information);

    private void SnapshotHint() =>
        Notice($"Das ist ein gespeicherter Scan vom {StampText(_scanner?.ScannedAt)}, nicht der aktuelle Stand der Platte.\n\n" +
               "Löschen und das Windows-Kontextmenü gibt es nur in einem aktuellen Scan (Scannen). " +
               "Mit „Ab hier neu scannen“ lässt sich ein Ordner, der auf diesem Rechner existiert, neu einlesen.");

    /// <summary>Rescan below a loaded snapshot only makes sense when the folder exists here.</summary>
    private bool CanRescan(Node node)
    {
        if (_scanner is not { IsSnapshot: true } || Directory.Exists(node.FullPath)) return true;
        Notice($"Der Ordner existiert auf diesem Rechner nicht:\n{node.FullPath}\n\nEr kann nicht neu gescannt werden.");
        return false;
    }

    /// <summary>In a snapshot the Windows menu (with "Löschen") is replaced by a small menu with our own entries.</summary>
    private void ShowSnapshotMenu(string path, bool isDir)
    {
        var menu = new ContextMenu();
        foreach (var (text, run) in Extras(path, isDir))
        {
            // Rescan and "open in Explorer" need the path to exist on this machine.
            bool needsDisk = !text.StartsWith("Pfad");
            var item = new MenuItem { Header = text, IsEnabled = !needsDisk || Exists(isDir ? path : Path.GetDirectoryName(path) ?? "") };
            item.Click += (_, _) => run();
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Gespeicherter Scan: Löschen nicht möglich", IsEnabled = false });
        menu.IsOpen = true;
    }

    // ---------------------------------------------------------------- jump from the comparison

    private void JumpToPath(string path)
    {
        var node = _scanner?.FindNode(path);
        if (_root == null || node == null)
        {
            StatusText.Text = $"Der Ordner ist im aktuellen Baum nicht vorhanden: {path}";
            return;
        }

        var chain = new List<Node>();
        for (var n = node; n != null; n = n.Parent) chain.Insert(0, n);

        var vms = new List<NodeVM> { _root };
        var vm = _root;
        foreach (var n in chain.Skip(1))
        {
            vm.IsExpanded = true; // creates the child rows
            vm = _root.FindDescendant(n);
            if (vm == null) return;
            vms.Add(vm);
        }

        // The containers only exist after the expanded rows were laid out.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            ItemsControl parent = Tree;
            TreeViewItem? item = null;
            foreach (var v in vms)
            {
                parent.UpdateLayout();
                item = parent.ItemContainerGenerator.ContainerFromItem(v) as TreeViewItem;
                if (item == null) return;
                if (!ReferenceEquals(v, vms[^1])) item.IsExpanded = true;
                parent = item;
            }
            if (item == null) return;
            item.IsSelected = true;
            item.BringIntoView();
            item.Focus();
        });
    }
}
