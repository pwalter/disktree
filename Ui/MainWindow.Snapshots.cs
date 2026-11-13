using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DiskTree.Core;
using DiskTree.Features.Scan;
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
        Compare.JumpRequested += path =>
        {
            if (!_tree.TryJumpToPath(path)) _status.ShowText($"Der Ordner ist im aktuellen Baum nicht vorhanden: {path}");
        };
    }

    // ---- ISnapshotHost
    ScanSession ISnapshotHost.Current => _cur;
    bool ISnapshotHost.TabsLocked => TabsLocked;
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

    void ISnapshotHost.ShowSnapshotStatus(ScanSession session, string text)
    {
        _status.SetSnapshotStatus(session, text);
    }

    void ISnapshotHost.RefreshViews() => _charts.RefreshViews();
}
