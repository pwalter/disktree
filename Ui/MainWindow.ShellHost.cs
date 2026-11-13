using DiskTree.Core;
using DiskTree.Ui.Controllers;

namespace DiskTree.Ui;

// What the shell menu code (ShellMenuCoordinator) reads from and asks of the window.
public partial class MainWindow : IShellMenuHost
{
    Scanner? IShellMenuHost.Scanner => _scanner;

    bool IShellMenuHost.BlockedByOtherScan() => _tabs.BlockedByOtherScan();

    void IShellMenuHost.RefreshAfterDelete()
    {
        _tree.RefreshRows();
        _charts.RefreshViews();
    }

    void IShellMenuHost.Rescan(Node? node) => _ = _scan.RescanAsync(node);

    IReadOnlyList<(string Text, Action Run)> IShellMenuHost.OperationExtras(string path, bool isDir) => _fileOps.OperationExtras(path, isDir);
}
