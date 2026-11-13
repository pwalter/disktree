using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DiskTree.Core;
using DiskTree.Ui.Controllers;

namespace DiskTree.Ui;

// Glue between the stored scans (SnapshotController), the "Vergleich" tab and the tree of the main window.
public partial class MainWindow : ISnapshotHost
{
    private SnapshotController _snapshots = null!;

    private void InitSnapshots()
    {
        _snapshots = new SnapshotController(this, this, SnapshotSaveButton, OpenSnapshotButton);
        Compare.CurrentScanner = () => _cts == null ? _scanner : null;
        Compare.JumpRequested += JumpToPath;
    }

    // ---- ISnapshotHost
    ScanSession ISnapshotHost.Current => _cur;
    bool ISnapshotHost.TabsLocked => TabsLocked;
    ScanSession ISnapshotHost.NewTab() => NewTab();
    void ISnapshotHost.SetStatus(ScanSession session, string text) => SetStatus(session, text);

    void ISnapshotHost.ShowSnapshot(ScanSession session, Scanner scanner, AgeStamp stamp)
    {
        OnSnapshotAgeStamp(stamp); // the tab's choice follows the file (the scanner already carries it in Ages)
        session.SelectedVm = null;
        _scanner = scanner;
        _scanTarget = scanner.ScanTarget;
        _root = new NodeVM(scanner.Root!, Kind.Dir, null, 0) { IsExpanded = true };
        Treemap.Reset(_scanTarget ?? scanner.Root);
        Tree.ItemsSource = new[] { _root };
        ExpandToScanTarget();
        PathBox.Text = scanner.ScanTarget?.FullPath ?? "";
        CurrentText.Text = "";
    }

    void ISnapshotHost.ShowSnapshotStatus(ScanSession session, string text)
    {
        session.StatusTip = null;
        StatusText.ToolTip = null;
        SetStatus(session, text);
    }

    void ISnapshotHost.RefreshViews()
    {
        _ = UpdatePieAsync();
        RefreshTop();
    }

    // ---------------------------------------------------------------- actions that need the disk

    private static bool Exists(string path) => Directory.Exists(path) || File.Exists(path);

    private void Notice(string text) =>
        Dialogs.Info(this, text);

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
